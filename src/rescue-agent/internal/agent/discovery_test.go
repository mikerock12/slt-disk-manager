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
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

// lockedBuffer lets the test read what the agent printed while the agent is still running.
type lockedBuffer struct {
	mu sync.Mutex
	b  bytes.Buffer
}

func (l *lockedBuffer) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.b.Write(p)
}
func (l *lockedBuffer) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.b.String()
}

func TestPairingCodeSharedVector(t *testing.T) {
	const csr = "-----BEGIN CERTIFICATE REQUEST-----\nSLT\n-----END CERTIFICATE REQUEST-----\n"
	pin, client, server := strings.Repeat("ab", 32), strings.Repeat("01", 32), strings.Repeat("fe", 32)
	// Same vector as the server (PairingTests.cs) and the Windows agent.
	if code := PairingCode(pin, csr, client, server); code != "07111918" {
		t.Fatalf("código %s difere do vetor compartilhado", code)
	}
	if PairingCode(strings.ToUpper(pin), csr, strings.ToUpper(client), server) != "07111918" {
		t.Fatal("código deve ignorar caixa do hexadecimal")
	}
	if PairingCode(strings.Repeat("00", 32), csr, client, server) == "07111918" {
		t.Fatal("outro certificado produziu o mesmo código")
	}
	commit, err := PairingCommit(client)
	if err != nil || commit != "72cd6e8422c407fb6d098690f1130b7ded7ec2f7f5e1d30bd9d521f015363793" {
		t.Fatalf("compromisso inesperado: %s %v", commit, err)
	}
	if _, err = PairingCommit("abcd"); err == nil {
		t.Fatal("nonce curto aceito")
	}
}

func TestDiscoveryReplyValidation(t *testing.T) {
	pin := strings.Repeat("ab", 32)
	reply := func(url, replyPin, nonce string) []byte {
		data, _ := json.Marshal(map[string]any{"slt": "server", "v": 1, "nonce": nonce, "name": "BANCADA", "url": url, "pin": replyPin, "version": "0.1.0-beta.7"})
		return data
	}
	source := net.ParseIP("192.168.1.23")
	endpoint, ok := ParseDiscoveryReply(reply("https://192.168.1.23:7443", pin, "0011223344556677"), "0011223344556677", source)
	if !ok || endpoint.URL != "https://192.168.1.23:7443" || endpoint.Pin != pin || endpoint.Name != "BANCADA" {
		t.Fatalf("resposta válida recusada: %+v", endpoint)
	}
	for name, packet := range map[string][]byte{
		"outro endereço":   reply("https://192.168.1.99:7443", pin, "0011223344556677"),
		"nonce estranho":   reply("https://192.168.1.23:7443", pin, "ffffffffffffffff"),
		"http simples":     reply("http://192.168.1.23:7443", pin, "0011223344556677"),
		"caminho na URL":   reply("https://192.168.1.23:7443/x", pin, "0011223344556677"),
		"pin inválido":     reply("https://192.168.1.23:7443", pin[:60], "0011223344556677"),
		"sem porta":        reply("https://192.168.1.23", pin, "0011223344556677"),
		"não é JSON":       []byte("isto não é uma resposta de descoberta válida, de jeito nenhum"),
		"credenciais":      reply("https://user@192.168.1.23:7443", pin, "0011223344556677"),
		"nome de máquina": reply("https://servidor.local:7443", pin, "0011223344556677"),
	} {
		if _, ok := ParseDiscoveryReply(packet, "0011223344556677", source); ok {
			t.Fatalf("resposta inválida aceita: %s", name)
		}
	}
	if _, ok := ParseDiscoveryReply(reply("https://8.8.8.8:7443", pin, "0011223344556677"), "0011223344556677", net.ParseIP("8.8.8.8")); ok {
		t.Fatal("endereço público aceito")
	}
	if got := directedBroadcast(net.ParseIP("10.20.30.40"), net.CIDRMask(20, 32)).String(); got != "10.20.31.255" {
		t.Fatalf("broadcast dirigido incorreto: %s", got)
	}
}

func TestDiscoverFindsLoopbackResponder(t *testing.T) {
	listener, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	port := listener.LocalAddr().(*net.UDPAddr).Port
	pin := strings.Repeat("cd", 32)
	go func() {
		buffer := make([]byte, 1024)
		for {
			n, from, err := listener.ReadFromUDP(buffer)
			if err != nil {
				return
			}
			var probe struct {
				Nonce string `json:"nonce"`
			}
			if json.Unmarshal(buffer[:n], &probe) != nil {
				continue
			}
			data, _ := json.Marshal(map[string]any{"slt": "server", "v": 1, "nonce": probe.Nonce, "name": "LAB", "url": "https://127.0.0.1:7443", "pin": pin, "version": "test"})
			_, _ = listener.WriteToUDP(data, from)
		}
	}()
	servers, err := Discover(context.Background(), port, "127.0.0.1", 1200*time.Millisecond)
	if err != nil || len(servers) != 1 || servers[0].Pin != pin || servers[0].URL != "https://127.0.0.1:7443" {
		t.Fatalf("descoberta falhou: %+v %v", servers, err)
	}
	var out bytes.Buffer
	server, located, typed, err := LocateServer(context.Background(), "", "", port, "127.0.0.1", &out, nil)
	if err != nil || server != "https://127.0.0.1:7443" || located != pin || typed {
		t.Fatalf("localização inesperada: %s %s %v %v", server, located, typed, err)
	}
	if _, _, typed, _ = LocateServer(context.Background(), "https://192.168.1.10:7443", pin, port, "", &out, nil); !typed {
		t.Fatal("pin digitado deve ser marcado como conferido")
	}
	if _, err = Discover(context.Background(), port, "8.8.8.8", time.Second); err == nil {
		t.Fatal("descoberta em endereço público aceita")
	}
}

func TestSltKernelCommandLine(t *testing.T) {
	pin, ticket := strings.Repeat("ab", 32), strings.Repeat("0f", 32)
	line := "BOOT_IMAGE=vmlinuz boot=live fetch=http://192.168.1.23:8088/boot/v1/assets/rescue-rootfs.squashfs console=tty0 slt.server=https://192.168.1.23:7443 slt.pin=" + pin + " slt.ticket=" + ticket + " slt.write=1"
	boot, present, err := ParseSltCommandLine(line)
	if err != nil || !present || boot.Server != "https://192.168.1.23:7443" || boot.Pin != pin || boot.Ticket != ticket || !boot.Write {
		t.Fatalf("linha válida recusada: %+v %v %v", boot, present, err)
	}
	if _, present, err = ParseSltCommandLine("boot=live components quiet"); present || err != nil {
		t.Fatal("boot comum confundido com modo SLT")
	}
	if boot, _, _ = ParseSltCommandLine(strings.Replace(line, "slt.write=1", "slt.write=0", 1)); boot.Write {
		t.Fatal("gravação habilitada sem slt.write=1")
	}
	for name, bad := range map[string]string{
		"servidor público": strings.Replace(line, "192.168.1.23:7443", "8.8.8.8:7443", 1),
		"servidor http":    strings.Replace(line, "slt.server=https://", "slt.server=http://", 1),
		"pin curto":        strings.Replace(line, pin, pin[:40], 1),
		"sem servidor":     "slt.ticket=" + ticket + " slt.pin=" + pin,
	} {
		if _, present, err = ParseSltCommandLine(bad); !present || err == nil {
			t.Fatalf("linha inválida aceita: %s", name)
		}
	}
}

func TestFormatPlanScriptsAndCommands(t *testing.T) {
	for _, bad := range []*FormatOptions{nil, {TableType: "APM", FileSystem: "NTFS"}, {TableType: "GPT", FileSystem: "ZFS"}, {TableType: "GPT", FileSystem: "NTFS", Label: "a b"}, {TableType: "GPT", FileSystem: "NTFS", Label: "x;reboot"}, {TableType: "MBR", FileSystem: "FAT32", Label: "ABCDEFGHIJKL"}} {
		if validateFormat(bad) == nil {
			t.Fatalf("opções inválidas aceitas: %+v", bad)
		}
	}
	script, err := formatPartitionScript(FormatOptions{TableType: "GPT", FileSystem: "NTFS"}, 512)
	if err != nil || script != "label: gpt\nstart=2048, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\n" {
		t.Fatalf("script GPT inesperado: %q %v", script, err)
	}
	if script, _ = formatPartitionScript(FormatOptions{TableType: "MBR", FileSystem: "FAT32"}, 4096); script != "label: dos\nstart=256, type=c\n" {
		t.Fatalf("script MBR 4Kn inesperado: %q", script)
	}
	if script, _ = formatPartitionScript(FormatOptions{TableType: "GPT", FileSystem: "EXT4"}, 512); !strings.Contains(script, "0FC63DAF-8483-4772-8E79-3D69D8477DE4") {
		t.Fatal("ext4 em GPT deve usar o tipo Linux")
	}
	if _, err = formatPartitionScript(FormatOptions{TableType: "GPT", FileSystem: "NTFS"}, 520); err == nil {
		t.Fatal("setor inválido aceito")
	}
	for fs, want := range map[string]string{"NTFS": "mkfs.ntfs --quick --force --label DADOS /dev/loop9p1", "EXFAT": "mkfs.exfat -L DADOS /dev/loop9p1", "FAT32": "mkfs.fat -F 32 -n DADOS /dev/loop9p1", "EXT4": "mkfs.ext4 -F -q -L DADOS /dev/loop9p1"} {
		if got := strings.Join(formatCommand(FormatOptions{TableType: "GPT", FileSystem: fs, Label: "DADOS"}, "/dev/loop9p1"), " "); got != want {
			t.Fatalf("%s: %q", fs, got)
		}
	}
	key, cert, _ := testCertificate(t)
	sign := func(p Plan) SignedAssignment {
		payload, _ := json.Marshal(p)
		h := sha256.Sum256(payload)
		sig, _ := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, h[:])
		return SignedAssignment{Payload: payload, Signature: sig}
	}
	plan := Plan{ID: "job-format", Kind: "FORMAT_DISK", DeviceID: "pc-1", Consent: true, ExpiresAtUTC: time.Now().Add(time.Minute), DiskFingerprint: "fp", DiskPath: "/dev/sdz", DiskSizeBytes: 1 << 30, LogicalSectorBytes: 512, Challenge: "FORMATAR pc fp", Format: &FormatOptions{TableType: "GPT", FileSystem: "NTFS", Label: "DADOS"}}
	if _, err = VerifyAssignment(sign(plan), cert, "pc-1", time.Now()); err != nil {
		t.Fatal(err)
	}
	withoutChallenge := plan
	withoutChallenge.Challenge = " "
	withoutOptions := plan
	withoutOptions.Format = nil
	foreign := plan
	foreign.Kind = "CAPTURE"
	for name, bad := range map[string]Plan{"sem confirmação": withoutChallenge, "sem opções": withoutOptions, "opções em outro job": foreign} {
		if _, err = VerifyAssignment(sign(bad), cert, "pc-1", time.Now()); err == nil {
			t.Fatalf("plano inválido aceito: %s", name)
		}
	}
}

// A minimal controller: short-code pairing and ticket enrollment as the real server does them.
func TestShortCodeAndTicketEnrollment(t *testing.T) {
	caKey, serverCert, pair := testCertificate(t)
	var mu sync.Mutex
	pinBytes := sha256.Sum256(serverCert.Raw)
	pin := hex.EncodeToString(pinBytes[:])
	type pendingRequest struct{ csr, commitment, serverNonce, code, ticket string }
	requests := map[string]*pendingRequest{}
	issue := func(csrPEM, deviceID string) string {
		block, _ := pem.Decode([]byte(csrPEM))
		request, err := x509.ParseCertificateRequest(block.Bytes)
		if err != nil {
			t.Fatal(err)
		}
		template := &x509.Certificate{SerialNumber: big.NewInt(7), Subject: pkix.Name{CommonName: deviceID}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth}}
		der, err := x509.CreateCertificate(rand.Reader, template, serverCert, request.PublicKey, caKey)
		if err != nil {
			t.Fatal(err)
		}
		return string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}))
	}
	approved := map[string]bool{}
	handler := http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		switch {
		case req.URL.Path == "/agent/v1/enroll":
			var body map[string]string
			json.NewDecoder(req.Body).Decode(&body)
			id := "enroll-" + body["hostname"] + "-" + body["mode"] + "-" + body["secret"][:6]
			p := &pendingRequest{csr: body["csrPem"], commitment: body["commitment"], ticket: body["ticket"]}
			requests[id] = p
			response := map[string]any{"id": id, "code": "", "expiresAtUtc": time.Now().Add(10 * time.Minute)}
			if p.ticket != "" {
				approved[id] = true
			} else if p.commitment != "" {
				p.serverNonce = strings.Repeat("5a", 32)
				response["serverNonce"] = p.serverNonce
			} else {
				p.code = "11112222"
				response["code"] = p.code
			}
			json.NewEncoder(w).Encode(response)
		case strings.HasSuffix(req.URL.Path, "/reveal"):
			id := strings.Split(req.URL.Path, "/")[4]
			var body map[string]string
			json.NewDecoder(req.Body).Decode(&body)
			p := requests[id]
			if commit, _ := PairingCommit(body["nonce"]); p == nil || commit != p.commitment || req.Header.Get("X-Enrollment-Secret") == "" {
				http.Error(w, "commitment", 400)
				return
			}
			p.code = PairingCode(pin, p.csr, body["nonce"], p.serverNonce)
			io.WriteString(w, `{"status":"PENDING"}`)
		case strings.HasPrefix(req.URL.Path, "/agent/v1/enroll/"):
			id := strings.Split(req.URL.Path, "/")[4]
			if !approved[id] {
				io.WriteString(w, `{"status":"PENDING"}`)
				return
			}
			json.NewEncoder(w).Encode(map[string]string{"status": "APPROVED", "deviceId": "device-" + id, "certificatePem": issue(requests[id].csr, "device-"+id)})
		default:
			http.NotFound(w, req)
		}
	})
	server := httptest.NewUnstartedServer(handler)
	server.TLS = &tls.Config{Certificates: []tls.Certificate{pair}}
	server.StartTLS()
	defer server.Close()
	client, err := NewClient(server.URL, pin, nil)
	if err != nil {
		t.Fatal(err)
	}
	observed, err := ObservePin(context.Background(), server.URL)
	if err != nil || observed != pin {
		t.Fatalf("pin observado %s difere do certificado: %v", observed, err)
	}

	// Ticket: no code, approved at once.
	out := &lockedBuffer{}
	deviceID, certificate, err := enrollWith(context.Background(), client, t.TempDir(), out, "RESCUE", true, strings.Repeat("0f", 32))
	if err != nil || certificate == nil || !strings.HasPrefix(deviceID, "device-") || strings.Contains(out.String(), "CÓDIGO") {
		t.Fatalf("pareamento por ticket falhou: %v %s", err, out.String())
	}

	// Short code: the agent shows the code derived from the certificate it saw; the "operator" approves it.
	out = &lockedBuffer{}
	state := t.TempDir()
	done := make(chan error, 1)
	go func() {
		_, _, e := enrollWith(context.Background(), client, state, out, "RESCUE", false, "")
		done <- e
	}()
	var shown string
	deadline := time.Now().Add(20 * time.Second)
	for shown == "" && time.Now().Before(deadline) {
		time.Sleep(100 * time.Millisecond)
		printed := out.String()
		mu.Lock()
		for id, p := range requests {
			if p.commitment != "" && p.code != "" && strings.Contains(printed, "código "+p.code) {
				shown = p.code
				approved[id] = true
			}
		}
		mu.Unlock()
	}
	if shown == "" {
		t.Fatalf("agente não mostrou o mesmo código que o servidor derivou: %s", out.String())
	}
	if err = <-done; err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "CÓDIGO :   "+shown[:4]+" "+shown[4:]) {
		t.Fatal("código não foi exibido em destaque")
	}
}
