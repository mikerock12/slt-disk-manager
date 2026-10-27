//go:build !linux

package agent

import (
	"errors"
	"os"
)

func openExclusiveDisk(path string, write, fixture bool) (*os.File, error) {
	if !fixture {
		return nil, errors.New("dispositivos físicos exigem Linux")
	}
	flags := os.O_RDONLY
	if write {
		flags = os.O_RDWR
	}
	return os.OpenFile(path, flags, 0)
}

// Other operating systems run fixture tests only; directory fsync is Linux-specific.
func syncDirectory(path string) error { return nil }
