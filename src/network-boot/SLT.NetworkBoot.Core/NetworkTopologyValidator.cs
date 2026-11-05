using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Slt.NetworkBoot;

public static class NetworkTopologyValidator
{
    public static BootInterface[] Interfaces() => NetworkInterface.GetAllNetworkInterfaces().SelectMany(n =>
    {
        bool physical = n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.OperationalStatus == OperationalStatus.Up &&
            !new[] { "virtual", "vpn", "hyper-v", "vmware", "virtualbox", "tap", "tun", "tailscale", "wireguard", "wsl" }.Any(s => (n.Name + " " + n.Description).Contains(s, StringComparison.OrdinalIgnoreCase));
        return n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a =>
        {
            return new BootInterface(n.Id, n.Name, n.Description, a.Address.ToString(), CanonicalCidr(a.Address, a.PrefixLength), n.Speed / 1_000_000,
                physical && BootJson.Private(a.Address), physical ? "" : "Selecione Ethernet física ativa; VPN/virtual/Wi-Fi recusadas por padrão.");
        });
    }).ToArray();

    private static string CanonicalCidr(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        for (int i = 0; i < 4; i++) bytes[i] &= (byte)(prefix >= (i + 1) * 8 ? 255 : prefix <= i * 8 ? 0 : 255 << (8 - prefix % 8));
        return $"{new IPAddress(bytes)}/{prefix}";
    }
    public static BootValidation Validate(NetworkBootConfig c, bool laboratory = false)
    {
        var errors = new List<string>();
        if (c.SchemaVersion != 1) errors.Add("Versão de configuração inválida.");
        if (c.Mode != "EXTERNAL_DHCP") errors.Add("Somente EXTERNAL_DHCP disponível; ProxyDHCP ainda não validado.");
        if (c.HttpPort is < 1024 or > 65535 || c.HttpPort == 7443) errors.Add("HTTP boot deve usar porta 1024–65535 diferente da API 7443.");
        if (c.MaxConcurrentTransfers is < 1 or > 32) errors.Add("Limite de transferências deve ser 1–32.");
        if (!IPAddress.TryParse(c.BindIPv4, out var ip) || !(BootJson.Private(ip) || laboratory && IPAddress.IsLoopback(ip))) errors.Add("Bind deve ser IPv4 RFC1918 explícito.");
        if (!IPNetwork.TryParse(c.AllowedCidr, out var network) || network.BaseAddress.AddressFamily != AddressFamily.InterNetwork ||
            !(BootJson.Private(network.BaseAddress) || laboratory && IPAddress.IsLoopback(network.BaseAddress)) || network.PrefixLength < 8 || ip == null || !network.Contains(ip)) errors.Add("CIDR privado canônico deve conter o IP selecionado.");
        else
        {
            var last = network.BaseAddress.GetAddressBytes();
            for (int i = 0; i < 4; i++) last[i] |= (byte)(network.PrefixLength >= (i + 1) * 8 ? 0 : network.PrefixLength <= i * 8 ? 255 : 255 >> network.PrefixLength % 8);
            if (!(BootJson.Private(new IPAddress(last)) || laboratory && IPAddress.IsLoopback(new IPAddress(last)))) errors.Add("Todo o CIDR deve permanecer em RFC1918.");
        }
        if (ip != null && !laboratory)
        {
            var selected = Interfaces().SingleOrDefault(n => n.Id == c.InterfaceId && n.Ipv4 == c.BindIPv4 && n.Eligible);
            if (selected == null) errors.Add("Interface/IP não é uma Ethernet física elegível e ativa.");
            else if (IPNetwork.TryParse(c.AllowedCidr, out var requested) && IPNetwork.Parse(selected.Cidr) is var actual &&
                     (requested.PrefixLength < actual.PrefixLength || !actual.Contains(requested.BaseAddress))) errors.Add("CIDR autorizado deve ficar dentro da sub-rede da Ethernet selecionada.");
        }
        return new(errors.Count == 0, errors.ToArray(), ["DHCP externo deve fornecer IP, next-server e bootfile por arquitetura; SLT não altera o roteador.", "Secure Boot e hardware físico não homologados. HTTP/TFTP pré-boot exigem rede de bancada isolada."]);
    }
}
