package agent

import (
	"encoding/binary"
	"hash/crc32"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestGPTHeadersAndArraysMustAgree(t *testing.T) {
	const size = int64(16 * 1024 * 1024)
	const sector = 512
	f, err := os.OpenFile(filepath.Join(t.TempDir(), "gpt.raw"), os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err = f.Truncate(size); err != nil {
		t.Fatal(err)
	}
	array := make([]byte, 128*128)
	arrayCRC := crc32.ChecksumIEEE(array)
	last := size/sector - 1
	makeHeader := func(current, alternate, arrayLba int64) []byte {
		b := make([]byte, sector)
		copy(b, "EFI PART")
		binary.LittleEndian.PutUint32(b[8:12], 0x00010000)
		binary.LittleEndian.PutUint32(b[12:16], 92)
		binary.LittleEndian.PutUint64(b[24:32], uint64(current))
		binary.LittleEndian.PutUint64(b[32:40], uint64(alternate))
		binary.LittleEndian.PutUint64(b[40:48], 34)
		binary.LittleEndian.PutUint64(b[48:56], uint64(last-33))
		binary.LittleEndian.PutUint64(b[72:80], uint64(arrayLba))
		binary.LittleEndian.PutUint32(b[80:84], 128)
		binary.LittleEndian.PutUint32(b[84:88], 128)
		binary.LittleEndian.PutUint32(b[88:92], arrayCRC)
		binary.LittleEndian.PutUint32(b[16:20], crc32.ChecksumIEEE(b[:92]))
		return b
	}
	primary := makeHeader(1, last, 2)
	backup := makeHeader(last, 1, last-32)
	for _, entry := range []struct {
		offset int64
		data   []byte
	}{{512, primary}, {1024, array}, {(last - 32) * 512, array}, {last * 512, backup}} {
		if _, err = f.WriteAt(entry.data, entry.offset); err != nil {
			t.Fatal(err)
		}
	}
	start, length, err := verifyGPT(f, size, 512)
	if err != nil || start != (last-32)*512 || length != 33*512 {
		t.Fatalf("GPT válida rejeitada: %v", err)
	}
	if _, err = f.WriteAt([]byte{1}, (last-32)*512); err != nil {
		t.Fatal(err)
	}
	if _, _, err = verifyGPT(f, size, 512); err == nil {
		t.Fatal("array secundária corrompida aceita")
	}
	if _, err = f.WriteAt(array, (last-32)*512); err != nil {
		t.Fatal(err)
	}
	backup[16] ^= 1
	if _, err = f.WriteAt(backup, last*512); err != nil {
		t.Fatal(err)
	}
	if _, _, err = verifyGPT(f, size, 512); err == nil {
		t.Fatal("header secundário corrompido aceito")
	}
}

func testLayout(table string) *DiskLayout {
	const mib = int64(1024 * 1024)
	l := &DiskLayout{TableType: table, SectorSize: 512, DiskIdentifier: "12345678-1234-5678-9012-123456789012", FirstUsableLba: 34, LastUsableLba: 256*mib/512 - 34, Partitions: []LayoutPartition{
		{Number: 1, StartLba: mib / 512, LengthLba: 32 * mib / 512, Type: "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", UUID: "11111111-1111-1111-1111-111111111111", Name: "EFI", FileSystem: "vfat"},
		{Number: 2, StartLba: 33 * mib / 512, LengthLba: 160 * mib / 512, Type: "0fc63daf-8483-4772-8e79-3d69d8477de4", UUID: "22222222-2222-2222-2222-222222222222", Name: "System", FileSystem: "ext4"},
		{Number: 3, StartLba: 193 * mib / 512, LengthLba: 8 * mib / 512, Type: "de94bba4-06d1-4d40-a16a-bfd50179d6ac", UUID: "33333333-3333-3333-3333-333333333333", Name: "WinRE", Attributes: "RequiredPartition GUID:63"},
	}}
	if table == "MBR" {
		l.DiskIdentifier = "0x1234abcd"
		l.FirstUsableLba = 1
		l.LastUsableLba = 256*mib/512 - 1
		l.Partitions[0].Type = "c"
		l.Partitions[0].Bootable = true
		l.Partitions[1].Type = "83"
		l.Partitions[2].Type = "27"
		for i := range l.Partitions {
			l.Partitions[i].UUID = ""
			l.Partitions[i].Name = ""
			l.Partitions[i].Attributes = ""
		}
	}
	return l
}

func TestFitLayoutPreservesRecoveryAndIdentity(t *testing.T) {
	const mib = int64(1024 * 1024)
	for _, table := range []string{"GPT", "MBR"} {
		for _, target := range []int64{192 * mib, 320 * mib} {
			source := testLayout(table)
			actual, err := FitLayout(source, 256*mib, target, 2)
			if err != nil {
				t.Fatal(err)
			}
			if len(actual.Partitions) != 3 || actual.DiskIdentifier != source.DiskIdentifier || actual.Partitions[0] != source.Partitions[0] {
				t.Fatal("boot/identidade alterada")
			}
			delta := (target - 256*mib) / 512
			if actual.Partitions[1].StartLba != source.Partitions[1].StartLba || actual.Partitions[1].LengthLba != source.Partitions[1].LengthLba+delta {
				t.Fatal("partição selecionada incorreta")
			}
			q, p := actual.Partitions[2], source.Partitions[2]
			if q.StartLba != p.StartLba+delta || q.LengthLba != p.LengthLba || q.UUID != p.UUID || q.Type != p.Type || q.Attributes != p.Attributes {
				t.Fatal("partição WinRE perdida ou alterada")
			}
			if source.Partitions[2].StartLba != 193*mib/512 {
				t.Fatal("layout de origem modificado")
			}
		}
	}
}

func TestAdaptiveLayoutRejectsUnsafePlans(t *testing.T) {
	const mib = int64(1024 * 1024)
	source := testLayout("GPT")
	if _, err := FitLayout(source, 256*mib, 32*mib, 2); err == nil {
		t.Fatal("tamanho insuficiente aceito")
	}
	if _, err := FitLayout(source, 256*mib, 192*mib, 1); err == nil {
		t.Fatal("resize FAT aceito")
	}
	source.Partitions[2].Bootable = true
	if _, err := FitLayout(source, 256*mib, 192*mib, 2); err == nil {
		t.Fatal("boot posterior movido")
	}
	source = testLayout("MBR")
	source.Partitions[2].Type = "f"
	if err := ValidateLayout(source, 256*mib); err == nil {
		t.Fatal("EBR aceito")
	}
	source = testLayout("GPT")
	source.Partitions[2].Name = "bad\nlabel: dos"
	if err := ValidateLayout(source, 256*mib); err == nil {
		t.Fatal("metadados com linha aceita")
	}
	source = testLayout("GPT")
	source.Partitions[2].StartLba = source.Partitions[1].StartLba
	if err := ValidateLayout(source, 256*mib); err == nil {
		t.Fatal("sobreposição aceita")
	}
	if !protectedFilesystem("BitLocker") || !protectedFilesystem("LVM2_member") || !protectedPartitionType("MBR", "fd") {
		t.Fatal("crypto/RAID não detectados")
	}
}

func TestManifestCloneStrategies(t *testing.T) {
	data := make([]byte, 512)
	hash := strings.Repeat("a", 64)
	m := Manifest{SchemaVersion: 1, ID: "image", State: "AVAILABLE", SourceDeviceID: "source", SourceFingerprint: "source-disk", LogicalSizeBytes: int64(len(data)), LogicalSectorBytes: 512, Chunks: []ImageChunk{{Sequence: 0, SizeBytes: 512, SHA256: hash}}}
	p := Plan{DeviceID: "target", DiskFingerprint: "target-disk", DiskSizeBytes: 1024, LogicalSectorBytes: 512, ImageID: "image", Clone: &CloneOptions{Strategy: "EXACT_RAW"}}
	if err := ValidateManifest(m, p); err == nil {
		t.Fatal("EXACT_RAW maior aceito")
	}
	p.DiskSizeBytes = 512
	if err := ValidateManifest(m, p); err != nil {
		t.Fatal(err)
	}
	p.Clone = &CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2}
	p.DiskSizeBytes = 256
	if err := ValidateManifest(m, p); err == nil {
		t.Fatal("FIT sem layout aceito")
	}
	if _, err := scratchRequired(Manifest{LogicalSizeBytes: 1 << 62}, 1<<62); err == nil {
		t.Fatal("overflow scratch aceito")
	}
}
