package agent

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"hash/crc32"
	"io"
	"math"
	"os"
	"os/exec"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

type sfdiskTable struct {
	PartitionTable struct {
		Label      string `json:"label"`
		ID         string `json:"id"`
		SectorSize int    `json:"sectorsize"`
		FirstLba   int64  `json:"firstlba"`
		LastLba    int64  `json:"lastlba"`
		Partitions []struct {
			Node     string `json:"node"`
			Start    int64  `json:"start"`
			Size     int64  `json:"size"`
			Type     string `json:"type"`
			UUID     string `json:"uuid"`
			Name     string `json:"name"`
			Attrs    string `json:"attrs"`
			Bootable bool   `json:"bootable"`
		} `json:"partitions"`
	} `json:"partitiontable"`
}

func tool(ctx context.Context, input io.Reader, name string, args ...string) ([]byte, error) {
	bounded, cancel := context.WithTimeout(ctx, 30*time.Minute)
	defer cancel()
	command := exec.CommandContext(bounded, name, args...)
	command.Stdin = input
	command.Env = append(os.Environ(), "LC_ALL=C", "LANG=C")
	var output limitedBuffer
	command.Stdout = &output
	command.Stderr = &output
	if err := command.Run(); err != nil {
		return output.data, fmt.Errorf("%s: %w: %s", name, err, strings.TrimSpace(string(output.data)))
	}
	return output.data, nil
}

type limitedBuffer struct{ data []byte }

func (b *limitedBuffer) Write(p []byte) (int, error) {
	n := len(p)
	remaining := 1024*1024 - len(b.data)
	if remaining > 0 {
		if len(p) > remaining {
			p = p[:remaining]
		}
		b.data = append(b.data, p...)
	}
	return n, nil
}

func partitionNumber(node string, fallback int) int {
	last := len(node)
	first := last
	for first > 0 && node[first-1] >= '0' && node[first-1] <= '9' {
		first--
	}
	if first == last {
		return fallback
	}
	n, err := strconv.Atoi(node[first:])
	if err != nil || n < 1 {
		return fallback
	}
	return n
}

func parseLayout(data []byte, totalBytes int64, sector int) (*DiskLayout, error) {
	var value sfdiskTable
	if err := json.Unmarshal(data, &value); err != nil {
		return nil, err
	}
	t := value.PartitionTable
	label := ""
	switch t.Label {
	case "gpt":
		label = "GPT"
	case "dos":
		label = "MBR"
	default:
		return nil, errors.New("tabela não suportada para clonagem adaptada")
	}
	if t.SectorSize != 0 && t.SectorSize != sector {
		return nil, errors.New("sfdisk retornou tamanho de setor diferente")
	}
	l := &DiskLayout{TableType: label, SectorSize: sector, DiskIdentifier: t.ID, FirstUsableLba: t.FirstLba, LastUsableLba: t.LastLba, Partitions: []LayoutPartition{}}
	if label == "MBR" {
		l.FirstUsableLba = 1
		l.LastUsableLba = totalBytes/int64(sector) - 1
	}
	for i, p := range t.Partitions {
		l.Partitions = append(l.Partitions, LayoutPartition{Number: partitionNumber(p.Node, i+1), StartLba: p.Start, LengthLba: p.Size, Type: p.Type, UUID: p.UUID, Name: p.Name, Attributes: p.Attrs, Bootable: p.Bootable})
	}
	if err := ValidateLayout(l, totalBytes); err != nil {
		return nil, err
	}
	return l, nil
}

var guidPattern = regexp.MustCompile(`(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
var mbrIDPattern = regexp.MustCompile(`(?i)^0x[0-9a-f]{8}$`)
var mbrTypePattern = regexp.MustCompile(`(?i)^[0-9a-f]{1,2}$`)

func ValidateLayout(l *DiskLayout, totalBytes int64) error {
	if l == nil || l.SectorSize != 512 && l.SectorSize != 4096 || totalBytes <= 0 || totalBytes%int64(l.SectorSize) != 0 {
		return errors.New("geometria de layout inválida")
	}
	total := totalBytes / int64(l.SectorSize)
	if l.TableType != "GPT" && l.TableType != "MBR" {
		return errors.New("tipo de tabela inválido")
	}
	if l.FirstUsableLba < 1 || l.LastUsableLba < l.FirstUsableLba || l.LastUsableLba >= total {
		return errors.New("limites úteis inválidos")
	}
	if l.TableType == "GPT" && !guidPattern.MatchString(l.DiskIdentifier) || l.TableType == "MBR" && !mbrIDPattern.MatchString(l.DiskIdentifier) {
		return errors.New("identificador de disco inválido")
	}
	if len(l.Partitions) == 0 || len(l.Partitions) > 128 {
		return errors.New("número de partições inválido")
	}
	seen := map[int]bool{}
	sorted := append([]LayoutPartition(nil), l.Partitions...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].StartLba < sorted[j].StartLba })
	var previousEnd int64 = l.FirstUsableLba
	for _, p := range sorted {
		if p.Number < 1 || p.Number > 128 || seen[p.Number] || p.StartLba < previousEnd || p.LengthLba <= 0 || p.StartLba > l.LastUsableLba || p.LengthLba > l.LastUsableLba-p.StartLba+1 {
			return errors.New("partições sobrepostas, fora do disco ou inválidas")
		}
		seen[p.Number] = true
		previousEnd = p.StartLba + p.LengthLba
		if l.TableType == "MBR" {
			if p.StartLba > math.MaxUint32 || p.LengthLba > math.MaxUint32 || p.StartLba+p.LengthLba > int64(math.MaxUint32)+1 {
				return errors.New("partição MBR excede endereçamento de 32 bits")
			}
			if p.Number > 4 || !mbrTypePattern.MatchString(p.Type) {
				return errors.New("MBR com partições lógicas/estendidas não suportado")
			}
			kind, _ := strconv.ParseUint(p.Type, 16, 8)
			if kind == 0x05 || kind == 0x0f || kind == 0x85 || kind == 0x42 || kind == 0xee {
				return errors.New("MBR estendido, dinâmico ou híbrido não suportado")
			}
		} else if !guidPattern.MatchString(p.Type) || !guidPattern.MatchString(p.UUID) {
			return errors.New("tipo ou GUID de partição inválido")
		}
		for _, s := range []string{p.Name, p.Attributes} {
			if strings.ContainsAny(s, "\r\n\x00") || len(s) > 1024 {
				return errors.New("metadados de partição inválidos")
			}
		}
	}
	return nil
}

func InspectLayout(ctx context.Context, path string, totalBytes int64, sector int) (*DiskLayout, error) {
	data, err := tool(ctx, nil, "sfdisk", "--json", "--sector-size", strconv.Itoa(sector), path)
	if err != nil {
		return nil, err
	}
	l, err := parseLayout(data, totalBytes, sector)
	if err != nil {
		return nil, err
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	first := make([]byte, 512)
	if _, err = io.ReadFull(f, first); err != nil {
		return nil, err
	}
	if first[510] != 0x55 || first[511] != 0xaa {
		return nil, errors.New("assinatura MBR ausente")
	}
	if l.TableType == "GPT" {
		protective := 0
		other := 0
		for i := 0; i < 4; i++ {
			kind := first[446+i*16+4]
			if kind == 0xee {
				protective++
			} else if kind != 0 {
				other++
			}
		}
		if protective != 1 || other != 0 {
			return nil, errors.New("GPT híbrida ou MBR protetora inválida")
		}
		if _, _, err = verifyGPT(f, totalBytes, sector); err != nil {
			return nil, err
		}
	}
	for i := range l.Partitions {
		p := &l.Partitions[i]
		out, e := tool(ctx, nil, "blkid", "--probe", "--output", "export", "--offset", strconv.FormatInt(p.StartLba*int64(sector), 10), "--size", strconv.FormatInt(p.LengthLba*int64(sector), 10), path)
		if e == nil {
			for _, line := range strings.Split(string(out), "\n") {
				if strings.HasPrefix(line, "TYPE=") {
					p.FileSystem = strings.TrimPrefix(line, "TYPE=")
				}
			}
		}
	}
	return l, nil
}

func layoutsEqual(a, b *DiskLayout) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	if a.TableType != b.TableType || a.SectorSize != b.SectorSize || !strings.EqualFold(a.DiskIdentifier, b.DiskIdentifier) || a.FirstUsableLba != b.FirstUsableLba || a.LastUsableLba != b.LastUsableLba || len(a.Partitions) != len(b.Partitions) {
		return false
	}
	byNumber := map[int]LayoutPartition{}
	for _, p := range a.Partitions {
		byNumber[p.Number] = p
	}
	for _, p := range b.Partitions {
		q, ok := byNumber[p.Number]
		if !ok || q.StartLba != p.StartLba || q.LengthLba != p.LengthLba || !strings.EqualFold(q.Type, p.Type) || !strings.EqualFold(q.UUID, p.UUID) || q.Name != p.Name || q.Attributes != p.Attributes || q.Bootable != p.Bootable || q.FileSystem != p.FileSystem {
			return false
		}
	}
	return true
}

func cloneStrategy(p Plan) string {
	if p.Clone == nil {
		return "LEGACY_RAW"
	}
	return p.Clone.Strategy
}

func FitLayout(source *DiskLayout, sourceBytes, targetBytes int64, resizeNumber int) (*DiskLayout, error) {
	if err := ValidateLayout(source, sourceBytes); err != nil {
		return nil, err
	}
	if targetBytes <= 0 || targetBytes%int64(source.SectorSize) != 0 {
		return nil, errors.New("tamanho alvo inválido")
	}
	copyLayout := *source
	copyLayout.Partitions = append([]LayoutPartition(nil), source.Partitions...)
	difference := targetBytes - sourceBytes
	alignment := int64(1024 * 1024)
	delta := difference / alignment * alignment
	if difference < 0 && difference%alignment != 0 {
		delta -= alignment
	}
	deltaLbas := delta / int64(source.SectorSize)
	selected := -1
	for i, p := range copyLayout.Partitions {
		if p.Number == resizeNumber {
			selected = i
		}
	}
	if selected < 0 {
		return nil, errors.New("partição selecionada ausente")
	}
	part := &copyLayout.Partitions[selected]
	if part.FileSystem != "ntfs" && part.FileSystem != "ext4" {
		return nil, errors.New("FIT suporta redimensionamento somente NTFS/ext4")
	}
	if part.LengthLba+deltaLbas <= 0 {
		return nil, errors.New("destino insuficiente para todas as partições")
	}
	start := part.StartLba
	part.LengthLba += deltaLbas
	part.MinimumSizeBytes = nil
	for i := range copyLayout.Partitions {
		p := &copyLayout.Partitions[i]
		if p.StartLba > start {
			mbrType, _ := strconv.ParseUint(p.Type, 16, 8)
			if p.Bootable || copyLayout.TableType == "MBR" && mbrType == 0xef || strings.EqualFold(p.Type, "c12a7328-f81f-11d2-ba4b-00a0c93ec93b") || strings.EqualFold(p.Type, "21686148-6449-6e6f-744e-656564454649") {
				return nil, errors.New("FIT não move partição de boot/ESP/BIOS posterior ao volume redimensionado")
			}
			p.StartLba += deltaLbas
		}
	}
	tail := sourceBytes/int64(source.SectorSize) - 1 - source.LastUsableLba
	copyLayout.LastUsableLba = targetBytes/int64(source.SectorSize) - 1 - tail
	if err := ValidateLayout(&copyLayout, targetBytes); err != nil {
		return nil, err
	}
	return &copyLayout, nil
}

func renderLayout(l *DiskLayout) string {
	label := "gpt"
	if l.TableType == "MBR" {
		label = "dos"
	}
	var b strings.Builder
	fmt.Fprintf(&b, "label: %s\nlabel-id: %s\nunit: sectors\nsector-size: %d\n", label, l.DiskIdentifier, l.SectorSize)
	if l.TableType == "GPT" {
		fmt.Fprintf(&b, "first-lba: %d\nlast-lba: %d\n", l.FirstUsableLba, l.LastUsableLba)
	}
	parts := append([]LayoutPartition(nil), l.Partitions...)
	sort.Slice(parts, func(i, j int) bool { return parts[i].Number < parts[j].Number })
	for _, p := range parts {
		typeValue := p.Type
		if l.TableType == "MBR" {
			kind, _ := strconv.ParseUint(typeValue, 16, 8)
			typeValue = fmt.Sprintf("%02x", kind)
		}
		fmt.Fprintf(&b, "sltpartition%d : start=%d, size=%d, type=%s", p.Number, p.StartLba, p.LengthLba, typeValue)
		if l.TableType == "GPT" {
			fmt.Fprintf(&b, ", uuid=%s", p.UUID)
			if p.Name != "" {
				fmt.Fprintf(&b, ", name=%s", strconv.Quote(p.Name))
			}
			if p.Attributes != "" {
				fmt.Fprintf(&b, ", attrs=%s", strconv.Quote(p.Attributes))
			}
		}
		if p.Bootable {
			b.WriteString(", bootable")
		}
		b.WriteByte('\n')
	}
	return b.String()
}

func rewriteLayout(ctx context.Context, path string, l *DiskLayout, totalBytes int64) error {
	if err := ValidateLayout(l, totalBytes); err != nil {
		return err
	}
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	boot := make([]byte, 446)
	_, err = io.ReadFull(f, boot)
	f.Close()
	if err != nil {
		return err
	}
	if _, err = tool(ctx, strings.NewReader(renderLayout(l)), "sfdisk", "--sector-size", strconv.Itoa(l.SectorSize), "--no-reread", "--no-tell-kernel", "--wipe", "never", "--wipe-partitions", "never", path); err != nil {
		return err
	}
	f, err = os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		return err
	}
	defer f.Close()
	// Preserve machine boot code; the new partition entries and GPT CRCs remain engine-generated.
	if _, err = f.WriteAt(boot[:440], 0); err != nil {
		return err
	}
	if l.TableType == "MBR" {
		if _, err = f.WriteAt(boot[444:446], 444); err != nil {
			return err
		}
	}
	if err = f.Sync(); err != nil {
		return err
	}
	if _, err = tool(ctx, nil, "sfdisk", "--verify", "--sector-size", strconv.Itoa(l.SectorSize), path); err != nil {
		return err
	}
	actual, err := InspectLayout(ctx, path, totalBytes, l.SectorSize)
	if err != nil {
		return err
	}
	if !layoutsEqual(l, actual) {
		return errors.New("layout produzido difere do plano; destino não será aberto")
	}
	return nil
}

func protectedFilesystem(fs string) bool {
	v := strings.ToLower(fs)
	return strings.Contains(v, "crypto") || strings.Contains(v, "bitlocker") || strings.Contains(v, "lvm") || strings.Contains(v, "raid") || strings.Contains(v, "luks")
}

func protectedPartitionType(table, kind string) bool {
	if table == "MBR" {
		v, _ := strconv.ParseUint(kind, 16, 8)
		return v == 0x42 || v == 0x8e || v == 0xfd
	}
	switch strings.ToLower(kind) {
	case "e6d6d379-f507-44c2-a23c-238f2a3df928", "a19d880f-05fc-4d3b-a006-743f0f84911e", "ca7d7ccb-63ed-4c53-861c-1742536059cc", "af9b60a0-1431-4f62-bc68-3311714a69ad", "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3", "e75caf8f-f680-4cee-afa3-b001e56efc2d":
		return true
	}
	return false
}

func safeByteRange(start, length int64, sector int) (int64, int64, error) {
	if start < 0 || length < 0 || sector <= 0 || start > math.MaxInt64/int64(sector) || length > math.MaxInt64/int64(sector) {
		return 0, 0, errors.New("overflow de intervalo de disco")
	}
	return start * int64(sector), length * int64(sector), nil
}

// NTFS keeps the dirty bit in $Volume; this only reads the source's boot sector here.
func ntfsGeometry(path string) (int64, error) {
	f, e := os.Open(path)
	if e != nil {
		return 0, e
	}
	defer f.Close()
	b := make([]byte, 512)
	if _, e = io.ReadFull(f, b); e != nil {
		return 0, e
	}
	if string(b[3:11]) != "NTFS    " {
		return 0, errors.New("assinatura NTFS ausente")
	}
	sector := int64(binary.LittleEndian.Uint16(b[11:13]))
	count := binary.LittleEndian.Uint64(b[40:48])
	if sector < 512 || count >= uint64(math.MaxInt64/sector) {
		return 0, errors.New("geometria NTFS inválida")
	}
	return (int64(count) + 1) * sector, nil
}

func verifyGPT(f *os.File, totalBytes int64, sector int) (int64, int64, error) {
	last := totalBytes/int64(sector) - 1
	type header struct {
		current, alternate, first, last, array int64
		count, size                            uint32
		crc                                    uint32
		raw                                    []byte
	}
	read := func(lba int64) (header, error) {
		h := header{}
		if lba < 1 || lba > last {
			return h, errors.New("GPT aponta fora do disco")
		}
		data := make([]byte, sector)
		if _, err := f.ReadAt(data, lba*int64(sector)); err != nil {
			return h, err
		}
		if string(data[:8]) != "EFI PART" {
			return h, errors.New("cabeçalho GPT ausente")
		}
		length := binary.LittleEndian.Uint32(data[12:16])
		if length < 92 || length > uint32(sector) {
			return h, errors.New("tamanho cabeçalho GPT inválido")
		}
		expected := binary.LittleEndian.Uint32(data[16:20])
		copyData := append([]byte(nil), data[:length]...)
		for i := 16; i < 20; i++ {
			copyData[i] = 0
		}
		if crc32.ChecksumIEEE(copyData) != expected {
			return h, errors.New("CRC do cabeçalho GPT inválido")
		}
		toInt := func(offset int) (int64, error) {
			v := binary.LittleEndian.Uint64(data[offset : offset+8])
			if v > uint64(last) {
				return 0, errors.New("LBA GPT excede disco")
			}
			return int64(v), nil
		}
		var err error
		h.current, err = toInt(24)
		if err != nil {
			return h, err
		}
		h.alternate, err = toInt(32)
		if err != nil {
			return h, err
		}
		h.first, err = toInt(40)
		if err != nil {
			return h, err
		}
		h.last, err = toInt(48)
		if err != nil {
			return h, err
		}
		h.array, err = toInt(72)
		if err != nil {
			return h, err
		}
		h.count = binary.LittleEndian.Uint32(data[80:84])
		h.size = binary.LittleEndian.Uint32(data[84:88])
		h.crc = binary.LittleEndian.Uint32(data[88:92])
		h.raw = data
		bytes := uint64(h.count) * uint64(h.size)
		if h.count == 0 || h.size < 128 || bytes > 16*1024*1024 || h.array < 2 || bytes > uint64(totalBytes-h.array*int64(sector)) {
			return h, errors.New("array GPT inválido ou excessivo")
		}
		array := make([]byte, int(bytes))
		if _, err = f.ReadAt(array, h.array*int64(sector)); err != nil {
			return h, err
		}
		if crc32.ChecksumIEEE(array) != h.crc {
			return h, errors.New("CRC do array GPT inválido")
		}
		return h, nil
	}
	a, err := read(1)
	if err != nil {
		return 0, 0, err
	}
	b, err := read(last)
	if err != nil {
		return 0, 0, err
	}
	if a.current != 1 || a.alternate != last || b.current != last || b.alternate != 1 || a.first != b.first || a.last != b.last || a.count != b.count || a.size != b.size || a.crc != b.crc || string(a.raw[56:72]) != string(b.raw[56:72]) {
		return 0, 0, errors.New("GPT primária/secundária não conferem")
	}
	arrayBytes := (int64(b.count)*int64(b.size) + int64(sector) - 1) / int64(sector) * int64(sector)
	if b.array*int64(sector)+arrayBytes != last*int64(sector) || a.last >= b.array || a.first <= a.array {
		return 0, 0, errors.New("layout GPT não convencional")
	}
	return b.array * int64(sector), arrayBytes + int64(sector), nil
}
