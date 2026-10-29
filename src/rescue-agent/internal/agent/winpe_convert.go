package agent

import (
	"bufio"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
)

var unattendLocales = map[string]struct{ input, administrators string }{
	"pt-BR": {"0416:00010416", "Administradores"},
	"en-US": {"0409:00000409", "Administrators"},
	"es-ES": {"040a:0000040a", "Administradores"},
}

func xmlText(value string) string {
	var b bytes.Buffer
	_ = xml.EscapeText(&b, []byte(value))
	return b.String()
}

// BuildUnattend skips OOBE, creates the initial local administrator and disables
// hibernation/fast startup so the NTFS volume stays clean for later cloning.
func BuildUnattend(a *DeploymentAccount) (string, error) {
	if err := validateAccount(a, false); err != nil || a == nil {
		return "", errors.New("conta Windows inválida para unattend")
	}
	locale := unattendLocales[a.Locale]
	component := func(name string) string {
		return `<component name="` + name + `" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">`
	}
	return `<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
 <settings pass="specialize">
  ` + component("Microsoft-Windows-International-Core") + `
   <InputLocale>` + locale.input + `</InputLocale>
   <SystemLocale>` + a.Locale + `</SystemLocale>
   <UILanguage>` + a.Locale + `</UILanguage>
   <UserLocale>` + a.Locale + `</UserLocale>
  </component>
  ` + component("Microsoft-Windows-Shell-Setup") + `
   <ComputerName>` + xmlText(a.ComputerName) + `</ComputerName>
   <TimeZone>` + xmlText(a.TimeZone) + `</TimeZone>
  </component>
  ` + component("Microsoft-Windows-Deployment") + `
   <RunSynchronous>
    <RunSynchronousCommand wcm:action="add"><Order>1</Order><Description>SLT sem hibernacao</Description><Path>powercfg.exe /hibernate off</Path></RunSynchronousCommand>
   </RunSynchronous>
  </component>
 </settings>
 <settings pass="oobeSystem">
  ` + component("Microsoft-Windows-Shell-Setup") + `
   <OOBE>
    <SkipMachineOOBE>true</SkipMachineOOBE>
    <SkipUserOOBE>true</SkipUserOOBE>
    <HideEULAPage>true</HideEULAPage>
    <HideLocalAccountScreen>true</HideLocalAccountScreen>
    <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
    <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
    <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
    <ProtectYourPC>3</ProtectYourPC>
   </OOBE>
   <UserAccounts>
    <LocalAccounts>
     <LocalAccount wcm:action="add">
      <Name>` + xmlText(a.UserName) + `</Name>
      <DisplayName>` + xmlText(a.UserName) + `</DisplayName>
      <Group>` + locale.administrators + `</Group>
      <Password><Value>` + xmlText(a.Password) + `</Value><PlainText>true</PlainText></Password>
     </LocalAccount>
    </LocalAccounts>
   </UserAccounts>
  </component>
 </settings>
</unattend>
`, nil
}

// checkConvertedLayout accepts the GPT produced by MBR2GPT: an EFI system partition and
// at least one basic data partition of 1 GiB or more, all starting MiB-aligned.
func checkConvertedLayout(data []byte) error {
	if len(data) < 48 {
		return errors.New("layout Windows truncado")
	}
	style, count := binary.LittleEndian.Uint32(data), int(binary.LittleEndian.Uint32(data[4:]))
	if style != 1 || count < 2 || count > 128 || 48+count*144 > len(data) {
		return errors.New("disco não ficou com tabela GPT")
	}
	efi := []byte{0x28, 0x73, 0x2a, 0xc1, 0x1f, 0xf8, 0xd2, 0x11, 0xba, 0x4b, 0x00, 0xa0, 0xc9, 0x3e, 0xc9, 0x3b}
	basic := []byte{0xa2, 0xa0, 0xd0, 0xeb, 0xe5, 0xb9, 0x33, 0x44, 0x87, 0xc0, 0x68, 0xb6, 0xb7, 0x26, 0x99, 0xc7}
	var esp, windows bool
	for i := 0; i < count; i++ {
		entry := data[48+i*144 : 48+(i+1)*144]
		if binary.LittleEndian.Uint32(entry) != 1 {
			return errors.New("partição sem estilo GPT")
		}
		length := int64(binary.LittleEndian.Uint64(entry[16:]))
		kind := entry[32:48]
		esp = esp || bytes.Equal(kind, efi) && length >= 100*1024*1024
		windows = windows || bytes.Equal(kind, basic) && length >= 1024*1024*1024
	}
	if !esp || !windows {
		return errors.New("GPT convertida sem partição EFI ou sem volume Windows")
	}
	return nil
}

func (r *winPERunner) convert(ctx context.Context, p Plan) (string, error) {
	if p.Kind != "CONVERT_GPT" || !r.options.AllowDeployment || p.Deployment == nil || !p.Consent || strings.TrimSpace(p.Challenge) == "" {
		return "", errors.New("conversão requer --allow-deployment e plano autorizado")
	}
	firmware, err := r.platform.Check(ctx)
	if err != nil {
		return "", err
	}
	if firmware != "GPT_UEFI" {
		return "", errors.New("inicie o WinPE em modo UEFI para converter e dar boot em GPT")
	}
	disks, err := r.platform.Disks(ctx, r.protected)
	if err != nil {
		return "", err
	}
	disk, err := validateWinPETarget(p, disks)
	if err != nil {
		return "", err
	}
	number, _ := WinPEDiskNumber(disk.Path)
	data, _, err := r.client.Request(ctx, "GET", "/agent/v1/jobs/"+p.ID+"/tool", nil, nil)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(data)
	if hex.EncodeToString(sum[:]) != p.Deployment.ToolSHA256 {
		return "", errors.New("MBR2GPT recebido difere do hash assinado no plano")
	}
	dir := filepath.Join(r.options.StateDir, "convert-"+p.ID)
	if err = os.Mkdir(dir, 0700); err != nil {
		return "", err
	}
	if err = r.platform.PrivateState(dir); err != nil {
		return "", err
	}
	tool := filepath.Join(dir, "MBR2GPT.EXE")
	if err = atomicPrivateFile(tool, data); err != nil {
		return "", err
	}
	logs := filepath.Join(dir, "logs")
	if err = os.Mkdir(logs, 0700); err != nil {
		return "", err
	}
	args := func(action string) []string {
		return []string{action, fmt.Sprintf("/disk:%d", number), "/logs:" + logs}
	}
	if err = r.command(ctx, p, "VALIDATING", tool, args("/validate")); err != nil {
		return "", fmt.Errorf("MBR2GPT recusou o disco; nada foi alterado: %w", err)
	}
	if err = r.report(ctx, p, "AWAITING_LOCAL_CONFIRMATION", 0, disk.SizeBytes); err != nil {
		return "", err
	}
	fmt.Fprintf(r.options.Output, "\nCONVERTER disco %s | %s | serial %s | %d bytes para GPT/UEFI.\nConfirme LOCALMENTE digitando %q: ", disk.Path, disk.Model, disk.Serial, disk.SizeBytes, p.Challenge)
	text, err := bufio.NewReader(io.LimitReader(r.options.Input, 4097)).ReadString('\n')
	if err != nil || strings.TrimSpace(text) != p.Challenge {
		return "", errors.New("confirmação local ausente ou incorreta")
	}
	if !p.ExpiresAtUTC.After(time.Now()) {
		return "", errors.New("plano expirou durante a confirmação; replaneje")
	}
	if disks, err = r.platform.Disks(ctx, r.protected); err != nil {
		return "", err
	}
	if _, err = validateWinPETarget(p, disks); err != nil {
		return "", err
	}
	if err = r.command(ctx, p, "CONVERTING", tool, args("/convert")); err != nil {
		return "", fmt.Errorf("MBR2GPT falhou; confira o disco e os logs em %s antes de reiniciar: %w", logs, err)
	}
	layout, err := r.platform.DriveLayout(number)
	if err != nil {
		return "", err
	}
	if err = checkConvertedLayout(layout); err != nil {
		return "", err
	}
	report, _ := json.Marshal(map[string]any{"converted": true, "partitionStyle": "GPT", "firmware": "GPT_UEFI", "bootVerified": false, "logs": logs})
	return string(report), nil
}
