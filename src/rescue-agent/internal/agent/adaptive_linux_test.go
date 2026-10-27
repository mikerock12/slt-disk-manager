//go:build linux

package agent

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func linuxIntegration(t *testing.T) {
	t.Helper()
	if os.Getenv("SLT_LINUX_INTEGRATION") != "1" {
		t.Skip("set SLT_LINUX_INTEGRATION=1 for synthetic filesystem-engine integration")
	}
	for _, name := range []string{"sfdisk", "blkid", "e2fsck", "resize2fs", "mkfs.ext4", "mkfs.fat", "mkntfs", "ntfscp", "ntfscat", "ntfsresize", "ntfs-3g.probe", "debugfs"} {
		if _, err := exec.LookPath(name); err != nil {
			t.Fatalf("required engine missing: %s", name)
		}
	}
}

func makeLinuxImage(t *testing.T, table, fs string) (string, Manifest, []byte) {
	t.Helper()
	ctx := context.Background()
	dir := t.TempDir()
	path := filepath.Join(dir, "source.raw")
	const mib = int64(1024 * 1024)
	f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err = f.Truncate(256 * mib); err != nil {
		t.Fatal(err)
	}
	l := testLayout(table)
	l.Partitions[1].FileSystem = fs
	if fs == "ntfs" {
		if table == "GPT" {
			l.Partitions[1].Type = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"
		} else {
			l.Partitions[1].Type = "7"
		}
	}
	if _, err = f.WriteAt([]byte("SLT_BOOT_CODE_SENTINEL"), 0); err != nil {
		t.Fatal(err)
	}
	bootPath := filepath.Join(dir, "esp.raw")
	if err = os.WriteFile(bootPath, nil, 0600); err != nil {
		t.Fatal(err)
	}
	if err = os.Truncate(bootPath, 32*mib); err != nil {
		t.Fatal(err)
	}
	if _, err = tool(ctx, nil, "mkfs.fat", "-F", "32", bootPath); err != nil {
		t.Fatal(err)
	}
	boot, err := os.Open(bootPath)
	if err != nil {
		t.Fatal(err)
	}
	err = copyRange(ctx, f, mib, boot, 0, 32*mib)
	boot.Close()
	if err != nil {
		t.Fatal(err)
	}
	partPath := filepath.Join(dir, "system.raw")
	if err = os.WriteFile(partPath, nil, 0600); err != nil {
		t.Fatal(err)
	}
	if err = os.Truncate(partPath, 160*mib); err != nil {
		t.Fatal(err)
	}
	sentinel := bytes.Repeat([]byte("SLT_OS_AND_USER_DATA_SENTINEL\x00"), 1024)
	sentinelPath := filepath.Join(dir, "sentinel.bin")
	if err = os.WriteFile(sentinelPath, sentinel, 0600); err != nil {
		t.Fatal(err)
	}
	if fs == "ext4" {
		if _, err = tool(ctx, nil, "mkfs.ext4", "-q", "-F", partPath); err != nil {
			t.Fatal(err)
		}
		if _, err = tool(ctx, nil, "debugfs", "-w", "-R", "write "+sentinelPath+" /sentinel.bin", partPath); err != nil {
			t.Fatal(err)
		}
	} else {
		if _, err = tool(ctx, nil, "mkntfs", "--fast", "--force", partPath); err != nil {
			t.Fatal(err)
		}
		if _, err = tool(ctx, nil, "ntfscp", partPath, sentinelPath, "/sentinel.bin"); err != nil {
			t.Fatal(err)
		}
	}
	part, err := os.Open(partPath)
	if err != nil {
		t.Fatal(err)
	}
	err = copyRange(ctx, f, 33*mib, part, 0, 160*mib)
	part.Close()
	if err != nil {
		t.Fatal(err)
	}
	recovery := bytes.Repeat([]byte("SLT_WINRE_OEM_PRESERVE"), 4096)
	if _, err = f.WriteAt(recovery, 193*mib); err != nil {
		t.Fatal(err)
	}
	if err = f.Sync(); err != nil {
		t.Fatal(err)
	}
	if err = rewriteLayout(ctx, path, l, 256*mib); err != nil {
		t.Fatal(err)
	}
	actual, err := InspectLayout(ctx, path, 256*mib, 512)
	if err != nil {
		t.Fatal(err)
	}
	chunks, err := chunksForFile(ctx, f, 256*mib)
	if err != nil {
		t.Fatal(err)
	}
	m := Manifest{SchemaVersion: 1, ID: "synthetic-image", State: "AVAILABLE", SourceDeviceID: "synthetic-source", SourceFingerprint: "synthetic-fingerprint", LogicalSizeBytes: 256 * mib, LogicalSectorBytes: 512, Layout: actual, Chunks: chunks}
	return path, m, sentinel
}

func TestAdaptiveLinuxFitNTFSAndExt4PreserveWholeDisk(t *testing.T) {
	linuxIntegration(t)
	ctx := context.Background()
	const mib = int64(1024 * 1024)
	for _, table := range []string{"GPT", "MBR"} {
		for _, fs := range []string{"ext4", "ntfs"} {
			t.Run(table+"_"+fs, func(t *testing.T) {
				sourcePath, m, sentinel := makeLinuxImage(t, table, fs)
				source, err := os.Open(sourcePath)
				if err != nil {
					t.Fatal(err)
				}
				defer source.Close()
				before, err := hashRange(ctx, source, 0, m.LogicalSizeBytes)
				if err != nil {
					t.Fatal(err)
				}
				for _, size := range []int64{192 * mib, 320 * mib} {
					work := t.TempDir()
					prepared, err := prepareImage(ctx, sourcePath, work, m, size, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2})
					if err != nil {
						t.Fatalf("target=%d: %v", size, err)
					}
					if len(prepared.Layout.Partitions) != 3 || prepared.Report.BootVerified || !prepared.Report.TableVerified || !prepared.Report.BootPayloadPreserved {
						t.Fatal("relatório/todas partições incorretos")
					}
					output, err := os.Open(prepared.Path)
					if err != nil {
						t.Fatal(err)
					}
					defer output.Close()
					for i, p := range m.Layout.Partitions {
						if p.Number == 2 {
							continue
						}
						q := prepared.Layout.Partitions[i]
						if err = compareRange(ctx, source, p.StartLba*512, output, q.StartLba*512, p.LengthLba*512); err != nil {
							t.Fatalf("boot/recovery part %d: %v", p.Number, err)
						}
					}
					partFile, err := extractPartition(ctx, output, prepared.Layout.Partitions[1], 512, filepath.Join(work, "data-check.raw"))
					if err != nil {
						t.Fatal(err)
					}
					partFile.Close()
					var restored []byte
					if fs == "ntfs" {
						// ntfsresize intentionally schedules CHKDSK on the private prepared
						// copy. Read that synthetic copy with ntfscat's READONLY+RECOVER
						// mode; never clear its dirty flag or relax the production source
						// ntfs-3g.probe gate. Verify the inspection did not modify it.
						checkPath := filepath.Join(work, "data-check.raw")
						checkFile, openErr := os.Open(checkPath)
						if openErr != nil {
							t.Fatal(openErr)
						}
						checkSize := prepared.Layout.Partitions[1].LengthLba * 512
						beforeRead, hashErr := hashRange(ctx, checkFile, 0, checkSize)
						if hashErr != nil {
							checkFile.Close()
							t.Fatal(hashErr)
						}
						command := exec.CommandContext(ctx, "ntfscat", "--force", checkPath, "/sentinel.bin")
						var stderr bytes.Buffer
						command.Stderr = &stderr
						restored, err = command.Output()
						if err != nil {
							checkFile.Close()
							t.Fatalf("target=%d: ntfscat readonly prepared fixture: %v: %s", size, err, stderr.String())
						}
						afterRead, hashErr := hashRange(ctx, checkFile, 0, checkSize)
						checkFile.Close()
						if hashErr != nil || afterRead != beforeRead {
							t.Fatalf("NTFS read inspection modified fixture: %v", hashErr)
						}
					} else {
						dest := filepath.Join(work, "sentinel-restored.bin")
						_, err = tool(ctx, nil, "debugfs", "-R", "dump /sentinel.bin "+dest, filepath.Join(work, "data-check.raw"))
						if err == nil {
							restored, err = os.ReadFile(dest)
						}
					}
					if err != nil || !bytes.Equal(restored, sentinel) {
						t.Fatalf("dados de OS/usuário divergiram: %v", err)
					}
					if _, err = tool(ctx, nil, "sfdisk", "--verify", prepared.Path); err != nil {
						t.Fatal(err)
					}
					after, err := hashRange(ctx, source, 0, m.LogicalSizeBytes)
					if err != nil || after != before {
						t.Fatal("imagem original foi modificada")
					}
				}
			})
		}
	}
}

func TestAdaptiveLinuxLargerGPTAndSafetyFailures(t *testing.T) {
	linuxIntegration(t)
	ctx := context.Background()
	const mib = int64(1024 * 1024)
	sourcePath, m, _ := makeLinuxImage(t, "GPT", "ext4")
	prepared, err := prepareImage(ctx, sourcePath, t.TempDir(), m, 320*mib, CloneOptions{Strategy: "PRESERVE_LAYOUT"})
	if err != nil {
		t.Fatal(err)
	}
	if prepared.Layout.LastUsableLba != 320*mib/512-34 {
		t.Fatal("backup GPT não adaptou à capacidade real")
	}
	for i, p := range m.Layout.Partitions {
		if p.StartLba != prepared.Layout.Partitions[i].StartLba || p.LengthLba != prepared.Layout.Partitions[i].LengthLba {
			t.Fatal("PRESERVE_LAYOUT mudou partições")
		}
	}
	if _, err = prepareImage(ctx, sourcePath, t.TempDir(), m, 48*mib, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2}); err == nil {
		t.Fatal("destino insuficiente aceito")
	}
	var copied DiskLayout
	data, _ := json.Marshal(m.Layout)
	json.Unmarshal(data, &copied)
	copied.Partitions[1].LengthLba--
	wrong := m
	wrong.Layout = &copied
	if _, err = prepareImage(ctx, sourcePath, t.TempDir(), wrong, 192*mib, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2}); err == nil {
		t.Fatal("layout adulterado aceito")
	}
	// Corrupt the selected ext4 superblock in a private image; never touch a device.
	bad := filepath.Join(t.TempDir(), "bad.raw")
	in, err := os.Open(sourcePath)
	if err != nil {
		t.Fatal(err)
	}
	out, err := os.Create(bad)
	if err != nil {
		t.Fatal(err)
	}
	_, err = io.Copy(out, in)
	in.Close()
	if err != nil {
		t.Fatal(err)
	}
	_, err = out.WriteAt([]byte{0, 0}, 33*mib+1024+56)
	out.Close()
	if err != nil {
		t.Fatal(err)
	}
	if _, err = prepareImage(ctx, bad, t.TempDir(), m, 192*mib, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2}); err == nil {
		t.Fatal("filesystem corrompido aceito")
	}
}

func TestAdaptiveLinuxLoopMappingOnlySyntheticFiles(t *testing.T) {
	linuxIntegration(t)
	if os.Geteuid() != 0 {
		t.Fatal("Linux integration loop test requires root for synthetic loop devices")
	}
	if _, err := exec.LookPath("losetup"); err != nil {
		t.Fatal(err)
	}
	ctx := context.Background()
	path, m, _ := makeLinuxImage(t, "GPT", "ext4")
	output, err := tool(ctx, nil, "losetup", "--find", "--show", "--partscan", "--nooverlap", path)
	if err != nil {
		t.Fatal(err)
	}
	loop := strings.TrimSpace(string(output))
	if !strings.HasPrefix(loop, "/dev/loop") || strings.ContainsAny(loop, "\r\n ") {
		t.Fatal("loop retornado inválido")
	}
	t.Cleanup(func() {
		backing, err := os.ReadFile(filepath.Join("/sys/class/block", filepath.Base(loop), "loop/backing_file"))
		if err != nil {
			t.Error(err)
			return
		}
		if strings.TrimSpace(string(backing)) != path {
			t.Error("não desconectarei loop com backing_file diferente")
			return
		}
		if _, err = tool(context.Background(), nil, "losetup", "--detach", loop); err != nil {
			t.Error(err)
		}
	})
	actual, err := InspectLayout(ctx, loop, m.LogicalSizeBytes, 512)
	if err != nil {
		t.Fatal(err)
	}
	if !layoutsEqual(m.Layout, actual) {
		t.Fatal("layout loop diverge de imagem sintética")
	}
}

func TestAdaptiveLinuxNTFSHibernationBlockedBeforeDestination(t *testing.T) {
	linuxIntegration(t)
	ctx := context.Background()
	sourcePath, m, _ := makeLinuxImage(t, "GPT", "ntfs")
	work := t.TempDir()
	source, err := os.OpenFile(sourcePath, os.O_RDWR, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer source.Close()
	partPath := filepath.Join(work, "hibernated-partition.raw")
	part, err := extractPartition(ctx, source, m.Layout.Partitions[1], 512, partPath)
	if err != nil {
		t.Fatal(err)
	}
	part.Close()
	hiber := make([]byte, 4096)
	copy(hiber, "hibr")
	hiberPath := filepath.Join(work, "hiberfil.sys")
	if err = os.WriteFile(hiberPath, hiber, 0600); err != nil {
		t.Fatal(err)
	}
	if _, err = tool(ctx, nil, "ntfscp", partPath, hiberPath, "/hiberfil.sys"); err != nil {
		t.Fatal(err)
	}
	if _, err = tool(ctx, nil, "ntfs-3g.probe", "--readwrite", partPath); err == nil {
		t.Fatal("fixture hibernada não foi detectada pelo motor")
	}
	part, err = os.Open(partPath)
	if err != nil {
		t.Fatal(err)
	}
	err = copyRange(ctx, source, m.Layout.Partitions[1].StartLba*512, part, 0, m.Layout.Partitions[1].LengthLba*512)
	part.Close()
	if err != nil {
		t.Fatal(err)
	}
	if err = source.Sync(); err != nil {
		t.Fatal(err)
	}
	m.Chunks, err = chunksForFile(ctx, source, m.LogicalSizeBytes)
	if err != nil {
		t.Fatal(err)
	}
	_, err = prepareImage(ctx, sourcePath, t.TempDir(), m, 192*1024*1024, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2})
	if err == nil || !strings.Contains(err.Error(), "NTFS hibernado") {
		t.Fatalf("hibernação não bloqueada: %v", err)
	}
}

func TestAdaptiveLinuxPreparedVHDXRoundTrip(t *testing.T) {
	linuxIntegration(t)
	if _, err := exec.LookPath("qemu-img"); err != nil {
		t.Skip("qemu-utils não instalado: teste opcional de contêiner VHDX não executado")
	}
	ctx := context.Background()
	sourcePath, m, _ := makeLinuxImage(t, "GPT", "ext4")
	work := t.TempDir()
	prepared, err := prepareImage(ctx, sourcePath, work, m, 192*1024*1024, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2})
	if err != nil {
		t.Fatal(err)
	}
	vhdx := filepath.Join(work, "prepared.vhdx")
	roundTrip := filepath.Join(work, "roundtrip.raw")
	if _, err = tool(ctx, nil, "qemu-img", "convert", "-f", "raw", "-O", "vhdx", prepared.Path, vhdx); err != nil {
		t.Fatal(err)
	}
	if _, err = tool(ctx, nil, "qemu-img", "convert", "-f", "vhdx", "-O", "raw", vhdx, roundTrip); err != nil {
		t.Fatal(err)
	}
	a, err := os.Open(prepared.Path)
	if err != nil {
		t.Fatal(err)
	}
	defer a.Close()
	b, err := os.Open(roundTrip)
	if err != nil {
		t.Fatal(err)
	}
	defer b.Close()
	if err = compareRange(ctx, a, 0, b, 0, 192*1024*1024); err != nil {
		t.Fatal(err)
	}
}
