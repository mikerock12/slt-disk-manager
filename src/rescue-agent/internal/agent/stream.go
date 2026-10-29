package agent

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"hash"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"
)

// Batch clone: the source sends 4 MiB chunks through a small server window while the
// target writes them straight to its disk. No full image exists on the server nor on any
// scratch storage. Adaptation happens on the target itself after every written byte was
// re-read and compared; the source is never modified. Shrinking uses a device-mapper
// overlay: the partition's first newSize bytes are the target disk, the tail is a sparse
// RAM file that only holds non-zero data until ntfsresize/resize2fs moves it inside.

const (
	streamDisk    = 0
	streamOverlay = 1
	// Non-zero source bytes that may sit beyond the new partition end during a shrink.
	streamOverflowLimit = int64(1024 * 1024 * 1024)
)

type streamCopy struct {
	Source int64
	Dest   int64
	Length int64
	Device int
}

type streamZero struct {
	Offset int64
	Length int64
}

type streamResize struct {
	Partition  LayoutPartition
	FileSystem string
	OldSize    int64
	NewSize    int64
}

type streamMap struct {
	Strategy     string
	SourceBytes  int64
	TargetBytes  int64
	Copies       []streamCopy
	Zeros        []streamZero
	SourceLayout *DiskLayout
	TargetLayout *DiskLayout
	Resize       *streamResize
}

func (m streamMap) shrink() bool { return m.Resize != nil && m.Resize.NewSize < m.Resize.OldSize }

// planStream maps source byte ranges onto the target exactly like prepareImage builds its
// private copy, so the target ends with the same bytes the image mode would write.
func planStream(source *DiskLayout, sourceBytes, targetBytes int64, sector int, options CloneOptions) (streamMap, error) {
	m := streamMap{Strategy: options.Strategy, SourceBytes: sourceBytes, TargetBytes: targetBytes, SourceLayout: source}
	if sourceBytes <= 0 || targetBytes <= 0 || (sector != 512 && sector != 4096) || sourceBytes%int64(sector) != 0 || targetBytes%int64(sector) != 0 {
		return m, errors.New("geometria da transmissão inválida")
	}
	switch options.Strategy {
	case "EXACT_RAW":
		if sourceBytes != targetBytes {
			return m, errors.New("EXACT_RAW exige mesma capacidade")
		}
		m.Copies = []streamCopy{{0, 0, sourceBytes, streamDisk}}
		m.TargetLayout = source
		return m, nil
	case "PRESERVE_LAYOUT", "FIT":
	default:
		return m, errors.New("estratégia desconhecida")
	}
	if options.Strategy == "PRESERVE_LAYOUT" && targetBytes < sourceBytes {
		return m, errors.New("PRESERVE_LAYOUT não permite truncar um disco maior")
	}
	if err := ValidateLayout(source, sourceBytes); err != nil {
		return m, err
	}
	if source.SectorSize != sector {
		return m, errors.New("layout e disco possuem setores diferentes")
	}
	for _, p := range source.Partitions {
		if protectedFilesystem(p.FileSystem) || protectedPartitionType(source.TableType, p.Type) {
			return m, errors.New("adaptação de criptografia/RAID/LVM bloqueada")
		}
	}
	target := *source
	target.Partitions = append([]LayoutPartition(nil), source.Partitions...)
	if options.Strategy == "PRESERVE_LAYOUT" {
		target.LastUsableLba += (targetBytes - sourceBytes) / int64(sector)
		end := sourceBytes
		// The old backup GPT would survive as a stale signature; the new one is written at the end.
		if source.TableType == "GPT" && targetBytes > sourceBytes {
			end = (source.LastUsableLba + 1) * int64(sector)
		}
		m.Copies = []streamCopy{{0, 0, end, streamDisk}}
		if end < targetBytes {
			m.Zeros = []streamZero{{end, targetBytes - end}}
		}
	} else {
		fitted, err := FitLayout(source, sourceBytes, targetBytes, options.ResizePartitionNumber)
		if err != nil {
			return m, err
		}
		target = *fitted
		selected := -1
		for i, p := range source.Partitions {
			if p.Number == options.ResizePartitionNumber {
				selected = i
			}
		}
		if selected < 0 {
			return m, errors.New("partição selecionada inexistente")
		}
		original, modified := source.Partitions[selected], target.Partitions[selected]
		start, oldSize, err := safeByteRange(original.StartLba, original.LengthLba, sector)
		if err != nil {
			return m, err
		}
		_, newSize, err := safeByteRange(modified.StartLba, modified.LengthLba, sector)
		if err != nil {
			return m, err
		}
		suffixStart, suffixEnd := start+oldSize, (source.LastUsableLba+1)*int64(sector)
		if suffixEnd < suffixStart {
			return m, errors.New("sufixo inválido")
		}
		m.Resize = &streamResize{Partition: modified, FileSystem: original.FileSystem, OldSize: oldSize, NewSize: newSize}
		if newSize < oldSize {
			// The whole partition goes through the overlay; its first newSize bytes are the target.
			m.Copies = []streamCopy{{0, 0, start, streamDisk}, {start, 0, oldSize, streamOverlay}}
		} else {
			m.Copies = []streamCopy{{0, 0, suffixStart, streamDisk}}
			if newSize > oldSize {
				m.Zeros = append(m.Zeros, streamZero{start + oldSize, newSize - oldSize})
			}
		}
		if suffixEnd > suffixStart {
			m.Copies = append(m.Copies, streamCopy{suffixStart, start + newSize, suffixEnd - suffixStart, streamDisk})
		}
		if tail := start + newSize + suffixEnd - suffixStart; tail < targetBytes {
			m.Zeros = append(m.Zeros, streamZero{tail, targetBytes - tail})
		}
	}
	if err := ValidateLayout(&target, targetBytes); err != nil {
		return m, err
	}
	m.TargetLayout = &target
	return m, nil
}

func allZero(data []byte) bool {
	for _, b := range data {
		if b != 0 {
			return false
		}
	}
	return true
}

var zeroHashes = map[int]string{}

func zeroHash(size int) string {
	if h, ok := zeroHashes[size]; ok {
		return h
	}
	sum := sha256.Sum256(make([]byte, size))
	zeroHashes[size] = hex.EncodeToString(sum[:])
	return zeroHashes[size]
}

type streamWriter struct {
	targets       []io.WriterAt
	m             streamMap
	sums          []hash.Hash
	next          []int64
	written       int64
	overflowStart int64
	overflowUsed  int64
	overflowLimit int64
}

func newStreamWriter(m streamMap, targets ...io.WriterAt) *streamWriter {
	w := &streamWriter{targets: targets, m: m, overflowStart: -1, overflowLimit: streamOverflowLimit}
	if m.shrink() {
		w.overflowStart = m.Resize.NewSize
	}
	for _, c := range m.Copies {
		w.sums = append(w.sums, sha256.New())
		w.next = append(w.next, c.Source)
	}
	return w
}

func (w *streamWriter) put(device int, piece []byte, offset int64) error {
	if device == streamOverlay && w.overflowStart >= 0 && offset+int64(len(piece)) > w.overflowStart {
		split := max(int64(0), w.overflowStart-offset)
		if split > 0 {
			if err := w.put(device, piece[:split], offset); err != nil {
				return err
			}
		}
		// The overflow is a fresh sparse file: zero blocks need no write and no memory.
		const block = 64 * 1024
		tail, at := piece[split:], offset+split
		for i := 0; i < len(tail); i += block {
			part := tail[i:min(i+block, len(tail))]
			if allZero(part) {
				continue
			}
			if w.overflowUsed += int64(len(part)); w.overflowUsed > w.overflowLimit {
				return fmt.Errorf("dados além do novo tamanho da partição excedem %d MiB; use o modo com imagem completa no servidor", w.overflowLimit/1024/1024)
			}
			if err := w.writeAt(device, part, at+int64(i)); err != nil {
				return err
			}
		}
		return nil
	}
	return w.writeAt(device, piece, offset)
}

func (w *streamWriter) writeAt(device int, piece []byte, offset int64) error {
	n, err := w.targets[device].WriteAt(piece, offset)
	if err != nil {
		return err
	}
	if n != len(piece) {
		return io.ErrShortWrite
	}
	w.written += int64(n)
	return nil
}

// write places the parts of one source chunk that belong to a copy region at their target offset.
func (w *streamWriter) write(offset int64, data []byte) error {
	end := offset + int64(len(data))
	for i, c := range w.m.Copies {
		lo, hi := max(offset, c.Source), min(end, c.Source+c.Length)
		if lo >= hi {
			continue
		}
		if lo != w.next[i] {
			return errors.New("lotes fora de ordem para a região de cópia")
		}
		piece := data[lo-offset : hi-offset]
		if err := w.put(c.Device, piece, c.Dest+lo-c.Source); err != nil {
			return err
		}
		w.sums[i].Write(piece)
		w.next[i] = hi
	}
	return nil
}

func (w *streamWriter) complete() bool {
	for i, c := range w.m.Copies {
		if w.next[i] != c.Source+c.Length {
			return false
		}
	}
	return true
}

type progressFunc func(phase string, processed, total int64) error

// verify fills new space with zeros, then re-reads every written copy region and zero
// region from the target before any table change or filesystem adaptation.
func (w *streamWriter) verify(ctx context.Context, files []*os.File, progress progressFunc) error {
	disk := files[streamDisk]
	var total int64
	for _, z := range w.m.Zeros {
		total += z.Length
	}
	zero := make([]byte, ChunkSize)
	var done int64
	for _, z := range w.m.Zeros {
		for offset := int64(0); offset < z.Length; {
			if err := ctx.Err(); err != nil {
				return err
			}
			n := min(int64(len(zero)), z.Length-offset)
			if _, err := disk.WriteAt(zero[:n], z.Offset+offset); err != nil {
				return err
			}
			offset += n
			done += n
			if err := progress("ZEROING", done, total); err != nil {
				return err
			}
		}
	}
	for _, f := range files {
		if err := f.Sync(); err != nil {
			return err
		}
	}
	total, done = 0, 0
	for _, c := range w.m.Copies {
		total += c.Length
	}
	for _, z := range w.m.Zeros {
		total += z.Length
	}
	buffer := make([]byte, ChunkSize)
	for i, c := range w.m.Copies {
		h := sha256.New()
		for offset := int64(0); offset < c.Length; {
			n := min(int64(len(buffer)), c.Length-offset)
			if _, err := files[c.Device].ReadAt(buffer[:n], c.Dest+offset); err != nil {
				return err
			}
			h.Write(buffer[:n])
			offset += n
			done += n
			if err := progress("VERIFYING", done, total); err != nil {
				return err
			}
		}
		if !bytes.Equal(h.Sum(nil), w.sums[i].Sum(nil)) {
			return fmt.Errorf("releitura do destino diverge na região %d", i)
		}
	}
	for _, z := range w.m.Zeros {
		for offset := int64(0); offset < z.Length; {
			n := min(int64(len(buffer)), z.Length-offset)
			if _, err := disk.ReadAt(buffer[:n], z.Offset+offset); err != nil {
				return err
			}
			if !bytes.Equal(buffer[:n], zero[:n]) {
				return errors.New("espaço novo do destino não foi zerado")
			}
			offset += n
			done += n
			if err := progress("VERIFYING", done, total); err != nil {
				return err
			}
		}
	}
	return nil
}

func loopToolsAvailable() bool {
	if runtime.GOOS != "linux" || os.Geteuid() != 0 {
		return false
	}
	_, err := exec.LookPath("losetup")
	return err == nil
}

func shrinkToolsAvailable() bool {
	if !loopToolsAvailable() || !adaptiveToolsAvailable(true) {
		return false
	}
	_, err := exec.LookPath("dmsetup")
	return err == nil
}

func attachLoop(ctx context.Context, backing string, args ...string) (string, error) {
	output, err := tool(ctx, nil, "losetup", append(append([]string{"--find", "--show"}, args...), backing)...)
	if err != nil {
		return "", err
	}
	device := strings.TrimSpace(string(output))
	if !strings.HasPrefix(device, "/dev/loop") || strings.ContainsAny(device, "\r\n ") {
		return "", errors.New("loop retornado inválido")
	}
	return device, nil
}

func detachLoop(device, backing string) error {
	data, err := os.ReadFile(filepath.Join("/sys/class/block", filepath.Base(device), "loop/backing_file"))
	if err == nil && strings.TrimSpace(string(data)) != backing {
		err = errors.New("loop com backing_file diferente não será desconectado")
	}
	if err == nil {
		_, err = tool(context.Background(), nil, "losetup", "--detach", device)
	}
	return err
}

// withLoop maps one partition range of the target disk (or synthetic file) to a loop device.
func withLoop(ctx context.Context, backing string, offset, size int64, fn func(string) error) (err error) {
	if !loopToolsAvailable() {
		return errors.New("ajuste no destino exige Linux Rescue como root e losetup")
	}
	if backing, err = filepath.Abs(backing); err != nil {
		return err
	}
	device, err := attachLoop(ctx, backing, "--offset", strconv.FormatInt(offset, 10), "--sizelimit", strconv.FormatInt(size, 10))
	if err != nil {
		return err
	}
	defer func() {
		if e := detachLoop(device, backing); e != nil && err == nil {
			err = e
		}
	}()
	return fn(device)
}

// shrinkDevices exposes the target disk and the oversized partition view used while
// streaming a partition into a smaller target.
type shrinkDevices struct {
	diskLoop, overflowLoop, overflowFile, name string
	disk, overlay                              string
	backing                                    string
}

func setupShrink(ctx context.Context, diskPath string, m streamMap, overflowDir string) (s *shrinkDevices, err error) {
	if !shrinkToolsAvailable() {
		return nil, errors.New("redução em lotes exige Rescue como root com losetup, dmsetup e ferramentas NTFS/ext4")
	}
	s = &shrinkDevices{name: "slt-shrink-" + strconv.FormatInt(time.Now().UnixNano(), 36)}
	defer func() {
		if err != nil {
			s.close()
			s = nil
		}
	}()
	if s.backing, err = filepath.Abs(diskPath); err != nil {
		return s, err
	}
	base := s.backing
	if info, e := os.Stat(base); e == nil && info.Mode().IsRegular() {
		if s.diskLoop, err = attachLoop(ctx, base); err != nil {
			return s, err
		}
		base = s.diskLoop
	}
	if overflowDir == "" {
		overflowDir = os.TempDir()
	}
	overflow, err := os.CreateTemp(overflowDir, "slt-overflow-*.img")
	if err != nil {
		return s, err
	}
	s.overflowFile = overflow.Name()
	tailBytes := m.Resize.OldSize - m.Resize.NewSize
	err = overflow.Truncate(tailBytes)
	if e := overflow.Close(); err == nil {
		err = e
	}
	if err != nil {
		return s, err
	}
	if s.overflowLoop, err = attachLoop(ctx, s.overflowFile); err != nil {
		return s, err
	}
	start, _, err := safeByteRange(m.Resize.Partition.StartLba, m.Resize.Partition.LengthLba, m.TargetLayout.SectorSize)
	if err != nil {
		return s, err
	}
	// device-mapper tables always count 512-byte sectors.
	table := fmt.Sprintf("0 %d linear %s 0\n", m.TargetBytes/512, base)
	if _, err = tool(ctx, strings.NewReader(table), "dmsetup", "create", s.name+"-disk"); err != nil {
		return s, err
	}
	s.disk = "/dev/mapper/" + s.name + "-disk"
	table = fmt.Sprintf("0 %d linear %s %d\n%d %d linear %s 0\n", m.Resize.NewSize/512, base, start/512, m.Resize.NewSize/512, tailBytes/512, s.overflowLoop)
	if _, err = tool(ctx, strings.NewReader(table), "dmsetup", "create", s.name+"-part"); err != nil {
		return s, err
	}
	s.overlay = "/dev/mapper/" + s.name + "-part"
	_, _ = tool(ctx, nil, "dmsetup", "mknodes")
	return s, nil
}

func (s *shrinkDevices) close() error {
	if s == nil {
		return nil
	}
	var first error
	keep := func(err error) {
		if err != nil && first == nil {
			first = err
		}
	}
	for _, name := range []string{s.name + "-part", s.name + "-disk"} {
		if _, err := os.Stat("/dev/mapper/" + name); err == nil {
			_, err = tool(context.Background(), nil, "dmsetup", "remove", "--retry", name)
			keep(err)
		}
	}
	if s.overflowLoop != "" {
		keep(detachLoop(s.overflowLoop, s.overflowFile))
		s.overflowLoop = ""
	}
	if s.overflowFile != "" {
		keep(os.Remove(s.overflowFile))
		s.overflowFile = ""
	}
	if s.diskLoop != "" {
		keep(detachLoop(s.diskLoop, s.backing))
		s.diskLoop = ""
	}
	return first
}

// adaptStreamTarget runs after verify, with the target files closed, on the target path itself.
func adaptStreamTarget(ctx context.Context, path string, m streamMap, shrink *shrinkDevices) (RestoreReport, error) {
	report := RestoreReport{Strategy: m.Strategy, DataVerified: true, BootPayloadPreserved: true, BytesWritten: m.TargetBytes, SourceLayout: m.SourceLayout, TargetLayout: m.TargetLayout,
		Warnings: []string{"Boot exige teste real no computador destino. Integridade de transferência não comprova sistema inicializável.", "Preservação de GUIDs/UUIDs requer evitar conexão simultânea de origem e clone no mesmo sistema."}}
	if m.Strategy == "EXACT_RAW" {
		if m.SourceLayout != nil && adaptiveToolsAvailable(false) {
			actual, err := InspectLayout(ctx, path, m.TargetBytes, m.SourceLayout.SectorSize)
			if err != nil || !layoutsEqual(m.SourceLayout, actual) {
				return report, errors.New("tabela gravada não confere com a origem")
			}
			report.TableVerified = true
		}
		return report, nil
	}
	if !adaptiveToolsAvailable(m.Resize != nil) {
		return report, errors.New("ferramentas Linux de adaptação não disponíveis")
	}
	sector := m.TargetLayout.SectorSize
	if m.shrink() {
		if shrink == nil {
			return report, errors.New("redução sem dispositivo de sobreposição")
		}
		if err := validateFilesystem(ctx, shrink.overlay, m.Resize.FileSystem, false); err != nil {
			return report, fmt.Errorf("filesystem da partição %d não pode ser reduzido (hibernado, sujo ou inválido): %w", m.Resize.Partition.Number, err)
		}
		if err := resizeFilesystemAt(ctx, shrink.overlay, m.Resize.FileSystem, m.Resize.OldSize, m.Resize.NewSize, false); err != nil {
			return report, err
		}
		if m.Resize.FileSystem == "ntfs" {
			if actual, err := ntfsGeometry(shrink.overlay); err != nil || actual > m.Resize.NewSize {
				return report, errors.New("NTFS reduzido ainda ocupa espaço além da partição de destino")
			}
		}
		// Everything now lives inside the target partition; the RAM overflow is discarded.
		if err := shrink.close(); err != nil {
			return report, err
		}
	} else if m.Resize != nil {
		// Check the copied filesystem at its original size before touching the table.
		start, _, err := safeByteRange(m.Resize.Partition.StartLba, m.Resize.Partition.LengthLba, sector)
		if err != nil {
			return report, err
		}
		if err = withLoop(ctx, path, start, m.Resize.OldSize, func(device string) error { return validateFilesystem(ctx, device, m.Resize.FileSystem, false) }); err != nil {
			return report, fmt.Errorf("filesystem da partição %d não pode ser expandido (hibernado, sujo ou inválido); destino ficou com cópia sem ajuste: %w", m.Resize.Partition.Number, err)
		}
	}
	if err := rewriteLayout(ctx, path, m.TargetLayout, m.TargetBytes); err != nil {
		return report, err
	}
	report.TableVerified = true
	if m.Resize != nil {
		start, size, err := safeByteRange(m.Resize.Partition.StartLba, m.Resize.Partition.LengthLba, sector)
		if err != nil {
			return report, err
		}
		err = withLoop(ctx, path, start, size, func(device string) error {
			if m.shrink() {
				return validateFilesystem(ctx, device, m.Resize.FileSystem, true)
			}
			return resizeFilesystemAt(ctx, device, m.Resize.FileSystem, m.Resize.OldSize, m.Resize.NewSize, false)
		})
		if err != nil {
			return report, err
		}
		if m.Resize.FileSystem == "ntfs" {
			report.Warnings = append(report.Warnings, "NTFS ajustado fica marcado pelo ntfsresize para CHKDSK no primeiro boot; essa marca não foi removida.")
		}
	}
	actual, err := InspectLayout(ctx, path, m.TargetBytes, sector)
	if err != nil || !layoutsEqual(m.TargetLayout, actual) {
		return report, errors.New("layout do destino difere do plano após ajuste")
	}
	return report, nil
}

func pause(ctx context.Context, d time.Duration) error {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

func (r *Runner) sendStream(ctx context.Context, p Plan, d Disk) error {
	f, err := r.openDisk(ctx, p, d, false)
	if err != nil {
		return err
	}
	defer f.Close()
	var layout *DiskLayout
	if adaptiveToolsAvailable(false) {
		layout, _ = InspectLayout(ctx, d.Path, d.SizeBytes, d.LogicalSectorBytes)
	}
	var status StreamStatus
	if _, err = r.Client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/stream-start", map[string]any{"layout": layout}, &status); err != nil {
		return err
	}
	if status.LogicalSizeBytes != d.SizeBytes || status.LogicalSectorBytes != d.LogicalSectorBytes || status.SourceFingerprint != d.Fingerprint {
		return errors.New("transmissão não corresponde ao disco de origem")
	}
	buffer := make([]byte, ChunkSize)
	var offset int64
	for sequence := 0; offset < d.SizeBytes; sequence++ {
		if err = r.report(ctx, p, "STREAMING", offset, d.SizeBytes); err != nil {
			return err
		}
		length := min(int64(ChunkSize), d.SizeBytes-offset)
		if _, err = io.ReadFull(f, buffer[:length]); err != nil {
			return fmt.Errorf("erro de leitura em %d; usar ddrescue para mídia defeituosa: %w", offset, err)
		}
		h := sha256.Sum256(buffer[:length])
		expected := hex.EncodeToString(h[:])
		body, headers := io.Reader(bytes.NewReader(buffer[:length])), map[string]string{"Content-Type": "application/octet-stream", "X-Content-SHA256": expected}
		if allZero(buffer[:length]) {
			// Empty disk areas cross the network as a marker; the hash still covers every byte.
			body, headers["X-Content-Zero"] = bytes.NewReader(nil), "1"
		}
		for {
			if seeker, ok := body.(io.Seeker); ok {
				seeker.Seek(0, io.SeekStart)
			}
			data, code, e := r.Client.Request(ctx, "PUT", fmt.Sprintf("/agent/v1/jobs/%s/chunks/%d", p.ID, sequence), body, headers)
			if code == http.StatusTooManyRequests {
				// Server window full: the target has not yet confirmed earlier batches.
				if err = r.report(ctx, p, "WAITING_TARGET", offset, d.SizeBytes); err != nil {
					return err
				}
				if err = pause(ctx, time.Second); err != nil {
					return err
				}
				continue
			}
			if e != nil {
				return e
			}
			var receipt ImageChunk
			if err = json.Unmarshal(data, &receipt); err != nil {
				return err
			}
			if receipt.Sequence != sequence || receipt.SizeBytes != int(length) || !strings.EqualFold(receipt.SHA256, expected) {
				return errors.New("recibo do lote não confere")
			}
			break
		}
		offset += length
	}
	if layout != nil {
		after, e := InspectLayout(ctx, d.Path, d.SizeBytes, d.LogicalSectorBytes)
		if e != nil || !layoutsEqual(layout, after) {
			return errors.New("layout da origem mudou durante o envio")
		}
	}
	var manifest Manifest
	if _, err = r.Client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/capture-complete", map[string]any{"layout": layout}, &manifest); err != nil {
		return err
	}
	if manifest.State != "STREAMED" || manifest.LogicalSizeBytes != d.SizeBytes || manifest.SourceFingerprint != d.Fingerprint {
		return errors.New("servidor não confirmou o envio completo dos lotes")
	}
	return r.report(ctx, p, "SENT", d.SizeBytes, d.SizeBytes)
}

func (r *Runner) receiveStream(ctx context.Context, p Plan, d Disk) error {
	r.LastRestoreReport = nil
	if p.Clone == nil {
		return errors.New("clone em lotes sem estratégia assinada")
	}
	var status StreamStatus
	for {
		if _, err := r.Client.JSON(ctx, "GET", "/agent/v1/jobs/"+p.ID+"/stream", nil, &status); err != nil {
			return err
		}
		if status.SourceStarted {
			break
		}
		if err := r.report(ctx, p, "WAITING_SOURCE", 0, d.SizeBytes); err != nil {
			return err
		}
		if err := pause(ctx, time.Second); err != nil {
			return err
		}
	}
	if status.State != "OPEN" || status.LogicalSectorBytes != p.LogicalSectorBytes || status.AckedThrough != -1 || status.TotalChunks != int((status.LogicalSizeBytes+ChunkSize-1)/ChunkSize) {
		return errors.New("transmissão incompatível com o plano ou já iniciada por outro destino")
	}
	m, err := planStream(status.Layout, status.LogicalSizeBytes, d.SizeBytes, d.LogicalSectorBytes, *p.Clone)
	if err != nil {
		return err
	}
	if m.Strategy != "EXACT_RAW" && !adaptiveToolsAvailable(m.Resize != nil) || m.Resize != nil && (!loopToolsAvailable() || p.Clone.ResizePartitionNumber != m.Resize.Partition.Number) || m.shrink() && !shrinkToolsAvailable() {
		return errors.New("Rescue destino sem ferramentas para esta estratégia em lotes")
	}
	r.LastRestoreReport = &RestoreReport{Strategy: m.Strategy, SourceLayout: status.Layout, Warnings: []string{"Destino incompleto até conclusão e releitura; boot não validado."}}
	if err = r.report(ctx, p, "PREFLIGHT", 0, status.LogicalSizeBytes); err != nil {
		return err
	}
	f, err := r.openDisk(ctx, p, d, true)
	if err != nil {
		return err
	}
	files := []*os.File{f}
	var shrink *shrinkDevices
	defer func() {
		for _, file := range files {
			file.Close()
		}
		shrink.close()
	}()
	if m.shrink() {
		// Local confirmation and revalidation are done; device-mapper takes the exclusive claim.
		f.Close()
		files = nil
		if shrink, err = setupShrink(ctx, d.Path, m, r.Options.ScratchDir); err != nil {
			return err
		}
		for _, path := range []string{shrink.disk, shrink.overlay} {
			file, e := os.OpenFile(path, os.O_RDWR, 0)
			if e != nil {
				return e
			}
			files = append(files, file)
		}
	}
	targets := make([]io.WriterAt, len(files))
	for i, file := range files {
		targets[i] = file
	}
	w := newStreamWriter(m, targets...)
	next := 0
	for next < status.TotalChunks {
		processed := 0
		for _, c := range status.Pending {
			if c.Sequence < next {
				continue
			}
			if c.Sequence != next || c.OffsetBytes != int64(next)*ChunkSize || c.SizeBytes <= 0 || c.SizeBytes > ChunkSize {
				return errors.New("lotes recebidos fora de ordem")
			}
			var data []byte
			if strings.EqualFold(c.SHA256, zeroHash(c.SizeBytes)) {
				data = make([]byte, c.SizeBytes)
			} else {
				data, err = r.Client.FetchChunk(ctx, fmt.Sprintf("/agent/v1/jobs/%s/chunks/%d", p.ID, c.Sequence))
				if err != nil {
					return err
				}
			}
			h := sha256.Sum256(data)
			if len(data) != c.SizeBytes || !strings.EqualFold(hex.EncodeToString(h[:]), c.SHA256) {
				return fmt.Errorf("lote %d corrompido: bloqueado antes da escrita", c.Sequence)
			}
			if err = w.write(c.OffsetBytes, data); err != nil {
				return err
			}
			r.LastRestoreReport.BytesWritten = w.written
			next++
			processed++
			if err = r.report(ctx, p, "RESTORING", c.OffsetBytes+int64(c.SizeBytes), status.LogicalSizeBytes); err != nil {
				return err
			}
		}
		if processed > 0 {
			// Durable on the target first; only then may the server discard these batches.
			for _, file := range files {
				if err = file.Sync(); err != nil {
					return err
				}
			}
			if _, err = r.Client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/stream-ack", map[string]any{"through": next - 1}, &status); err != nil {
				return err
			}
			continue
		}
		if err = r.report(ctx, p, "WAITING_SOURCE", int64(next)*ChunkSize, status.LogicalSizeBytes); err != nil {
			return err
		}
		if err = pause(ctx, 500*time.Millisecond); err != nil {
			return err
		}
		if _, err = r.Client.JSON(ctx, "GET", "/agent/v1/jobs/"+p.ID+"/stream", nil, &status); err != nil {
			return err
		}
	}
	if !w.complete() {
		return errors.New("transmissão terminou sem cobrir todas as regiões do plano")
	}
	if err = w.verify(ctx, files, func(phase string, processed, total int64) error { return r.report(ctx, p, phase, processed, total) }); err != nil {
		return err
	}
	for _, file := range files {
		if err = file.Close(); err != nil {
			return err
		}
	}
	files = nil
	if err = r.report(ctx, p, "ADAPTING", d.SizeBytes, d.SizeBytes); err != nil {
		return err
	}
	report, err := adaptStreamTarget(ctx, d.Path, m, shrink)
	r.LastRestoreReport = &report
	if err != nil {
		return err
	}
	return r.report(ctx, p, "VERIFIED", d.SizeBytes, d.SizeBytes)
}
