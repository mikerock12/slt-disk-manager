package agent

// Kamikaze mode: the SLT server reboots itself into a WinPE/WinRE kept in RAM and runs, on its
// own disks, the operations the operator prepared in the panel. The plan is signed by the
// Controller before the reboot, verified here against the certificate baked into the boot
// image, confirmed by a person typing a phrase at the server console, and every disk is
// re-identified right before it is written. Nothing is repeated automatically.

import (
	"bufio"
	"context"
	"crypto"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/x509"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"hash/crc32"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

const (
	KamikazeDirectory = "SLT-Kamikaze"
	kamikazeChunk     = 4 * 1024 * 1024
	kamikazeMaxOps    = 8
)

type KamikazeDisk struct {
	Identity           string `json:"identity"`
	Model              string `json:"model"`
	Serial             string `json:"serial"`
	SizeBytes          int64  `json:"sizeBytes"`
	LogicalSectorBytes int    `json:"logicalSectorBytes"`
}

type KamikazeDeployment struct {
	// Relative to the root of the volume that holds the bundle (the server repository volume).
	MediaPath      string             `json:"mediaPath"`
	MediaSHA256    string             `json:"mediaSha256"`
	MediaSizeBytes int64              `json:"mediaSizeBytes"`
	ImageIndex     int                `json:"imageIndex"`
	Firmware       string             `json:"firmware"`
	Account        *DeploymentAccount `json:"account,omitempty"`
}

type KamikazeOperation struct {
	Kind       string              `json:"kind"`
	Target     KamikazeDisk        `json:"target"`
	Source     *KamikazeDisk       `json:"source,omitempty"`
	Format     *FormatOptions      `json:"format,omitempty"`
	Deployment *KamikazeDeployment `json:"deployment,omitempty"`
	Challenge  string              `json:"challenge"`
}

type KamikazeBundle struct {
	SchemaVersion int                 `json:"schemaVersion"`
	SessionID     string              `json:"sessionId"`
	ServerName    string              `json:"serverName"`
	CreatedAtUTC  time.Time           `json:"createdAtUtc"`
	ExpiresAtUTC  time.Time           `json:"expiresAtUtc"`
	Operations    []KamikazeOperation `json:"operations"`
}

type KamikazeOperationResult struct {
	Kind    string `json:"kind"`
	Success bool   `json:"success"`
	Message string `json:"message"`
	Details string `json:"details,omitempty"`
}

type KamikazeResult struct {
	SessionID     string                    `json:"sessionId"`
	StartedAtUTC  time.Time                 `json:"startedAtUtc"`
	FinishedAtUTC time.Time                 `json:"finishedAtUtc"`
	Success       bool                      `json:"success"`
	Cancelled     bool                      `json:"cancelled"`
	Message       string                    `json:"message"`
	Operations    []KamikazeOperationResult `json:"operations"`
}

// LocalDisk is a physical disk seen by this machine, numbered as \\.\PhysicalDriveN.
type LocalDisk struct {
	Number             int    `json:"number"`
	Identity           string `json:"identity"`
	Model              string `json:"model"`
	Serial             string `json:"serial"`
	SizeBytes          int64  `json:"sizeBytes"`
	LogicalSectorBytes int    `json:"logicalSectorBytes"`
	Offline            bool   `json:"offline"`
	ReadOnly           bool   `json:"readOnly"`
}

type rawDisk interface {
	io.ReaderAt
	io.WriterAt
	Sync() error
	Close() error
}

type kamikazePlatform interface {
	winPEPlatform
	LocalDisks(ctx context.Context) ([]LocalDisk, error)
	Roots() []string
	VolumeDisks(root string) ([]int, error)
	OpenDisk(number int, write bool) (rawDisk, error)
	SetOffline(ctx context.Context, number int, offline bool) error
	Reboot(ctx context.Context) error
}

type KamikazeOptions struct {
	SessionID string
	TrustPath string
	NoReboot  bool
	Input     io.Reader
	Output    io.Writer
	Now       func() time.Time
}

var (
	kamikazeHex        = regexp.MustCompile(`^[0-9a-f]{64}$`)
	kamikazeMediaPath  = regexp.MustCompile(`^[A-Za-z0-9 ._()\\-]{1,400}$`)
	kamikazeChallenge  = regexp.MustCompile(`^[A-Z0-9 ]{6,64}$`)
	kamikazeServerName = regexp.MustCompile(`^[A-Za-z0-9 ._-]{1,64}$`)
)

// DiskIdentity does not include the disk number: WinPE may enumerate the disks in another order
// than the Windows that prepared the plan.
func DiskIdentity(model, serial string, size int64, sector int) string {
	h := sha256.Sum256([]byte(fmt.Sprintf("%s|%s|%d|%d", model, serial, size, sector)))
	return hex.EncodeToString(h[:])
}

func validKamikazeDisk(d KamikazeDisk) bool {
	return kamikazeHex.MatchString(d.Identity) && d.SizeBytes > 0 && (d.LogicalSectorBytes == 512 || d.LogicalSectorBytes == 4096) &&
		d.Identity == DiskIdentity(d.Model, d.Serial, d.SizeBytes, d.LogicalSectorBytes)
}

func validKamikazeMedia(path string) bool {
	if !kamikazeMediaPath.MatchString(path) || strings.HasPrefix(path, `\`) {
		return false
	}
	for _, part := range strings.Split(path, `\`) {
		if part == "" || part == "." || part == ".." || strings.TrimRight(part, " .") != part {
			return false
		}
	}
	return true
}

func VerifyKamikazeBundle(s SignedAssignment, certificate *x509.Certificate, sessionID string, now time.Time) (KamikazeBundle, error) {
	var b KamikazeBundle
	if len(s.Payload) == 0 || len(s.Payload) > 262144 {
		return b, errors.New("pacote kamikaze vazio ou grande demais")
	}
	// Same RSA PKCS#1 v1.5 check as the job plans; the payload is never parsed before it verifies.
	if err := verifyRSA(s, certificate); err != nil {
		return b, err
	}
	if err := json.Unmarshal(s.Payload, &b); err != nil {
		return b, err
	}
	if b.SchemaVersion != 1 || !identifierPattern.MatchString(b.SessionID) || b.SessionID != sessionID || !kamikazeServerName.MatchString(b.ServerName) {
		return b, errors.New("pacote de outra sessão ou versão")
	}
	// The WinPE clock may run several hours off: Windows keeps the RTC in local time and WinPE
	// assumes another time zone. Plans last at most 24 h; 14 h of tolerance either way.
	if !b.ExpiresAtUTC.After(now) || b.ExpiresAtUTC.After(now.Add(38*time.Hour)) || b.CreatedAtUTC.After(now.Add(14*time.Hour)) || !b.ExpiresAtUTC.After(b.CreatedAtUTC) {
		return b, errors.New("pacote kamikaze expirado ou com validade inválida; prepare outro no painel")
	}
	if len(b.Operations) == 0 || len(b.Operations) > kamikazeMaxOps {
		return b, errors.New("quantidade de operações inválida")
	}
	targets := map[string]bool{}
	sources := map[string]bool{}
	for i, op := range b.Operations {
		if !validKamikazeDisk(op.Target) || !kamikazeChallenge.MatchString(op.Challenge) {
			return b, fmt.Errorf("operação %d: disco ou confirmação inválidos", i+1)
		}
		if targets[op.Target.Identity] || sources[op.Target.Identity] {
			return b, fmt.Errorf("operação %d: o mesmo disco é gravado mais de uma vez ou depois de servir de origem", i+1)
		}
		switch op.Kind {
		case "CLONE_DISK":
			if op.Source == nil || !validKamikazeDisk(*op.Source) || op.Source.Identity == op.Target.Identity || op.Source.LogicalSectorBytes != op.Target.LogicalSectorBytes || targets[op.Source.Identity] || op.Format != nil || op.Deployment != nil {
				return b, fmt.Errorf("operação %d: clone com origem inválida", i+1)
			}
			sources[op.Source.Identity] = true
		case "FORMAT_DISK":
			if op.Source != nil || op.Deployment != nil || validateFormat(op.Format) != nil || op.Format.FileSystem == "EXT4" {
				return b, fmt.Errorf("operação %d: formatação inválida (NTFS, exFAT ou FAT32)", i+1)
			}
		case "DEPLOY_WINDOWS":
			d := op.Deployment
			if op.Source != nil || op.Format != nil || d == nil || !validKamikazeMedia(d.MediaPath) || !kamikazeHex.MatchString(d.MediaSHA256) || d.MediaSizeBytes < 208 || d.ImageIndex < 1 || d.ImageIndex > 1000 || (d.Firmware != "GPT_UEFI" && d.Firmware != "MBR_BIOS") {
				return b, fmt.Errorf("operação %d: instalação do Windows inválida", i+1)
			}
			if d.Account != nil {
				if err := validateAccount(d.Account, false); err != nil {
					return b, fmt.Errorf("operação %d: %w", i+1, err)
				}
			}
		default:
			return b, fmt.Errorf("operação %d: tipo não permitido", i+1)
		}
		targets[op.Target.Identity] = true
	}
	return b, nil
}

func verifyRSA(s SignedAssignment, certificate *x509.Certificate) error {
	if certificate == nil {
		return errors.New("certificado do servidor ausente")
	}
	key, ok := certificate.PublicKey.(*rsa.PublicKey)
	if !ok || key.N.BitLen() < 2048 {
		return errors.New("pacote exige chave RSA do servidor de pelo menos 2048 bits")
	}
	hash := sha256.Sum256(s.Payload)
	if err := rsa.VerifyPKCS1v15(key, crypto.SHA256, hash[:], s.Signature); err != nil {
		return errors.New("assinatura do pacote kamikaze inválida")
	}
	return nil
}

func LoadTrustCertificate(path string) (*x509.Certificate, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("certificado do servidor ausente na imagem de boot: %w", err)
	}
	block, _ := pem.Decode(data)
	if block == nil || block.Type != "CERTIFICATE" {
		return nil, errors.New("certificado do servidor inválido")
	}
	return x509.ParseCertificate(block.Bytes)
}

// resolveDisk finds the one disk that matches the identity planned in Windows.
func resolveDisk(want KamikazeDisk, disks []LocalDisk) (LocalDisk, error) {
	var found []LocalDisk
	for _, d := range disks {
		if d.Identity == want.Identity {
			found = append(found, d)
		}
	}
	if len(found) != 1 {
		return LocalDisk{}, fmt.Errorf("disco %s %s (%d bytes): %d correspondências; nada foi gravado", want.Model, want.Serial, want.SizeBytes, len(found))
	}
	d := found[0]
	if d.SizeBytes != want.SizeBytes || d.LogicalSectorBytes != want.LogicalSectorBytes {
		return LocalDisk{}, errors.New("geometria do disco mudou desde o planejamento")
	}
	if d.ReadOnly {
		return LocalDisk{}, fmt.Errorf("disco %d está protegido contra gravação", d.Number)
	}
	return d, nil
}

type kamikazeRunner struct {
	options    KamikazeOptions
	platform   kamikazePlatform
	bundle     KamikazeBundle
	bundleDir  string
	bundleDisk map[int]bool
	reader     *bufio.Reader
}

func (r *kamikazeRunner) say(format string, args ...any) {
	fmt.Fprintf(r.options.Output, format, args...)
}

func (r *kamikazeRunner) ask(prompt string) string {
	r.say("%s", prompt)
	text, err := r.reader.ReadString('\n')
	if err != nil && text == "" {
		return ""
	}
	return strings.ToUpper(strings.TrimSpace(text))
}

func findKamikazeBundle(p kamikazePlatform, session string) (string, error) {
	var found []string
	for _, root := range p.Roots() {
		dir := filepath.Join(root, KamikazeDirectory, session)
		if info, err := os.Stat(filepath.Join(dir, "bundle.json")); err == nil && info.Mode().IsRegular() {
			found = append(found, dir)
		}
	}
	if len(found) != 1 {
		return "", fmt.Errorf("pacote da sessão %s encontrado em %d volumes; esperado exatamente 1", session, len(found))
	}
	return found[0], nil
}

// RunKamikaze is the console program started by winpeshl.ini inside the kamikaze boot image.
func RunKamikaze(ctx context.Context, options KamikazeOptions) error {
	return runKamikaze(ctx, newKamikazePlatform(), options)
}

func runKamikaze(ctx context.Context, platform kamikazePlatform, options KamikazeOptions) (runErr error) {
	if options.Input == nil {
		options.Input = os.Stdin
	}
	if options.Output == nil {
		options.Output = os.Stdout
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	r := &kamikazeRunner{options: options, platform: platform, reader: bufio.NewReader(io.LimitReader(options.Input, 1<<20))}
	r.say("\n==============================================================\n SLT DISK MANAGER - MODO KAMIKAZE\n==============================================================\n")
	defer func() {
		if runErr != nil {
			r.say("\nERRO: %v\n", runErr)
		}
		if options.NoReboot {
			return
		}
		r.say("\nO servidor vai reiniciar em 60 segundos (Enter reinicia agora).\n")
		done := make(chan struct{})
		go func() { r.reader.ReadString('\n'); close(done) }()
		select {
		case <-done:
		case <-time.After(60 * time.Second):
		case <-ctx.Done():
		}
		if err := platform.Reboot(context.Background()); err != nil {
			r.say("Reinício automático falhou (%v); desligue e ligue o servidor.\n", err)
		}
	}()
	if !identifierPattern.MatchString(options.SessionID) {
		return errors.New("sessão kamikaze inválida")
	}
	if _, err := platform.Check(ctx); err != nil {
		return err
	}
	dir, err := findKamikazeBundle(platform, options.SessionID)
	if err != nil {
		return err
	}
	r.bundleDir = dir
	data, err := os.ReadFile(filepath.Join(dir, "bundle.json"))
	if err != nil {
		return err
	}
	var signed SignedAssignment
	if err = json.Unmarshal(data, &signed); err != nil {
		return fmt.Errorf("pacote ilegível: %w", err)
	}
	cert, err := LoadTrustCertificate(options.TrustPath)
	if err != nil {
		return err
	}
	if r.bundle, err = VerifyKamikazeBundle(signed, cert, options.SessionID, options.Now()); err != nil {
		return err
	}
	// Single use: a started marker without a result means an earlier attempt was interrupted.
	if _, err = os.Stat(filepath.Join(dir, "result.json")); err == nil {
		return errors.New("este pacote já foi executado; prepare outro no painel")
	}
	if _, err = os.Stat(filepath.Join(dir, "started.json")); err == nil {
		return errors.New("uma execução anterior deste pacote foi interrompida; confira os discos e prepare outro pacote no painel")
	}
	started := options.Now().UTC()
	if err = atomicPrivateFile(filepath.Join(dir, "started.json"), []byte(fmt.Sprintf(`{"sessionId":%q,"startedAtUtc":%q}`, r.bundle.SessionID, started.Format(time.RFC3339)))); err != nil {
		return fmt.Errorf("não foi possível registrar o início: %w", err)
	}
	result := KamikazeResult{SessionID: r.bundle.SessionID, StartedAtUTC: started}
	defer func() {
		result.FinishedAtUTC = options.Now().UTC()
		if runErr != nil && result.Message == "" {
			result.Message = runErr.Error()
		}
		data, _ := json.MarshalIndent(result, "", "  ")
		if err := atomicPrivateFile(filepath.Join(dir, "result.json"), data); err != nil {
			r.say("\nResultado não pôde ser gravado no disco (%v). Anote o resumo acima.\n", err)
		}
	}()
	if r.bundleDisk, err = r.disksOf(dir); err != nil {
		return err
	}
	disks, err := platform.LocalDisks(ctx)
	if err != nil {
		return err
	}
	r.say("Servidor: %s | sessão %s | %d operação(ões)\n", r.bundle.ServerName, r.bundle.SessionID, len(r.bundle.Operations))
	for i, op := range r.bundle.Operations {
		target, e := resolveDisk(op.Target, disks)
		if e != nil {
			return fmt.Errorf("operação %d: %w", i+1, e)
		}
		if op.Kind == "DEPLOY_WINDOWS" && r.bundleDisk[target.Number] {
			return fmt.Errorf("operação %d: a mídia do Windows está no disco de destino", i+1)
		}
		r.say("\n[%d] %s\n    DESTINO: disco %d | %s | serial %s | %s\n", i+1, kamikazeLabel(op), target.Number, target.Model, emptyDash(target.Serial), humanBytes(target.SizeBytes))
		if op.Source != nil {
			source, e := resolveDisk(*op.Source, disks)
			if e != nil {
				return fmt.Errorf("operação %d (origem): %w", i+1, e)
			}
			r.say("    ORIGEM:  disco %d | %s | serial %s | %s\n", source.Number, source.Model, emptyDash(source.Serial), humanBytes(source.SizeBytes))
		}
		r.say("    TUDO NO DESTINO SERÁ APAGADO.\n")
	}
	r.say("\nConfirme cada operação digitando a frase indicada. Digite CANCELAR para voltar ao Windows sem gravar nada.\n")
	for i, op := range r.bundle.Operations {
		confirmed := false
		for attempt := 0; attempt < 3 && !confirmed; attempt++ {
			text := r.ask(fmt.Sprintf("[%d] Digite %q: ", i+1, op.Challenge))
			if text == "CANCELAR" {
				result.Cancelled = true
				result.Message = "Cancelado no console; nenhum disco foi gravado."
				r.say("%s\n", result.Message)
				return nil
			}
			confirmed = text == op.Challenge
			if !confirmed {
				r.say("Frase diferente.\n")
			}
		}
		if !confirmed {
			result.Cancelled = true
			result.Message = "Confirmação incorreta três vezes; nenhum disco foi gravado."
			return errors.New(result.Message)
		}
	}
	for i, op := range r.bundle.Operations {
		r.say("\n[%d] %s...\n", i+1, kamikazeLabel(op))
		details, e := r.execute(ctx, op)
		item := KamikazeOperationResult{Kind: op.Kind, Success: e == nil, Message: "Concluída", Details: details}
		if e != nil {
			item.Message = e.Error()
		}
		result.Operations = append(result.Operations, item)
		if e != nil {
			result.Message = fmt.Sprintf("Operação %d falhou; as seguintes não foram executadas.", i+1)
			return fmt.Errorf("operação %d: %w", i+1, e)
		}
		r.say("[%d] concluída.\n", i+1)
	}
	result.Success = true
	result.Message = "Todas as operações foram concluídas."
	r.say("\n%s\n", result.Message)
	return nil
}

func (r *kamikazeRunner) disksOf(path string) (map[int]bool, error) {
	numbers, err := r.platform.VolumeDisks(path)
	if err != nil {
		return nil, fmt.Errorf("disco do pacote não identificado: %w", err)
	}
	set := map[int]bool{}
	for _, n := range numbers {
		set[n] = true
	}
	return set, nil
}

func kamikazeLabel(op KamikazeOperation) string {
	switch op.Kind {
	case "CLONE_DISK":
		return "Clonar disco (cópia bruta conferida por SHA256)"
	case "FORMAT_DISK":
		return fmt.Sprintf("Formatar disco (%s, %s, rótulo %q)", op.Format.TableType, op.Format.FileSystem, op.Format.Label)
	case "DEPLOY_WINDOWS":
		return fmt.Sprintf("Instalar Windows (edição %d, %s)", op.Deployment.ImageIndex, op.Deployment.Firmware)
	}
	return op.Kind
}

func emptyDash(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

func humanBytes(n int64) string {
	return fmt.Sprintf("%.1f GB", float64(n)/1e9)
}

// revalidate re-reads the disks right before a write.
func (r *kamikazeRunner) revalidate(ctx context.Context, want KamikazeDisk) (LocalDisk, error) {
	disks, err := r.platform.LocalDisks(ctx)
	if err != nil {
		return LocalDisk{}, err
	}
	return resolveDisk(want, disks)
}

func (r *kamikazeRunner) execute(ctx context.Context, op KamikazeOperation) (string, error) {
	switch op.Kind {
	case "CLONE_DISK":
		return r.clone(ctx, op)
	case "FORMAT_DISK":
		return r.format(ctx, op)
	case "DEPLOY_WINDOWS":
		return r.deploy(ctx, op)
	}
	return "", errors.New("tipo não suportado")
}

func (r *kamikazeRunner) clone(ctx context.Context, op KamikazeOperation) (string, error) {
	source, err := r.revalidate(ctx, *op.Source)
	if err != nil {
		return "", err
	}
	target, err := r.revalidate(ctx, op.Target)
	if err != nil {
		return "", err
	}
	src, err := r.platform.OpenDisk(source.Number, false)
	if err != nil {
		return "", err
	}
	defer src.Close()
	length, err := CloneLength(src, source.SizeBytes, target.SizeBytes, source.LogicalSectorBytes)
	if err != nil {
		return "", err
	}
	for _, n := range []int{source.Number, target.Number} {
		if err = r.platform.SetOffline(ctx, n, true); err != nil {
			return "", fmt.Errorf("disco %d não ficou offline: %w", n, err)
		}
	}
	defer r.platform.SetOffline(context.Background(), source.Number, false)
	dst, err := r.platform.OpenDisk(target.Number, true)
	if err != nil {
		return "", err
	}
	defer dst.Close()
	report := func(done int64) {
		r.say("\r    %3d%% (%s de %s)", done*100/length, humanBytes(done), humanBytes(length))
	}
	if err = CopyAndVerify(ctx, src, dst, length, report); err != nil {
		return "", err
	}
	r.say("\n")
	relocated := false
	if length != source.SizeBytes || target.SizeBytes != source.SizeBytes {
		if relocated, err = RelocateGPT(dst, target.SizeBytes, target.LogicalSectorBytes); err != nil {
			return "", fmt.Errorf("cópia conferida, mas a GPT de reserva não foi ajustada: %w", err)
		}
	}
	details, _ := json.Marshal(map[string]any{"bytesCopied": length, "verified": true, "gptRelocated": relocated, "sourceBytes": source.SizeBytes, "targetBytes": target.SizeBytes})
	return string(details), nil
}

func (r *kamikazeRunner) format(ctx context.Context, op KamikazeOperation) (string, error) {
	target, err := r.revalidate(ctx, op.Target)
	if err != nil {
		return "", err
	}
	if op.Format.FileSystem == "FAT32" && target.SizeBytes > 32*1024*1024*1024 {
		return "", errors.New("FAT32 do Windows limitado a 32 GB; use exFAT ou NTFS")
	}
	firmware, err := r.platform.Check(ctx)
	if err != nil {
		return "", err
	}
	tools, err := r.platform.Tools(firmware, "")
	if err != nil {
		return "", err
	}
	script, err := BuildKamikazeFormatScript(target.Number, *op.Format, target.Offline)
	if err != nil {
		return "", err
	}
	path := filepath.Join(os.TempDir(), "slt-kamikaze-format.txt")
	if err = atomicPrivateFile(path, []byte(script)); err != nil {
		return "", err
	}
	if err = r.platform.Run(ctx, tools["diskpart"], []string{"/s", path}, r.options.Output); err != nil {
		return "", err
	}
	details, _ := json.Marshal(map[string]any{"tableType": op.Format.TableType, "fileSystem": op.Format.FileSystem, "label": op.Format.Label})
	return string(details), nil
}

// BuildKamikazeFormatScript writes one partition across the disk. No "noerr": any failure stops DiskPart.
func BuildKamikazeFormatScript(disk int, o FormatOptions, offline bool) (string, error) {
	if disk < 0 || disk > 4095 {
		return "", errors.New("disco inválido")
	}
	if err := validateFormat(&o); err != nil || o.FileSystem == "EXT4" {
		return "", errors.New("formatação inválida")
	}
	lines := []string{fmt.Sprintf("select disk %d", disk)}
	if offline {
		lines = append(lines, "online disk")
	}
	label := ""
	if o.Label != "" {
		label = fmt.Sprintf(` label="%s"`, o.Label)
	}
	lines = append(lines, "attributes disk clear readonly", "clean", "convert "+strings.ToLower(o.TableType), "create partition primary", "format quick fs="+strings.ToLower(o.FileSystem)+label, "assign", "exit")
	return strings.Join(lines, "\r\n") + "\r\n", nil
}

func (r *kamikazeRunner) deploy(ctx context.Context, op KamikazeOperation) (string, error) {
	d := op.Deployment
	firmware, err := r.platform.Check(ctx)
	if err != nil {
		return "", err
	}
	if firmware != d.Firmware {
		return "", errors.New("o servidor iniciou o modo kamikaze em outro firmware que o escolhido para a instalação")
	}
	bootsect := ""
	if firmware == "MBR_BIOS" {
		bootsect = kamikazeBootsect()
	}
	tools, err := r.platform.Tools(firmware, bootsect)
	if err != nil {
		return "", err
	}
	root := filepath.VolumeName(r.bundleDir) + `\`
	media := filepath.Join(root, d.MediaPath)
	if !safeWinPEPath(media) {
		return "", errors.New("caminho da mídia inválido")
	}
	r.say("    Conferindo a mídia por SHA256...\n")
	if err = verifyFileHash(ctx, media, d.MediaSizeBytes, d.MediaSHA256); err != nil {
		return "", err
	}
	target, err := r.revalidate(ctx, op.Target)
	if err != nil {
		return "", err
	}
	system, windows, err := r.platform.Letters()
	if err != nil {
		return "", err
	}
	script, err := BuildWinPEDiskPart(target.Number, firmware, system, windows, target.Offline)
	if err != nil {
		return "", err
	}
	scratch := filepath.Join(r.bundleDir, "scratch")
	if err = os.MkdirAll(scratch, 0700); err != nil {
		return "", err
	}
	commands, err := BuildWinPECommands(media, scratch, d.ImageIndex, firmware, system, windows)
	if err != nil {
		return "", err
	}
	scriptPath := filepath.Join(scratch, "partitions.txt")
	if err = atomicPrivateFile(scriptPath, []byte(script)); err != nil {
		return "", err
	}
	if err = r.platform.Run(ctx, tools["diskpart"], []string{"/s", scriptPath}, r.options.Output); err != nil {
		return "", fmt.Errorf("particionamento falhou; destino pode estar parcial: %w", err)
	}
	if err = r.platform.VerifyVolumes(target.Number, firmware, system, windows); err != nil {
		return "", err
	}
	for _, c := range commands {
		if err = r.platform.Run(ctx, tools[c.Tool], c.Args, r.options.Output); err != nil {
			return "", fmt.Errorf("%s falhou; destino pode estar parcial: %w", c.Tool, err)
		}
	}
	if err = r.platform.VerifyApplied(windows); err != nil {
		return "", err
	}
	unattend := false
	if d.Account != nil {
		content, e := BuildUnattend(d.Account)
		if e != nil {
			return "", e
		}
		panther := fmt.Sprintf("%c:\\Windows\\Panther", windows)
		if err = os.MkdirAll(panther, 0700); err != nil {
			return "", err
		}
		if err = os.WriteFile(filepath.Join(panther, "unattend.xml"), []byte(content), 0600); err != nil {
			return "", err
		}
		unattend = true
	}
	if err = r.platform.VerifyBoot(system, firmware); err != nil {
		return "", err
	}
	for _, letter := range []byte{system, windows} {
		if err = r.platform.FlushVolume(letter); err != nil {
			return "", err
		}
	}
	details, _ := json.Marshal(map[string]any{"imageIndex": d.ImageIndex, "firmware": firmware, "applied": true, "unattend": unattend})
	return string(details), nil
}

func verifyFileHash(ctx context.Context, path string, size int64, want string) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil || info.Size() != size {
		return errors.New("tamanho da mídia não confere com o plano")
	}
	h := sha256.New()
	buffer := make([]byte, kamikazeChunk)
	for {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		n, e := f.Read(buffer)
		h.Write(buffer[:n])
		if e == io.EOF {
			break
		}
		if e != nil {
			return e
		}
	}
	if hex.EncodeToString(h.Sum(nil)) != want {
		return errors.New("SHA256 da mídia não confere; nada foi gravado")
	}
	return nil
}

// CloneLength returns how many bytes of the source are copied. A smaller target is accepted
// only when every partition ends before it (GPT keeps room for its backup copy at the end).
func CloneLength(src io.ReaderAt, sourceBytes, targetBytes int64, sector int) (int64, error) {
	if sourceBytes <= 0 || targetBytes <= 0 || sector <= 0 || sourceBytes%int64(sector) != 0 || targetBytes%int64(sector) != 0 {
		return 0, errors.New("geometria inválida")
	}
	if targetBytes >= sourceBytes {
		return sourceBytes, nil
	}
	end, gpt, err := partitionsEnd(src, sourceBytes, sector)
	if err != nil {
		return 0, err
	}
	limit := targetBytes
	if gpt {
		limit -= 33 * int64(sector) // backup partition array (16 KiB) and header
	}
	if end > limit {
		return 0, fmt.Errorf("destino menor que o espaço ocupado pelas partições da origem (%s necessários); reduza a última partição antes", humanBytes(end))
	}
	length := (end + 1024*1024 - 1) / (1024 * 1024) * (1024 * 1024)
	if length > limit {
		length = end
	}
	return length, nil
}

// partitionsEnd reads the partition table and returns the byte after the last partition.
func partitionsEnd(f io.ReaderAt, total int64, sector int) (int64, bool, error) {
	mbr := make([]byte, 512)
	if _, err := f.ReadAt(mbr, 0); err != nil {
		return 0, false, err
	}
	if mbr[510] != 0x55 || mbr[511] != 0xAA {
		return 0, false, errors.New("origem sem tabela de partições reconhecida")
	}
	header := make([]byte, sector)
	if _, err := f.ReadAt(header, int64(sector)); err != nil {
		return 0, false, err
	}
	if string(header[:8]) == "EFI PART" {
		count := binary.LittleEndian.Uint32(header[80:84])
		size := binary.LittleEndian.Uint32(header[84:88])
		array := int64(binary.LittleEndian.Uint64(header[72:80]))
		if count == 0 || count > 1024 || size < 128 || size > 4096 {
			return 0, true, errors.New("GPT de origem inválida")
		}
		entries := make([]byte, int(count)*int(size))
		if _, err := f.ReadAt(entries, array*int64(sector)); err != nil {
			return 0, true, err
		}
		if crc32.ChecksumIEEE(entries) != binary.LittleEndian.Uint32(header[88:92]) {
			return 0, true, errors.New("CRC da GPT de origem inválido")
		}
		var last uint64
		for i := 0; i < int(count); i++ {
			e := entries[i*int(size):]
			if allZero(e[:16]) {
				continue
			}
			if end := binary.LittleEndian.Uint64(e[40:48]); end > last {
				last = end
			}
		}
		if last == 0 {
			return 34 * int64(sector), true, nil
		}
		return (int64(last) + 1) * int64(sector), true, nil
	}
	var last int64
	for i := 0; i < 4; i++ {
		e := mbr[446+i*16:]
		start := int64(binary.LittleEndian.Uint32(e[8:12]))
		length := int64(binary.LittleEndian.Uint32(e[12:16]))
		if e[4] != 0 && length > 0 {
			if end := (start + length) * int64(sector); end > last {
				last = end
			}
		}
	}
	if last == 0 || last > total {
		return 0, false, errors.New("tabela MBR de origem vazia ou inválida")
	}
	return last, false, nil
}

// CopyAndVerify copies length bytes, flushes, then re-reads the target and compares the
// SHA256 of each 4 MiB block with what was read from the source.
func CopyAndVerify(ctx context.Context, src io.ReaderAt, dst rawDisk, length int64, progress func(int64)) error {
	buffer := make([]byte, kamikazeChunk)
	var hashes [][32]byte
	for offset := int64(0); offset < length; offset += kamikazeChunk {
		if err := ctx.Err(); err != nil {
			return err
		}
		n := int64(kamikazeChunk)
		if length-offset < n {
			n = length - offset
		}
		block := buffer[:n]
		if _, err := src.ReadAt(block, offset); err != nil {
			return fmt.Errorf("leitura da origem em %d: %w", offset, err)
		}
		hashes = append(hashes, sha256.Sum256(block))
		if _, err := dst.WriteAt(block, offset); err != nil {
			return fmt.Errorf("gravação no destino em %d: %w", offset, err)
		}
		if progress != nil && (offset/kamikazeChunk)%64 == 0 {
			progress(offset + n)
		}
	}
	if err := dst.Sync(); err != nil {
		return fmt.Errorf("gravação não confirmada pelo disco: %w", err)
	}
	for i, offset := 0, int64(0); offset < length; i, offset = i+1, offset+kamikazeChunk {
		if err := ctx.Err(); err != nil {
			return err
		}
		n := int64(kamikazeChunk)
		if length-offset < n {
			n = length - offset
		}
		block := buffer[:n]
		if _, err := dst.ReadAt(block, offset); err != nil {
			return fmt.Errorf("releitura do destino em %d: %w", offset, err)
		}
		if sha256.Sum256(block) != hashes[i] {
			return fmt.Errorf("conferência SHA256 falhou no bloco %d; o destino não é uma cópia fiel", i)
		}
	}
	if progress != nil {
		progress(length)
	}
	return nil
}

// RelocateGPT moves the backup GPT to the real end of a disk that received a copy of a GPT
// disk of another size, and extends the usable area. Returns false for MBR disks.
func RelocateGPT(d rawDisk, totalBytes int64, sector int) (bool, error) {
	header := make([]byte, sector)
	if _, err := d.ReadAt(header, int64(sector)); err != nil {
		return false, err
	}
	if string(header[:8]) != "EFI PART" {
		return false, nil
	}
	size := binary.LittleEndian.Uint32(header[12:16])
	if size < 92 || size > uint32(sector) {
		return false, errors.New("cabeçalho GPT inválido")
	}
	crc := binary.LittleEndian.Uint32(header[16:20])
	check := append([]byte(nil), header[:size]...)
	binary.LittleEndian.PutUint32(check[16:20], 0)
	if crc32.ChecksumIEEE(check) != crc {
		return false, errors.New("CRC do cabeçalho GPT copiado inválido")
	}
	count := binary.LittleEndian.Uint32(header[80:84])
	entrySize := binary.LittleEndian.Uint32(header[84:88])
	array := int64(binary.LittleEndian.Uint64(header[72:80]))
	if count == 0 || count > 1024 || entrySize < 128 || entrySize > 4096 || array != 2 {
		return false, errors.New("array GPT não convencional")
	}
	entries := make([]byte, int(count)*int(entrySize))
	if _, err := d.ReadAt(entries, array*int64(sector)); err != nil {
		return false, err
	}
	if crc32.ChecksumIEEE(entries) != binary.LittleEndian.Uint32(header[88:92]) {
		return false, errors.New("CRC do array GPT copiado inválido")
	}
	last := totalBytes/int64(sector) - 1
	arraySectors := (int64(len(entries)) + int64(sector) - 1) / int64(sector)
	lastUsable := last - arraySectors - 1
	for i := 0; i < int(count); i++ {
		e := entries[i*int(entrySize):]
		if !allZero(e[:16]) && int64(binary.LittleEndian.Uint64(e[40:48])) > lastUsable {
			return false, errors.New("partição ultrapassa o fim do destino")
		}
	}
	seal := func(h []byte) {
		binary.LittleEndian.PutUint32(h[16:20], 0)
		binary.LittleEndian.PutUint32(h[16:20], crc32.ChecksumIEEE(h[:size]))
	}
	primary := append([]byte(nil), header...)
	binary.LittleEndian.PutUint64(primary[32:40], uint64(last))
	binary.LittleEndian.PutUint64(primary[48:56], uint64(lastUsable))
	seal(primary)
	backup := append([]byte(nil), primary...)
	binary.LittleEndian.PutUint64(backup[24:32], uint64(last))
	binary.LittleEndian.PutUint64(backup[32:40], 1)
	binary.LittleEndian.PutUint64(backup[72:80], uint64(last-arraySectors))
	seal(backup)
	padded := make([]byte, arraySectors*int64(sector))
	copy(padded, entries)
	if _, err := d.WriteAt(padded, (last-arraySectors)*int64(sector)); err != nil {
		return false, err
	}
	if _, err := d.WriteAt(backup, last*int64(sector)); err != nil {
		return false, err
	}
	if _, err := d.WriteAt(primary, int64(sector)); err != nil {
		return false, err
	}
	// Protective MBR: partition 1 covers the whole disk (capped at 2^32-1 sectors).
	mbr := make([]byte, 512)
	if _, err := d.ReadAt(mbr, 0); err == nil && mbr[446+4] == 0xEE {
		span := uint64(last)
		if span > 0xFFFFFFFF {
			span = 0xFFFFFFFF
		}
		binary.LittleEndian.PutUint32(mbr[446+12:446+16], uint32(span))
		if sector == 512 {
			if _, err = d.WriteAt(mbr, 0); err != nil {
				return false, err
			}
		} else {
			first := make([]byte, sector)
			if _, err = d.ReadAt(first, 0); err == nil {
				copy(first, mbr)
				if _, err = d.WriteAt(first, 0); err != nil {
					return false, err
				}
			}
		}
	}
	if err := d.Sync(); err != nil {
		return false, err
	}
	if _, _, err := verifyGPT(d, totalBytes, sector); err != nil {
		return false, fmt.Errorf("GPT ajustada não confere: %w", err)
	}
	return true, nil
}
