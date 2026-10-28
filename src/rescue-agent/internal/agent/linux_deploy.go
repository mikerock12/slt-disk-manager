package agent

import (
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
	"regexp"
	"runtime"
	"strings"
	"time"
)

// DEPLOY_LINUX installs an Ubuntu-family layered live ISO (casper) from the server catalog
// onto a Rescue client disk. The verified ISO is staged on the new root filesystem itself,
// so no scratch storage is needed, and it is deleted before the job finishes. Bootloader
// packages come from the ISO's own repository; nothing is downloaded from the internet.

var squashNamePattern = regexp.MustCompile(`^[A-Za-z0-9._-]{1,64}\.squashfs$`)
var suitePattern = regexp.MustCompile(`^[a-z][a-z0-9-]{1,31}$`)

func linuxDeployToolsAvailable() bool {
	if runtime.GOOS != "linux" || os.Geteuid() != 0 {
		return false
	}
	for _, name := range []string{"sfdisk", "losetup", "mkfs.ext4", "mkfs.fat", "mount", "umount", "unsquashfs", "chroot", "blkid", "udevadm"} {
		if _, err := exec.LookPath(name); err != nil {
			return false
		}
	}
	return true
}

type linuxInstall struct {
	r        *Runner
	p        Plan
	loop     string
	backing  string
	work     string
	root     string
	iso      string
	mounted  []string
	firmware string
}

// run executes one bounded installation step, reporting progress so the server sees the
// client alive and cancellation stays possible during long extractions.
func (l *linuxInstall) run(ctx context.Context, phase string, input io.Reader, name string, args ...string) error {
	if err := l.r.report(ctx, l.p, phase, 0, 0); err != nil {
		return err
	}
	step, cancel := context.WithTimeout(ctx, 2*time.Hour)
	defer cancel()
	command := exec.CommandContext(step, name, args...)
	command.Stdin = input
	command.Env = []string{"PATH=/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL=C", "LANG=C", "DEBIAN_FRONTEND=noninteractive", "DEBCONF_NONINTERACTIVE_SEEN=true"}
	var output limitedBuffer
	command.Stdout, command.Stderr = &output, &output
	if err := command.Start(); err != nil {
		return err
	}
	done := make(chan error, 1)
	go func() { done <- command.Wait() }()
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case err := <-done:
			if err != nil {
				return fmt.Errorf("%s: %w: %s", phase, err, strings.TrimSpace(string(output.data)))
			}
			return nil
		case <-ticker.C:
			if err := l.r.report(ctx, l.p, phase, 0, 0); err != nil {
				cancel()
				<-done
				return err
			}
		}
	}
}

func (l *linuxInstall) chroot(ctx context.Context, phase string, input io.Reader, args ...string) error {
	return l.run(ctx, phase, input, "chroot", append([]string{l.root}, args...)...)
}

func (l *linuxInstall) mount(ctx context.Context, args ...string) error {
	if _, err := tool(ctx, nil, "mount", args...); err != nil {
		return err
	}
	l.mounted = append(l.mounted, args[len(args)-1])
	return nil
}

func (l *linuxInstall) cleanup() error {
	var first error
	for i := len(l.mounted) - 1; i >= 0; i-- {
		if _, err := tool(context.Background(), nil, "umount", "-R", l.mounted[i]); err != nil && first == nil {
			first = err
		}
	}
	l.mounted = nil
	if l.loop != "" {
		if err := detachLoop(l.loop, l.backing); err != nil && first == nil {
			first = err
		}
		l.loop = ""
	}
	if l.work != "" {
		os.Remove(l.root)
		os.Remove(l.iso)
		os.Remove(l.work)
	}
	return first
}

func partitionNode(loop string, number int) string { return fmt.Sprintf("%sp%d", loop, number) }

func waitNode(ctx context.Context, path string) error {
	_, _ = tool(ctx, nil, "udevadm", "settle", "--timeout=10")
	for i := 0; i < 50; i++ {
		if info, err := os.Stat(path); err == nil && info.Mode()&os.ModeDevice != 0 {
			return nil
		}
		if err := pause(ctx, 200*time.Millisecond); err != nil {
			return err
		}
	}
	return errors.New("partição criada não apareceu: " + path)
}

func blkidUUID(ctx context.Context, device string) (string, error) {
	out, err := tool(ctx, nil, "blkid", "-s", "UUID", "-o", "value", device)
	uuid := strings.TrimSpace(string(out))
	if err != nil || uuid == "" || strings.ContainsAny(uuid, " \n\t\"") {
		return "", errors.New("UUID não identificado para " + device)
	}
	return uuid, nil
}

// linuxPartitionScript lays out ESP + root for UEFI or a single bootable root for BIOS.
func linuxPartitionScript(firmware string, sector int) (string, error) {
	mib := int64(1024*1024) / int64(sector)
	switch firmware {
	case "GPT_UEFI":
		return fmt.Sprintf("label: gpt\nstart=%d, size=%d, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B, name=\"EFI\"\nstart=%d, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name=\"root\"\n", mib, 512*mib, 513*mib), nil
	case "MBR_BIOS":
		return fmt.Sprintf("label: dos\nstart=%d, type=83, bootable\n", mib), nil
	}
	return "", errors.New("firmware inválido")
}

func (r *Runner) deployLinux(ctx context.Context, p Plan, d Disk) (string, error) {
	o := p.Deployment
	if o == nil || validateAccount(o.Account, true) != nil {
		return "", errors.New("instalação Linux sem opções ou conta válidas")
	}
	if !linuxDeployToolsAvailable() || r.Options.FixtureDir != "" {
		return "", errors.New("instalação Linux exige SLT Rescue em Linux como root com ferramentas de instalação")
	}
	_, statErr := os.Stat("/sys/firmware/efi")
	uefi := statErr == nil
	if (o.Firmware == "GPT_UEFI") != uefi {
		return "", errors.New("boot do Rescue difere do firmware solicitado; reinicie no modo correto (UEFI/BIOS)")
	}
	var media DeploymentMedia
	if _, err := r.Client.JSON(ctx, "GET", "/agent/v1/jobs/"+p.ID+"/media", nil, &media); err != nil {
		return "", err
	}
	if media.ID != o.MediaID || media.SizeBytes <= 0 || media.FileName != "media.iso" || len(media.Chunks) == 0 || media.SizeBytes > d.SizeBytes/2 {
		return "", errors.New("mídia Linux incompatível com o plano ou com o disco")
	}
	f, err := r.openDisk(ctx, p, d, true)
	if err != nil {
		return "", err
	}
	f.Close()
	l := &linuxInstall{r: r, p: p, firmware: o.Firmware, backing: d.Path}
	defer l.cleanup()
	if l.loop, err = attachLoop(ctx, d.Path, "--partscan"); err != nil {
		return "", err
	}
	script, err := linuxPartitionScript(o.Firmware, d.LogicalSectorBytes)
	if err != nil {
		return "", err
	}
	if err = l.run(ctx, "PARTITIONING", strings.NewReader(script), "sfdisk", "--wipe", "always", "--wipe-partitions", "always", l.loop); err != nil {
		return "", fmt.Errorf("particionamento falhou; destino pode estar parcial: %w", err)
	}
	rootNumber := 1
	if uefi {
		rootNumber = 2
	}
	rootDev, espDev := partitionNode(l.loop, rootNumber), partitionNode(l.loop, 1)
	if err = waitNode(ctx, rootDev); err != nil {
		return "", err
	}
	if uefi {
		if err = l.run(ctx, "FORMATTING", nil, "mkfs.fat", "-F", "32", "-n", "EFI", espDev); err != nil {
			return "", err
		}
	}
	if err = l.run(ctx, "FORMATTING", nil, "mkfs.ext4", "-F", "-q", "-L", "root", rootDev); err != nil {
		return "", err
	}
	if l.work, err = os.MkdirTemp("/run", "slt-linux-"); err != nil {
		return "", err
	}
	l.root, l.iso = filepath.Join(l.work, "root"), filepath.Join(l.work, "iso")
	for _, dir := range []string{l.root, l.iso} {
		if err = os.Mkdir(dir, 0700); err != nil {
			return "", err
		}
	}
	if err = l.mount(ctx, rootDev, l.root); err != nil {
		return "", err
	}
	staging := filepath.Join(l.root, ".slt-install")
	if err = os.Mkdir(staging, 0700); err != nil {
		return "", err
	}
	isoPath := filepath.Join(staging, "media.iso")
	if err = r.downloadMedia(ctx, p, media, isoPath); err != nil {
		return "", err
	}
	if err = l.mount(ctx, "-o", "loop,ro", isoPath, l.iso); err != nil {
		return "", err
	}
	sources, err := os.ReadFile(filepath.Join(l.iso, "casper", "install-sources.yaml"))
	if err != nil {
		return "", err
	}
	squash := ""
	for _, line := range strings.Split(string(sources), "\n") {
		if line = strings.TrimSpace(line); strings.HasPrefix(line, "path:") && squash == "" {
			squash = strings.TrimSpace(strings.TrimPrefix(line, "path:"))
		}
	}
	if !squashNamePattern.MatchString(squash) {
		return "", errors.New("camada squashfs padrão não reconhecida na ISO")
	}
	if err = l.run(ctx, "EXTRACTING", nil, "unsquashfs", "-f", "-n", "-d", l.root, filepath.Join(l.iso, "casper", squash)); err != nil {
		return "", err
	}
	suites, err := os.ReadDir(filepath.Join(l.iso, "dists"))
	if err != nil || len(suites) == 0 || !suitePattern.MatchString(suites[0].Name()) {
		return "", errors.New("repositório local da ISO ausente")
	}
	if err = l.configure(ctx, suites[0].Name()); err != nil {
		return "", err
	}
	if err = l.verify(); err != nil {
		return "", err
	}
	uuid, _ := blkidUUID(ctx, rootDev)
	if err = os.Remove(isoPath); err != nil && !os.IsNotExist(err) {
		return "", err
	}
	if err = l.cleanup(); err != nil {
		return "", err
	}
	os.Remove(staging)
	report, _ := json.Marshal(map[string]any{"installed": true, "mediaId": media.ID, "firmware": o.Firmware, "rootUuid": uuid, "user": o.Account.UserName, "passwordChangeRequired": true, "bootVerified": false})
	return string(report), nil
}

func (r *Runner) downloadMedia(ctx context.Context, p Plan, media DeploymentMedia, path string) error {
	out, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if err != nil {
		return err
	}
	defer out.Close()
	h := sha256.New()
	var copied int64
	for i, chunk := range media.Chunks {
		if chunk.Sequence != i || chunk.OffsetBytes != copied {
			return errors.New("manifesto de mídia não contíguo")
		}
		if err = r.report(ctx, p, "DOWNLOADING_MEDIA", copied, media.SizeBytes); err != nil {
			return err
		}
		data, _, e := r.Client.Request(ctx, "GET", fmt.Sprintf("/agent/v1/jobs/%s/media/chunks/%d", p.ID, chunk.Sequence), nil, nil)
		if e != nil {
			return e
		}
		sum := sha256.Sum256(data)
		if len(data) != chunk.SizeBytes || hex.EncodeToString(sum[:]) != chunk.SHA256 {
			return errors.New("checksum de chunk da mídia não confere")
		}
		if _, err = out.Write(data); err != nil {
			return err
		}
		h.Write(data)
		copied += int64(len(data))
	}
	if copied != media.SizeBytes || hex.EncodeToString(h.Sum(nil)) != media.SHA256 {
		return errors.New("checksum completo da mídia não confere")
	}
	return out.Sync()
}

func writeRootFile(root, name, content string, mode os.FileMode) error {
	path := filepath.Join(root, name)
	if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
		return err
	}
	return os.WriteFile(path, []byte(content), mode)
}

func (l *linuxInstall) configure(ctx context.Context, suite string) error {
	a := l.p.Deployment.Account
	// Recursive binds reach /dev/pts and efivars; rslave keeps unmounting from propagating to the Rescue host.
	for _, m := range [][]string{{"--rbind", "/dev", "dev"}, {"-t", "proc", "proc", "proc"}, {"--rbind", "/sys", "sys"}, {"-t", "tmpfs", "tmpfs", "run"}} {
		target := filepath.Join(l.root, m[len(m)-1])
		if err := os.MkdirAll(target, 0755); err != nil {
			return err
		}
		if err := l.mount(ctx, append(append([]string{}, m[:len(m)-1]...), target)...); err != nil {
			return err
		}
		if m[0] == "--rbind" {
			if _, err := tool(ctx, nil, "mount", "--make-rslave", target); err != nil {
				return err
			}
		}
	}
	if l.firmware == "GPT_UEFI" {
		target := filepath.Join(l.root, "boot", "efi")
		if err := os.MkdirAll(target, 0755); err != nil {
			return err
		}
		if err := l.mount(ctx, partitionNode(l.loop, 1), target); err != nil {
			return err
		}
	}
	cdrom := filepath.Join(l.root, "media", "slt-cdrom")
	if err := os.MkdirAll(cdrom, 0755); err != nil {
		return err
	}
	if err := l.mount(ctx, "--bind", l.iso, cdrom); err != nil {
		return err
	}
	rootNumber := 1
	if l.firmware == "GPT_UEFI" {
		rootNumber = 2
	}
	rootUUID, err := blkidUUID(ctx, partitionNode(l.loop, rootNumber))
	if err != nil {
		return err
	}
	fstab := "UUID=" + rootUUID + " / ext4 errors=remount-ro 0 1\n"
	if l.firmware == "GPT_UEFI" {
		espUUID, e := blkidUUID(ctx, partitionNode(l.loop, 1))
		if e != nil {
			return e
		}
		fstab += "UUID=" + espUUID + " /boot/efi vfat umask=0077 0 1\n"
	}
	files := map[string]string{
		"etc/fstab":                           fstab,
		"etc/hostname":                        a.ComputerName + "\n",
		"etc/hosts":                           "127.0.0.1 localhost\n127.0.1.1 " + a.ComputerName + "\n::1 localhost ip6-localhost ip6-loopback\n",
		"etc/machine-id":                      "",
		"etc/timezone":                        a.TimeZone + "\n",
		"etc/default/locale":                  "LANG=" + strings.ReplaceAll(a.Locale, "-", "_") + ".UTF-8\n",
		"etc/default/keyboard":                "XKBMODEL=\"pc105\"\nXKBLAYOUT=\"br\"\nXKBVARIANT=\"abnt2\"\nXKBOPTIONS=\"\"\nBACKSPACE=\"guess\"\n",
		"etc/cloud/cloud-init.disabled":       "SLT Disk Manager: instalação local sem datasource\n",
		"etc/apt/slt-sources.d/cdrom.sources": "Types: deb\nURIs: file:///media/slt-cdrom\nSuites: " + suite + "\nComponents: main restricted\nTrusted: yes\n",
	}
	for name, content := range files {
		if err = writeRootFile(l.root, name, content, 0644); err != nil {
			return err
		}
	}
	os.Remove(filepath.Join(l.root, "var", "lib", "dbus", "machine-id"))
	if err = os.Remove(filepath.Join(l.root, "etc", "localtime")); err != nil && !os.IsNotExist(err) {
		return err
	}
	if err = os.Symlink("/usr/share/zoneinfo/"+a.TimeZone, filepath.Join(l.root, "etc", "localtime")); err != nil {
		return err
	}
	apt := []string{"apt-get", "-o", "Dir::Etc::SourceList=/dev/null", "-o", "Dir::Etc::SourceParts=/etc/apt/slt-sources.d", "-o", "APT::Get::List-Cleanup=0"}
	if err = l.chroot(ctx, "PACKAGES", nil, append(apt, "update")...); err != nil {
		return err
	}
	if l.firmware == "GPT_UEFI" {
		if err = l.chroot(ctx, "PACKAGES", nil, append(apt, "install", "-y", "--no-install-recommends", "grub-efi-amd64", "grub-efi-amd64-signed", "shim-signed", "efibootmgr")...); err != nil {
			return err
		}
	}
	if err = l.chroot(ctx, "ACCOUNT", nil, "useradd", "-m", "-s", "/bin/bash", "-c", a.UserName, a.UserName); err != nil {
		return err
	}
	for _, group := range []string{"sudo", "adm", "cdrom", "dip", "plugdev", "users", "lpadmin"} {
		if l.chroot(ctx, "ACCOUNT", nil, "getent", "group", group) == nil {
			if err = l.chroot(ctx, "ACCOUNT", nil, "usermod", "-aG", group, a.UserName); err != nil {
				return err
			}
		}
	}
	if err = l.chroot(ctx, "ACCOUNT", strings.NewReader(a.UserName+":"+a.PasswordHash+"\n"), "chpasswd", "-e"); err != nil {
		return err
	}
	// The server-generated password is temporary: the operator must replace it at first logon.
	if err = l.chroot(ctx, "ACCOUNT", nil, "chage", "-d", "0", a.UserName); err != nil {
		return err
	}
	_ = l.chroot(ctx, "LOCALE", nil, "locale-gen", strings.ReplaceAll(a.Locale, "-", "_")+".UTF-8")
	kernels, _ := filepath.Glob(filepath.Join(l.root, "boot", "vmlinuz-*"))
	if len(kernels) == 0 {
		return errors.New("camada Linux sem kernel instalado")
	}
	if err = l.chroot(ctx, "INITRAMFS", nil, "update-initramfs", "-c", "-k", "all"); err != nil {
		if err = l.chroot(ctx, "INITRAMFS", nil, "update-initramfs", "-u", "-k", "all"); err != nil {
			return err
		}
	}
	if l.firmware == "GPT_UEFI" {
		for _, extra := range [][]string{{}, {"--removable"}} {
			args := append([]string{"grub-install", "--target=x86_64-efi", "--efi-directory=/boot/efi", "--bootloader-id=ubuntu", "--uefi-secure-boot", "--recheck"}, extra...)
			if err = l.chroot(ctx, "BOOTLOADER", nil, args...); err != nil {
				return err
			}
		}
	} else if err = l.chroot(ctx, "BOOTLOADER", nil, "grub-install", "--target=i386-pc", "--recheck", l.loop); err != nil {
		return err
	}
	if err = l.chroot(ctx, "BOOTLOADER", nil, "update-grub"); err != nil {
		return err
	}
	return os.RemoveAll(filepath.Join(l.root, "etc", "apt", "slt-sources.d"))
}

func (l *linuxInstall) verify() error {
	must := []string{"boot/grub/grub.cfg", "etc/fstab", "etc/passwd"}
	if l.firmware == "GPT_UEFI" {
		must = append(must, "boot/efi/EFI/BOOT/BOOTX64.EFI", "boot/efi/EFI/ubuntu/grub.cfg")
	}
	for _, name := range must {
		if info, err := os.Stat(filepath.Join(l.root, name)); err != nil || info.Size() == 0 {
			return errors.New("instalação incompleta: " + name)
		}
	}
	if initrds, _ := filepath.Glob(filepath.Join(l.root, "boot", "initrd.img-*")); len(initrds) == 0 {
		return errors.New("instalação sem initramfs")
	}
	grub, _ := os.ReadFile(filepath.Join(l.root, "boot", "grub", "grub.cfg"))
	if !strings.Contains(string(grub), "linux") {
		return errors.New("grub.cfg sem entrada Linux")
	}
	passwd, _ := os.ReadFile(filepath.Join(l.root, "etc", "passwd"))
	if !strings.Contains(string(passwd), "\n"+l.p.Deployment.Account.UserName+":") {
		return errors.New("usuário inicial não criado")
	}
	return nil
}
