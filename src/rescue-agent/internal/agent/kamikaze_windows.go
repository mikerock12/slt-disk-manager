//go:build windows

package agent

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"syscall"
)

type nativeKamikaze struct{ nativeWinPE }

func newKamikazePlatform() kamikazePlatform { return nativeKamikaze{} }

// ListLocalDisks enumerates \\.\PhysicalDrive0..255 with the same IOCTLs used inside WinPE, so
// the Controller (full Windows) and the kamikaze console compute the same disk identity.
func ListLocalDisks() ([]LocalDisk, error) {
	disks := []LocalDisk{}
	for number := 0; number < 256; number++ {
		d, err := peDiskIdentity(number, false)
		if err != nil {
			continue
		}
		disks = append(disks, LocalDisk{Number: number, Identity: DiskIdentity(d.Model, d.Serial, d.SizeBytes, d.LogicalSectorBytes), Model: d.Model, Serial: d.Serial, SizeBytes: d.SizeBytes, LogicalSectorBytes: d.LogicalSectorBytes, Offline: d.Offline, ReadOnly: d.IsReadOnly})
	}
	return disks, nil
}

func (nativeKamikaze) LocalDisks(ctx context.Context) ([]LocalDisk, error) {
	if !peMiniNT() {
		return nil, errors.New("modo kamikaze só grava dentro do WinPE")
	}
	return ListLocalDisks()
}

func (nativeKamikaze) Roots() []string {
	mask, err := peDriveMask()
	if err != nil {
		return nil
	}
	roots := []string{}
	for letter := byte('C'); letter <= 'Z'; letter++ {
		if letter == 'X' || mask&(1<<uint(letter-'A')) == 0 {
			continue
		}
		root := fmt.Sprintf("%c:\\", letter)
		if _, err := os.Stat(root); err == nil {
			roots = append(roots, root)
		}
	}
	return roots
}

func (nativeKamikaze) VolumeDisks(path string) ([]int, error) { return pePathDisks(path) }

func (nativeKamikaze) OpenDisk(number int, write bool) (rawDisk, error) {
	if number < 0 || number > 4095 {
		return nil, errors.New("disco inválido")
	}
	if write && !peMiniNT() {
		return nil, errors.New("gravação bruta recusada fora do WinPE")
	}
	access := uint32(syscall.GENERIC_READ)
	if write {
		access |= syscall.GENERIC_WRITE
	}
	path := fmt.Sprintf(`\\.\PhysicalDrive%d`, number)
	h, err := syscall.CreateFile(peUTF(path), access, syscall.FILE_SHARE_READ|syscall.FILE_SHARE_WRITE, nil, syscall.OPEN_EXISTING, 0, 0)
	if err != nil {
		return nil, peError("abrir "+path, err)
	}
	return os.NewFile(uintptr(h), path), nil
}

// SetOffline takes a disk offline through DiskPart so Windows unmounts its volumes before the
// raw copy (writes to sectors of mounted volumes are refused by Windows).
func (k nativeKamikaze) SetOffline(ctx context.Context, number int, offline bool) error {
	if number < 0 || number > 4095 {
		return errors.New("disco inválido")
	}
	root, err := peWindowsDir()
	if err != nil {
		return err
	}
	verb := "online disk"
	if offline {
		verb = "offline disk"
	}
	disks, err := ListLocalDisks()
	if err != nil {
		return err
	}
	for _, d := range disks {
		if d.Number == number && d.Offline == offline {
			return nil
		}
	}
	script := filepath.Join(os.TempDir(), fmt.Sprintf("slt-kamikaze-%d.txt", number))
	if err = atomicPrivateFile(script, []byte(fmt.Sprintf("select disk %d\r\n%s\r\nexit\r\n", number, verb))); err != nil {
		return err
	}
	return k.Run(ctx, filepath.Join(root, "System32", "diskpart.exe"), []string{"/s", script}, os.Stdout)
}

func (k nativeKamikaze) Reboot(ctx context.Context) error {
	root, err := peWindowsDir()
	if err != nil {
		return err
	}
	return k.Run(ctx, filepath.Join(root, "System32", "wpeutil.exe"), []string{"Reboot"}, os.Stdout)
}

// kamikazeBootsect is the bootsect.exe of the boot image itself (WinRE/WinPE), needed for BIOS.
func kamikazeBootsect() string {
	root, err := peWindowsDir()
	if err != nil {
		return ""
	}
	return filepath.Join(root, "System32", "bootsect.exe")
}
