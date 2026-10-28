package agent

import (
	"bytes"
	"context"
	"crypto/rand"
	"os"
	"path/filepath"
	"testing"
)

type memoryDisk []byte

func (m memoryDisk) WriteAt(p []byte, offset int64) (int, error) { return copy(m[offset:], p), nil }

func feed(t *testing.T, w *streamWriter, source []byte) {
	t.Helper()
	for offset := int64(0); offset < int64(len(source)); offset += ChunkSize {
		end := min(offset+ChunkSize, int64(len(source)))
		if err := w.write(offset, source[offset:end]); err != nil {
			t.Fatal(err)
		}
	}
	if !w.complete() {
		t.Fatal("regiões incompletas após todos os lotes")
	}
}

func TestStreamPlanExactAndPreserve(t *testing.T) {
	const mib = int64(1024 * 1024)
	m, err := planStream(nil, 256*mib, 256*mib, 512, CloneOptions{Strategy: "EXACT_RAW"})
	if err != nil || len(m.Copies) != 1 || m.Copies[0] != (streamCopy{0, 0, 256 * mib}) || len(m.Zeros) != 0 {
		t.Fatalf("EXACT_RAW: %+v %v", m, err)
	}
	if _, err = planStream(nil, 256*mib, 320*mib, 512, CloneOptions{Strategy: "EXACT_RAW"}); err == nil {
		t.Fatal("EXACT_RAW com capacidade diferente aceito")
	}
	gpt := testLayout("GPT")
	m, err = planStream(gpt, 256*mib, 320*mib, 512, CloneOptions{Strategy: "PRESERVE_LAYOUT"})
	if err != nil {
		t.Fatal(err)
	}
	end := (gpt.LastUsableLba + 1) * 512
	if m.Copies[0] != (streamCopy{0, 0, end}) || len(m.Zeros) != 1 || m.Zeros[0] != (streamZero{end, 320*mib - end}) || m.TargetLayout.LastUsableLba != 320*mib/512-34 {
		t.Fatalf("PRESERVE_LAYOUT GPT: %+v", m)
	}
	if gpt.LastUsableLba != 256*mib/512-34 {
		t.Fatal("layout de origem foi alterado")
	}
	mbr := testLayout("MBR")
	m, err = planStream(mbr, 256*mib, 320*mib, 512, CloneOptions{Strategy: "PRESERVE_LAYOUT"})
	if err != nil || m.Copies[0] != (streamCopy{0, 0, 256 * mib}) || m.Zeros[0] != (streamZero{256 * mib, 64 * mib}) {
		t.Fatalf("PRESERVE_LAYOUT MBR: %+v %v", m, err)
	}
	for _, options := range []CloneOptions{{Strategy: "PRESERVE_LAYOUT"}, {Strategy: "FIT", ResizePartitionNumber: 2}} {
		if _, err = planStream(gpt, 256*mib, 192*mib, 512, options); err == nil {
			t.Fatalf("%s: redução em lotes aceita", options.Strategy)
		}
	}
	crypto := testLayout("GPT")
	crypto.Partitions[1].FileSystem = "crypto_LUKS"
	if _, err = planStream(crypto, 256*mib, 320*mib, 512, CloneOptions{Strategy: "PRESERVE_LAYOUT"}); err == nil {
		t.Fatal("LUKS aceito para adaptação em lotes")
	}
}

func TestStreamPlanFitGrowShiftsSuffixAndZerosNewSpace(t *testing.T) {
	const mib = int64(1024 * 1024)
	for _, table := range []string{"GPT", "MBR"} {
		source := testLayout(table)
		m, err := planStream(source, 256*mib, 320*mib, 512, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2})
		if err != nil {
			t.Fatal(table, err)
		}
		start, oldSize := 33*mib, 160*mib
		suffixEnd := (source.LastUsableLba + 1) * 512
		if m.Resize == nil || m.Resize.OldSize != oldSize || m.Resize.NewSize != 224*mib || m.Resize.Partition.Number != 2 {
			t.Fatalf("%s resize: %+v", table, m.Resize)
		}
		if m.Copies[0] != (streamCopy{0, 0, start + oldSize}) || m.Copies[1] != (streamCopy{start + oldSize, start + 224*mib, suffixEnd - start - oldSize}) {
			t.Fatalf("%s cópias: %+v", table, m.Copies)
		}
		if m.Zeros[0] != (streamZero{start + oldSize, 64 * mib}) {
			t.Fatalf("%s espaço novo não zerado: %+v", table, m.Zeros)
		}
		if m.TargetLayout.Partitions[2].StartLba != source.Partitions[2].StartLba+64*mib/512 || m.TargetLayout.Partitions[2].UUID != source.Partitions[2].UUID {
			t.Fatalf("%s WinRE não deslocada com identidade", table)
		}
		var covered int64
		for _, c := range m.Copies {
			covered += c.Length
		}
		for _, z := range m.Zeros {
			covered += z.Length
		}
		// Every target byte is either copied or zeroed; sfdisk then writes the new backup GPT.
		if covered != 320*mib {
			t.Fatalf("%s mapeamento não cobre o destino: %d", table, covered)
		}
	}
}

func TestStreamWriterMapsChunksAndRejectsGaps(t *testing.T) {
	const mib = int64(1024 * 1024)
	source := make([]byte, 256*mib)
	if _, err := rand.Read(source); err != nil {
		t.Fatal(err)
	}
	m, err := planStream(testLayout("GPT"), 256*mib, 320*mib, 512, CloneOptions{Strategy: "FIT", ResizePartitionNumber: 2})
	if err != nil {
		t.Fatal(err)
	}
	disk := make(memoryDisk, 320*mib)
	feed(t, newStreamWriter(disk, m), source)
	for _, c := range m.Copies {
		if !bytes.Equal(disk[c.Dest:c.Dest+c.Length], source[c.Source:c.Source+c.Length]) {
			t.Fatalf("região %+v divergente", c)
		}
	}
	w := newStreamWriter(make(memoryDisk, 320*mib), m)
	if err = w.write(ChunkSize, source[ChunkSize:2*ChunkSize]); err == nil {
		t.Fatal("lote fora de ordem aceito")
	}
}

func TestStreamVerifyDetectsChangedTargetBytes(t *testing.T) {
	const mib = int64(1024 * 1024)
	source := make([]byte, 12*mib+512)
	if _, err := rand.Read(source); err != nil {
		t.Fatal(err)
	}
	m, err := planStream(nil, int64(len(source)), int64(len(source)), 512, CloneOptions{Strategy: "EXACT_RAW"})
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "target.raw")
	f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	w := newStreamWriter(f, m)
	feed(t, w, source)
	none := func(string, int64, int64) error { return nil }
	if err = w.verify(context.Background(), f, none); err != nil {
		t.Fatal(err)
	}
	written, _ := os.ReadFile(path)
	if !bytes.Equal(written, source) {
		t.Fatal("destino diverge da origem")
	}
	if _, err = f.WriteAt([]byte{source[5*mib] ^ 0xff}, 5*mib); err != nil {
		t.Fatal(err)
	}
	if err = w.verify(context.Background(), f, none); err == nil {
		t.Fatal("byte alterado no destino não detectado")
	}
}
