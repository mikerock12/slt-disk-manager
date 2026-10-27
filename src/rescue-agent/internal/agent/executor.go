package agent

import (
	"bufio"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

type Options struct {
	Server             string
	ServerPin          string
	StateDir           string
	FixtureDir         string
	AllowFileWrite     bool
	AllowPhysicalWrite bool
	Once               bool
	PollInterval       time.Duration
	Input              io.Reader
	Output             io.Writer
}

type Runner struct {
	Client   *Client
	Options  Options
	DeviceID string
}

func (r *Runner) report(ctx context.Context, p Plan, phase string, processed, total int64) error {
	var result struct {
		CancelRequested bool `json:"cancelRequested"`
	}
	_, err := r.Client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/progress", map[string]any{"phase": phase, "bytesProcessed": processed, "bytesTotal": total}, &result)
	if err != nil {
		return err
	}
	if result.CancelRequested {
		return errors.New("cancelamento solicitado: destino pode estar incompleto")
	}
	return nil
}

func (r *Runner) inventory(ctx context.Context) error {
	v, err := NewInventory(ctx, r.DeviceID, r.Options.FixtureDir)
	if err != nil {
		return err
	}
	_, err = r.Client.JSON(ctx, "POST", "/agent/v1/inventory", v, nil)
	return err
}

func (r *Runner) execute(ctx context.Context, p Plan) (string, error) {
	if p.Kind == "INVENTORY" {
		return "Inventário atualizado", r.inventory(ctx)
	}
	disks, err := CollectDisks(ctx, r.Options.FixtureDir)
	if err != nil {
		return "", err
	}
	d, err := ValidateDisk(p, disks, p.Kind == "RESTORE")
	if err != nil {
		return "", err
	}
	switch p.Kind {
	case "SMART":
		if r.Options.FixtureDir != "" {
			return "", errors.New("SMART indisponível para arquivo sintético")
		}
		toolCtx, cancel := context.WithTimeout(ctx, 45*time.Second)
		defer cancel()
		data, err := exec.CommandContext(toolCtx, "smartctl", "--json", "--all", d.Path).Output()
		// smartctl uses a bitmask exit status: bits 3..7 report health findings, not a failed command.
		if err != nil {
			var exit *exec.ExitError
			if !errors.As(err, &exit) || exit.ExitCode()&7 != 0 {
				return "", fmt.Errorf("smartctl: %w", err)
			}
		}
		if len(data) > 1024*1024 || !json.Valid(data) {
			return "", errors.New("resposta smartctl inválida ou excessiva")
		}
		return string(data), nil
	case "CAPTURE":
		return "Imagem raw capturada e validada no repositório", r.capture(ctx, p, d)
	case "RESTORE":
		return "Imagem raw gravada, sincronizada e verificada por releitura; boot não foi homologado", r.restore(ctx, p, d)
	default:
		return "", errors.New("tipo de job não suportado")
	}
}

func (r *Runner) openDisk(ctx context.Context, p Plan, d Disk, write bool) (*os.File, error) {
	fixture := r.Options.FixtureDir != ""
	if fixture {
		if write && !r.Options.AllowFileWrite {
			return nil, errors.New("gravação fixture requer --allow-file-write")
		}
		root, err := filepath.Abs(r.Options.FixtureDir)
		if err != nil {
			return nil, err
		}
		root, err = filepath.EvalSymlinks(root)
		if err != nil {
			return nil, err
		}
		resolved, err := filepath.EvalSymlinks(d.Path)
		if err != nil {
			return nil, err
		}
		relative, err := filepath.Rel(root, resolved)
		if err != nil || relative == ".." || strings.HasPrefix(relative, ".."+string(os.PathSeparator)) || filepath.IsAbs(relative) || !strings.HasSuffix(resolved, ".raw") {
			return nil, errors.New("arquivo fora do diretório fixture")
		}
		info, err := os.Lstat(d.Path)
		if err != nil {
			return nil, err
		}
		if !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
			return nil, errors.New("fixture deve ser arquivo regular sem link")
		}
	} else {
		if runtime.GOOS != "linux" {
			return nil, errors.New("disco físico somente no Linux Rescue")
		}
		if write && !r.Options.AllowPhysicalWrite {
			return nil, errors.New("gravação física desabilitada: exige --allow-physical-write e aprovação local")
		}
		info, err := os.Stat(d.Path)
		if err != nil {
			return nil, err
		}
		if info.Mode()&os.ModeDevice == 0 || info.Mode()&os.ModeCharDevice != 0 {
			return nil, errors.New("destino não é dispositivo de bloco")
		}
	}
	if write && !fixture {
		fmt.Fprintf(r.Options.Output, "\nATENÇÃO: sobrescrever disco %s | %s | serial %s | %d bytes.\nDigite a confirmação exata %q no teclado desta máquina: ", d.Path, d.Model, d.Serial, d.SizeBytes, p.Challenge)
		line, err := bufio.NewReader(r.Options.Input).ReadString('\n')
		if err != nil {
			return nil, fmt.Errorf("confirmação local indisponível: %w", err)
		}
		if strings.TrimSpace(line) != p.Challenge {
			return nil, errors.New("confirmação local incorreta")
		}
	}
	if !p.ExpiresAtUTC.After(time.Now()) {
		return nil, errors.New("plano expirou durante a confirmação")
	}
	before, err := CollectDisks(ctx, r.Options.FixtureDir)
	if err != nil {
		return nil, err
	}
	if _, err = ValidateDisk(p, before, write); err != nil {
		return nil, err
	}
	f, err := openExclusiveDisk(d.Path, write, fixture)
	if err != nil {
		return nil, err
	}
	// Revalidate while the Linux exclusive block-device claim is held.
	after, err := CollectDisks(ctx, r.Options.FixtureDir)
	if err == nil {
		_, err = ValidateDisk(p, after, write)
	}
	if err != nil {
		f.Close()
		return nil, err
	}
	return f, nil
}

func (r *Runner) capture(ctx context.Context, p Plan, d Disk) error {
	f, err := r.openDisk(ctx, p, d, false)
	if err != nil {
		return err
	}
	defer f.Close()
	buffer := make([]byte, ChunkSize)
	var offset int64
	for sequence := 0; offset < d.SizeBytes; sequence++ {
		if err = r.report(ctx, p, "CAPTURING", offset, d.SizeBytes); err != nil {
			return err
		}
		length := int64(ChunkSize)
		if d.SizeBytes-offset < length {
			length = d.SizeBytes - offset
		}
		if _, err = io.ReadFull(f, buffer[:int(length)]); err != nil {
			return fmt.Errorf("erro de leitura em %d; usar ddrescue para mídia defeituosa: %w", offset, err)
		}
		h := sha256.Sum256(buffer[:int(length)])
		expected := hex.EncodeToString(h[:])
		data, _, err := r.Client.Request(ctx, "PUT", fmt.Sprintf("/agent/v1/jobs/%s/chunks/%d", p.ID, sequence), bytes.NewReader(buffer[:int(length)]), map[string]string{"Content-Type": "application/octet-stream", "X-Content-SHA256": expected})
		if err != nil {
			return err
		}
		var receipt struct {
			Sequence  int    `json:"sequence"`
			SHA256    string `json:"sha256"`
			SizeBytes int    `json:"sizeBytes"`
		}
		if err = json.Unmarshal(data, &receipt); err != nil {
			return err
		}
		if receipt.Sequence != sequence || receipt.SizeBytes != int(length) || !strings.EqualFold(receipt.SHA256, expected) {
			return errors.New("recibo do chunk não confere")
		}
		offset += length
	}
	var manifest Manifest
	if _, err = r.Client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/capture-complete", struct{}{}, &manifest); err != nil {
		return err
	}
	if manifest.State != "AVAILABLE" || manifest.LogicalSizeBytes != d.SizeBytes || manifest.SourceFingerprint != d.Fingerprint {
		return errors.New("servidor não confirmou imagem disponível íntegra")
	}
	return r.report(ctx, p, "VERIFIED", offset, d.SizeBytes)
}

func (r *Runner) restore(ctx context.Context, p Plan, d Disk) error {
	var m Manifest
	if _, err := r.Client.JSON(ctx, "GET", "/agent/v1/jobs/"+p.ID+"/manifest", nil, &m); err != nil {
		return err
	}
	if err := ValidateManifest(m, p); err != nil {
		return err
	}
	if err := r.report(ctx, p, "PREFLIGHT", 0, m.LogicalSizeBytes); err != nil {
		return err
	}
	f, err := r.openDisk(ctx, p, d, true)
	if err != nil {
		return err
	}
	defer f.Close()
	for _, c := range m.Chunks {
		if err = r.report(ctx, p, "RESTORING", c.OffsetBytes, m.LogicalSizeBytes); err != nil {
			return err
		}
		data, _, err := r.Client.Request(ctx, "GET", fmt.Sprintf("/agent/v1/jobs/%s/chunks/%d", p.ID, c.Sequence), nil, nil)
		if err != nil {
			return err
		}
		h := sha256.Sum256(data)
		if len(data) != c.SizeBytes || !strings.EqualFold(hex.EncodeToString(h[:]), c.SHA256) {
			return fmt.Errorf("chunk %d corrompido: bloqueado antes da escrita", c.Sequence)
		}
		n, err := f.WriteAt(data, c.OffsetBytes)
		if err != nil {
			return err
		}
		if n != len(data) {
			return io.ErrShortWrite
		}
	}
	if err = f.Sync(); err != nil {
		return err
	}
	buffer := make([]byte, ChunkSize)
	for _, c := range m.Chunks {
		if err = r.report(ctx, p, "VERIFYING", c.OffsetBytes, m.LogicalSizeBytes); err != nil {
			return err
		}
		if _, err = f.ReadAt(buffer[:c.SizeBytes], c.OffsetBytes); err != nil {
			return err
		}
		h := sha256.Sum256(buffer[:c.SizeBytes])
		if !strings.EqualFold(hex.EncodeToString(h[:]), c.SHA256) {
			return fmt.Errorf("verificação de leitura falhou no chunk %d", c.Sequence)
		}
	}
	return r.report(ctx, p, "VERIFIED", m.LogicalSizeBytes, m.LogicalSizeBytes)
}

func Run(ctx context.Context, options Options) error {
	if options.Input == nil {
		options.Input = os.Stdin
	}
	if options.Output == nil {
		options.Output = os.Stdout
	}
	if options.PollInterval < time.Second {
		options.PollInterval = 5 * time.Second
	}
	if options.FixtureDir == "" && runtime.GOOS != "linux" {
		return errors.New("Rescue físico exige Linux; Windows pode executar somente fixtures sintéticas")
	}
	bootstrap, err := NewClient(options.Server, options.ServerPin, nil)
	if err != nil {
		return err
	}
	deviceID, certificate, err := Enroll(ctx, bootstrap, options.StateDir, options.Output)
	if err != nil {
		return err
	}
	client, err := NewClient(options.Server, options.ServerPin, certificate)
	if err != nil {
		return err
	}
	r := Runner{Client: client, Options: options, DeviceID: deviceID}
	for {
		if err = r.inventory(ctx); err != nil {
			return fmt.Errorf("inventário: %w", err)
		}
		var assignment SignedAssignment
		status, err := client.JSON(ctx, "GET", "/agent/v1/jobs/next", nil, &assignment)
		if err != nil {
			return err
		}
		if status != 204 {
			p, err := VerifyAssignment(assignment, client.ServerCertificate, deviceID, time.Now())
			if err != nil {
				return err
			}
			if err = ClaimJob(options.StateDir, p.ID); err != nil {
				return err
			}
			details, runErr := r.execute(ctx, p)
			message := "Concluído"
			if runErr != nil {
				message = runErr.Error()
				fmt.Fprintln(options.Output, "Falha na tarefa:", message)
			}
			if _, err = client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/result", map[string]any{"success": runErr == nil, "message": message, "details": details}, nil); err != nil {
				return fmt.Errorf("resultado não confirmado; job não será repetido: %w", err)
			}
		}
		if options.Once {
			return nil
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(options.PollInterval):
		}
	}
}
