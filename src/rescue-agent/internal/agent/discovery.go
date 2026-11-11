package agent

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/url"
	"regexp"
	"sort"
	"strings"
	"time"
)

// LAN discovery and short-code pairing (docs/ADR/003). A discovery reply only says where a
// server claims to be. Trust comes from the pairing code, which binds the TLS certificate
// this agent actually saw to the CSR the server actually received.

const DefaultDiscoveryPort = 7444

type ServerEndpoint struct {
	Name    string
	URL     string
	Pin     string
	Version string
}

var hex64Pattern = regexp.MustCompile(`^[0-9a-fA-F]{64}$`)

// PairingCommit and PairingCode are identical in the server and in the Windows agent.
func PairingCommit(nonceHex string) (string, error) {
	nonce, err := hex.DecodeString(nonceHex)
	if err != nil || len(nonce) != 32 {
		return "", errors.New("nonce de pareamento inválido")
	}
	sum := sha256.Sum256(nonce)
	return hex.EncodeToString(sum[:]), nil
}

func PairingCode(serverPin, csrPEM, clientNonce, serverNonce string) string {
	csr := sha256.Sum256([]byte(csrPEM))
	text := strings.Join([]string{"SLT-PAIR-2", strings.ToLower(serverPin), hex.EncodeToString(csr[:]), strings.ToLower(clientNonce), strings.ToLower(serverNonce)}, "\n") + "\n"
	digest := sha256.Sum256([]byte(text))
	return fmt.Sprintf("%08d", binary.BigEndian.Uint64(digest[:8])%100000000)
}

func ParseDiscoveryReply(packet []byte, nonce string, source net.IP) (ServerEndpoint, bool) {
	var reply struct {
		Slt     string `json:"slt"`
		V       int    `json:"v"`
		Nonce   string `json:"nonce"`
		Name    string `json:"name"`
		URL     string `json:"url"`
		Pin     string `json:"pin"`
		Version string `json:"version"`
	}
	if len(packet) < 40 || len(packet) > 1024 || json.Unmarshal(packet, &reply) != nil {
		return ServerEndpoint{}, false
	}
	if reply.Slt != "server" || reply.V != 1 || !strings.EqualFold(reply.Nonce, nonce) || !hex64Pattern.MatchString(reply.Pin) || len(reply.URL) > 64 {
		return ServerEndpoint{}, false
	}
	u, err := url.Parse(reply.URL)
	if err != nil || u.Scheme != "https" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || (u.Path != "" && u.Path != "/") || u.Port() == "" {
		return ServerEndpoint{}, false
	}
	host := net.ParseIP(u.Hostname())
	// The advertised address must be the one that answered: a reply cannot redirect elsewhere.
	if host == nil || !localIP(host) || !host.Equal(source) {
		return ServerEndpoint{}, false
	}
	clean := func(value string, limit int, extra string) string {
		var b strings.Builder
		for _, ch := range value {
			if b.Len() < limit && (ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z' || strings.ContainsRune(extra, ch)) {
				b.WriteRune(ch)
			}
		}
		return b.String()
	}
	name := clean(reply.Name, 48, "-_. ")
	if name == "" {
		name = u.Hostname()
	}
	return ServerEndpoint{Name: name, URL: "https://" + u.Host, Pin: strings.ToLower(reply.Pin), Version: clean(reply.Version, 32, "-.")}, true
}

func directedBroadcast(ip net.IP, mask net.IPMask) net.IP {
	v4 := ip.To4()
	if v4 == nil || len(mask) != 4 {
		return nil
	}
	out := make(net.IP, 4)
	for i := range out {
		out[i] = v4[i] | ^mask[i]
	}
	return out
}

// Discover probes the LAN (or one explicit address when target is set) for SLT servers.
func Discover(ctx context.Context, port int, target string, wait time.Duration) ([]ServerEndpoint, error) {
	raw := make([]byte, 16)
	if _, err := rand.Read(raw); err != nil {
		return nil, err
	}
	nonce := hex.EncodeToString(raw)
	probe := []byte(`{"slt":"discover","v":1,"nonce":"` + nonce + `"}`)
	type sender struct {
		conn    *net.UDPConn
		targets []*net.UDPAddr
	}
	var senders []sender
	defer func() {
		for _, s := range senders {
			s.conn.Close()
		}
	}()
	if target != "" {
		ip := net.ParseIP(target)
		if ip == nil || !localIP(ip) {
			return nil, errors.New("endereço de descoberta deve ser um IP da rede local")
		}
		conn, err := net.ListenUDP("udp4", nil)
		if err != nil {
			return nil, err
		}
		senders = append(senders, sender{conn, []*net.UDPAddr{{IP: ip, Port: port}}})
	} else {
		interfaces, err := net.Interfaces()
		if err != nil {
			return nil, err
		}
		for _, nic := range interfaces {
			if nic.Flags&net.FlagUp == 0 || nic.Flags&net.FlagLoopback != 0 {
				continue
			}
			addresses, _ := nic.Addrs()
			for _, address := range addresses {
				network, ok := address.(*net.IPNet)
				if !ok || network.IP.To4() == nil || !localIP(network.IP) {
					continue
				}
				if ones, bits := network.Mask.Size(); bits != 32 || ones < 8 || ones > 30 {
					continue
				}
				conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: network.IP.To4()})
				if err != nil {
					continue
				}
				senders = append(senders, sender{conn, []*net.UDPAddr{{IP: net.IPv4bcast, Port: port}, {IP: directedBroadcast(network.IP, network.Mask), Port: port}}})
			}
		}
	}
	if len(senders) == 0 {
		return nil, nil
	}
	deadline := time.Now().Add(wait)
	found := map[string]ServerEndpoint{}
	results := make(chan ServerEndpoint, 64)
	done := make(chan struct{}, len(senders))
	for _, s := range senders {
		_ = s.conn.SetReadDeadline(deadline)
		go func(conn *net.UDPConn) {
			defer func() { done <- struct{}{} }()
			buffer := make([]byte, 2048)
			for {
				n, from, err := conn.ReadFromUDP(buffer)
				if err != nil {
					return
				}
				if endpoint, ok := ParseDiscoveryReply(buffer[:n], nonce, from.IP); ok {
					select {
					case results <- endpoint:
					default:
					}
				}
			}
		}(s.conn)
	}
	for round := 0; round < 3 && time.Now().Before(deadline); round++ {
		for _, s := range senders {
			for _, address := range s.targets {
				if address.IP != nil {
					_, _ = s.conn.WriteToUDP(probe, address)
				}
			}
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(wait / 4):
		}
	}
	for finished := 0; finished < len(senders); {
		select {
		case endpoint := <-results:
			found[endpoint.URL+"|"+endpoint.Pin] = endpoint
		case <-done:
			finished++
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	for len(results) > 0 {
		endpoint := <-results
		found[endpoint.URL+"|"+endpoint.Pin] = endpoint
	}
	list := make([]ServerEndpoint, 0, len(found))
	for _, endpoint := range found {
		list = append(list, endpoint)
	}
	sort.Slice(list, func(i, j int) bool { return list[i].URL < list[j].URL })
	return list, nil
}

// ObservePin reads the certificate a server presents, without trusting it. The caller must
// still complete the short-code pairing, which is what confirms this pin.
func ObservePin(ctx context.Context, server string) (string, error) {
	u, err := url.Parse(server)
	if err != nil || u.Scheme != "https" || u.Host == "" {
		return "", errors.New("--server deve ser https://IP-LAN:porta")
	}
	ip := net.ParseIP(u.Hostname())
	if ip == nil || !localIP(ip) {
		return "", errors.New("servidor fora da LAN permitida")
	}
	dialer := &tls.Dialer{NetDialer: &net.Dialer{Timeout: 10 * time.Second}, Config: &tls.Config{MinVersion: tls.VersionTLS12, InsecureSkipVerify: true}} // Certificate is only observed here; the pairing code authenticates it.
	conn, err := dialer.DialContext(ctx, "tcp", u.Host)
	if err != nil {
		return "", err
	}
	defer conn.Close()
	state := conn.(*tls.Conn).ConnectionState()
	if len(state.PeerCertificates) == 0 {
		return "", errors.New("servidor não apresentou certificado")
	}
	sum := sha256.Sum256(state.PeerCertificates[0].Raw)
	return hex.EncodeToString(sum[:]), nil
}

// LocateServer fills Server/ServerPin when the technician did not type them. It reports whether
// the pin was typed by a person (true) or merely observed/discovered (false).
func LocateServer(ctx context.Context, server, pin string, port int, target string, output io.Writer, choose func([]ServerEndpoint) (ServerEndpoint, bool)) (string, string, bool, error) {
	if server != "" && pin != "" {
		return server, pin, true, nil
	}
	if server != "" {
		observed, err := ObservePin(ctx, server)
		if err != nil {
			return "", "", false, err
		}
		fmt.Fprintln(output, "Servidor respondeu. O código de pareamento confirmará que é o servidor certo.")
		return server, observed, false, nil
	}
	for attempt := 1; ; attempt++ {
		fmt.Fprintln(output, "Procurando o servidor SLT Disk Manager na rede local...")
		servers, err := Discover(ctx, port, target, 3*time.Second)
		if err != nil {
			return "", "", false, err
		}
		if len(servers) == 1 {
			fmt.Fprintf(output, "Servidor encontrado: %s (%s).\n", servers[0].Name, servers[0].URL)
			return servers[0].URL, servers[0].Pin, false, nil
		}
		if len(servers) > 1 {
			if choose != nil {
				if selected, ok := choose(servers); ok {
					return selected.URL, selected.Pin, false, nil
				}
			}
			return "", "", false, errors.New("mais de um servidor SLT respondeu; informe --server https://IP:7443")
		}
		if attempt >= 20 {
			return "", "", false, fmt.Errorf("nenhum servidor SLT Disk Manager respondeu na rede local (porta UDP %d); confira servidor, rede e firewall, ou informe --server", port)
		}
		if attempt == 3 {
			fmt.Fprintln(output, "Nada ainda. Confira: servidor ligado, mesma rede e firewall do servidor.")
		}
		select {
		case <-ctx.Done():
			return "", "", false, ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
}

// SltBoot is what a paired Windows client wrote on the kernel command line before restarting
// this PC into "SLT mode".
type SltBoot struct {
	Server string
	Pin    string
	Ticket string
	Write  bool
}

var sltServerPattern = regexp.MustCompile(`^https://[0-9]{1,3}(\.[0-9]{1,3}){3}:[0-9]{2,5}$`)

func ParseSltCommandLine(commandLine string) (SltBoot, bool, error) {
	var boot SltBoot
	seen := false
	for _, word := range strings.Fields(commandLine) {
		name, value, _ := strings.Cut(word, "=")
		switch name {
		case "slt.server":
			boot.Server = value
		case "slt.pin":
			boot.Pin = strings.ToLower(value)
		case "slt.ticket":
			boot.Ticket = strings.ToLower(value)
			seen = true
		case "slt.write":
			boot.Write = value == "1"
		}
	}
	if !seen {
		return boot, false, nil
	}
	if !sltServerPattern.MatchString(boot.Server) || !hex64Pattern.MatchString(boot.Pin) || !hex64Pattern.MatchString(boot.Ticket) {
		return boot, true, errors.New("parâmetros do modo SLT incompletos ou inválidos na linha de comando do kernel")
	}
	if ip := net.ParseIP(strings.TrimPrefix(boot.Server[:strings.LastIndex(boot.Server, ":")], "https://")); ip == nil || !localIP(ip) {
		return boot, true, errors.New("servidor do modo SLT fora da rede local")
	}
	return boot, true, nil
}
