//go:build windows

package agent

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unsafe"
)

var peKernel = syscall.NewLazyDLL("kernel32.dll")
var peAdvapi = syscall.NewLazyDLL("advapi32.dll")

type nativeWinPE struct{}

func newWinPEPlatform() winPEPlatform { return nativeWinPE{} }
func peUTF(s string) *uint16          { p, _ := syscall.UTF16PtrFromString(s); return p }
func peError(label string, err error) error {
	if err == syscall.Errno(0) {
		return errors.New(label)
	}
	return fmt.Errorf("%s: %w", label, err)
}
func peWindowsDir() (string, error) {
	buf := make([]uint16, 32768)
	n, _, e := peKernel.NewProc("GetWindowsDirectoryW").Call(uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)))
	if n == 0 || n >= uintptr(len(buf)) {
		return "", peError("Windows directory", e)
	}
	return syscall.UTF16ToString(buf[:n]), nil
}
func peMiniNT() bool {
	var h syscall.Handle
	code, _, _ := peAdvapi.NewProc("RegOpenKeyExW").Call(uintptr(0x80000002), uintptr(unsafe.Pointer(peUTF(`SYSTEM\CurrentControlSet\Control\MiniNT`))), 0, 0x20019, uintptr(unsafe.Pointer(&h)))
	if code != 0 {
		return false
	}
	peAdvapi.NewProc("RegCloseKey").Call(uintptr(h))
	return true
}
func (nativeWinPE) Check(ctx context.Context) (string, error) {
	if runtime.GOARCH != "amd64" || !peMiniNT() {
		return "", errors.New("implantação bloqueada: este Windows não é WinPE AMD64 (MiniNT ausente)")
	}
	root, err := peWindowsDir()
	if err != nil {
		return "", err
	}
	if err = peRegular(filepath.Join(root, "System32", "wpeutil.exe")); err != nil {
		return "", errors.New("WinPE sem wpeutil legítimo disponível")
	}
	var firmware uint32
	ok, _, e := peKernel.NewProc("GetFirmwareType").Call(uintptr(unsafe.Pointer(&firmware)))
	if ok == 0 {
		return "", peError("firmware não identificado", e)
	}
	switch firmware {
	case 1:
		return "MBR_BIOS", nil
	case 2:
		return "GPT_UEFI", nil
	default:
		return "", errors.New("firmware desconhecido")
	}
}
func peNoReparse(path string) error {
	return peNoReparseWalk(path, false)
}
func peNoReparseWalk(path string, allowMissing bool) error {
	if !safeWinPEPath(path) {
		return errors.New("caminho deve ser local absoluto sem stream/device/UNC")
	}
	full, err := filepath.Abs(path)
	if err != nil {
		return err
	}
	parts := strings.Split(full[3:], `\`)
	current := full[:3]
	for _, part := range parts {
		if part == "" {
			continue
		}
		current = filepath.Join(current, part)
		attrs, e := syscall.GetFileAttributes(peUTF(current))
		if e != nil {
			if allowMissing && (e == syscall.ERROR_FILE_NOT_FOUND || e == syscall.ERROR_PATH_NOT_FOUND) {
				return nil
			}
			return e
		}
		if attrs&syscall.FILE_ATTRIBUTE_REPARSE_POINT != 0 {
			return errors.New("junction/symlink/reparse bloqueado em caminho de implantação")
		}
	}
	return nil
}
func peRegular(path string) error {
	if err := peNoReparse(path); err != nil {
		return err
	}
	i, e := os.Stat(path)
	if e != nil {
		return e
	}
	if !i.Mode().IsRegular() || i.Size() == 0 {
		return errors.New("ferramenta não é arquivo regular")
	}
	return nil
}
func (nativeWinPE) PrivateState(path string) error {
	if !safeWinPEPath(path) {
		return errors.New("diretório privado local inválido")
	}
	if err := peNoReparseWalk(path, true); err != nil {
		return err
	}
	if err := os.MkdirAll(path, 0700); err != nil {
		return err
	}
	if err := peNoReparse(path); err != nil {
		return err
	}
	var descriptor uintptr
	ok, _, e := peAdvapi.NewProc("ConvertStringSecurityDescriptorToSecurityDescriptorW").Call(uintptr(unsafe.Pointer(peUTF("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"))), 1, uintptr(unsafe.Pointer(&descriptor)), 0)
	if ok == 0 {
		return peError("DACL privada indisponível", e)
	}
	defer peKernel.NewProc("LocalFree").Call(descriptor)
	ok, _, e = peAdvapi.NewProc("SetFileSecurityW").Call(uintptr(unsafe.Pointer(peUTF(path))), uintptr(0x80000004), descriptor)
	if ok == 0 {
		return peError("não foi possível proteger estado/staging com ACL SYSTEM/Administradores", e)
	}
	return nil
}
func peOpen(path string) (syscall.Handle, error) {
	return syscall.CreateFile(peUTF(path), syscall.GENERIC_READ, syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE, nil, syscall.OPEN_EXISTING, 0, 0)
}
func peIO(handle syscall.Handle, code uint32, input []byte, size int) ([]byte, error) {
	out := make([]byte, size)
	var n uint32
	var in *byte
	if len(input) > 0 {
		in = &input[0]
	}
	err := syscall.DeviceIoControl(handle, code, in, uint32(len(input)), &out[0], uint32(len(out)), &n, nil)
	if err != nil {
		return nil, err
	}
	if n > uint32(size) {
		return nil, errors.New("retorno IOCTL excessivo")
	}
	return out[:n], nil
}
func peDisk(number int) (Disk, error) {
	path := fmt.Sprintf(`\\.\PhysicalDrive%d`, number)
	h, err := peOpen(path)
	if err != nil {
		return Disk{}, err
	}
	defer syscall.CloseHandle(h)
	length, err := peIO(h, 0x7405c, nil, 8)
	if err != nil || len(length) < 8 {
		return Disk{}, errors.New("capacidade não identificada")
	}
	size := int64(binary.LittleEndian.Uint64(length))
	if size <= 0 {
		return Disk{}, errors.New("capacidade inválida")
	}
	query := make([]byte, 12)
	descriptor, err := peIO(h, 0x2d1400, query, 65536)
	if err != nil {
		return Disk{}, err
	}
	model, serial, err := decodeWinPEDescriptor(descriptor)
	if err != nil {
		return Disk{}, err
	}
	binary.LittleEndian.PutUint32(query, 6)
	alignment, err := peIO(h, 0x2d1400, query, 28)
	if err != nil || len(alignment) < 28 {
		return Disk{}, errors.New("alinhamento de setores não identificado")
	}
	logical := int(binary.LittleEndian.Uint32(alignment[16:]))
	physical := int(binary.LittleEndian.Uint32(alignment[20:]))
	if logical != 512 && logical != 4096 || physical < logical || size%int64(logical) != 0 {
		return Disk{}, errors.New("geometria inválida")
	}
	attrs, err := peIO(h, 0x700f0, nil, 16)
	if err != nil || len(attrs) < 16 {
		return Disk{}, errors.New("atributos de proteção não identificados")
	}
	readOnly := binary.LittleEndian.Uint64(attrs[8:])&2 != 0
	offline := binary.LittleEndian.Uint64(attrs[8:])&1 != 0
	disk := Disk{ID: fmt.Sprintf("disk-%d", number), Path: path, Model: model, Serial: serial, SizeBytes: size, LogicalSectorBytes: logical, PhysicalSectorBytes: physical, IsReadOnly: readOnly, Offline: offline, Health: "NOT_QUERIED", Partitions: []Partition{}}
	disk.Fingerprint = Fingerprint(disk)
	return disk, nil
}
func peDriveMask() (uint32, error) {
	mask, _, e := peKernel.NewProc("GetLogicalDrives").Call()
	if mask == 0 {
		return 0, peError("volumes não identificados", e)
	}
	return uint32(mask), nil
}
func pePathDisks(path string) ([]int, error) {
	if err := peNoReparse(path); err != nil {
		return nil, err
	}
	root := strings.ToUpper(path[:2])
	device := make([]uint16, 4096)
	n, _, e := peKernel.NewProc("QueryDosDeviceW").Call(uintptr(unsafe.Pointer(peUTF(root))), uintptr(unsafe.Pointer(&device[0])), uintptr(len(device)))
	if n == 0 {
		return nil, peError("mapeamento de volume indisponível", e)
	}
	if nonPhysicalWinPEVolume(syscall.UTF16ToString(device)) {
		return []int{}, nil
	}
	h, err := peOpen(`\\.\` + root)
	if err != nil {
		return nil, err
	}
	defer syscall.CloseHandle(h)
	data, err := peIO(h, 0x560000, nil, 8+24*256)
	if err != nil {
		return nil, err
	}
	return decodeWinPEExtents(data)
}
func (nativeWinPE) Disks(ctx context.Context, protected []string) ([]Disk, error) {
	if _, err := (nativeWinPE{}).Check(ctx); err != nil {
		return nil, err
	}
	windows, err := peWindowsDir()
	if err != nil {
		return nil, err
	}
	protected = append(append([]string{}, protected...), windows)
	mask, err := peDriveMask()
	if err != nil {
		return nil, err
	}
	mounted := map[int]bool{}
	protectedNumbers := map[int]bool{}
	for letter := byte('A'); letter <= 'Z'; letter++ {
		if mask&(1<<uint(letter-'A')) == 0 {
			continue
		}
		root := fmt.Sprintf("%c:\\", letter)
		if _, e := os.Stat(root); e != nil {
			continue
		}
		numbers, e := pePathDisks(root)
		if e == nil {
			for _, n := range numbers {
				mounted[n] = true
			}
		}
		if _, e := os.Stat(filepath.Join(root, "sources", "boot.wim")); e == nil {
			protected = append(protected, root)
		}
	}
	for _, path := range protected {
		if path == "" {
			continue
		}
		numbers, e := pePathDisks(path)
		if e != nil {
			return nil, fmt.Errorf("não foi possível identificar/proteger volume de %s: %w", path, e)
		}
		for _, n := range numbers {
			protectedNumbers[n] = true
		}
	}
	disks := []Disk{}
	for number := 0; number < 256; number++ {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		default:
		}
		d, e := peDisk(number)
		if e != nil {
			continue
		}
		d.IsSystem = protectedNumbers[number]
		d.Mounted = mounted[number]
		if d.IsSystem {
			d.Health = "PROTECTED_WINPE_STORAGE"
		}
		disks = append(disks, d)
	}
	return disks, nil
}
func (nativeWinPE) Scratch(path string, required int64, target int) error {
	if path == "" || required <= 0 {
		return errors.New("scratch local saudável obrigatório")
	}
	if err := peNoReparse(path); err != nil {
		return err
	}
	numbers, err := pePathDisks(path)
	if err != nil {
		return err
	}
	if len(numbers) == 0 {
		return errors.New("scratch em RAM bloqueado; use armazenamento físico saudável separado")
	}
	for _, n := range numbers {
		if n == target {
			return errors.New("scratch pertence ao disco de destino")
		}
	}
	var free, total, available uint64
	ok, _, e := peKernel.NewProc("GetDiskFreeSpaceExW").Call(uintptr(unsafe.Pointer(peUTF(path))), uintptr(unsafe.Pointer(&available)), uintptr(unsafe.Pointer(&total)), uintptr(unsafe.Pointer(&free)))
	if ok == 0 {
		return peError("espaço scratch não identificado", e)
	}
	if available < uint64(required) {
		return errors.New("scratch sem espaço para mídia + 2 GiB de trabalho")
	}
	return nil
}
func (nativeWinPE) Letters() (byte, byte, error) {
	mask, err := peDriveMask()
	if err != nil {
		return 0, 0, err
	}
	free := []byte{}
	for letter := byte('Z'); letter >= 'D'; letter-- {
		if letter != 'X' && mask&(1<<uint(letter-'A')) == 0 {
			free = append(free, letter)
			if len(free) == 2 {
				return free[0], free[1], nil
			}
		}
	}
	return 0, 0, errors.New("não há duas letras de volume livres")
}
func (nativeWinPE) Tools(firmware, bootsect string) (map[string]string, error) {
	root, err := peWindowsDir()
	if err != nil {
		return nil, err
	}
	tools := map[string]string{}
	for _, name := range []string{"diskpart", "dism", "bcdboot", "wpeutil"} {
		path := filepath.Join(root, "System32", name+".exe")
		if err = peRegular(path); err != nil {
			return nil, fmt.Errorf("ferramenta %s ausente/inválida: %w", name, err)
		}
		tools[name] = path
	}
	if firmware == "MBR_BIOS" {
		if bootsect == "" {
			return nil, errors.New("BIOS exige --bootsect com bootsect.exe de mídia Windows legítima")
		}
		if err = peRegular(bootsect); err != nil {
			return nil, err
		}
		tools["bootsect"] = bootsect
	}
	return tools, nil
}

type peBoundedWriter struct {
	output    io.Writer
	remaining int64
}

func (w *peBoundedWriter) Write(data []byte) (int, error) {
	n := len(data)
	if w.remaining > 0 {
		part := data
		if int64(len(part)) > w.remaining {
			part = part[:w.remaining]
		}
		_, e := w.output.Write(part)
		w.remaining -= int64(len(part))
		if e != nil {
			return n, e
		}
	}
	return n, nil
}
func (nativeWinPE) Run(ctx context.Context, path string, args []string, output io.Writer) error {
	if !peMiniNT() {
		return errors.New("comando de implantação recusado fora de WinPE")
	}
	if err := peRegular(path); err != nil {
		return err
	}
	cmd := exec.CommandContext(ctx, path, args...)
	writer := &peBoundedWriter{output: output, remaining: 2 * 1024 * 1024}
	cmd.Stdout = writer
	cmd.Stderr = writer
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("%s: %w", filepath.Base(path), err)
	}
	return nil
}
func (nativeWinPE) VerifyVolumes(number int, firmware string, system, windows byte) error {
	h, err := peOpen(fmt.Sprintf(`\\.\PhysicalDrive%d`, number))
	if err != nil {
		return err
	}
	defer syscall.CloseHandle(h)
	data, err := peIO(h, 0x70050, nil, 48+256*144)
	if err != nil {
		return err
	}
	parts, err := decodeWinPELayout(data, firmware)
	if err != nil {
		return err
	}
	for i, letter := range []byte{system, windows} {
		root := fmt.Sprintf("%c:\\", letter)
		if err = peNoReparse(root); err != nil {
			return err
		}
		volume, e := peOpen(fmt.Sprintf(`\\.\%c:`, letter))
		if e != nil {
			return e
		}
		extents, e := peIO(volume, 0x560000, nil, 8+24*256)
		syscall.CloseHandle(volume)
		if e != nil {
			return e
		}
		numbers, e := decodeWinPEExtents(extents)
		part := parts[0]
		if i == 1 {
			part = parts[len(parts)-1]
		}
		if e != nil || len(numbers) != 1 || numbers[0] != number || len(extents) < 32 || int64(binary.LittleEndian.Uint64(extents[16:])) != part.start || int64(binary.LittleEndian.Uint64(extents[24:])) != part.length {
			return errors.New("letra/limites do volume divergem das partições do destino confirmado")
		}
		fs := make([]uint16, 32)
		ok, _, e := peKernel.NewProc("GetVolumeInformationW").Call(uintptr(unsafe.Pointer(peUTF(root))), 0, 0, 0, 0, 0, uintptr(unsafe.Pointer(&fs[0])), uintptr(len(fs)))
		expected := "NTFS"
		if i == 0 && firmware == "GPT_UEFI" {
			expected = "FAT32"
		}
		if ok == 0 {
			return peError("filesystem não identificado", e)
		}
		if syscall.UTF16ToString(fs) != expected {
			return errors.New("filesystem do destino diverge do plano")
		}
	}
	return nil
}
func (nativeWinPE) VerifyApplied(windows byte) error {
	for _, name := range []string{`Windows\System32\config\SYSTEM`, `Windows\System32\ntoskrnl.exe`} {
		if err := peRegular(fmt.Sprintf("%c:\\%s", windows, name)); err != nil {
			return errors.New("imagem aplicada não contém os componentes Windows esperados")
		}
	}
	return nil
}
func (nativeWinPE) VerifyBoot(system byte, firmware string) error {
	files := []string{`EFI\Microsoft\Boot\BCD`, `EFI\Microsoft\Boot\bootmgfw.efi`}
	if firmware == "MBR_BIOS" {
		files = []string{`Boot\BCD`, `bootmgr`}
	}
	for _, file := range files {
		if err := peRegular(fmt.Sprintf("%c:\\%s", system, file)); err != nil {
			return errors.New("arquivos de boot esperados ausentes/inválidos após BCDBoot")
		}
	}
	return nil
}
