package agent

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"regexp"
	"runtime"
	"strings"
	"time"
)

// FORMAT_DISK erases one offline disk and leaves a single partition with the chosen
// filesystem. It follows the same gates as a restore: signed plan, fingerprint
// revalidation, exclusive open and confirmation.

type FormatOptions struct {
	TableType  string `json:"tableType"`
	FileSystem string `json:"fileSystem"`
	Label      string `json:"label"`
}

type FormatReport struct {
	TableType     string `json:"tableType"`
	FileSystem    string `json:"fileSystem"`
	Label         string `json:"label"`
	PartitionPath string `json:"partitionPath"`
	Verified      bool   `json:"verified"`
}

var formatLabelPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{0,16}$`)

func validateFormat(o *FormatOptions) error {
	if o == nil {
		return errors.New("formatação sem opções")
	}
	if o.TableType != "GPT" && o.TableType != "MBR" {
		return errors.New("tabela de partições não suportada")
	}
	switch o.FileSystem {
	case "NTFS", "EXT4":
	case "EXFAT", "FAT32":
		if len(o.Label) > 11 {
			return errors.New("rótulo longo demais para FAT")
		}
	default:
		return errors.New("sistema de arquivos não suportado")
	}
	if !formatLabelPattern.MatchString(o.Label) {
		return errors.New("rótulo inválido")
	}
	return nil
}

func formatMaker(fileSystem string) string {
	switch fileSystem {
	case "NTFS":
		return "mkfs.ntfs"
	case "EXFAT":
		return "mkfs.exfat"
	case "FAT32":
		return "mkfs.fat"
	}
	return "mkfs.ext4"
}

func formatToolsAvailable() bool {
	if runtime.GOOS != "linux" || os.Geteuid() != 0 {
		return false
	}
	for _, name := range []string{"sfdisk", "losetup", "udevadm", "blkid", "mkfs.ntfs", "mkfs.fat", "mkfs.ext4"} {
		if _, err := exec.LookPath(name); err != nil {
			return false
		}
	}
	return true
}

// formatPartitionScript describes one partition from 1 MiB to the end of the disk.
func formatPartitionScript(o FormatOptions, sector int) (string, error) {
	if sector != 512 && sector != 4096 {
		return "", errors.New("setor lógico inválido")
	}
	start := int64(1024*1024) / int64(sector)
	if o.TableType == "GPT" {
		kind := "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"
		if o.FileSystem == "EXT4" {
			kind = "0FC63DAF-8483-4772-8E79-3D69D8477DE4"
		}
		return fmt.Sprintf("label: gpt\nstart=%d, type=%s\n", start, kind), nil
	}
	kind := map[string]string{"NTFS": "7", "EXFAT": "7", "FAT32": "c", "EXT4": "83"}[o.FileSystem]
	return fmt.Sprintf("label: dos\nstart=%d, type=%s\n", start, kind), nil
}

func formatCommand(o FormatOptions, device string) []string {
	switch o.FileSystem {
	case "NTFS":
		args := []string{"mkfs.ntfs", "--quick", "--force"}
		if o.Label != "" {
			args = append(args, "--label", o.Label)
		}
		return append(args, device)
	case "EXFAT":
		args := []string{"mkfs.exfat"}
		if o.Label != "" {
			args = append(args, "-L", o.Label)
		}
		return append(args, device)
	case "FAT32":
		args := []string{"mkfs.fat", "-F", "32"}
		if o.Label != "" {
			args = append(args, "-n", strings.ToUpper(o.Label))
		}
		return append(args, device)
	}
	args := []string{"mkfs.ext4", "-F", "-q"}
	if o.Label != "" {
		args = append(args, "-L", o.Label)
	}
	return append(args, device)
}

func (r *Runner) formatDisk(ctx context.Context, p Plan, d Disk) (string, error) {
	if err := validateFormat(p.Format); err != nil {
		return "", err
	}
	o := *p.Format
	if !formatToolsAvailable() {
		return "", errors.New("formatação exige SLT Rescue em Linux como root com sfdisk, losetup e mkfs")
	}
	if _, err := exec.LookPath(formatMaker(o.FileSystem)); err != nil {
		return "", errors.New("esta mídia Rescue não traz " + formatMaker(o.FileSystem) + "; use a ISO SLT atual")
	}
	script, err := formatPartitionScript(o, d.LogicalSectorBytes)
	if err != nil {
		return "", err
	}
	// Same path as a restore: write permission, confirmation, fingerprint revalidated under the exclusive claim.
	f, err := r.openDisk(ctx, p, d, true)
	if err != nil {
		return "", err
	}
	f.Close()
	loop, err := attachLoop(ctx, d.Path, "--partscan")
	if err != nil {
		return "", err
	}
	defer detachLoop(loop, d.Path)
	step := func(phase string, input string, name string, args ...string) error {
		if err := r.report(ctx, p, phase, 0, d.SizeBytes); err != nil {
			return err
		}
		bounded, cancel := context.WithTimeout(ctx, 30*time.Minute)
		defer cancel()
		var reader *strings.Reader
		if input != "" {
			reader = strings.NewReader(input)
		}
		var out []byte
		if reader != nil {
			out, err = tool(bounded, reader, name, args...)
		} else {
			out, err = tool(bounded, nil, name, args...)
		}
		if err != nil {
			return fmt.Errorf("%s falhou; o disco pode estar sem partição válida: %w %s", name, err, strings.TrimSpace(string(out)))
		}
		return nil
	}
	if err = step("PARTITIONING", script, "sfdisk", "--wipe", "always", "--wipe-partitions", "always", loop); err != nil {
		return "", err
	}
	partition := partitionNode(loop, 1)
	if err = waitNode(ctx, partition); err != nil {
		return "", err
	}
	command := formatCommand(o, partition)
	if err = step("FORMATTING", "", command[0], command[1:]...); err != nil {
		return "", err
	}
	_, _ = tool(ctx, nil, "udevadm", "settle", "--timeout=10")
	out, err := tool(ctx, nil, "blkid", "-p", "-s", "TYPE", "-o", "value", partition)
	found := strings.TrimSpace(string(out))
	expected := map[string]string{"NTFS": "ntfs", "EXFAT": "exfat", "FAT32": "vfat", "EXT4": "ext4"}[o.FileSystem]
	if err != nil || found != expected {
		return "", fmt.Errorf("conferência após formatar encontrou %q em vez de %s", found, expected)
	}
	if _, err = tool(ctx, nil, "sync"); err != nil {
		return "", err
	}
	if err = r.report(ctx, p, "VERIFIED", d.SizeBytes, d.SizeBytes); err != nil {
		return "", err
	}
	details, _ := json.Marshal(FormatReport{TableType: o.TableType, FileSystem: o.FileSystem, Label: o.Label, PartitionPath: d.Path + " (partição 1)", Verified: true})
	return string(details), nil
}
