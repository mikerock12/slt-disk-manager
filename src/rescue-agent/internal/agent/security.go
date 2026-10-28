package agent

import (
	"crypto"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

var identifierPattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$`)

func VerifyAssignment(s SignedAssignment, certificate *x509.Certificate, deviceID string, now time.Time) (Plan, error) {
	var p Plan
	if len(s.Payload) == 0 || len(s.Payload) > 65536 {
		return p, errors.New("plano vazio ou muito grande")
	}
	if certificate == nil {
		return p, errors.New("certificado servidor ausente")
	}
	key, ok := certificate.PublicKey.(*rsa.PublicKey)
	if !ok || key.N.BitLen() < 2048 {
		return p, errors.New("plano exige chave RSA servidor de pelo menos 2048 bits")
	}
	hash := sha256.Sum256(s.Payload)
	if err := rsa.VerifyPKCS1v15(key, crypto.SHA256, hash[:], s.Signature); err != nil {
		return p, errors.New("assinatura do plano inválida")
	}
	if err := json.Unmarshal(s.Payload, &p); err != nil {
		return p, err
	}
	if !identifierPattern.MatchString(p.ID) || p.DeviceID != deviceID || !p.Consent {
		return p, errors.New("identidade, consentimento ou id de plano inválido")
	}
	if !p.ExpiresAtUTC.After(now) || p.ExpiresAtUTC.After(now.Add(24*time.Hour)) {
		return p, errors.New("plano expirado ou validade excessiva")
	}
	switch p.Kind {
	case "INVENTORY", "SMART", "CAPTURE", "RESTORE", "DEPLOY_WINDOWS", "DEPLOY_LINUX", "CONVERT_GPT":
	default:
		return p, errors.New("tipo de tarefa não permitido")
	}
	if p.Kind != "INVENTORY" && (p.DiskFingerprint == "" || p.DiskPath == "" || p.DiskSizeBytes <= 0) {
		return p, errors.New("identidade do disco incompleta")
	}
	if p.Kind == "RESTORE" && ((p.ImageID == "") == (p.StreamID == "") || strings.TrimSpace(p.Challenge) == "") {
		return p, errors.New("restauração sem imagem ou confirmação")
	}
	if p.StreamID != "" && (!identifierPattern.MatchString(p.StreamID) || p.ImageID != "" || p.Kind != "CAPTURE" && p.Kind != "RESTORE" || p.Kind == "RESTORE" && p.Clone == nil) {
		return p, errors.New("clone em lotes com identificador, tipo ou estratégia inválidos")
	}
	if p.Clone != nil {
		switch p.Clone.Strategy {
		case "EXACT_RAW", "PRESERVE_LAYOUT", "FIT":
		default:
			return p, errors.New("estratégia de clonagem não permitida")
		}
		if p.Clone.Strategy == "FIT" && p.Clone.ResizePartitionNumber < 1 {
			return p, errors.New("FIT exige partição selecionada")
		}
	}
	if p.Kind == "DEPLOY_WINDOWS" || p.Kind == "DEPLOY_LINUX" {
		d := p.Deployment
		if d == nil || !identifierPattern.MatchString(d.MediaID) || d.ImageIndex < 1 || d.ImageIndex > 1000 || (d.Firmware != "GPT_UEFI" && d.Firmware != "MBR_BIOS") || strings.TrimSpace(p.Challenge) == "" || (p.LogicalSectorBytes != 512 && p.LogicalSectorBytes != 4096) {
			return p, errors.New("implantação sem mídia, índice, firmware, setor ou confirmação válidos")
		}
		if err := validateAccount(d.Account, p.Kind == "DEPLOY_LINUX"); err != nil {
			return p, err
		}
		if d.ToolSHA256 != "" {
			return p, errors.New("ferramenta auxiliar em tipo de job diferente")
		}
	} else if p.Kind == "CONVERT_GPT" {
		d := p.Deployment
		if d == nil || !identifierPattern.MatchString(d.MediaID) || d.Firmware != "GPT_UEFI" || d.Account != nil || !sha256Pattern.MatchString(d.ToolSHA256) || strings.TrimSpace(p.Challenge) == "" {
			return p, errors.New("conversão sem ferramenta assinada, firmware UEFI ou confirmação contextual")
		}
	} else if p.Deployment != nil {
		return p, errors.New("opções de implantação em tipo de job diferente")
	}
	return p, nil
}

func ClaimJob(stateDir, jobID string) error {
	if !identifierPattern.MatchString(jobID) {
		return errors.New("id de job inválido")
	}
	dir := filepath.Join(stateDir, "ledger")
	if err := os.MkdirAll(dir, 0700); err != nil {
		return err
	}
	if err := syncDirectory(stateDir); err != nil {
		return err
	}
	f, err := os.OpenFile(filepath.Join(dir, jobID+".claimed"), os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if os.IsExist(err) {
		return errors.New("job já recebido: replay bloqueado; reconciliar no controlador")
	}
	if err != nil {
		return err
	}
	defer f.Close()
	if _, err = f.WriteString(time.Now().UTC().Format(time.RFC3339Nano)); err != nil {
		return err
	}
	if err := f.Sync(); err != nil {
		return err
	}
	return syncDirectory(dir)
}

func ValidateDisk(p Plan, disks []Disk, restore bool) (Disk, error) {
	matches := []Disk{}
	for _, d := range disks {
		if d.Fingerprint == p.DiskFingerprint {
			matches = append(matches, d)
		}
	}
	if len(matches) != 1 {
		return Disk{}, errors.New("fingerprint ausente ou ambíguo; disco pode ter mudado")
	}
	d := matches[0]
	if d.Path != p.DiskPath || d.SizeBytes != p.DiskSizeBytes || d.LogicalSectorBytes != p.LogicalSectorBytes || Fingerprint(d) != p.DiskFingerprint {
		return Disk{}, errors.New("identidade ou geometria do disco mudou")
	}
	if p.Kind == "CAPTURE" || restore {
		if d.IsSystem || d.Mounted {
			return Disk{}, errors.New("disco de sistema ou montado: operação offline bloqueada")
		}
		if d.Serial == "" || d.Health == "IDENTITY_INCOMPLETE" {
			return Disk{}, errors.New("disco sem identidade estável")
		}
	}
	if restore && d.IsReadOnly {
		return Disk{}, errors.New("destino somente leitura")
	}
	return d, nil
}

func ValidateManifest(m Manifest, p Plan) error {
	if m.SchemaVersion != 1 || m.State != "AVAILABLE" || m.ID != p.ImageID {
		return errors.New("imagem incompleta, incompatível ou diferente do plano")
	}
	if m.LogicalSizeBytes <= 0 || m.LogicalSectorBytes != p.LogicalSectorBytes {
		return errors.New("imagem maior que destino ou setor incompatível")
	}
	switch cloneStrategy(p) {
	case "EXACT_RAW":
		if m.LogicalSizeBytes != p.DiskSizeBytes {
			return errors.New("EXACT_RAW exige mesma capacidade")
		}
	case "LEGACY_RAW", "PRESERVE_LAYOUT":
		if m.LogicalSizeBytes > p.DiskSizeBytes {
			return errors.New("imagem maior que destino")
		}
	case "FIT":
		if p.DiskSizeBytes <= 0 || p.Clone.ResizePartitionNumber < 1 {
			return errors.New("destino ou partição FIT inválidos")
		}
	default:
		return errors.New("estratégia desconhecida")
	}
	if cloneStrategy(p) == "FIT" || cloneStrategy(p) == "PRESERVE_LAYOUT" {
		if err := ValidateLayout(m.Layout, m.LogicalSizeBytes); err != nil {
			return err
		}
		if m.Layout.SectorSize != m.LogicalSectorBytes {
			return errors.New("layout e manifest possuem setores diferentes")
		}
	}
	if m.SourceDeviceID == p.DeviceID && m.SourceFingerprint == p.DiskFingerprint {
		return errors.New("origem e destino são o mesmo disco")
	}
	var offset int64
	for i, c := range m.Chunks {
		if c.Sequence != i || c.OffsetBytes != offset || c.SizeBytes <= 0 || c.SizeBytes > ChunkSize || (i < len(m.Chunks)-1 && c.SizeBytes != ChunkSize) {
			return errors.New("manifest com chunks fora de ordem ou tamanhos inválidos")
		}
		hash, err := hex.DecodeString(c.SHA256)
		if err != nil || len(hash) != 32 {
			return errors.New("checksum de chunk inválido")
		}
		if int64(c.SizeBytes) > m.LogicalSizeBytes-offset {
			return errors.New("manifest excede tamanho lógico")
		}
		offset += int64(c.SizeBytes)
	}
	if offset != m.LogicalSizeBytes {
		return fmt.Errorf("imagem truncada: %d de %d bytes", offset, m.LogicalSizeBytes)
	}
	return nil
}

func atomicPrivateFile(path string, data []byte) error {
	f, err := os.OpenFile(path+".tmp", os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0600)
	if err != nil {
		return err
	}
	if _, err = f.Write(data); err != nil {
		f.Close()
		return err
	}
	if err = f.Sync(); err != nil {
		f.Close()
		return err
	}
	if err = f.Close(); err != nil {
		return err
	}
	if err := os.Rename(path+".tmp", path); err != nil {
		return err
	}
	return syncDirectory(filepath.Dir(path))
}

var accountNamePattern = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,19}$`)
var computerNamePattern = regexp.MustCompile(`^[A-Za-z0-9]([A-Za-z0-9-]{0,13}[A-Za-z0-9])?$`)
var passwordHashPattern = regexp.MustCompile(`^\$6\$[./0-9A-Za-z]{1,16}\$[./0-9A-Za-z]{86}$`)

// validateAccount keeps every value that reaches unattend.xml, chpasswd or a chroot command
// to a strict, signed alphabet.
func validateAccount(a *DeploymentAccount, linux bool) error {
	if a == nil {
		if linux {
			return errors.New("instalação Linux exige usuário inicial")
		}
		return nil
	}
	if !accountNamePattern.MatchString(a.UserName) || !computerNamePattern.MatchString(a.ComputerName) {
		return errors.New("usuário ou nome do computador inválido")
	}
	switch a.Locale {
	case "pt-BR", "en-US", "es-ES":
	default:
		return errors.New("idioma não suportado")
	}
	if linux {
		if a.Password != "" || !passwordHashPattern.MatchString(a.PasswordHash) || a.TimeZone != "America/Sao_Paulo" {
			return errors.New("conta Linux deve trazer só o hash SHA-512 e fuso suportado")
		}
		return nil
	}
	if a.PasswordHash != "" || len(a.Password) < 8 || len(a.Password) > 64 || a.TimeZone != "E. South America Standard Time" {
		return errors.New("conta Windows sem senha temporária ou fuso suportado")
	}
	for _, ch := range a.Password {
		if ch < '0' || ch > 'z' || (ch > '9' && ch < 'A') || (ch > 'Z' && ch < 'a') {
			return errors.New("senha temporária com caractere não permitido")
		}
	}
	return nil
}

var sha256Pattern = regexp.MustCompile(`^[0-9a-f]{64}$`)
