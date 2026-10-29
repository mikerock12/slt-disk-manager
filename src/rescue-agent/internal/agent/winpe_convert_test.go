package agent

import (
	"encoding/binary"
	"encoding/xml"
	"strings"
	"testing"
)

func TestUnattendSkipsOOBEWithLocalizedAdministrators(t *testing.T) {
	account := &DeploymentAccount{ComputerName: "CLIENTE2", UserName: "tecnico", Password: "Abc23456defGH", Locale: "pt-BR", TimeZone: "E. South America Standard Time"}
	text, err := BuildUnattend(account)
	if err != nil {
		t.Fatal(err)
	}
	var parsed struct{ XMLName xml.Name }
	if err = xml.Unmarshal([]byte(text), &parsed); err != nil || parsed.XMLName.Local != "unattend" {
		t.Fatalf("XML inválido: %v", err)
	}
	for _, expected := range []string{"<ComputerName>CLIENTE2</ComputerName>", "<Group>Administradores</Group>", "powercfg.exe /hibernate off", "<HideOnlineAccountScreens>true</HideOnlineAccountScreens>", "<InputLocale>0416:00010416</InputLocale>", "E. South America Standard Time"} {
		if !strings.Contains(text, expected) {
			t.Fatalf("unattend sem %q", expected)
		}
	}
	// Language goes to specialize: applied in oobeSystem it restarted OOBE in the lab, which then
	// looped on the restart-recovery page. Machine and user OOBE are skipped outright.
	oobe := strings.Index(text, `pass="oobeSystem"`)
	if intl := strings.Index(text, "Microsoft-Windows-International-Core"); intl < 0 || intl > oobe || strings.Count(text, "International-Core") != 1 {
		t.Fatal("International-Core deve ficar só no specialize")
	}
	if !strings.Contains(text[oobe:], "<SkipMachineOOBE>true</SkipMachineOOBE>") || !strings.Contains(text[oobe:], "<SkipUserOOBE>true</SkipUserOOBE>") {
		t.Fatal("OOBE de máquina e de usuário devem ser pulados")
	}
	for _, bad := range []*DeploymentAccount{
		{ComputerName: "CLIENTE2", UserName: "tecnico", Password: "short", Locale: "pt-BR", TimeZone: "E. South America Standard Time"},
		{ComputerName: "CLIENTE2", UserName: "tec<nico>", Password: "Abc23456defGH", Locale: "pt-BR", TimeZone: "E. South America Standard Time"},
		{ComputerName: "CLIENTE2", UserName: "tecnico", Password: "Abc23456def&GH", Locale: "pt-BR", TimeZone: "E. South America Standard Time"},
		{ComputerName: "CLIENTE2", UserName: "tecnico", Password: "Abc23456defGH", Locale: "fr-FR", TimeZone: "E. South America Standard Time"},
	} {
		if _, err = BuildUnattend(bad); err == nil {
			t.Fatalf("conta insegura aceita: %+v", bad)
		}
	}
}

func gptEntry(kind []byte, start, length int64) []byte {
	entry := make([]byte, 144)
	binary.LittleEndian.PutUint32(entry, 1)
	binary.LittleEndian.PutUint64(entry[8:], uint64(start))
	binary.LittleEndian.PutUint64(entry[16:], uint64(length))
	copy(entry[32:48], kind)
	return entry
}

func TestConvertedLayoutRequiresGPTWithESPAndWindows(t *testing.T) {
	efi := []byte{0x28, 0x73, 0x2a, 0xc1, 0x1f, 0xf8, 0xd2, 0x11, 0xba, 0x4b, 0x00, 0xa0, 0xc9, 0x3e, 0xc9, 0x3b}
	basic := []byte{0xa2, 0xa0, 0xd0, 0xeb, 0xe5, 0xb9, 0x33, 0x44, 0x87, 0xc0, 0x68, 0xb6, 0xb7, 0x26, 0x99, 0xc7}
	const mib = int64(1024 * 1024)
	layout := func(style uint32, entries ...[]byte) []byte {
		data := make([]byte, 48)
		binary.LittleEndian.PutUint32(data, style)
		binary.LittleEndian.PutUint32(data[4:], uint32(len(entries)))
		for _, e := range entries {
			data = append(data, e...)
		}
		return data
	}
	good := layout(1, gptEntry(basic, mib, 550*mib), gptEntry(basic, 551*mib, 60*1024*mib), gptEntry(efi, 61*1024*mib, 100*mib))
	if err := checkConvertedLayout(good); err != nil {
		t.Fatal(err)
	}
	if err := checkConvertedLayout(layout(0, gptEntry(basic, mib, 60*1024*mib), gptEntry(efi, 61*1024*mib, 100*mib))); err == nil {
		t.Fatal("MBR aceito como convertido")
	}
	if err := checkConvertedLayout(layout(1, gptEntry(basic, mib, 550*mib), gptEntry(basic, 551*mib, 60*1024*mib))); err == nil {
		t.Fatal("GPT sem ESP aceita")
	}
}
