//go:build linux

package agent

import (
	"os"
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

func syncDirectory(path string) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	return f.Sync()
}
