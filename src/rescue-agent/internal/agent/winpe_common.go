package agent

import (
	"bufio"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

type WindowsImage struct {
	Index            int    `json:"index"`
	Name             string `json:"name"`
	Description      string `json:"description"`
	Architecture     string `json:"architecture"`
	Version          string `json:"version"`
	MinimumSizeBytes int64  `json:"minimumSizeBytes"`
}
type DeploymentMedia struct {
	ID           string         `json:"id"`
	Name         string         `json:"name"`
	FileName     string         `json:"fileName"`
	SizeBytes    int64          `json:"sizeBytes"`
	SHA256       string         `json:"sha256"`
	Chunks       []ImageChunk   `json:"chunks"`
	Images       []WindowsImage `json:"images"`
	State        string         `json:"state"`
	CreatedAtUTC time.Time      `json:"createdAtUtc"`
}
type WinPEOptions struct {
	Server, ServerPin, StateDir, ScratchDir, BootsectPath string
	AllowDeployment, Once                                 bool
	PollInterval                                          time.Duration
	Input                                                 io.Reader
	Output                                                io.Writer
}
type WinPECommand struct {
	Tool string
	Args []string
}
type winPEPlatform interface {
	Check(context.Context) (string, error)
	PrivateState(string) error
	Disks(context.Context, []string) ([]Disk, error)
	Scratch(string, int64, int) error
	Letters() (byte, byte, error)
	Tools(string, string) (map[string]string, error)
	Run(context.Context, string, []string, io.Writer) error
	VerifyVolumes(int, string, byte, byte) error
	VerifyApplied(byte) error
	VerifyBoot(byte, string) error
}

func WinPEDiskNumber(path string) (int, error) {
	const prefix = `\\.\PhysicalDrive`
	if !strings.HasPrefix(path, prefix) {
		return 0, errors.New("destino deve ser PhysicalDrive identificado por IOCTL")
	}
	number := strings.TrimPrefix(path, prefix)
	n, err := strconv.Atoi(number)
	if err != nil || n < 0 || n > 4095 || strconv.Itoa(n) != number {
		return 0, errors.New("número físico inválido")
	}
	return n, nil
}
func BuildWinPEDiskPart(disk int, firmware string, system, windows byte, offline bool) (string, error) {
	if disk < 0 || disk > 4095 || system < 'D' || system > 'Z' || windows < 'D' || windows > 'Z' || system == windows || system == 'X' || windows == 'X' {
		return "", errors.New("número ou letras de destino inválidos")
	}
	lines := []string{fmt.Sprintf("select disk %d", disk)}
	// DiskPart /s aborts on "online disk" for a disk already online (0x80042595), so
	// bring it online only when IOCTL attributes reported it offline. No noerr.
	if offline {
		lines = append(lines, "online disk")
	}
	lines = append(lines, "clean")
	switch firmware {
	case "GPT_UEFI":
		lines = append(lines, "convert gpt", "create partition efi size=260", "format quick fs=fat32 label=SLT-System", fmt.Sprintf("assign letter=%c", system), "create partition msr size=16", "create partition primary", "format quick fs=ntfs label=Windows", fmt.Sprintf("assign letter=%c", windows))
	case "MBR_BIOS":
		lines = append(lines, "convert mbr", "create partition primary size=550", "format quick fs=ntfs label=SLT-System", "active", fmt.Sprintf("assign letter=%c", system), "create partition primary", "format quick fs=ntfs label=Windows", fmt.Sprintf("assign letter=%c", windows))
	default:
		return "", errors.New("firmware não suportado")
	}
	return strings.Join(append(lines, "exit"), "\r\n") + "\r\n", nil
}
func BuildWinPECommands(media, scratch string, index int, firmware string, system, windows byte) ([]WinPECommand, error) {
	if _, err := BuildWinPEDiskPart(0, firmware, system, windows, false); err != nil {
		return nil, err
	}
	if index < 1 || index > 1000 || !safeWinPEPath(media) || !safeWinPEPath(scratch) {
		return nil, errors.New("caminho local ou índice inválido")
	}
	boot := "UEFI"
	if firmware == "MBR_BIOS" {
		boot = "BIOS"
	}
	commands := []WinPECommand{{"dism", []string{"/Apply-Image", "/ImageFile:" + media, fmt.Sprintf("/Index:%d", index), fmt.Sprintf("/ApplyDir:%c:\\", windows), "/CheckIntegrity", "/Verify", "/ScratchDir:" + scratch, "/LogPath:" + scratch + `\dism.log`}}, {"bcdboot", []string{fmt.Sprintf("%c:\\Windows", windows), "/s", fmt.Sprintf("%c:", system), "/f", boot}}}
	if firmware == "MBR_BIOS" {
		commands = append(commands, WinPECommand{"bootsect", []string{"/nt60", fmt.Sprintf("%c:", system), "/mbr"}})
	}
	return commands, nil
}
func safeWinPEPath(path string) bool {
	if len(path) < 3 || len(path) > 2000 || path[1] != ':' || path[2] != '\\' || !(path[0] >= 'A' && path[0] <= 'Z' || path[0] >= 'a' && path[0] <= 'z') {
		return false
	}
	if strings.ContainsAny(path, "\r\n\x00\"<>|?*/") || strings.Contains(path[2:], ":") {
		return false
	}
	for _, part := range strings.Split(path[3:], `\`) {
		if part != "" && (strings.TrimRight(part, " .") != part || strings.IndexFunc(part, func(r rune) bool { return r < 32 || r == 127 }) >= 0) {
			return false
		}
		base := strings.ToUpper(strings.SplitN(part, ".", 2)[0])
		if base == "CON" || base == "PRN" || base == "AUX" || base == "NUL" || (len(base) == 4 && (strings.HasPrefix(base, "COM") || strings.HasPrefix(base, "LPT")) && base[3] >= '1' && base[3] <= '9') {
			return false
		}
	}
	return true
}
func validateWinPEMedia(m DeploymentMedia, p Plan) error {
	if p.Deployment == nil || m.ID != p.Deployment.MediaID || m.State != "AVAILABLE" || m.SizeBytes < 208 || m.SizeBytes > 1024*1024*1024*1024 || len(m.Images) > 1000 {
		return errors.New("mídia ausente, incompleta ou diferente do plano")
	}
	hash, err := hex.DecodeString(m.SHA256)
	if err != nil || len(hash) != 32 {
		return errors.New("hash da mídia inválido")
	}
	ext := strings.ToLower(filepath.Ext(m.FileName))
	if ext != ".wim" && ext != ".esd" {
		return errors.New("mídia deve ser WIM ou ESD")
	}
	found := false
	for _, image := range m.Images {
		if image.Index == p.Deployment.ImageIndex {
			if found {
				return errors.New("índice ambíguo")
			}
			found = true
			if !strings.EqualFold(image.Architecture, "AMD64") || image.MinimumSizeBytes <= 0 || image.MinimumSizeBytes > p.DiskSizeBytes-1024*1024*1024 {
				return errors.New("edição/arquitetura/capacidade incompatível")
			}
		}
	}
	if !found {
		return errors.New("edição não existe na mídia")
	}
	var offset int64
	for i, c := range m.Chunks {
		h, e := hex.DecodeString(c.SHA256)
		if e != nil || len(h) != 32 || c.Sequence != i || c.OffsetBytes != offset || c.SizeBytes <= 0 || c.SizeBytes > ChunkSize || int64(c.SizeBytes) > m.SizeBytes-offset || (i < len(m.Chunks)-1 && c.SizeBytes != ChunkSize) {
			return errors.New("manifesto de mídia inválido")
		}
		offset += int64(c.SizeBytes)
	}
	if offset != m.SizeBytes {
		return errors.New("mídia truncada")
	}
	return nil
}
func validateWinPETarget(p Plan, disks []Disk) (Disk, error) {
	matches := []Disk{}
	for _, d := range disks {
		if d.Fingerprint == p.DiskFingerprint {
			matches = append(matches, d)
		}
	}
	if len(matches) != 1 {
		return Disk{}, errors.New("destino ausente ou ambíguo")
	}
	d := matches[0]
	if d.Path != p.DiskPath || d.SizeBytes != p.DiskSizeBytes || d.LogicalSectorBytes != p.LogicalSectorBytes || Fingerprint(d) != p.DiskFingerprint || d.Serial == "" || d.Model == "" || d.IsReadOnly || d.IsSystem || d.Health == "IDENTITY_INCOMPLETE" {
		return Disk{}, errors.New("identidade mudou ou disco protegido/sem serial/somente leitura")
	}
	if _, err := WinPEDiskNumber(d.Path); err != nil {
		return Disk{}, err
	}
	if p.Deployment != nil && p.Deployment.Firmware == "MBR_BIOS" && (d.LogicalSectorBytes != 512 || d.SizeBytes > 2*1024*1024*1024*1024) {
		return Disk{}, errors.New("BIOS/MBR exige setores de 512 bytes e disco de até 2 TiB")
	}
	return d, nil
}
func decodeWinPEDescriptor(data []byte) (string, string, error) {
	if len(data) < 36 {
		return "", "", errors.New("STORAGE_DEVICE_DESCRIPTOR truncado")
	}
	size := int(binary.LittleEndian.Uint32(data[4:8]))
	if size < 36 || size > len(data) {
		return "", "", errors.New("tamanho de descriptor inválido")
	}
	data = data[:size]
	read := func(offset int) (string, error) {
		start := int(binary.LittleEndian.Uint32(data[offset : offset+4]))
		if start == 0 {
			return "", nil
		}
		if start < 36 || start >= len(data) {
			return "", errors.New("offset do descriptor inválido")
		}
		end := bytes.IndexByte(data[start:], 0)
		if end < 0 || end > 256 {
			return "", errors.New("string do descriptor inválida")
		}
		s := strings.TrimSpace(string(data[start : start+end]))
		if strings.ContainsAny(s, "\r\n\x00") {
			return "", errors.New("identidade contém controle")
		}
		return s, nil
	}
	vendor, e := read(12)
	if e != nil {
		return "", "", e
	}
	product, e := read(16)
	if e != nil {
		return "", "", e
	}
	serial, e := read(24)
	if e != nil {
		return "", "", e
	}
	model := strings.TrimSpace(vendor + " " + product)
	if model == "" || serial == "" {
		return "", "", errors.New("modelo/serial não informado pelo dispositivo")
	}
	return model, serial, nil
}
func decodeWinPEExtents(data []byte) ([]int, error) {
	if len(data) < 8 {
		return nil, errors.New("extents truncados")
	}
	count := int(binary.LittleEndian.Uint32(data[:4]))
	if count < 1 || count > 256 || 8+count*24 > len(data) {
		return nil, errors.New("extents inválidos")
	}
	out := []int{}
	for i := 0; i < count; i++ {
		n := int(binary.LittleEndian.Uint32(data[8+i*24:]))
		if n > 4095 {
			return nil, errors.New("número físico de extent inválido")
		}
		out = append(out, n)
	}
	return out, nil
}

func nonPhysicalWinPEVolume(device string) bool {
	device = strings.ToLower(device)
	if strings.HasPrefix(device, `\device\ramdisk`) {
		return true
	}
	const cdPrefix = `\device\cdrom`
	if strings.HasPrefix(device, cdPrefix) {
		number := strings.TrimPrefix(device, cdPrefix)
		n, err := strconv.Atoi(number)
		return err == nil && n >= 0 && n <= 4095 && strconv.Itoa(n) == number
	}
	return false
}

type winPERunner struct {
	options            WinPEOptions
	platform           winPEPlatform
	client             *Client
	deviceID, firmware string
	protected          []string
}

func (r *winPERunner) inventory(ctx context.Context) error {
	disks, err := r.platform.Disks(ctx, r.protected)
	if err != nil {
		return err
	}
	hostname, _ := os.Hostname()
	caps := []string{"INVENTORY"}
	if r.options.AllowDeployment && r.options.ScratchDir != "" {
		caps = append(caps, "DEPLOY_WINDOWS")
	}
	v := Inventory{DeviceID: r.deviceID, Hostname: hostname, OS: "Windows PE AMD64", Mode: "WINPE", AgentVersion: "0.1.0", Capabilities: caps, Disks: disks, CapturedAtUTC: time.Now().UTC(), Notes: []string{"WinPE firmware=" + r.firmware, "DEPLOY_WINDOWS exige confirmação local; boot/OOBE não homologados"}}
	_, err = r.client.JSON(ctx, "POST", "/agent/v1/inventory", v, nil)
	return err
}
func (r *winPERunner) report(ctx context.Context, p Plan, phase string, n, total int64) error {
	return (&Runner{Client: r.client}).report(ctx, p, phase, n, total)
}
func (r *winPERunner) command(ctx context.Context, p Plan, phase, path string, args []string) error {
	if err := r.report(ctx, p, phase, 0, 0); err != nil {
		return err
	}
	commandCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	result := make(chan error, 1)
	go func() { result <- r.platform.Run(commandCtx, path, args, r.options.Output) }()
	stop := func(cause error) error { cancel(); <-result; return cause }
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case err := <-result:
			return err
		case <-ctx.Done():
			return stop(ctx.Err())
		case <-ticker.C:
			if err := r.inventory(ctx); err != nil {
				return stop(fmt.Errorf("conexão perdida durante %s; destino pode estar parcial: %w", phase, err))
			}
			if err := r.report(ctx, p, phase, 0, 0); err != nil {
				return stop(err)
			}
		}
	}
}
func (r *winPERunner) download(ctx context.Context, p Plan, m DeploymentMedia, dir string) (string, error) {
	path := filepath.Join(dir, "install"+strings.ToLower(filepath.Ext(m.FileName)))
	f, err := os.OpenFile(path+".partial", os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	var copied int64
	for _, chunk := range m.Chunks {
		if err = r.report(ctx, p, "DOWNLOADING_MEDIA", copied, m.SizeBytes); err != nil {
			return "", err
		}
		data, _, e := r.client.Request(ctx, "GET", fmt.Sprintf("/agent/v1/jobs/%s/media/chunks/%d", p.ID, chunk.Sequence), nil, nil)
		if e != nil {
			return "", e
		}
		sum := sha256.Sum256(data)
		if len(data) != chunk.SizeBytes || hex.EncodeToString(sum[:]) != chunk.SHA256 {
			return "", errors.New("checksum de chunk da mídia não confere")
		}
		if _, err = f.Write(data); err != nil {
			return "", err
		}
		h.Write(data)
		copied += int64(len(data))
		if copied > m.SizeBytes {
			return "", errors.New("download excedeu manifesto")
		}
		if copied%int64(ChunkSize*4) == 0 {
			if err = r.inventory(ctx); err != nil {
				return "", err
			}
		}
	}
	if copied != m.SizeBytes || hex.EncodeToString(h.Sum(nil)) != m.SHA256 {
		return "", errors.New("checksum completo da mídia não confere")
	}
	if err = f.Sync(); err != nil {
		return "", err
	}
	if err = f.Close(); err != nil {
		return "", err
	}
	check, err := os.Open(path + ".partial")
	if err != nil {
		return "", err
	}
	err = verifyWinPEMediaFile(ctx, check, m)
	check.Close()
	if err != nil {
		return "", err
	}
	if err = os.Rename(path+".partial", path); err != nil {
		return "", err
	}
	return path, nil
}
func (r *winPERunner) deploy(ctx context.Context, p Plan) (details string, deploymentErr error) {
	writeStarted := false
	defer func() {
		if deploymentErr != nil && writeStarted {
			report, _ := json.Marshal(map[string]any{"partialDiskPossible": true, "applied": false, "bootVerified": false, "message": "Falha após iniciar particionamento; não repetir automaticamente. Reconciliar disco localmente."})
			details = string(report)
			deploymentErr = fmt.Errorf("destino pode estar parcial; implantação interrompida: %w", deploymentErr)
		}
	}()
	if p.Kind != "DEPLOY_WINDOWS" || !r.options.AllowDeployment || p.Deployment == nil || !p.Consent || !identifierPattern.MatchString(p.ID) || strings.TrimSpace(p.Challenge) == "" {
		return "", errors.New("implantação requer --allow-deployment e plano autorizado")
	}
	firmware, err := r.platform.Check(ctx)
	if err != nil {
		return "", err
	}
	if firmware != p.Deployment.Firmware {
		return "", errors.New("boot do WinPE difere do firmware solicitado; reinicie a mídia no modo correto")
	}
	tools, err := r.platform.Tools(firmware, r.options.BootsectPath)
	if err != nil {
		return "", err
	}
	disks, err := r.platform.Disks(ctx, r.protected)
	if err != nil {
		return "", err
	}
	disk, err := validateWinPETarget(p, disks)
	if err != nil {
		return "", err
	}
	number, _ := WinPEDiskNumber(disk.Path)
	var media DeploymentMedia
	if _, err = r.client.JSON(ctx, "GET", "/agent/v1/jobs/"+p.ID+"/media", nil, &media); err != nil {
		return "", err
	}
	if err = validateWinPEMedia(media, p); err != nil {
		return "", err
	}
	if media.SizeBytes > int64(^uint64(0)>>1)-2*1024*1024*1024 {
		return "", errors.New("tamanho de mídia excessivo")
	}
	if err = r.platform.Scratch(r.options.ScratchDir, media.SizeBytes+2*1024*1024*1024, number); err != nil {
		return "", err
	}
	system, windows, err := r.platform.Letters()
	if err != nil {
		return "", err
	}
	if _, err = BuildWinPEDiskPart(number, firmware, system, windows, disk.Offline); err != nil {
		return "", err
	}
	dir := filepath.Join(r.options.ScratchDir, "slt-deploy-"+p.ID)
	if err = os.Mkdir(dir, 0700); err != nil {
		return "", err
	}
	if err = r.platform.PrivateState(dir); err != nil {
		return "", err
	}
	staging, err := r.download(ctx, p, media, dir)
	if err != nil {
		return "", err
	}
	commands, err := BuildWinPECommands(staging, dir, p.Deployment.ImageIndex, firmware, system, windows)
	if err != nil {
		return "", err
	}
	fmt.Fprintf(r.options.Output, "\nAPAGAR disco %s | %s | serial %s | %d bytes.\nImagem: %s, edição %d. Confirme LOCALMENTE digitando %q: ", disk.Path, disk.Model, disk.Serial, disk.SizeBytes, media.Name, p.Deployment.ImageIndex, p.Challenge)
	text, err := bufio.NewReader(io.LimitReader(r.options.Input, 4097)).ReadString('\n')
	if err != nil || strings.TrimSpace(text) != p.Challenge {
		return "", errors.New("confirmação local ausente ou incorreta")
	}
	if !p.ExpiresAtUTC.After(time.Now()) {
		return "", errors.New("plano expirou durante download/confirmação; replaneje")
	}
	if _, err = r.platform.Check(ctx); err != nil {
		return "", err
	}
	disks, err = r.platform.Disks(ctx, r.protected)
	if err != nil {
		return "", err
	}
	confirmed, err := validateWinPETarget(p, disks)
	if err != nil {
		return "", err
	}
	if err = r.platform.Scratch(r.options.ScratchDir, 2*1024*1024*1024, number); err != nil {
		return "", err
	}
	// Online state comes from the revalidation right before writing, not the earlier scan.
	script, err := BuildWinPEDiskPart(number, firmware, system, windows, confirmed.Offline)
	if err != nil {
		return "", err
	}
	scriptPath := filepath.Join(dir, "partitions.txt")
	if err = atomicPrivateFile(scriptPath, []byte(script)); err != nil {
		return "", err
	}
	commandCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	writeStarted = true
	if err = r.command(commandCtx, p, "PARTITIONING", tools["diskpart"], []string{"/s", scriptPath}); err != nil {
		return "", fmt.Errorf("particionamento falhou; destino pode estar parcial: %w", err)
	}
	if err = r.platform.VerifyVolumes(number, firmware, system, windows); err != nil {
		return "", err
	}
	for _, command := range commands {
		if command.Tool != "dism" {
			if err = r.platform.VerifyApplied(windows); err != nil {
				return "", err
			}
			if err = r.platform.VerifyVolumes(number, firmware, system, windows); err != nil {
				return "", err
			}
		}
		if err = r.command(commandCtx, p, strings.ToUpper(command.Tool), tools[command.Tool], command.Args); err != nil {
			return "", fmt.Errorf("%s falhou; destino pode estar parcial: %w", command.Tool, err)
		}
	}
	if err = r.platform.VerifyApplied(windows); err != nil {
		return "", err
	}
	disks, err = r.platform.Disks(ctx, r.protected)
	if err != nil {
		return "", err
	}
	if _, err = validateWinPETarget(p, disks); err != nil {
		return "", err
	}
	if err = r.platform.VerifyVolumes(number, firmware, system, windows); err != nil {
		return "", err
	}
	if err = r.platform.VerifyBoot(system, firmware); err != nil {
		return "", err
	}
	report, _ := json.Marshal(map[string]any{"mediaId": media.ID, "imageIndex": p.Deployment.ImageIndex, "firmware": firmware, "applied": true, "bootFilesCreated": true, "bootVerified": false, "oobe": "primeiro boot pendente de validação local", "retainedStaging": dir})
	return string(report), nil
}
func RunWinPE(ctx context.Context, options WinPEOptions) error {
	platform := newWinPEPlatform()
	firmware, err := platform.Check(ctx)
	if err != nil {
		return err
	}
	if options.Input == nil {
		options.Input = os.Stdin
	}
	if options.Output == nil {
		options.Output = os.Stdout
	}
	if options.PollInterval < time.Second {
		options.PollInterval = 5 * time.Second
	}
	if !safeWinPEPath(options.StateDir) || len(strings.TrimRight(options.StateDir, `\`)) <= 2 || (options.ScratchDir != "" && (!safeWinPEPath(options.ScratchDir) || len(strings.TrimRight(options.ScratchDir, `\`)) <= 2)) {
		return errors.New("state/scratch devem ser caminhos locais Windows explícitos")
	}
	if err = platform.PrivateState(options.StateDir); err != nil {
		return err
	}
	lockPath := filepath.Join(options.StateDir, "winpe.lock")
	lock, err := os.OpenFile(lockPath, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return fmt.Errorf("diretório de estado já em uso; após crash confira e remova apenas lock obsoleto: %w", err)
	}
	defer func() { lock.Close(); os.Remove(lockPath) }()
	bootstrap, err := NewClient(options.Server, options.ServerPin, nil)
	if err != nil {
		return err
	}
	id, cert, err := EnrollMode(ctx, bootstrap, options.StateDir, options.Output, "WINPE")
	if err != nil {
		return err
	}
	client, err := NewClient(options.Server, options.ServerPin, cert)
	if err != nil {
		return err
	}
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	r := winPERunner{options: options, platform: platform, client: client, deviceID: id, firmware: firmware, protected: []string{options.StateDir, options.ScratchDir, exe, options.BootsectPath}}
	for {
		if err = r.inventory(ctx); err != nil {
			return err
		}
		var signed SignedAssignment
		status, e := client.JSON(ctx, "GET", "/agent/v1/jobs/next", nil, &signed)
		if e != nil {
			return e
		}
		if status != 204 {
			p, e := VerifyAssignment(signed, client.ServerCertificate, id, time.Now())
			if e != nil {
				return e
			}
			if e = ClaimJob(options.StateDir, p.ID); e != nil {
				return e
			}
			var details string
			var runErr error
			if p.Kind == "INVENTORY" {
				runErr = r.inventory(ctx)
				details = "Inventário WinPE atualizado"
			} else if p.Kind == "DEPLOY_WINDOWS" {
				details, runErr = r.deploy(ctx, p)
			} else {
				runErr = errors.New("tipo de tarefa não oferecido pelo agente WinPE")
			}
			message := "Imagem Windows aplicada; boot/OOBE ainda não validados"
			if p.Kind == "INVENTORY" {
				message = "Inventário atualizado"
			}
			if runErr != nil {
				message = runErr.Error()
			}
			if _, e = client.JSON(ctx, "POST", "/agent/v1/jobs/"+p.ID+"/result", map[string]any{"success": runErr == nil, "message": message, "details": details}, nil); e != nil {
				return fmt.Errorf("resultado não confirmado; job nunca será repetido automaticamente: %w", e)
			}
			if runErr == nil && p.Kind == "DEPLOY_WINDOWS" && p.Deployment.RebootAfterInstall {
				tools, e := platform.Tools(p.Deployment.Firmware, options.BootsectPath)
				if e != nil {
					return e
				}
				return platform.Run(ctx, tools["wpeutil"], []string{"Reboot"}, options.Output)
			}
			if runErr != nil && p.Kind == "DEPLOY_WINDOWS" {
				return fmt.Errorf("agente interrompido após falha de implantação; reconciliar processos e disco localmente antes de reiniciar: %w", runErr)
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
