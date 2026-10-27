package agent

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

func peFixtureMedia(data []byte) DeploymentMedia {
	sum := sha256.Sum256(data)
	m := DeploymentMedia{ID: "synthetic-media", Name: "SYNTHETIC fixture - not a Windows image", FileName: "install.esd", SizeBytes: int64(len(data)), SHA256: hex.EncodeToString(sum[:]), State: "AVAILABLE", Images: []WindowsImage{{Index: 1, Architecture: "AMD64", MinimumSizeBytes: 16 * 1024 * 1024 * 1024}}}
	for offset := 0; offset < len(data); offset += ChunkSize {
		end := offset + ChunkSize
		if end > len(data) {
			end = len(data)
		}
		h := sha256.Sum256(data[offset:end])
		m.Chunks = append(m.Chunks, ImageChunk{Sequence: len(m.Chunks), OffsetBytes: int64(offset), SizeBytes: end - offset, SHA256: hex.EncodeToString(h[:])})
	}
	return m
}
func peFixtureDiskPlan() (Disk, Plan) {
	d := Disk{ID: "fixture", Path: `\\.\PhysicalDrive7`, Model: "SYNTHETIC TEST DEVICE", Serial: "FIXTURE-DO-NOT-OPEN", SizeBytes: 64 * 1024 * 1024 * 1024, LogicalSectorBytes: 512, PhysicalSectorBytes: 4096, Health: "SYNTHETIC_FIXTURE", Mounted: true}
	d.Fingerprint = Fingerprint(d)
	p := Plan{ID: "synthetic-job", Kind: "DEPLOY_WINDOWS", DeviceID: "fixture-device", DiskFingerprint: d.Fingerprint, DiskPath: d.Path, DiskSizeBytes: d.SizeBytes, LogicalSectorBytes: 512, Challenge: "APAGAR SYNTHETIC", Consent: true, ExpiresAtUTC: time.Now().Add(time.Hour), Deployment: &DeploymentOptions{MediaID: "synthetic-media", ImageIndex: 1, Firmware: "GPT_UEFI"}}
	return d, p
}
func TestWinPEOutsidePEIsBlocked(t *testing.T) {
	if _, err := newWinPEPlatform().Check(context.Background()); err == nil {
		t.Skip("test runner is WinPE: fixture does not enumerate physical disks")
	}
	if err := RunWinPE(context.Background(), WinPEOptions{StateDir: `X:\never-created-by-test`}); err == nil {
		t.Fatal("agente aceito fora de WinPE")
	}
}
func TestWinPECommandsAreTyped(t *testing.T) {
	for _, firmware := range []string{"GPT_UEFI", "MBR_BIOS"} {
		script, err := BuildWinPEDiskPart(7, firmware, 'S', 'W')
		if err != nil {
			t.Fatal(err)
		}
		if !strings.HasPrefix(script, "select disk 7\r\n") || !strings.Contains(script, "clean\r\n") || strings.Contains(script, "noerr") {
			t.Fatal(script)
		}
		commands, err := BuildWinPECommands(`E:\scratch\install.esd`, `E:\scratch`, 1, firmware, 'S', 'W')
		if err != nil {
			t.Fatal(err)
		}
		if commands[0].Tool != "dism" || commands[1].Tool != "bcdboot" {
			t.Fatal(commands)
		}
		if firmware == "MBR_BIOS" && commands[2].Tool != "bootsect" {
			t.Fatal("BIOS sem bootsect")
		}
	}
	for _, path := range []string{`\\.\PhysicalDrive01`, `\\.\PhysicalDrive-1`, `\\.\PhysicalDrive7\anything`, `\\.\PhysicalDrive7` + "\nclean", `C:\`} {
		if _, err := WinPEDiskNumber(path); err == nil {
			t.Fatalf("path físico inválido aceito %q", path)
		}
	}
	for _, path := range []string{`C:\a\..\b`, `C:\a\file:stream`, `C:\NUL.txt`, `C:\a.\b`, `C:\a /b`, `\\server\share`, `C:\a` + "\n", `C:\COM1`} {
		if safeWinPEPath(path) {
			t.Fatalf("path inválido aceito %q", path)
		}
	}
	for _, path := range []string{`X:\SLT-State`, `E:\SLT scratch\install.esd`, `E:\`} {
		if !safeWinPEPath(path) {
			t.Fatalf("path válido rejeitado %q", path)
		}
	}
	if _, err := BuildWinPEDiskPart(7, "GPT_UEFI", 'X', 'W'); err == nil {
		t.Fatal("volume RAM aceito")
	}
	if _, err := BuildWinPECommands(`E:\a.esd`, `E:\scratch`, 0, "GPT_UEFI", 'S', 'W'); err == nil {
		t.Fatal("índice zero aceito")
	}
}
func TestWinPEStorageDescriptorBounds(t *testing.T) {
	for _, device := range []string{`\Device\Ramdisk{synthetic}`, `\Device\CdRom0`, `\Device\CdRom7`} {
		if !nonPhysicalWinPEVolume(device) {
			t.Fatalf("read-only optical/RAM volume rejected: %s", device)
		}
	}
	for _, device := range []string{`\Device\HarddiskVolume3`, `\Device\Harddisk0\DR0`, `\Device\CdRomFake`, `\Device\CdRom0\anything`} {
		if nonPhysicalWinPEVolume(device) {
			t.Fatalf("physical/unknown mapping skipped: %s", device)
		}
	}
	data := make([]byte, 128)
	binary.LittleEndian.PutUint32(data[4:], 128)
	binary.LittleEndian.PutUint32(data[12:], 40)
	binary.LittleEndian.PutUint32(data[16:], 48)
	binary.LittleEndian.PutUint32(data[24:], 80)
	copy(data[40:], "Vendor\x00")
	copy(data[48:], "SYNTHETIC Disk\x00")
	copy(data[80:], "FIXTURE-ONLY\x00")
	model, serial, err := decodeWinPEDescriptor(data)
	if err != nil || model != "Vendor SYNTHETIC Disk" || serial != "FIXTURE-ONLY" {
		t.Fatalf("%s %s %v", model, serial, err)
	}
	binary.LittleEndian.PutUint32(data[24:], 129)
	if _, _, err = decodeWinPEDescriptor(data); err == nil {
		t.Fatal("offset fora do buffer aceito")
	}
	binary.LittleEndian.PutUint32(data[24:], 0)
	if _, _, err = decodeWinPEDescriptor(data); err == nil {
		t.Fatal("serial vazio aceito")
	}
	extents := make([]byte, 32)
	binary.LittleEndian.PutUint32(extents, 1)
	binary.LittleEndian.PutUint32(extents[8:], 7)
	if ns, e := decodeWinPEExtents(extents); e != nil || len(ns) != 1 || ns[0] != 7 {
		t.Fatal(ns, e)
	}
	binary.LittleEndian.PutUint32(extents, 256)
	if _, e := decodeWinPEExtents(extents); e == nil {
		t.Fatal("extents truncados aceitos")
	}
}
func peFixtureLayout(firmware string) []byte {
	count, style := 3, uint32(1)
	if firmware == "MBR_BIOS" {
		count = 2
		style = 0
	}
	data := make([]byte, 48+count*144)
	binary.LittleEndian.PutUint32(data, style)
	binary.LittleEndian.PutUint32(data[4:], uint32(count))
	start := int64(1024 * 1024)
	kinds := [][]byte{{0x28, 0x73, 0x2a, 0xc1, 0x1f, 0xf8, 0xd2, 0x11, 0xba, 0x4b, 0, 0xa0, 0xc9, 0x3e, 0xc9, 0x3b}, {0x16, 0xe3, 0xc9, 0xe3, 0x5c, 0x0b, 0xb8, 0x4d, 0x81, 0x7d, 0xf9, 0x2d, 0xf0, 2, 0x15, 0xae}, {0xa2, 0xa0, 0xd0, 0xeb, 0xe5, 0xb9, 0x33, 0x44, 0x87, 0xc0, 0x68, 0xb6, 0xb7, 0x26, 0x99, 0xc7}}
	for i := 0; i < count; i++ {
		entry := data[48+i*144:]
		length := int64(32 * 1024 * 1024 * 1024)
		if i == 0 {
			length = 260 * 1024 * 1024
			if style == 0 {
				length = 550 * 1024 * 1024
			}
		} else if style == 1 && i == 1 {
			length = 16 * 1024 * 1024
		}
		binary.LittleEndian.PutUint32(entry, style)
		binary.LittleEndian.PutUint64(entry[8:], uint64(start))
		binary.LittleEndian.PutUint64(entry[16:], uint64(length))
		binary.LittleEndian.PutUint32(entry[24:], uint32(i+1))
		if style == 1 {
			copy(entry[32:], kinds[i])
		} else {
			entry[32] = 7
			if i == 0 {
				entry[33] = 1
			}
		}
		start += length
	}
	return data
}
func TestWinPELayoutMatchesBeforeApply(t *testing.T) {
	for _, fw := range []string{"GPT_UEFI", "MBR_BIOS"} {
		data := peFixtureLayout(fw)
		parts, err := decodeWinPELayout(data, fw)
		if err != nil || len(parts) < 2 {
			t.Fatal(err)
		}
		data[48+32] ^= 1
		if _, err = decodeWinPELayout(data, fw); err == nil {
			t.Fatal("tipo incorreto aceito")
		}
	}
	data := peFixtureLayout("GPT_UEFI")
	if _, err := decodeWinPELayout(data, "MBR_BIOS"); err == nil {
		t.Fatal("tabela incorreta aceita")
	}
	if _, err := decodeWinPELayout(data[:len(data)-1], "GPT_UEFI"); err == nil {
		t.Fatal("layout truncado aceito")
	}
}
func TestWinPEMediaAndTargetPreflight(t *testing.T) {
	d, p := peFixtureDiskPlan()
	data := make([]byte, 208)
	copy(data, "MSWIM\x00\x00\x00")
	m := peFixtureMedia(data)
	if err := validateWinPEMedia(m, p); err != nil {
		t.Fatal(err)
	}
	if _, err := validateWinPETarget(p, []Disk{d}); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*Disk){func(d *Disk) { d.IsSystem = true }, func(d *Disk) { d.IsReadOnly = true }, func(d *Disk) { d.Serial = "" }, func(d *Disk) { d.SizeBytes++ }, func(d *Disk) { d.Path = `\\.\PhysicalDrive8` }} {
		bad := d
		mutate(&bad)
		if _, err := validateWinPETarget(p, []Disk{bad}); err == nil {
			t.Fatal("destino alterado/protegido aceito")
		}
	}
	if _, err := validateWinPETarget(p, []Disk{d, d}); err == nil {
		t.Fatal("destino ambíguo aceito")
	}
	for _, mutate := range []func(*DeploymentMedia){func(m *DeploymentMedia) { m.State = "IMPORTING" }, func(m *DeploymentMedia) { m.ID = "other" }, func(m *DeploymentMedia) { m.SHA256 = "bad" }, func(m *DeploymentMedia) { m.SizeBytes++ }, func(m *DeploymentMedia) {
		m.Images = []WindowsImage{{Index: 1, Architecture: "ARM64", MinimumSizeBytes: 1}}
	}, func(m *DeploymentMedia) { m.FileName = "install.iso" }} {
		bad := m
		mutate(&bad)
		if err := validateWinPEMedia(bad, p); err == nil {
			t.Fatal("mídia incompatível aceita")
		}
	}
}
func TestWinPEPersistedMediaReadback(t *testing.T) {
	data := make([]byte, ChunkSize+208)
	copy(data, "MSWIM\x00\x00\x00")
	m := peFixtureMedia(data)
	if err := verifyWinPEMediaFile(context.Background(), bytes.NewReader(data), m); err != nil {
		t.Fatal(err)
	}
	data[ChunkSize+1] ^= 1
	if err := verifyWinPEMediaFile(context.Background(), bytes.NewReader(data), m); err == nil {
		t.Fatal("corrupção persistida aceita")
	}
	data[ChunkSize+1] ^= 1
	if err := verifyWinPEMediaFile(context.Background(), bytes.NewReader(append(data, 1)), m); err == nil {
		t.Fatal("bytes extras aceitos")
	}
	if err := verifyWinPEMediaFile(context.Background(), bytes.NewReader(data[:len(data)-1]), m); err == nil {
		t.Fatal("truncamento aceito")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := verifyWinPEMediaFile(ctx, bytes.NewReader(data), m); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}

type peFakePlatform struct {
	disks          []Disk
	scans          int
	changeOnSecond bool
	commands       []string
	fail           string
	block          bool
	finished       chan struct{}
	mu             sync.Mutex
}

func (f *peFakePlatform) Check(context.Context) (string, error) { return "GPT_UEFI", nil }
func (f *peFakePlatform) PrivateState(string) error             { return nil }
func (f *peFakePlatform) Disks(context.Context, []string) ([]Disk, error) {
	f.scans++
	if f.changeOnSecond && f.scans > 1 {
		return nil, nil
	}
	return f.disks, nil
}
func (f *peFakePlatform) Scratch(string, int64, int) error { return nil }
func (f *peFakePlatform) Letters() (byte, byte, error)     { return 'S', 'W', nil }
func (f *peFakePlatform) Tools(string, string) (map[string]string, error) {
	return map[string]string{"diskpart": "synthetic-diskpart", "dism": "synthetic-dism", "bcdboot": "synthetic-bcdboot"}, nil
}
func (f *peFakePlatform) Run(ctx context.Context, path string, args []string, out io.Writer) error {
	f.mu.Lock()
	f.commands = append(f.commands, path)
	f.mu.Unlock()
	if f.block {
		<-ctx.Done()
		close(f.finished)
		return ctx.Err()
	}
	if f.fail == path {
		return errors.New("synthetic failure")
	}
	return nil
}
func (f *peFakePlatform) VerifyVolumes(int, string, byte, byte) error { return nil }
func (f *peFakePlatform) VerifyApplied(byte) error                    { return nil }
func (f *peFakePlatform) VerifyBoot(byte, string) error {
	if f.fail == "synthetic-boot-verification" {
		return errors.New("synthetic missing boot files")
	}
	return nil
}
func peFakeServer(t *testing.T, m DeploymentMedia, data []byte, corrupt bool) *Client {
	t.Helper()
	s := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case strings.HasSuffix(r.URL.Path, "/media"):
			json.NewEncoder(w).Encode(m)
		case strings.Contains(r.URL.Path, "/media/chunks/"):
			chunk := append([]byte{}, data...)
			if corrupt {
				chunk[20] ^= 1
			}
			w.Write(chunk)
		default:
			io.WriteString(w, "{}")
		}
	}))
	t.Cleanup(s.Close)
	return &Client{base: s.URL, HTTP: s.Client()}
}
func TestWinPECommandCancellationWaitsForProcess(t *testing.T) {
	f := &peFakePlatform{block: true, finished: make(chan struct{})}
	m := peFixtureMedia(make([]byte, 208))
	r := winPERunner{platform: f, client: peFakeServer(t, m, nil, false), options: WinPEOptions{Output: io.Discard}}
	ctx, cancel := context.WithCancel(context.Background())
	go func() { time.Sleep(20 * time.Millisecond); cancel() }()
	err := r.command(ctx, Plan{ID: "fixture"}, "FIXTURE", "synthetic", nil)
	if !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	select {
	case <-f.finished:
	default:
		t.Fatal("retornou antes de encerrar processo")
	}
}
func TestWinPEDeploymentSyntheticStages(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("staging uses explicit Windows paths; no physical disk is accessed")
	}
	for _, scenario := range []string{"success", "wrong-confirmation", "corrupt-download", "changed-disk", "dism-failure", "boot-files-missing"} {
		t.Run(scenario, func(t *testing.T) {
			d, p := peFixtureDiskPlan()
			data := make([]byte, 208)
			copy(data, "MSWIM\x00\x00\x00")
			m := peFixtureMedia(data)
			f := &peFakePlatform{disks: []Disk{d}, changeOnSecond: scenario == "changed-disk"}
			if scenario == "dism-failure" {
				f.fail = "synthetic-dism"
			}
			if scenario == "boot-files-missing" {
				f.fail = "synthetic-boot-verification"
			}
			confirm := p.Challenge + "\n"
			if scenario == "wrong-confirmation" {
				confirm = "wrong\n"
			}
			dir := t.TempDir()
			r := winPERunner{platform: f, client: peFakeServer(t, m, data, scenario == "corrupt-download"), options: WinPEOptions{AllowDeployment: true, ScratchDir: dir, Input: strings.NewReader(confirm), Output: io.Discard}}
			details, err := r.deploy(context.Background(), p)
			if scenario == "success" {
				if err != nil {
					t.Fatal(err)
				}
				if len(f.commands) != 3 || !strings.Contains(details, `"bootVerified":false`) {
					t.Fatal(f.commands, details)
				}
				if _, e := os.Stat(filepath.Join(dir, "slt-deploy-"+p.ID, "install.esd")); e != nil {
					t.Fatal(e)
				}
			} else {
				if err == nil {
					t.Fatal("cenário inseguro aceito")
				}
				if scenario != "dism-failure" && scenario != "boot-files-missing" && len(f.commands) != 0 {
					t.Fatal("comando executado antes das barreiras", f.commands)
				}
				if len(f.commands) > 0 && !strings.Contains(details, `"partialDiskPossible":true`) {
					t.Fatal("falha de escrita sem relatório parcial", details)
				}
			}
		})
	}
}
