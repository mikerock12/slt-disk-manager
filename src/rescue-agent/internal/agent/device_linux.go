//go:build linux

package agent

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"syscall"
)

func openExclusiveDisk(path string, write, fixture bool) (*os.File, error) {
	flags := os.O_RDONLY
	if write {
		flags = os.O_RDWR
	}
	if !fixture {
		flags |= syscall.O_EXCL
	}
	return os.OpenFile(path, flags, 0)
}

func checkScratchStorage(path string, required int64, diskPath string) error {
	var fs syscall.Statfs_t
	if err := syscall.Statfs(path, &fs); err != nil {
		return err
	}
	if required < 0 || fs.Bsize <= 0 {
		return errors.New("geometria scratch inválida")
	}
	if fs.Type == 0x01021994 || fs.Type == 0x858458f6 || fs.Type == 0x794c7630 {
		return errors.New("scratch em tmpfs/ramfs/overlay não é armazenamento persistente autorizado")
	}
	if fs.Bavail > uint64(^uint64(0))/uint64(fs.Bsize) || uint64(required) > fs.Bavail*uint64(fs.Bsize) {
		return errors.New("espaço scratch insuficiente (origem + cópia destino + partição temporária + margem)")
	}
	if diskPath != "" && strings.HasPrefix(diskPath, "/dev/") {
		var target, scratch syscall.Stat_t
		if err := syscall.Stat(diskPath, &target); err != nil {
			return err
		}
		if err := syscall.Stat(path, &scratch); err != nil {
			return err
		}
		major := func(dev uint64) uint64 { return (dev>>8)&0xfff | (dev>>32)&0xfffff000 }
		minor := func(dev uint64) uint64 { return dev&0xff | (dev>>12)&0xffffff00 }
		targetSys, err := filepath.EvalSymlinks(fmt.Sprintf("/sys/dev/block/%d:%d", major(uint64(target.Rdev)), minor(uint64(target.Rdev))))
		if err != nil {
			return err
		}
		scratchSys, err := filepath.EvalSymlinks(fmt.Sprintf("/sys/dev/block/%d:%d", major(uint64(scratch.Dev)), minor(uint64(scratch.Dev))))
		if err != nil {
			return errors.New("não foi possível comprovar que scratch está fora do disco destino")
		}
		if scratchSys == targetSys || strings.HasPrefix(scratchSys, targetSys+"/") {
			return errors.New("scratch está no disco destino")
		}
	}
	return nil
}

func syncDirectory(path string) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	return f.Sync()
}
