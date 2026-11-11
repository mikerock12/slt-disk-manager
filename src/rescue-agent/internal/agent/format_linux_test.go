//go:build linux

package agent

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Real sfdisk/mkfs on synthetic files through loop devices (root, SLT_LINUX_INTEGRATION=1).
func TestAdaptiveLinuxFormatDiskOnSyntheticFiles(t *testing.T) {
	if os.Getenv("SLT_LINUX_INTEGRATION") != "1" {
		t.Skip("set SLT_LINUX_INTEGRATION=1 for synthetic formatting integration")
	}
	if !formatToolsAvailable() {
		t.Fatal("formatting engines missing (root, sfdisk, losetup, mkfs.ntfs, mkfs.fat, mkfs.ext4)")
	}
	_, cert, pair := testCertificate(t)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"cancelRequested":false}`)
	}))
	server.TLS = &tls.Config{Certificates: []tls.Certificate{pair}}
	server.StartTLS()
	defer server.Close()
	pin := sha256.Sum256(cert.Raw)
	client, err := NewClient(server.URL, hex.EncodeToString(pin[:]), nil)
	if err != nil {
		t.Fatal(err)
	}
	cases := []FormatOptions{{TableType: "GPT", FileSystem: "NTFS", Label: "DADOS"}, {TableType: "MBR", FileSystem: "FAT32", Label: "PENDRIVE"}, {TableType: "GPT", FileSystem: "EXT4", Label: "linux"}, {TableType: "MBR", FileSystem: "NTFS"}}
	if _, err := exec.LookPath("mkfs.exfat"); err == nil {
		cases = append(cases, FormatOptions{TableType: "GPT", FileSystem: "EXFAT", Label: "EXTERNO"})
	} else {
		t.Log("mkfs.exfat ausente neste host: exFAT não exercitado")
	}
	expected := map[string]string{"NTFS": "ntfs", "EXFAT": "exfat", "FAT32": "vfat", "EXT4": "ext4"}
	for _, options := range cases {
		options := options
		t.Run(options.TableType+"-"+options.FileSystem, func(t *testing.T) {
			dir := t.TempDir()
			garbage := make([]byte, 1024*1024)
			for i := range garbage {
				garbage[i] = byte(i*31 + 7)
			}
			path := filepath.Join(dir, "target.raw")
			f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
			if err != nil {
				t.Fatal(err)
			}
			// Old content at both ends: the new table and filesystem must replace it.
			if _, err = f.WriteAt(garbage, 0); err != nil {
				t.Fatal(err)
			}
			if err = f.Truncate(160 * 1024 * 1024); err != nil {
				t.Fatal(err)
			}
			f.Close()
			disks, err := CollectDisks(context.Background(), dir)
			if err != nil || len(disks) != 1 {
				t.Fatalf("fixture: %v %d", err, len(disks))
			}
			d := disks[0]
			plan := Plan{ID: "format-" + strings.ToLower(options.TableType+options.FileSystem), Kind: "FORMAT_DISK", DeviceID: "pc-1", Consent: true, ExpiresAtUTC: time.Now().Add(10 * time.Minute), DiskFingerprint: d.Fingerprint, DiskPath: d.Path, DiskSizeBytes: d.SizeBytes, LogicalSectorBytes: d.LogicalSectorBytes, Challenge: "FORMATAR fixture", Format: &options}
			readOnly := Runner{Client: client, Options: Options{FixtureDir: dir, Output: io.Discard}, DeviceID: "pc-1"}
			if _, err = readOnly.execute(context.Background(), plan); err == nil {
				t.Fatal("formatação executada sem permissão de gravação")
			}
			r := Runner{Client: client, Options: Options{FixtureDir: dir, AllowFileWrite: true, Output: io.Discard}, DeviceID: "pc-1"}
			details, err := r.execute(context.Background(), plan)
			if err != nil {
				t.Fatal(err)
			}
			var report FormatReport
			if json.Unmarshal([]byte(details), &report) != nil || !report.Verified || report.FileSystem != options.FileSystem {
				t.Fatalf("relatório inesperado: %s", details)
			}
			table, err := exec.Command("sfdisk", "--json", path).Output()
			if err != nil {
				t.Fatal(err)
			}
			var parsed struct {
				PartitionTable struct {
					Label      string `json:"label"`
					Partitions []struct {
						Start int64 `json:"start"`
					} `json:"partitions"`
				} `json:"partitiontable"`
			}
			if json.Unmarshal(table, &parsed) != nil || len(parsed.PartitionTable.Partitions) != 1 || parsed.PartitionTable.Partitions[0].Start != 2048 {
				t.Fatalf("tabela inesperada: %s", table)
			}
			if want := map[string]string{"GPT": "gpt", "MBR": "dos"}[options.TableType]; parsed.PartitionTable.Label != want {
				t.Fatalf("tabela %s em vez de %s", parsed.PartitionTable.Label, want)
			}
			kind, err := exec.Command("blkid", "-p", "-O", "1048576", "-s", "TYPE", "-o", "value", path).Output()
			if err != nil || strings.TrimSpace(string(kind)) != expected[options.FileSystem] {
				t.Fatalf("sistema de arquivos %q em vez de %s: %v", strings.TrimSpace(string(kind)), expected[options.FileSystem], err)
			}
			if options.Label != "" {
				label, _ := exec.Command("blkid", "-p", "-O", "1048576", "-s", "LABEL", "-o", "value", path).Output()
				if !strings.EqualFold(strings.TrimSpace(string(label)), options.Label) {
					t.Fatalf("rótulo %q em vez de %s", strings.TrimSpace(string(label)), options.Label)
				}
			}
			loops, _ := exec.Command("losetup", "--associated", path).Output()
			if strings.TrimSpace(string(loops)) != "" {
				t.Fatalf("loop não liberado: %s", loops)
			}
		})
	}
}
