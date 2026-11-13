//go:build !windows

package agent

import (
	"context"
	"errors"
)

type unsupportedKamikaze struct{ unsupportedWinPE }

func newKamikazePlatform() kamikazePlatform { return unsupportedKamikaze{} }

func ListLocalDisks() ([]LocalDisk, error) {
	return nil, errors.New("inventário kamikaze disponível só no Windows")
}
func (unsupportedKamikaze) LocalDisks(context.Context) ([]LocalDisk, error) {
	return nil, errors.New("WinPE necessário")
}
func (unsupportedKamikaze) Roots() []string { return nil }
func (unsupportedKamikaze) VolumeDisks(string) ([]int, error) {
	return nil, errors.New("WinPE necessário")
}
func (unsupportedKamikaze) OpenDisk(int, bool) (rawDisk, error) {
	return nil, errors.New("WinPE necessário")
}
func (unsupportedKamikaze) SetOffline(context.Context, int, bool) error {
	return errors.New("WinPE necessário")
}
func (unsupportedKamikaze) Reboot(context.Context) error { return errors.New("WinPE necessário") }

func kamikazeBootsect() string { return "" }
