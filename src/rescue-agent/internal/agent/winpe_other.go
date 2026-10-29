//go:build !windows

package agent

import (
	"context"
	"errors"
	"io"
)

type unsupportedWinPE struct{}

func newWinPEPlatform() winPEPlatform { return unsupportedWinPE{} }
func (unsupportedWinPE) Check(context.Context) (string, error) {
	return "", errors.New("implantação Windows exige WinPE AMD64; este sistema não é WinPE")
}
func (unsupportedWinPE) PrivateState(string) error { return errors.New("WinPE necessário") }
func (unsupportedWinPE) Disks(context.Context, []string) ([]Disk, error) {
	return nil, errors.New("WinPE necessário")
}
func (unsupportedWinPE) Scratch(string, int64, int) error { return errors.New("WinPE necessário") }
func (unsupportedWinPE) Letters() (byte, byte, error)     { return 0, 0, errors.New("WinPE necessário") }
func (unsupportedWinPE) Tools(string, string) (map[string]string, error) {
	return nil, errors.New("WinPE necessário")
}
func (unsupportedWinPE) Run(context.Context, string, []string, io.Writer) error {
	return errors.New("WinPE necessário")
}
func (unsupportedWinPE) VerifyVolumes(int, string, byte, byte) error {
	return errors.New("WinPE necessário")
}
func (unsupportedWinPE) VerifyApplied(byte) error        { return errors.New("WinPE necessário") }
func (unsupportedWinPE) VerifyBoot(byte, string) error   { return errors.New("WinPE necessário") }
func (unsupportedWinPE) FlushVolume(byte) error          { return errors.New("WinPE necessário") }
func (unsupportedWinPE) DriveLayout(int) ([]byte, error) { return nil, errors.New("WinPE necessário") }
