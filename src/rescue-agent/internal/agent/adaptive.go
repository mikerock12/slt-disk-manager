package agent

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
)

type preparedImage struct {
	Path   string
	Layout *DiskLayout
	Chunks []ImageChunk
	Report RestoreReport
}

func adaptiveToolsAvailable(fit bool) bool {
	if runtime.GOOS != "linux" {
		return false
	}
	tools := []string{"sfdisk", "blkid"}
	if fit {
		tools = append(tools, "ntfsresize", "ntfs-3g.probe", "resize2fs", "e2fsck")
	}
	for _, name := range tools {
		if _, err := exec.LookPath(name); err != nil {
			return false
		}
	}
	return true
}

func scratchPath(root string) (string, error) {
	if root == "" {
		return "", errors.New("clonagem adaptada exige --scratch-dir em armazenamento saudável")
	}
	abs, err := filepath.Abs(root)
	if err != nil {
		return "", err
	}
	resolved, err := filepath.EvalSymlinks(abs)
	if err != nil {
		return "", err
	}
	if resolved != abs {
		return "", errors.New("scratch não pode conter symlink")
	}
	info, err := os.Stat(abs)
	if err != nil || !info.IsDir() {
		return "", errors.New("scratch deve ser diretório existente")
	}
	return abs, nil
}

func scratchRequired(m Manifest, targetBytes int64) (int64, error) {
	maxPart := int64(0)
	if m.Layout != nil {
		for _, p := range m.Layout.Partitions {
			_, n, e := safeByteRange(p.StartLba, p.LengthLba, m.Layout.SectorSize)
			if e != nil {
				return 0, e
			}
			if n > maxPart {
				maxPart = n
			}
		}
	}
	if targetBytes > maxPart {
		maxPart = targetBytes
	}
	total := int64(64 * 1024 * 1024)
	for _, n := range []int64{m.LogicalSizeBytes, targetBytes, maxPart} {
		if n <= 0 || total > math.MaxInt64-n {
			return 0, errors.New("tamanho scratch inválido ou overflow")
		}
		total += n
	}
	return total, nil
}

func newWorkspace(root string, required int64, diskPath string) (string, error) {
	canonical, err := scratchPath(root)
	if err != nil {
		return "", err
	}
	if err = checkScratchStorage(canonical, required, diskPath); err != nil {
		return "", err
	}
	return os.MkdirTemp(canonical, "slt-clone-")
}

func removeWorkspace(root, work string) error {
	canonical, err := scratchPath(root)
	if err != nil {
		return err
	}
	abs, err := filepath.Abs(work)
	if err != nil {
		return err
	}
	rel, err := filepath.Rel(canonical, abs)
	if err != nil || filepath.IsAbs(rel) || rel == "." || strings.ContainsAny(rel, "/\\") || !strings.HasPrefix(rel, "slt-clone-") {
		return errors.New("limpeza scratch fora do diretório autorizado")
	}
	return os.RemoveAll(abs)
}

func copyRange(ctx context.Context, destination *os.File, destOffset int64, source *os.File, sourceOffset, length int64) error {
	buffer := make([]byte, ChunkSize)
	for offset := int64(0); offset < length; {
		if err := ctx.Err(); err != nil {
			return err
		}
		n := int64(len(buffer))
		if length-offset < n {
			n = length - offset
		}
		if _, err := source.ReadAt(buffer[:int(n)], sourceOffset+offset); err != nil {
			return err
		}
		written, err := destination.WriteAt(buffer[:int(n)], destOffset+offset)
		if err != nil {
			return err
		}
		if written != int(n) {
			return io.ErrShortWrite
		}
		offset += n
	}
	return nil
}

func hashRange(ctx context.Context, f *os.File, offset, length int64) (string, error) {
	h := sha256.New()
	buffer := make([]byte, ChunkSize)
	for done := int64(0); done < length; {
		if err := ctx.Err(); err != nil {
			return "", err
		}
		n := int64(len(buffer))
		if length-done < n {
			n = length - done
		}
		if _, err := f.ReadAt(buffer[:int(n)], offset+done); err != nil {
			return "", err
		}
		h.Write(buffer[:int(n)])
		done += n
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

func compareRange(ctx context.Context, a *os.File, aOffset int64, b *os.File, bOffset, length int64) error {
	x, err := hashRange(ctx, a, aOffset, length)
	if err != nil {
		return err
	}
	y, err := hashRange(ctx, b, bOffset, length)
	if err != nil {
		return err
	}
	if x != y {
		return errors.New("região copiada difere da origem")
	}
	return nil
}

func extractPartition(ctx context.Context, source *os.File, p LayoutPartition, sector int, path string) (*os.File, error) {
	start, size, err := safeByteRange(p.StartLba, p.LengthLba, sector)
	if err != nil {
		return nil, err
	}
	f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return nil, err
	}
	if err = f.Truncate(size); err == nil {
		err = copyRange(ctx, f, 0, source, start, size)
	}
	if err == nil {
		err = f.Sync()
	}
	if err != nil {
		f.Close()
		return nil, err
	}
	return f, nil
}

func validateFilesystem(ctx context.Context, path, fs string, resized bool) error {
	switch fs {
	case "ntfs":
		if !resized {
			if _, err := tool(ctx, nil, "ntfs-3g.probe", "--readwrite", path); err != nil {
				return fmt.Errorf("NTFS hibernado, dirty ou indisponível: %w", err)
			}
		}
		_, err := tool(ctx, nil, "ntfsresize", "--check", "--no-action", path)
		return err
	case "ext4":
		_, err := tool(ctx, nil, "e2fsck", "-f", "-n", path)
		return err
	default:
		if protectedFilesystem(fs) {
			return errors.New("criptografia, RAID ou LVM não suportado na adaptação")
		}
		return nil
	}
}

func resizeFilesystem(ctx context.Context, path, fs string, oldSize, newSize int64) error {
	if newSize == oldSize {
		return nil
	}
	if err := validateFilesystem(ctx, path, fs, false); err != nil {
		return err
	}
	switch fs {
	case "ntfs":
		if newSize > oldSize {
			if err := os.Truncate(path, newSize); err != nil {
				return err
			}
		}
		if _, err := tool(ctx, nil, "ntfsresize", "--no-action", "--size", strconv.FormatInt(newSize, 10), path); err != nil {
			return fmt.Errorf("redução/expansão NTFS não comprovada: %w", err)
		}
		if _, err := tool(ctx, strings.NewReader("y\n"), "ntfsresize", "--size", strconv.FormatInt(newSize, 10), path); err != nil {
			return err
		}
		if err := os.Truncate(path, newSize); err != nil {
			return err
		}
		actual, err := ntfsGeometry(path)
		if err != nil {
			return err
		}
		if actual > newSize || actual < newSize-1024*1024 {
			return errors.New("geometria NTFS após resize não confere")
		}
	case "ext4":
		// Initial read-only fsck must pass first. This full check only updates the private copy's check metadata.
		if _, err := tool(ctx, nil, "e2fsck", "-f", "-p", path); err != nil {
			return err
		}
		if newSize > oldSize {
			if err := os.Truncate(path, newSize); err != nil {
				return err
			}
		}
		if _, err := tool(ctx, nil, "resize2fs", path, strconv.FormatInt(newSize/512, 10)+"s"); err != nil {
			return err
		}
		if err := os.Truncate(path, newSize); err != nil {
			return err
		}
	default:
		return errors.New("filesystem não redimensionável")
	}
	return validateFilesystem(ctx, path, fs, true)
}

func prepareImage(ctx context.Context, sourcePath, workDir string, m Manifest, targetBytes int64, options CloneOptions) (preparedImage, error) {
	result := preparedImage{Report: RestoreReport{Strategy: options.Strategy, Warnings: []string{"Verificação comprova transferência e preparação da imagem; boot no hardware de destino ainda precisa ser testado.", "Preservação de GUIDs/UUIDs requer evitar conexão simultânea de origem e clone no mesmo sistema."}}}
	if !adaptiveToolsAvailable(options.Strategy == "FIT") {
		return result, errors.New("ferramentas Linux de adaptação não disponíveis")
	}
	if m.Layout == nil {
		return result, errors.New("imagem sem layout: adaptação bloqueada")
	}
	if err := ValidateLayout(m.Layout, m.LogicalSizeBytes); err != nil {
		return result, err
	}
	actual, err := InspectLayout(ctx, sourcePath, m.LogicalSizeBytes, m.LogicalSectorBytes)
	if err != nil {
		return result, err
	}
	if !layoutsEqual(m.Layout, actual) {
		return result, errors.New("layout capturado não confere com a imagem: destino não será aberto")
	}
	for _, p := range actual.Partitions {
		if protectedFilesystem(p.FileSystem) || protectedPartitionType(actual.TableType, p.Type) {
			return result, errors.New("adaptação de criptografia/RAID/LVM bloqueada")
		}
	}
	source, err := os.Open(sourcePath)
	if err != nil {
		return result, err
	}
	defer source.Close()
	info, err := source.Stat()
	if err != nil {
		return result, err
	}
	if !info.Mode().IsRegular() || info.Size() != m.LogicalSizeBytes {
		return result, errors.New("scratch origem não é arquivo regular do tamanho esperado")
	}
	var chunkEnd int64
	for i, c := range m.Chunks {
		if c.Sequence != i || c.OffsetBytes != chunkEnd || c.SizeBytes <= 0 || c.SizeBytes > ChunkSize || int64(c.SizeBytes) > m.LogicalSizeBytes-chunkEnd {
			return result, errors.New("chunks inválidos na imagem scratch")
		}
		sum, e := hashRange(ctx, source, c.OffsetBytes, int64(c.SizeBytes))
		if e != nil {
			return result, e
		}
		if !strings.EqualFold(sum, c.SHA256) {
			return result, errors.New("imagem scratch difere dos hashes do manifest")
		}
		chunkEnd += int64(c.SizeBytes)
	}
	if chunkEnd != m.LogicalSizeBytes {
		return result, errors.New("imagem scratch incompleta")
	}
	sourceHash, err := hashRange(ctx, source, 0, m.LogicalSizeBytes)
	if err != nil {
		return result, err
	}
	targetLayout := *actual
	targetLayout.Partitions = append([]LayoutPartition(nil), actual.Partitions...)
	if options.Strategy == "FIT" {
		layout, e := FitLayout(actual, m.LogicalSizeBytes, targetBytes, options.ResizePartitionNumber)
		if e != nil {
			return result, e
		}
		targetLayout = *layout
	} else if options.Strategy == "PRESERVE_LAYOUT" {
		if targetBytes < m.LogicalSizeBytes {
			return result, errors.New("PRESERVE_LAYOUT exige capacidade maior ou igual")
		}
		targetLayout.LastUsableLba += (targetBytes - m.LogicalSizeBytes) / int64(actual.SectorSize)
	} else {
		return result, errors.New("estratégia adaptada inválida")
	}
	if err = ValidateLayout(&targetLayout, targetBytes); err != nil {
		return result, err
	}
	outputPath := filepath.Join(workDir, "prepared.raw")
	output, err := os.OpenFile(outputPath, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return result, err
	}
	defer output.Close()
	if err = output.Truncate(targetBytes); err != nil {
		return result, err
	}
	selected := -1
	for i, p := range actual.Partitions {
		if p.Number == options.ResizePartitionNumber {
			selected = i
		}
	}
	if options.Strategy == "FIT" {
		if selected < 0 {
			return result, errors.New("partição selecionada inexistente")
		}
		original := actual.Partitions[selected]
		modified := targetLayout.Partitions[selected]
		start, oldSize, e := safeByteRange(original.StartLba, original.LengthLba, actual.SectorSize)
		if e != nil {
			return result, e
		}
		_, newSize, e := safeByteRange(modified.StartLba, modified.LengthLba, actual.SectorSize)
		if e != nil {
			return result, e
		}
		if err = copyRange(ctx, output, 0, source, 0, start); err != nil {
			return result, err
		}
		partitionPath := filepath.Join(workDir, "resize-partition.raw")
		partFile, e := extractPartition(ctx, source, original, actual.SectorSize, partitionPath)
		if e != nil {
			return result, e
		}
		partFile.Close()
		if err = resizeFilesystem(ctx, partitionPath, original.FileSystem, oldSize, newSize); err != nil {
			return result, err
		}
		partFile, err = os.Open(partitionPath)
		if err != nil {
			return result, err
		}
		err = copyRange(ctx, output, start, partFile, 0, newSize)
		partFile.Close()
		if err != nil {
			return result, err
		}
		suffixStart := start + oldSize
		suffixEnd := (actual.LastUsableLba + 1) * int64(actual.SectorSize)
		if suffixEnd < suffixStart {
			return result, errors.New("sufixo inválido")
		}
		if err = copyRange(ctx, output, start+newSize, source, suffixStart, suffixEnd-suffixStart); err != nil {
			return result, err
		}
		if err = os.Remove(partitionPath); err != nil {
			return result, err
		}
		if original.FileSystem == "ntfs" {
			result.Report.Warnings = append(result.Report.Warnings, "NTFS redimensionado fica marcado pelo ntfsresize para CHKDSK no primeiro boot; essa marca não foi removida.")
		}
	} else {
		if err = copyRange(ctx, output, 0, source, 0, m.LogicalSizeBytes); err != nil {
			return result, err
		}
		// The original backup GPT at the old disk end must not survive as a stale signature in unallocated space.
		if actual.TableType == "GPT" && targetBytes > m.LogicalSizeBytes {
			offset, size, e := verifyGPT(source, m.LogicalSizeBytes, actual.SectorSize)
			if e != nil {
				return result, e
			}
			if _, err = output.WriteAt(make([]byte, int(size)), offset); err != nil {
				return result, err
			}
		}
	}
	if err = output.Sync(); err != nil {
		return result, err
	}
	if err = rewriteLayout(ctx, outputPath, &targetLayout, targetBytes); err != nil {
		return result, err
	}
	// Every untouched partition is copied byte for byte, including recovery, ESP, MSR and unknown OEM data.
	for i, p := range actual.Partitions {
		q := targetLayout.Partitions[i]
		sourceOffset, size, e := safeByteRange(p.StartLba, p.LengthLba, actual.SectorSize)
		if e != nil {
			return result, e
		}
		destOffset, _, e := safeByteRange(q.StartLba, q.LengthLba, actual.SectorSize)
		if e != nil {
			return result, e
		}
		if options.Strategy != "FIT" || p.Number != options.ResizePartitionNumber {
			if err = compareRange(ctx, source, sourceOffset, output, destOffset, size); err != nil {
				return result, fmt.Errorf("partição %d: %w", p.Number, err)
			}
		}
		if p.FileSystem == "ntfs" || p.FileSystem == "ext4" {
			checkPath := filepath.Join(workDir, "check-partition.raw")
			checkFile, e := extractPartition(ctx, output, q, actual.SectorSize, checkPath)
			if e != nil {
				return result, e
			}
			checkFile.Close()
			resized := options.Strategy == "FIT" && p.Number == options.ResizePartitionNumber && p.LengthLba != q.LengthLba
			err = validateFilesystem(ctx, checkPath, p.FileSystem, resized)
			removeErr := os.Remove(checkPath)
			if err != nil {
				return result, err
			}
			if removeErr != nil {
				return result, removeErr
			}
		}
	}
	if err = compareRange(ctx, source, 0, output, 0, 440); err != nil {
		return result, fmt.Errorf("código de boot: %w", err)
	}
	newHash, err := hashRange(ctx, source, 0, m.LogicalSizeBytes)
	if err != nil {
		return result, err
	}
	if newHash != sourceHash {
		return result, errors.New("imagem de origem foi alterada durante adaptação")
	}
	result.Path = outputPath
	result.Layout = &targetLayout
	result.Chunks, err = chunksForFile(ctx, output, targetBytes)
	if err != nil {
		return result, err
	}
	result.Report.SourceLayout = actual
	result.Report.TargetLayout = &targetLayout
	result.Report.TableVerified = true
	result.Report.BootPayloadPreserved = true
	return result, nil
}

func chunksForFile(ctx context.Context, f *os.File, total int64) ([]ImageChunk, error) {
	chunks := []ImageChunk{}
	for offset := int64(0); offset < total; {
		length := int64(ChunkSize)
		if total-offset < length {
			length = total - offset
		}
		hash, err := hashRange(ctx, f, offset, length)
		if err != nil {
			return nil, err
		}
		chunks = append(chunks, ImageChunk{Sequence: len(chunks), OffsetBytes: offset, SizeBytes: int(length), SHA256: hash})
		offset += length
	}
	return chunks, nil
}
