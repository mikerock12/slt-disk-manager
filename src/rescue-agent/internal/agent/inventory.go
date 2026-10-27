package agent

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"
)

func Fingerprint(d Disk) string {
	h := sha256.Sum256([]byte(fmt.Sprintf("%s|%s|%d|%d|%s", d.Model, d.Serial, d.SizeBytes, d.LogicalSectorBytes, d.Path)))
	return hex.EncodeToString(h[:])
}

type block struct {
	Name        string          `json:"name"`
	Path        string          `json:"path"`
	Type        string          `json:"type"`
	Model       string          `json:"model"`
	Serial      string          `json:"serial"`
	Size        json.Number     `json:"size"`
	Logical     json.Number     `json:"log-sec"`
	Physical    json.Number     `json:"phy-sec"`
	RO          json.RawMessage `json:"ro"`
	Filesystem  string          `json:"fstype"`
	Mountpoints []*string       `json:"mountpoints"`
	Children    []block         `json:"children"`
}

func parseBlocks(data []byte) ([]Disk, error) {
	var result struct {
		Blockdevices []block `json:"blockdevices"`
	}
	if err := json.Unmarshal(data, &result); err != nil {
		return nil, err
	}
	disks := []Disk{}
	for _, b := range result.Blockdevices {
		if b.Type != "disk" {
			continue
		}
		size, err := b.Size.Int64()
		if err != nil || size <= 0 {
			return nil, errors.New("lsblk retornou tamanho inválido")
		}
		logical, err := strconv.Atoi(b.Logical.String())
		if err != nil || logical <= 0 {
			return nil, errors.New("lsblk retornou setor lógico inválido")
		}
		physical, _ := strconv.Atoi(b.Physical.String())
		d := Disk{Path: b.Path, Model: strings.TrimSpace(b.Model), Serial: strings.TrimSpace(b.Serial), SizeBytes: size, LogicalSectorBytes: logical, PhysicalSectorBytes: physical, IsReadOnly: string(b.RO) == "true" || string(b.RO) == "1", Health: "UNKNOWN", Partitions: []Partition{}}
		var walk func(block)
		walk = func(node block) {
			for _, mount := range node.Mountpoints {
				if mount != nil && *mount != "" {
					d.Mounted = true
					if *mount == "/" || *mount == "/boot" || *mount == "/boot/efi" || *mount == "[SWAP]" || strings.HasPrefix(*mount, "/run/live") {
						d.IsSystem = true
					}
				}
			}
			if node.Type != "disk" {
				n, _ := node.Size.Int64()
				mount := ""
				for _, v := range node.Mountpoints {
					if v != nil && *v != "" {
						mount = *v
						break
					}
				}
				d.Partitions = append(d.Partitions, Partition{Name: node.Name, Path: node.Path, SizeBytes: n, FileSystem: node.Filesystem, MountPoint: mount})
			}
			for _, c := range node.Children {
				walk(c)
			}
		}
		walk(b)
		if !strings.HasPrefix(d.Path, "/dev/") || d.Serial == "" {
			d.Health = "IDENTITY_INCOMPLETE"
		}
		d.Fingerprint = Fingerprint(d)
		d.ID = d.Fingerprint
		disks = append(disks, d)
	}
	return disks, nil
}

func CollectDisks(ctx context.Context, fixtureDir string) ([]Disk, error) {
	if fixtureDir != "" {
		return fixtureDisks(fixtureDir)
	}
	if runtime.GOOS != "linux" {
		return nil, errors.New("inventário físico Rescue exige Linux; use --fixture-dir apenas em laboratório")
	}
	command := exec.CommandContext(ctx, "lsblk", "--json", "--bytes", "--paths", "--output", "NAME,PATH,TYPE,MODEL,SERIAL,SIZE,LOG-SEC,PHY-SEC,RO,FSTYPE,MOUNTPOINTS")
	output, err := command.Output()
	if err != nil {
		return nil, fmt.Errorf("inventário lsblk: %w", err)
	}
	return parseBlocks(output)
}

func fixtureDisks(dir string) ([]Disk, error) {
	root, err := filepath.Abs(dir)
	if err != nil {
		return nil, err
	}
	root, err = filepath.EvalSymlinks(root)
	if err != nil {
		return nil, err
	}
	entries, err := os.ReadDir(root)
	if err != nil {
		return nil, err
	}
	disks := []Disk{}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".raw") || e.Type()&os.ModeSymlink != 0 {
			continue
		}
		info, err := e.Info()
		if err != nil {
			return nil, err
		}
		if !info.Mode().IsRegular() || info.Size() <= 0 || info.Size()%512 != 0 {
			continue
		}
		d := Disk{Path: filepath.Join(root, e.Name()), Model: "SLT SYNTHETIC FILE", Serial: e.Name(), SizeBytes: info.Size(), LogicalSectorBytes: 512, PhysicalSectorBytes: 512, Health: "FIXTURE_NOT_HARDWARE", Partitions: []Partition{}}
		d.Fingerprint = Fingerprint(d)
		d.ID = d.Fingerprint
		disks = append(disks, d)
	}
	return disks, nil
}

func NewInventory(ctx context.Context, deviceID, fixtureDir string) (Inventory, error) {
	disks, err := CollectDisks(ctx, fixtureDir)
	if err != nil {
		return Inventory{}, err
	}
	hostname, _ := os.Hostname()
	capabilities := []string{"INVENTORY", "SMART", "CAPTURE", "RESTORE"}
	notes := []string{"Captura raw de disco offline. Restauração física exige aprovação local adicional. Boot e hardware ainda exigem homologação."}
	if fixtureDir != "" {
		hostname += "-FIXTURE"
		notes = append(notes, "MODO DE TESTE: discos são arquivos .raw sintéticos.")
	}
	return Inventory{DeviceID: deviceID, Hostname: hostname, OS: runtime.GOOS + " " + runtime.GOARCH, Mode: "RESCUE", AgentVersion: "0.1.0", Capabilities: capabilities, Disks: disks, CapturedAtUTC: time.Now().UTC(), Notes: notes}, nil
}
