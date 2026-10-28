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
	ScratchDir         string
	Input              io.Reader
	Output             io.Writer
}

type Runner struct {
	Client            *Client
	Options           Options
	DeviceID          string
	LastRestoreReport *RestoreReport
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
	v, err := NewInventoryWithScratch(ctx, r.DeviceID, r.Options.FixtureDir, r.Options.ScratchDir)
	if err != nil {
		return err
	}
	if r.Options.AllowPhysicalWrite && r.Options.FixtureDir == "" && linuxDeployToolsAvailable() {
		v.Capabilities = append(v.Capabilities, "DEPLOY_LINUX")
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
	d, err := ValidateDisk(p, disks, p.Kind == "RESTORE" || p.Kind == "DEPLOY_LINUX")
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
		if p.StreamID != "" {
			return "Origem enviada em lotes; o servidor descarta cada lote após a gravação no destino", r.sendStream(ctx, p, d)
		}
		return "Imagem raw capturada e validada no repositório", r.capture(ctx, p, d)
	case "DEPLOY_LINUX":
		return r.deployLinux(ctx, p, d)
	case "RESTORE":
		if p.StreamID != "" {
			err := r.receiveStream(ctx, p, d)
			details, _ := json.Marshal(r.LastRestoreReport)
			return string(details), err
		}
		err := r.restore(ctx, p, d)
		details, _ := json.Marshal(r.LastRestoreReport)
		return string(details), err
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
	var capturedLayout *DiskLayout
	if adaptiveToolsAvailable(false) {
		capturedLayout, _ = InspectLayout(ctx, d.Path, d.SizeBytes, d.LogicalSectorBytes)
	}
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
	if capturedLayout != nil {
		afterLayout, e := InspectLayout(ctx, d.Path, d.SizeBytes, d.LogicalSectorBytes)
		if e != nil || !layoutsEqual(capturedLayout, afterLayout) {
			return errors.New("layout da origem mudou durante captura")
		}
	}
	if _, err = r.Client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/capture-complete", map[string]any{"layout": capturedLayout}, &manifest); err != nil {
		return err
	}
	if manifest.State != "AVAILABLE" || manifest.LogicalSizeBytes != d.SizeBytes || manifest.SourceFingerprint != d.Fingerprint {
		return errors.New("servidor não confirmou imagem disponível íntegra")
	}
	if capturedLayout != nil && !layoutsEqual(capturedLayout, manifest.Layout) {
		return errors.New("layout confirmado pelo servidor difere do capturado")
	}
	return r.report(ctx, p, "VERIFIED", offset, d.SizeBytes)
}

func (r *Runner) restore(ctx context.Context, p Plan, d Disk) error {
	r.LastRestoreReport = nil
	var m Manifest
	if _, err := r.Client.JSON(ctx, "GET", "/agent/v1/jobs/"+p.ID+"/manifest", nil, &m); err != nil {
		return err
	}
	if err := ValidateManifest(m, p); err != nil {
		return err
	}
	r.LastRestoreReport = &RestoreReport{Strategy: cloneStrategy(p), SourceLayout: m.Layout, Warnings: []string{"Destino incompleto até conclusão e releitura; boot não validado."}}
	if cloneStrategy(p) == "FIT" || cloneStrategy(p) == "PRESERVE_LAYOUT" {
		return r.restoreAdapted(ctx, p, d, m)
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
		r.LastRestoreReport.BytesWritten = c.OffsetBytes + int64(c.SizeBytes)
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
	tableVerified := false
	if m.Layout != nil && m.LogicalSizeBytes == d.SizeBytes && adaptiveToolsAvailable(false) {
		layout, e := InspectLayout(ctx, d.Path, d.SizeBytes, d.LogicalSectorBytes)
		if e != nil || !layoutsEqual(m.Layout, layout) {
			return errors.New("tabela restaurada não confere com manifest")
		}
		tableVerified = true
	}
	r.LastRestoreReport = &RestoreReport{Strategy: cloneStrategy(p), DataVerified: true, TableVerified: tableVerified, BootPayloadPreserved: true, BootVerified: false, BytesWritten: m.LogicalSizeBytes, SourceLayout: m.Layout, TargetLayout: m.Layout, Warnings: []string{"Boot exige teste real no computador destino. Integridade de transferência não comprova sistema inicializável."}}
	return r.report(ctx, p, "VERIFIED", m.LogicalSizeBytes, m.LogicalSizeBytes)
}

func (r *Runner) restoreAdapted(ctx context.Context, p Plan, d Disk, m Manifest) error {
	required, err := scratchRequired(m, d.SizeBytes)
	if err != nil {
		return err
	}
	workspace, err := newWorkspace(r.Options.ScratchDir, required, d.Path)
	if err != nil {
		return err
	}
	defer func() {
		if cleanupErr := removeWorkspace(r.Options.ScratchDir, workspace); cleanupErr != nil {
			fmt.Fprintln(r.Options.Output, "Aviso: cópia privada scratch foi preservada por falha de limpeza:", cleanupErr)
		}
	}()
	sourcePath := filepath.Join(workspace, "source.raw")
	source, err := os.OpenFile(sourcePath, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return err
	}
	for _, c := range m.Chunks {
		if err = r.report(ctx, p, "PREPARING", c.OffsetBytes, m.LogicalSizeBytes); err != nil {
			source.Close()
			return err
		}
		data, _, e := r.Client.Request(ctx, "GET", fmt.Sprintf("/agent/v1/jobs/%s/chunks/%d", p.ID, c.Sequence), nil, nil)
		if e != nil {
			source.Close()
			return e
		}
		h := sha256.Sum256(data)
		if len(data) != c.SizeBytes || !strings.EqualFold(hex.EncodeToString(h[:]), c.SHA256) {
			source.Close()
			return errors.New("imagem corrompida durante preparação; destino não foi aberto")
		}
		if _, err = source.WriteAt(data, c.OffsetBytes); err != nil {
			source.Close()
			return err
		}
	}
	if err = source.Sync(); err != nil {
		source.Close()
		return err
	}
	if err = source.Close(); err != nil {
		return err
	}
	prepared, err := prepareImage(ctx, sourcePath, workspace, m, d.SizeBytes, *p.Clone)
	if err != nil {
		return err
	}
	r.LastRestoreReport = &prepared.Report
	r.LastRestoreReport.TableVerified = false
	r.LastRestoreReport.BootPayloadPreserved = false
	if err = r.report(ctx, p, "PREPARED", m.LogicalSizeBytes, m.LogicalSizeBytes); err != nil {
		return err
	}
	// Destination is opened only after download, full image adaptation, table/filesystem checks and hashes have passed.
	destination, err := r.openDisk(ctx, p, d, true)
	if err != nil {
		return err
	}
	defer destination.Close()
	image, err := os.Open(prepared.Path)
	if err != nil {
		return err
	}
	defer image.Close()
	buffer := make([]byte, ChunkSize)
	for _, c := range prepared.Chunks {
		if err = r.report(ctx, p, "RESTORING", c.OffsetBytes, d.SizeBytes); err != nil {
			return err
		}
		if _, err = image.ReadAt(buffer[:c.SizeBytes], c.OffsetBytes); err != nil {
			return err
		}
		h := sha256.Sum256(buffer[:c.SizeBytes])
		if !strings.EqualFold(hex.EncodeToString(h[:]), c.SHA256) {
			return errors.New("cópia preparada mudou antes de escrita")
		}
		n, e := destination.WriteAt(buffer[:c.SizeBytes], c.OffsetBytes)
		if e != nil {
			return e
		}
		if n != c.SizeBytes {
			return io.ErrShortWrite
		}
		r.LastRestoreReport.BytesWritten = c.OffsetBytes + int64(c.SizeBytes)
	}
	if err = destination.Sync(); err != nil {
		return err
	}
	for _, c := range prepared.Chunks {
		if err = r.report(ctx, p, "VERIFYING", c.OffsetBytes, d.SizeBytes); err != nil {
			return err
		}
		if _, err = destination.ReadAt(buffer[:c.SizeBytes], c.OffsetBytes); err != nil {
			return err
		}
		h := sha256.Sum256(buffer[:c.SizeBytes])
		if !strings.EqualFold(hex.EncodeToString(h[:]), c.SHA256) {
			return fmt.Errorf("releitura do clone diverge em %d", c.OffsetBytes)
		}
	}
	layout, err := InspectLayout(ctx, d.Path, d.SizeBytes, d.LogicalSectorBytes)
	if err != nil {
		return err
	}
	if !layoutsEqual(prepared.Layout, layout) {
		return errors.New("layout do destino difere da cópia validada")
	}
	r.LastRestoreReport.DataVerified = true
	r.LastRestoreReport.TableVerified = true
	r.LastRestoreReport.BootPayloadPreserved = true
	r.LastRestoreReport.BytesWritten = d.SizeBytes
	return r.report(ctx, p, "VERIFIED", d.SizeBytes, d.SizeBytes)
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
