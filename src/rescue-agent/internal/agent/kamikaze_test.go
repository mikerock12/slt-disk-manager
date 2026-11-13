package agent

import (
	"bytes"
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/binary"
	"encoding/json"
	"encoding/pem"
	"errors"
	"hash/crc32"
	"io"
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Synthetic disks are plain files; nothing here opens a physical drive.
type fakeKamikaze struct {
	fakePEBase
	disks    []LocalDisk
	files    map[int]string
	roots    map[string][]int
	scripts  []string
	offline  map[int]bool
	rebooted bool
}

func (f *fakeKamikaze) Check(context.Context) (string, error) { return "GPT_UEFI", nil }
func (f *fakeKamikaze) Tools(string, string) (map[string]string, error) {
	return map[string]string{"diskpart": "diskpart.exe", "dism": "dism.exe", "bcdboot": "bcdboot.exe", "wpeutil": "wpeutil.exe"}, nil
}
func (f *fakeKamikaze) Run(_ context.Context, path string, args []string, _ io.Writer) error {
	if len(args) == 2 && args[0] == "/s" {
		data, err := os.ReadFile(args[1])
		if err != nil {
			return err
		}
		f.scripts = append(f.scripts, string(data))
	}
	return nil
}
func (f *fakeKamikaze) LocalDisks(context.Context) ([]LocalDisk, error) {
	out := append([]LocalDisk(nil), f.disks...)
	for i := range out {
		out[i].Offline = f.offline[out[i].Number]
	}
	return out, nil
}
func (f *fakeKamikaze) Roots() []string {
	roots := []string{}
	for r := range f.roots {
		roots = append(roots, r)
	}
	return roots
}
func (f *fakeKamikaze) VolumeDisks(path string) ([]int, error) {
	for root, n := range f.roots {
		if strings.HasPrefix(path, root) {
			return n, nil
		}
	}
	return nil, errors.New("volume desconhecido")
}
func (f *fakeKamikaze) OpenDisk(number int, write bool) (rawDisk, error) {
	if write && !f.offline[number] {
		return nil, errors.New("gravação em disco online")
	}
	flag := os.O_RDONLY
	if write {
		flag = os.O_RDWR
	}
	return os.OpenFile(f.files[number], flag, 0)
}
func (f *fakeKamikaze) SetOffline(_ context.Context, number int, offline bool) error {
	f.offline[number] = offline
	return nil
}
func (f *fakeKamikaze) Reboot(context.Context) error { f.rebooted = true; return nil }

func kamikazeCertificate(t *testing.T) (*rsa.PrivateKey, *x509.Certificate, string) {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(7), Subject: pkix.Name{CommonName: "SLT Controller"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour)}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	cert, _ := x509.ParseCertificate(der)
	path := filepath.Join(t.TempDir(), "trust.pem")
	os.WriteFile(path, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), 0600)
	return key, cert, path
}

func signBundle(t *testing.T, key *rsa.PrivateKey, b KamikazeBundle) SignedAssignment {
	payload, _ := json.Marshal(b)
	hash := sha256.Sum256(payload)
	sig, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, hash[:])
	if err != nil {
		t.Fatal(err)
	}
	return SignedAssignment{Payload: payload, Signature: sig}
}

// writeTestGPT writes a protective MBR and both GPT copies with one partition [start,end].
func writeTestGPT(t *testing.T, path string, total int64, start, end uint64) {
	const sector = 512
	f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE, 0600)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err = f.Truncate(total); err != nil {
		t.Fatal(err)
	}
	last := uint64(total/sector - 1)
	mbr := make([]byte, 512)
	mbr[446+4] = 0xEE
	binary.LittleEndian.PutUint32(mbr[446+8:], 1)
	binary.LittleEndian.PutUint32(mbr[446+12:], uint32(last))
	mbr[510], mbr[511] = 0x55, 0xAA
	f.WriteAt(mbr, 0)
	entries := make([]byte, 128*128)
	copy(entries[0:16], bytes.Repeat([]byte{0xAB}, 16))
	copy(entries[16:32], bytes.Repeat([]byte{0xCD}, 16))
	binary.LittleEndian.PutUint64(entries[32:], start)
	binary.LittleEndian.PutUint64(entries[40:], end)
	header := func(current, alternate, array uint64) []byte {
		h := make([]byte, sector)
		copy(h, "EFI PART")
		binary.LittleEndian.PutUint32(h[8:], 0x00010000)
		binary.LittleEndian.PutUint32(h[12:], 92)
		binary.LittleEndian.PutUint64(h[24:], current)
		binary.LittleEndian.PutUint64(h[32:], alternate)
		binary.LittleEndian.PutUint64(h[40:], 34)
		binary.LittleEndian.PutUint64(h[48:], last-33)
		copy(h[56:72], bytes.Repeat([]byte{0x11}, 16))
		binary.LittleEndian.PutUint64(h[72:], array)
		binary.LittleEndian.PutUint32(h[80:], 128)
		binary.LittleEndian.PutUint32(h[84:], 128)
		binary.LittleEndian.PutUint32(h[88:], crc32.ChecksumIEEE(entries))
		binary.LittleEndian.PutUint32(h[16:], crc32.ChecksumIEEE(h[:92]))
		return h
	}
	f.WriteAt(entries, 2*sector)
	f.WriteAt(header(1, last, 2), sector)
	f.WriteAt(entries, int64(last-32)*sector)
	f.WriteAt(header(last, 1, last-32), int64(last)*sector)
	data := make([]byte, int64(end-start+1)*sector)
	rand.Read(data)
	f.WriteAt(data, int64(start)*sector)
}

type kamikazeFixture struct {
	platform *fakeKamikaze
	key      *rsa.PrivateKey
	trust    string
	dir      string
	source   KamikazeDisk
	target   KamikazeDisk
}

func newKamikazeFixture(t *testing.T, sourceBytes, targetBytes int64, partEnd uint64) *kamikazeFixture {
	work := t.TempDir()
	src, dst := filepath.Join(work, "source.img"), filepath.Join(work, "target.img")
	writeTestGPT(t, src, sourceBytes, 2048, partEnd)
	if err := os.WriteFile(dst, bytes.Repeat([]byte{0x5A}, int(targetBytes)), 0600); err != nil {
		t.Fatal(err)
	}
	volume := filepath.Join(work, "volume") + string(os.PathSeparator)
	disk := func(n int, model, serial string, size int64) (LocalDisk, KamikazeDisk) {
		id := DiskIdentity(model, serial, size, 512)
		return LocalDisk{Number: n, Identity: id, Model: model, Serial: serial, SizeBytes: size, LogicalSectorBytes: 512}, KamikazeDisk{Identity: id, Model: model, Serial: serial, SizeBytes: size, LogicalSectorBytes: 512}
	}
	a, sa := disk(0, "SYNTHETIC SOURCE", "SRC-1", sourceBytes)
	b, sb := disk(1, "SYNTHETIC TARGET", "DST-1", targetBytes)
	key, _, trust := kamikazeCertificate(t)
	f := &kamikazeFixture{platform: &fakeKamikaze{disks: []LocalDisk{a, b}, files: map[int]string{0: src, 1: dst}, roots: map[string][]int{volume: {0}}, offline: map[int]bool{}}, key: key, trust: trust, source: sa, target: sb}
	f.dir = filepath.Join(volume, KamikazeDirectory, "sessao-1")
	os.MkdirAll(f.dir, 0700)
	return f
}

func (f *kamikazeFixture) write(t *testing.T, ops ...KamikazeOperation) {
	b := KamikazeBundle{SchemaVersion: 1, SessionID: "sessao-1", ServerName: "SERVIDOR-TESTE", CreatedAtUTC: time.Now().UTC(), ExpiresAtUTC: time.Now().UTC().Add(24 * time.Hour), Operations: ops}
	data, _ := json.Marshal(signBundle(t, f.key, b))
	if err := os.WriteFile(filepath.Join(f.dir, "bundle.json"), data, 0600); err != nil {
		t.Fatal(err)
	}
}

func (f *kamikazeFixture) run(input string) (string, error) {
	var out bytes.Buffer
	err := runKamikaze(context.Background(), f.platform, KamikazeOptions{SessionID: "sessao-1", TrustPath: f.trust, NoReboot: true, Input: strings.NewReader(input), Output: &out})
	return out.String(), err
}

func (f *kamikazeFixture) result(t *testing.T) KamikazeResult {
	var r KamikazeResult
	data, err := os.ReadFile(filepath.Join(f.dir, "result.json"))
	if err != nil {
		t.Fatal(err)
	}
	json.Unmarshal(data, &r)
	return r
}

func TestKamikazeBundleVerification(t *testing.T) {
	key, cert, _ := kamikazeCertificate(t)
	other, _, _ := kamikazeCertificate(t)
	disk := func(model string, size int64) KamikazeDisk {
		return KamikazeDisk{Identity: DiskIdentity(model, "S", size, 512), Model: model, Serial: "S", SizeBytes: size, LogicalSectorBytes: 512}
	}
	a, b := disk("A", 1<<30), disk("B", 2<<30)
	format := &FormatOptions{TableType: "GPT", FileSystem: "NTFS", Label: "DADOS"}
	good := KamikazeBundle{SchemaVersion: 1, SessionID: "s1", ServerName: "SRV", CreatedAtUTC: time.Now(), ExpiresAtUTC: time.Now().Add(24 * time.Hour), Operations: []KamikazeOperation{{Kind: "CLONE_DISK", Source: &a, Target: b, Challenge: "APAGAR 123ABC"}}}
	if _, err := VerifyKamikazeBundle(signBundle(t, key, good), cert, "s1", time.Now()); err != nil {
		t.Fatal(err)
	}
	signed := signBundle(t, key, good)
	signed.Payload = bytes.Replace(signed.Payload, []byte(`"SRV"`), []byte(`"XYZ"`), 1)
	cases := map[string]func() (SignedAssignment, string, time.Time){
		"payload alterado": func() (SignedAssignment, string, time.Time) { return signed, "s1", time.Now() },
		"chave de outro":   func() (SignedAssignment, string, time.Time) { return signBundle(t, other, good), "s1", time.Now() },
		"outra sessão":     func() (SignedAssignment, string, time.Time) { return signBundle(t, key, good), "s2", time.Now() },
		"expirado": func() (SignedAssignment, string, time.Time) {
			return signBundle(t, key, good), "s1", time.Now().Add(25 * time.Hour)
		},
		"criado no futuro": func() (SignedAssignment, string, time.Time) {
			return signBundle(t, key, good), "s1", time.Now().Add(-15 * time.Hour)
		},
		"identidade forjada": func() (SignedAssignment, string, time.Time) {
			x := good
			y := b
			y.Serial = "OUTRO"
			x.Operations = []KamikazeOperation{{Kind: "FORMAT_DISK", Target: y, Format: format, Challenge: "APAGAR 123ABC"}}
			return signBundle(t, key, x), "s1", time.Now()
		},
		"origem igual destino": func() (SignedAssignment, string, time.Time) {
			x := good
			x.Operations = []KamikazeOperation{{Kind: "CLONE_DISK", Source: &b, Target: b, Challenge: "APAGAR 123ABC"}}
			return signBundle(t, key, x), "s1", time.Now()
		},
		"gravado duas vezes": func() (SignedAssignment, string, time.Time) {
			x := good
			x.Operations = []KamikazeOperation{{Kind: "FORMAT_DISK", Target: b, Format: format, Challenge: "APAGAR 123ABC"}, {Kind: "FORMAT_DISK", Target: b, Format: format, Challenge: "APAGAR 123ABC"}}
			return signBundle(t, key, x), "s1", time.Now()
		},
		"destino depois de origem": func() (SignedAssignment, string, time.Time) {
			x := good
			x.Operations = []KamikazeOperation{{Kind: "CLONE_DISK", Source: &a, Target: b, Challenge: "APAGAR 123ABC"}, {Kind: "FORMAT_DISK", Target: a, Format: format, Challenge: "APAGAR 123ABC"}}
			return signBundle(t, key, x), "s1", time.Now()
		},
		"ext4 no WinPE": func() (SignedAssignment, string, time.Time) {
			x := good
			x.Operations = []KamikazeOperation{{Kind: "FORMAT_DISK", Target: b, Format: &FormatOptions{TableType: "GPT", FileSystem: "EXT4"}, Challenge: "APAGAR 123ABC"}}
			return signBundle(t, key, x), "s1", time.Now()
		},
		"mídia fora do volume": func() (SignedAssignment, string, time.Time) {
			x := good
			x.Operations = []KamikazeOperation{{Kind: "DEPLOY_WINDOWS", Target: b, Deployment: &KamikazeDeployment{MediaPath: `..\outro\install.wim`, MediaSHA256: strings.Repeat("a", 64), MediaSizeBytes: 4096, ImageIndex: 1, Firmware: "GPT_UEFI"}, Challenge: "APAGAR 123ABC"}}
			return signBundle(t, key, x), "s1", time.Now()
		},
		"tipo desconhecido": func() (SignedAssignment, string, time.Time) {
			x := good
			x.Operations = []KamikazeOperation{{Kind: "SHELL", Target: b, Challenge: "APAGAR 123ABC"}}
			return signBundle(t, key, x), "s1", time.Now()
		},
	}
	for name, c := range cases {
		s, session, now := c()
		if _, err := VerifyKamikazeBundle(s, cert, session, now); err == nil {
			t.Fatalf("%s aceito", name)
		}
	}
	// The WinPE clock often runs hours ahead (RTC in local time read as another zone).
	if _, err := VerifyKamikazeBundle(signBundle(t, key, good), cert, "s1", time.Now().Add(8*time.Hour)); err != nil {
		t.Fatalf("relógio adiantado recusado: %v", err)
	}
}

func TestKamikazeCloneToLargerDiskRelocatesGPT(t *testing.T) {
	const mib = 1024 * 1024
	f := newKamikazeFixture(t, 8*mib, 16*mib, 12000)
	f.write(t, KamikazeOperation{Kind: "CLONE_DISK", Source: &f.source, Target: f.target, Challenge: "APAGAR DST1"})
	out, err := f.run("APAGAR DST1\n")
	if err != nil {
		t.Fatalf("%v\n%s", err, out)
	}
	src, _ := os.ReadFile(f.platform.files[0])
	dst, _ := os.ReadFile(f.platform.files[1])
	if !bytes.Equal(src[:512], dst[:512][:512]) && dst[446+4] != 0xEE {
		t.Fatal("MBR protetor não copiado")
	}
	if !bytes.Equal(src[2048*512:12001*512], dst[2048*512:12001*512]) {
		t.Fatal("dados da partição diferem")
	}
	file, _ := os.Open(f.platform.files[1])
	defer file.Close()
	if _, _, err = verifyGPT(file, 16*mib, 512); err != nil {
		t.Fatalf("GPT do destino inválida: %v", err)
	}
	r := f.result(t)
	if !r.Success || len(r.Operations) != 1 || !strings.Contains(r.Operations[0].Details, `"gptRelocated":true`) {
		t.Fatalf("resultado: %+v", r)
	}
	if f.platform.offline[0] {
		t.Fatal("origem ficou offline após o clone")
	}
	if _, err = f.run("APAGAR DST1\n"); err == nil || !strings.Contains(err.Error(), "já foi executado") {
		t.Fatal("pacote reutilizado")
	}
}

func TestKamikazeCloneToSmallerDisk(t *testing.T) {
	const mib = 1024 * 1024
	f := newKamikazeFixture(t, 16*mib, 8*mib, 12000)
	f.write(t, KamikazeOperation{Kind: "CLONE_DISK", Source: &f.source, Target: f.target, Challenge: "APAGAR DST1"})
	if _, err := f.run("APAGAR DST1\n"); err != nil {
		t.Fatal(err)
	}
	file, _ := os.Open(f.platform.files[1])
	defer file.Close()
	if _, _, err := verifyGPT(file, 8*mib, 512); err != nil {
		t.Fatalf("GPT do destino menor inválida: %v", err)
	}
	// Partition past the end of the target: refused before anything is written.
	g := newKamikazeFixture(t, 16*mib, 8*mib, 30000)
	g.write(t, KamikazeOperation{Kind: "CLONE_DISK", Source: &g.source, Target: g.target, Challenge: "APAGAR DST1"})
	if _, err := g.run("APAGAR DST1\n"); err == nil || !strings.Contains(err.Error(), "reduza") {
		t.Fatalf("destino pequeno aceito: %v", err)
	}
	if data, _ := os.ReadFile(g.platform.files[1]); !bytes.Equal(data, bytes.Repeat([]byte{0x5A}, len(data))) {
		t.Fatal("destino alterado apesar da recusa")
	}
}

func TestKamikazeCancelAndWrongConfirmationWriteNothing(t *testing.T) {
	const mib = 1024 * 1024
	for _, input := range []string{"CANCELAR\n", "errado\nerrado\nerrado\n", ""} {
		f := newKamikazeFixture(t, 8*mib, 8*mib, 12000)
		f.write(t, KamikazeOperation{Kind: "CLONE_DISK", Source: &f.source, Target: f.target, Challenge: "APAGAR DST1"})
		f.run(input)
		if data, _ := os.ReadFile(f.platform.files[1]); !bytes.Equal(data, bytes.Repeat([]byte{0x5A}, len(data))) {
			t.Fatalf("destino gravado com entrada %q", input)
		}
		if r := f.result(t); r.Success || !r.Cancelled || len(f.platform.scripts) != 0 {
			t.Fatalf("resultado com entrada %q: %+v", input, r)
		}
	}
}

func TestKamikazeRefusesChangedOrAmbiguousDisks(t *testing.T) {
	const mib = 1024 * 1024
	f := newKamikazeFixture(t, 8*mib, 8*mib, 12000)
	f.write(t, KamikazeOperation{Kind: "CLONE_DISK", Source: &f.source, Target: f.target, Challenge: "APAGAR DST1"})
	f.platform.disks = append(f.platform.disks, LocalDisk{Number: 2, Identity: f.target.Identity, Model: f.target.Model, Serial: f.target.Serial, SizeBytes: f.target.SizeBytes, LogicalSectorBytes: 512})
	if _, err := f.run("APAGAR DST1\n"); err == nil || !strings.Contains(err.Error(), "correspondências") {
		t.Fatalf("disco ambíguo aceito: %v", err)
	}
	g := newKamikazeFixture(t, 8*mib, 8*mib, 12000)
	g.write(t, KamikazeOperation{Kind: "CLONE_DISK", Source: &g.source, Target: g.target, Challenge: "APAGAR DST1"})
	g.platform.disks[1].ReadOnly = true
	if _, err := g.run("APAGAR DST1\n"); err == nil {
		t.Fatal("destino protegido contra gravação aceito")
	}
	// Interrupted earlier run: never resumed automatically.
	h := newKamikazeFixture(t, 8*mib, 8*mib, 12000)
	h.write(t, KamikazeOperation{Kind: "CLONE_DISK", Source: &h.source, Target: h.target, Challenge: "APAGAR DST1"})
	os.WriteFile(filepath.Join(h.dir, "started.json"), []byte("{}"), 0600)
	if _, err := h.run("APAGAR DST1\n"); err == nil || !strings.Contains(err.Error(), "interrompida") {
		t.Fatalf("execução interrompida retomada: %v", err)
	}
}

func TestKamikazeFormatUsesTypedDiskPart(t *testing.T) {
	const mib = 1024 * 1024
	f := newKamikazeFixture(t, 8*mib, 8*mib, 12000)
	f.write(t, KamikazeOperation{Kind: "FORMAT_DISK", Target: f.target, Format: &FormatOptions{TableType: "MBR", FileSystem: "EXFAT", Label: "PENDRIVE"}, Challenge: "APAGAR DST1"})
	if out, err := f.run("apagar dst1\n"); err != nil {
		t.Fatalf("%v\n%s", err, out)
	}
	want := "select disk 1\r\nattributes disk clear readonly\r\nclean\r\nconvert mbr\r\ncreate partition primary\r\nformat quick fs=exfat label=\"PENDRIVE\"\r\nassign\r\nexit\r\n"
	if len(f.platform.scripts) != 1 || f.platform.scripts[0] != want {
		t.Fatalf("script: %q", f.platform.scripts)
	}
	if _, err := BuildKamikazeFormatScript(1, FormatOptions{TableType: "GPT", FileSystem: "NTFS", Label: `a" & del`}, false); err == nil {
		t.Fatal("rótulo com aspas aceito")
	}
}

func TestCloneLengthMBR(t *testing.T) {
	mbr := make([]byte, 4096)
	mbr[446+4] = 0x07
	binary.LittleEndian.PutUint32(mbr[446+8:], 2048)
	binary.LittleEndian.PutUint32(mbr[446+12:], 4096)
	mbr[510], mbr[511] = 0x55, 0xAA
	r := bytes.NewReader(append(mbr, make([]byte, 8*1024*1024-len(mbr))...))
	n, err := CloneLength(r, 8*1024*1024, 4*1024*1024, 512)
	if err != nil || n != 3*1024*1024 {
		t.Fatalf("%d %v", n, err)
	}
	if _, err = CloneLength(r, 8*1024*1024, 2*1024*1024, 512); err == nil {
		t.Fatal("MBR maior que o destino aceito")
	}
}

// fakePEBase refuses everything the kamikaze fixture does not override.
type fakePEBase struct{}

func (fakePEBase) Check(context.Context) (string, error)           { return "", errors.New("fixture") }
func (fakePEBase) PrivateState(string) error                       { return errors.New("fixture") }
func (fakePEBase) Disks(context.Context, []string) ([]Disk, error) { return nil, errors.New("fixture") }
func (fakePEBase) Scratch(string, int64, int) error                { return errors.New("fixture") }
func (fakePEBase) Letters() (byte, byte, error)                    { return 'S', 'W', nil }
func (fakePEBase) Tools(string, string) (map[string]string, error) { return nil, errors.New("fixture") }
func (fakePEBase) Run(context.Context, string, []string, io.Writer) error {
	return errors.New("fixture")
}
func (fakePEBase) VerifyVolumes(int, string, byte, byte) error { return nil }
func (fakePEBase) VerifyApplied(byte) error                    { return nil }
func (fakePEBase) VerifyBoot(byte, string) error               { return nil }
func (fakePEBase) FlushVolume(byte) error                      { return nil }
func (fakePEBase) DriveLayout(int) ([]byte, error)             { return nil, errors.New("fixture") }
