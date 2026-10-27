package agent

import (
	"bytes"
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"io"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func testCertificate(t *testing.T) (*rsa.PrivateKey, *x509.Certificate, tls.Certificate) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "fixture-server"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}, IsCA: true, BasicConstraintsValid: true}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatal(err)
	}
	pair, err := tls.X509KeyPair(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}))
	if err != nil {
		t.Fatal(err)
	}
	return key, cert, pair
}

func TestSignedAssignmentAndReplay(t *testing.T) {
	key, cert, _ := testCertificate(t)
	p := Plan{ID: "job-1", Kind: "INVENTORY", DeviceID: "pc-1", Consent: true, ExpiresAtUTC: time.Now().Add(time.Minute)}
	payload, _ := json.Marshal(p)
	h := sha256.Sum256(payload)
	sig, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, h[:])
	if err != nil {
		t.Fatal(err)
	}
	a := SignedAssignment{Payload: payload, Signature: sig}
	if _, err = VerifyAssignment(a, cert, "pc-1", time.Now()); err != nil {
		t.Fatal(err)
	}
	a.Payload = append([]byte(nil), payload...)
	a.Payload[0] = '['
	if _, err = VerifyAssignment(a, cert, "pc-1", time.Now()); err == nil {
		t.Fatal("tamper aceito")
	}
	a.Payload = payload
	if _, err = VerifyAssignment(a, cert, "outro-pc", time.Now()); err == nil {
		t.Fatal("outro device aceito")
	}
	if _, err = VerifyAssignment(a, cert, "pc-1", time.Now().Add(time.Hour)); err == nil {
		t.Fatal("expirado aceito")
	}
	dir := t.TempDir()
	if err = ClaimJob(dir, p.ID); err != nil {
		t.Fatal(err)
	}
	if err = ClaimJob(dir, p.ID); err == nil {
		t.Fatal("replay aceito")
	}
	if err = ClaimJob(dir, "../escape"); err == nil {
		t.Fatal("path traversal aceito")
	}
}

func TestLsblkSafetyAndFingerprint(t *testing.T) {
	data := []byte(`{"blockdevices":[{"name":"/dev/fake0","path":"/dev/fake0","type":"disk","model":" Test ","serial":"SN-1","size":4096,"log-sec":512,"phy-sec":4096,"ro":false,"mountpoints":[null],"children":[{"name":"/dev/fake0p1","path":"/dev/fake0p1","type":"part","size":2048,"fstype":"ext4","mountpoints":["/"]}]}]}`)
	d, err := parseBlocks(data)
	if err != nil {
		t.Fatal(err)
	}
	if len(d) != 1 || !d[0].Mounted || !d[0].IsSystem || len(d[0].Partitions) != 1 {
		t.Fatalf("inventário inseguro: %+v", d)
	}
	p := Plan{Kind: "RESTORE", DiskPath: d[0].Path, DiskFingerprint: d[0].Fingerprint, DiskSizeBytes: d[0].SizeBytes, LogicalSectorBytes: d[0].LogicalSectorBytes}
	if _, err = ValidateDisk(p, d, true); err == nil {
		t.Fatal("sistema montado aceito")
	}
	d[0].Mounted = false
	d[0].IsSystem = false
	if _, err = ValidateDisk(p, d, true); err != nil {
		t.Fatal(err)
	}
	d[0].Serial = "TROCADO"
	if _, err = ValidateDisk(p, d, true); err == nil {
		t.Fatal("troca de identidade aceita")
	}
}

func TestManifestRejectsTruncationGeometryAndSameDisk(t *testing.T) {
	h := sha256.Sum256(make([]byte, 512))
	m := Manifest{SchemaVersion: 1, ID: "image-1", State: "AVAILABLE", SourceDeviceID: "source", SourceFingerprint: "source-fp", LogicalSizeBytes: 512, LogicalSectorBytes: 512, Chunks: []ImageChunk{{Sequence: 0, OffsetBytes: 0, SizeBytes: 512, SHA256: hex.EncodeToString(h[:])}}}
	p := Plan{ImageID: m.ID, DeviceID: "target", DiskFingerprint: "target-fp", DiskSizeBytes: 512, LogicalSectorBytes: 512}
	if err := ValidateManifest(m, p); err != nil {
		t.Fatal(err)
	}
	p.DiskSizeBytes = 256
	if err := ValidateManifest(m, p); err == nil {
		t.Fatal("destino menor aceito")
	}
	p.DiskSizeBytes = 512
	p.LogicalSectorBytes = 4096
	if err := ValidateManifest(m, p); err == nil {
		t.Fatal("setor incompatível aceito")
	}
	p.LogicalSectorBytes = 512
	m.Chunks = nil
	if err := ValidateManifest(m, p); err == nil {
		t.Fatal("truncado aceito")
	}
}

func fixtureFile(t *testing.T, dir, name string, data []byte) Disk {
	t.Helper()
	if err := os.WriteFile(filepath.Join(dir, name), data, 0600); err != nil {
		t.Fatal(err)
	}
	disks, err := CollectDisks(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	for _, d := range disks {
		if d.Serial == name {
			return d
		}
	}
	t.Fatal("fixture não encontrada")
	return Disk{}
}

func TestCaptureRestoreIntegrityAndCorruption(t *testing.T) {
	fixtureDir := t.TempDir()
	original := bytes.Repeat([]byte{0x17, 0x92, 0x35, 0xa1}, (ChunkSize+512)/4)
	source := fixtureFile(t, fixtureDir, "source.raw", original)
	target := fixtureFile(t, fixtureDir, "target.raw", make([]byte, len(original)))
	m := Manifest{SchemaVersion: 1, ID: "image-1", State: "CREATING", SourceDeviceID: "source-device", SourceFingerprint: source.Fingerprint, LogicalSizeBytes: source.SizeBytes, LogicalSectorBytes: 512, Chunks: []ImageChunk{}}
	chunks := map[int][]byte{}
	corrupt := false
	handler := http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case strings.HasSuffix(req.URL.Path, "/progress"):
			io.WriteString(w, `{"cancelRequested":false}`)
		case strings.HasSuffix(req.URL.Path, "/capture-complete"):
			m.State = "AVAILABLE"
			json.NewEncoder(w).Encode(m)
		case strings.HasSuffix(req.URL.Path, "/manifest"):
			json.NewEncoder(w).Encode(m)
		case strings.Contains(req.URL.Path, "/chunks/"):
			parts := strings.Split(req.URL.Path, "/")
			sequence, _ := strconv.Atoi(parts[len(parts)-1])
			if req.Method == "PUT" {
				data, _ := io.ReadAll(req.Body)
				h := sha256.Sum256(data)
				sum := hex.EncodeToString(h[:])
				if req.Header.Get("X-Content-SHA256") != sum {
					http.Error(w, "hash", 400)
					return
				}
				chunks[sequence] = data
				m.Chunks = append(m.Chunks, ImageChunk{Sequence: sequence, OffsetBytes: int64(sequence * ChunkSize), SizeBytes: len(data), SHA256: sum})
				json.NewEncoder(w).Encode(map[string]any{"sequence": sequence, "sizeBytes": len(data), "sha256": sum})
			} else {
				data := append([]byte(nil), chunks[sequence]...)
				if corrupt {
					data[0] ^= 1
				}
				w.Header().Set("Content-Type", "application/octet-stream")
				w.Write(data)
			}
		default:
			http.NotFound(w, req)
		}
	})
	_, cert, pair := testCertificate(t)
	server := httptest.NewUnstartedServer(handler)
	server.TLS = &tls.Config{Certificates: []tls.Certificate{pair}}
	server.StartTLS()
	defer server.Close()
	pin := sha256.Sum256(cert.Raw)
	client, err := NewClient(server.URL, hex.EncodeToString(pin[:]), nil)
	if err != nil {
		t.Fatal(err)
	}
	r := Runner{Client: client, DeviceID: "source-device", Options: Options{FixtureDir: fixtureDir, AllowFileWrite: true, Input: strings.NewReader(""), Output: io.Discard}}
	capture := Plan{ID: "capture-1", Kind: "CAPTURE", DeviceID: "source-device", DiskFingerprint: source.Fingerprint, DiskPath: source.Path, DiskSizeBytes: source.SizeBytes, LogicalSectorBytes: 512, ImageID: m.ID, ExpiresAtUTC: time.Now().Add(time.Minute), Consent: true}
	if err = r.capture(context.Background(), capture, source); err != nil {
		t.Fatal(err)
	}
	restore := Plan{ID: "restore-1", Kind: "RESTORE", DeviceID: "target-device", DiskFingerprint: target.Fingerprint, DiskPath: target.Path, DiskSizeBytes: target.SizeBytes, LogicalSectorBytes: 512, ImageID: m.ID, ExpiresAtUTC: time.Now().Add(time.Minute), Consent: true, Challenge: "CONFIRM"}
	if err = r.restore(context.Background(), restore, target); err != nil {
		t.Fatal(err)
	}
	restored, err := os.ReadFile(target.Path)
	if err != nil || !bytes.Equal(original, restored) {
		t.Fatal("restauração diferente da origem")
	}
	zero := make([]byte, len(original))
	if err = os.WriteFile(target.Path, zero, 0600); err != nil {
		t.Fatal(err)
	}
	corrupt = true
	if err = r.restore(context.Background(), restore, target); err == nil {
		t.Fatal("chunk corrompido aceito")
	}
	restored, _ = os.ReadFile(target.Path)
	if !bytes.Equal(restored, zero) {
		t.Fatal("chunk corrompido gravou destino")
	}
	r.Options.AllowFileWrite = false
	corrupt = false
	if err = r.restore(context.Background(), restore, target); err == nil {
		t.Fatal("gravação sem permissão aceita")
	}
}

func TestTLSPinAndRedirects(t *testing.T) {
	_, cert, pair := testCertificate(t)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/redirect" {
			http.Redirect(w, r, "https://example.com", http.StatusFound)
			return
		}
		io.WriteString(w, `{"ok":true}`)
	}))
	server.TLS = &tls.Config{Certificates: []tls.Certificate{pair}}
	server.StartTLS()
	defer server.Close()
	good := sha256.Sum256(cert.Raw)
	client, err := NewClient(server.URL, hex.EncodeToString(good[:]), nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.Request(context.Background(), "GET", "/", nil, nil); err != nil {
		t.Fatal(err)
	}
	wrong, err := NewClient(server.URL, strings.Repeat("00", 32), nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = wrong.Request(context.Background(), "GET", "/", nil, nil); err == nil {
		t.Fatal("pin errado aceito")
	}
	if _, _, err = client.Request(context.Background(), "GET", "/redirect", nil, nil); err == nil {
		t.Fatal("redirect aceito")
	}
}
