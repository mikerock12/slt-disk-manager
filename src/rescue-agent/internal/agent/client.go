package agent

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

type Client struct {
	base              string
	HTTP              *http.Client
	ServerCertificate *x509.Certificate
	pin               []byte
}

func localIP(ip net.IP) bool {
	return ip != nil && (ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast())
}

func NewClient(server, pin string, cert *tls.Certificate) (*Client, error) {
	u, err := url.Parse(server)
	if err != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || (u.Path != "" && u.Path != "/") {
		return nil, errors.New("--server deve ser https://IP-LAN:porta sem caminho")
	}
	pinBytes, err := hex.DecodeString(strings.ReplaceAll(pin, ":", ""))
	if err != nil || len(pinBytes) != 32 {
		return nil, errors.New("pin deve conter SHA256 DER hexadecimal de 64 caracteres")
	}
	c := &Client{base: strings.TrimRight(server, "/"), pin: pinBytes}
	tlsConfig := &tls.Config{MinVersion: tls.VersionTLS12, InsecureSkipVerify: true} // Trust is the mandatory out-of-band DER SHA256 pin below; never a permissive TLS callback.
	if cert != nil {
		tlsConfig.Certificates = []tls.Certificate{*cert}
	}
	tlsConfig.VerifyConnection = func(cs tls.ConnectionState) error {
		if len(cs.PeerCertificates) == 0 {
			return errors.New("servidor não apresentou certificado")
		}
		peer := cs.PeerCertificates[0]
		h := sha256.Sum256(peer.Raw)
		if subtle.ConstantTimeCompare(h[:], c.pin) != 1 {
			return errors.New("PIN SERVIDOR DIFERENTE: conexão bloqueada")
		}
		now := time.Now()
		if now.Before(peer.NotBefore) || now.After(peer.NotAfter) {
			return errors.New("certificado servidor fora da validade")
		}
		if key, ok := peer.PublicKey.(*rsa.PublicKey); !ok || key.N.BitLen() < 2048 {
			return errors.New("servidor exige RSA >=2048")
		}
		c.ServerCertificate = peer
		return nil
	}
	transport := &http.Transport{TLSClientConfig: tlsConfig, Proxy: nil, ResponseHeaderTimeout: 30 * time.Second, MaxIdleConns: 2}
	transport.DialContext = func(ctx context.Context, network, address string) (net.Conn, error) {
		host, port, err := net.SplitHostPort(address)
		if err != nil {
			return nil, err
		}
		ips, err := net.DefaultResolver.LookupIPAddr(ctx, host)
		if err != nil {
			return nil, err
		}
		for _, ip := range ips {
			if !localIP(ip.IP) {
				return nil, errors.New("servidor fora da LAN permitida")
			}
		}
		for _, ip := range ips {
			conn, e := (&net.Dialer{Timeout: 10 * time.Second}).DialContext(ctx, network, net.JoinHostPort(ip.IP.String(), port))
			if e == nil {
				return conn, nil
			}
			err = e
		}
		if err == nil {
			err = errors.New("servidor sem endereço LAN")
		}
		return nil, err
	}
	c.HTTP = &http.Client{Transport: transport, Timeout: 2 * time.Minute, CheckRedirect: func(*http.Request, []*http.Request) error { return errors.New("redirecionamentos bloqueados") }}
	return c, nil
}

func (c *Client) Request(ctx context.Context, method, path string, body io.Reader, headers map[string]string) ([]byte, int, error) {
	return c.requestLimited(ctx, method, path, body, headers, ChunkSize+65536)
}

// Whole-disk manifests grow with the number of 4 MiB chunks. JSON operations
// explicitly select this bound; an HTTP Content-Type supplied by the peer can
// never relax the binary transfer limit. Plan/signature limits remain separate.
const maximumJSONResponseBytes int64 = 256 * 1024 * 1024

func (c *Client) requestLimited(ctx context.Context, method, path string, body io.Reader, headers map[string]string, maximum int64) ([]byte, int, error) {
	req, err := http.NewRequestWithContext(ctx, method, c.base+path, body)
	if err != nil {
		return nil, 0, err
	}
	req.Header.Set("Content-Type", "application/json")
	for k, v := range headers {
		req.Header.Set(k, v)
	}
	r, err := c.HTTP.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer r.Body.Close()
	if r.ContentLength > maximum {
		return nil, r.StatusCode, errors.New("resposta excede limite")
	}
	data, err := io.ReadAll(io.LimitReader(r.Body, maximum+1))
	if err != nil {
		return nil, r.StatusCode, err
	}
	if int64(len(data)) > maximum {
		return nil, r.StatusCode, errors.New("resposta excede limite")
	}
	if r.StatusCode < 200 || r.StatusCode >= 300 {
		return nil, r.StatusCode, fmt.Errorf("HTTP %d: %s", r.StatusCode, strings.TrimSpace(string(data)))
	}
	return data, r.StatusCode, nil
}

func (c *Client) JSON(ctx context.Context, method, path string, input, output any) (int, error) {
	var reader io.Reader
	if input != nil {
		data, err := json.Marshal(input)
		if err != nil {
			return 0, err
		}
		reader = bytes.NewReader(data)
	}
	data, status, err := c.requestLimited(ctx, method, path, reader, nil, maximumJSONResponseBytes)
	if err != nil {
		return status, err
	}
	if output != nil && len(data) > 0 {
		err = json.Unmarshal(data, output)
	}
	return status, err
}

type credentials struct {
	DeviceID       string `json:"deviceId"`
	CertificatePEM string `json:"certificatePem"`
	KeyPEM         string `json:"keyPem"`
	Mode           string `json:"mode,omitempty"`
}
type enrollment struct {
	ID           string    `json:"id"`
	Secret       string    `json:"secret"`
	KeyPEM       string    `json:"keyPem"`
	Code         string    `json:"code"`
	ExpiresAtUTC time.Time `json:"expiresAtUtc"`
	Mode         string    `json:"mode,omitempty"`
}

func Enroll(ctx context.Context, c *Client, stateDir string, output io.Writer) (string, *tls.Certificate, error) {
	return EnrollMode(ctx, c, stateDir, output, "RESCUE")
}

func EnrollMode(ctx context.Context, c *Client, stateDir string, output io.Writer, mode string) (string, *tls.Certificate, error) {
	if mode != "RESCUE" && mode != "WINPE" {
		return "", nil, errors.New("modo de pareamento não suportado")
	}
	if err := os.MkdirAll(stateDir, 0700); err != nil {
		return "", nil, err
	}
	if err := os.Chmod(stateDir, 0700); err != nil {
		return "", nil, err
	}
	path := filepath.Join(stateDir, "credentials.json")
	if data, err := os.ReadFile(path); err == nil {
		var saved credentials
		if err = json.Unmarshal(data, &saved); err != nil {
			return "", nil, err
		}
		savedMode := saved.Mode
		if savedMode == "" {
			savedMode = "RESCUE"
		}
		if savedMode != mode {
			return "", nil, errors.New("diretório de identidade pertence a outro modo de agente")
		}
		cert, err := tls.X509KeyPair([]byte(saved.CertificatePEM), []byte(saved.KeyPEM))
		if err != nil {
			return "", nil, err
		}
		leaf, err := x509.ParseCertificate(cert.Certificate[0])
		if err != nil {
			return "", nil, err
		}
		if leaf.Subject.CommonName != saved.DeviceID || time.Now().After(leaf.NotAfter) {
			return "", nil, errors.New("credencial expirada ou identidade inválida; revogue e pareie novamente")
		}
		return saved.DeviceID, &cert, nil
	} else if !os.IsNotExist(err) {
		return "", nil, err
	}
	var pending enrollment
	pendingPath := filepath.Join(stateDir, "enrollment.json")
	if data, err := os.ReadFile(pendingPath); err == nil {
		if err = json.Unmarshal(data, &pending); err != nil {
			return "", nil, err
		}
		pendingMode := pending.Mode
		if pendingMode == "" {
			pendingMode = "RESCUE"
		}
		if pendingMode != mode {
			return "", nil, errors.New("pareamento pendente pertence a outro modo de agente")
		}
		if time.Now().After(pending.ExpiresAtUTC) {
			pending = enrollment{}
		}
	} else if !os.IsNotExist(err) {
		return "", nil, err
	}
	if pending.ID == "" {
		pending.Mode = mode
		key, err := rsa.GenerateKey(rand.Reader, 3072)
		if err != nil {
			return "", nil, err
		}
		der, err := x509.CreateCertificateRequest(rand.Reader, &x509.CertificateRequest{Subject: pkix.Name{CommonName: "SLT Rescue enrollment"}}, key)
		if err != nil {
			return "", nil, err
		}
		secret := make([]byte, 32)
		if _, err = rand.Read(secret); err != nil {
			return "", nil, err
		}
		pending.Secret = hex.EncodeToString(secret)
		pending.KeyPEM = string(pem.EncodeToMemory(&pem.Block{Type: "RSA PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}))
		hostname, _ := os.Hostname()
		var result struct {
			ID           string    `json:"id"`
			Code         string    `json:"code"`
			ExpiresAtUTC time.Time `json:"expiresAtUtc"`
		}
		_, err = c.JSON(ctx, "POST", "/agent/v1/enroll", map[string]string{"hostname": hostname, "mode": mode, "csrPem": string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE REQUEST", Bytes: der})), "secret": pending.Secret}, &result)
		if err != nil {
			return "", nil, err
		}
		pending.ID = result.ID
		pending.Code = result.Code
		pending.ExpiresAtUTC = result.ExpiresAtUTC
		data, _ := json.Marshal(pending)
		if err = atomicPrivateFile(pendingPath, data); err != nil {
			return "", nil, err
		}
	}
	fmt.Fprintf(output, "Pareamento %s: código %s. Confira e aprove no painel do servidor.\n", mode, pending.Code)
	for {
		if !time.Now().Before(pending.ExpiresAtUTC) {
			return "", nil, errors.New("pareamento expirou; execute novamente")
		}
		data, _, err := c.Request(ctx, "GET", "/agent/v1/enroll/"+url.PathEscape(pending.ID), nil, map[string]string{"X-Enrollment-Secret": pending.Secret})
		if err != nil {
			return "", nil, err
		}
		var result struct {
			Status         string `json:"status"`
			DeviceID       string `json:"deviceId"`
			CertificatePEM string `json:"certificatePem"`
		}
		if err = json.Unmarshal(data, &result); err != nil {
			return "", nil, err
		}
		switch result.Status {
		case "APPROVED":
			cert, err := tls.X509KeyPair([]byte(result.CertificatePEM), []byte(pending.KeyPEM))
			if err != nil {
				return "", nil, err
			}
			leaf, err := x509.ParseCertificate(cert.Certificate[0])
			if err != nil {
				return "", nil, err
			}
			if leaf.Subject.CommonName != result.DeviceID {
				return "", nil, errors.New("certificado não corresponde ao deviceId")
			}
			saved := credentials{DeviceID: result.DeviceID, CertificatePEM: result.CertificatePEM, KeyPEM: pending.KeyPEM, Mode: mode}
			data, _ := json.Marshal(saved)
			if err = atomicPrivateFile(path, data); err != nil {
				return "", nil, err
			}
			if err = os.Remove(pendingPath); err != nil {
				return "", nil, err
			}
			return result.DeviceID, &cert, nil
		case "PENDING":
		case "EXPIRED", "REJECTED":
			return "", nil, errors.New("pareamento expirado ou rejeitado")
		default:
			return "", nil, errors.New("estado de pareamento desconhecido")
		}
		select {
		case <-ctx.Done():
			return "", nil, ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
}
