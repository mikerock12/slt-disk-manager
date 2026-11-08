namespace Slt.NetworkBoot;

public static class BootScriptRenderer
{
    public static string Autoexec(NetworkBootConfig c) => $"#!ipxe\ndhcp || exit\nchain http://{c.BindIPv4}:{c.HttpPort}/boot/v1/menu.ipxe || exit\n";
    public static string Menu(NetworkBootConfig c, BootProfile[] profiles)
    {
        if (!NetworkTopologyValidator.Validate(c, true).Valid) throw new InvalidDataException("Configuração inválida para menu.");
        var url = $"http://{c.BindIPv4}:{c.HttpPort}/boot/v1/assets/";
        var p = profiles.Where(p => p.Enabled && p.Kind == "RESCUE_LINUX").ToArray();
        foreach (var profile in p) BootArtifactStore.Validate(profile);
        // Labels, URLs and arguments are server-generated. Display names from import are never script source.
        var lines = new List<string> { "#!ipxe", "menu SLT Network Boot - laboratorio", "item exit Sair / retornar ao firmware (padrao)" };
        foreach (var profile in p) lines.Add($"item {profile.Id} SLT Rescue Linux {profile.Id} - BIOS/UEFI sem Secure Boot");
        lines.AddRange(["choose --timeout 15000 --default exit selected || goto exit", "goto ${selected}"]);
        foreach (var profile in p)
        {
            lines.AddRange([$":{profile.Id}", "imgfree", $"kernel --name vmlinuz {url}{profile.Id}-kernel boot=live components nopersistence noeject fetch={url}{profile.Id}-rootfs.squashfs hostname=slt-rescue username=technician initrd=initrd.img console=tty0 console=ttyS0,115200n8 || goto failed", $"initrd --name initrd.img {url}{profile.Id}-initrd || goto failed", "boot || goto failed"]);
        }
        lines.AddRange([":failed", "echo Falha de boot. Verifique rede/servidor; nenhum disco foi modificado.", "sleep 5", ":exit", "exit"]);
        return string.Join('\n', lines) + "\n";
    }
}
