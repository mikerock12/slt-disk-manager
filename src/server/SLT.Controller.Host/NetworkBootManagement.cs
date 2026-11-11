using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Slt.NetworkBoot;

namespace Slt.Controller;

public sealed class NetworkBootManagement : IAsyncDisposable
{
    private readonly string root;
    private readonly string executable;
    private readonly Action<string, string> audit;
    private readonly TrustedBootProfiles trusted = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Queue<BootEvent> events = new();
    private Task? eventReader;
    private NetworkBootConfig config = new();
    private Process? process;
    private volatile string state = "STOPPED";
    private string? lastError;
    private DateTimeOffset? started;
    private volatile BootProfile[] profiles = [];
    public NetworkBootManagement(string directory, string executable, Action<string, string> audit)
    {
        root = Path.Combine(directory, "network-boot"); this.executable = Path.GetFullPath(executable); this.audit = audit;
        Directory.CreateDirectory(Store);
        try
        {
            trusted = new TrustedBootProfiles(Path.Combine(Path.GetDirectoryName(this.executable)!, "trusted-boot-profiles.json"));
            if (File.Exists(ConfigPath)) config = BootJson.Parse<NetworkBootConfig>(File.ReadAllText(ConfigPath));
            profiles = Directory.GetDirectories(Store).Where(p => !Path.GetFileName(p).StartsWith("incoming-", StringComparison.Ordinal)).Select(p =>
            {
                var f = BootJson.Parse<BootProfile>(File.ReadAllText(BootArtifactStore.SafeFile(p, "netboot-manifest.json"))); BootArtifactStore.Validate(f); return trusted.Classify(f);
            }).ToArray();
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Text.Json.JsonException) { lastError = "Configuração de boot inválida: " + e.Message; profiles = []; }
        // A saved configuration is never permission to bind after restart.
    }
    private string Store => Path.Combine(root, "store");
    private string ConfigPath => Path.Combine(root, "config.json");
    private void Record(string action, string message)
    {
        AddEvent(new(DateTimeOffset.UtcNow, action, message)); audit("NETWORK_BOOT_" + action, message);
    }
    private void AddEvent(BootEvent item) { lock (events) { events.Enqueue(item); while (events.Count > 200) events.Dequeue(); } }
    private BootEvent[] Events() { lock (events) return events.ToArray(); }
    private void CheckProcess()
    {
        if (process != null && process.HasExited && state == "RUNNING") { state = "FAILED"; lastError = "Processo de boot encerrou; nenhum reinício automático."; Record("FAILED", lastError); }
    }
    private BootStatus Snapshot() { CheckProcess(); return new(state, config, started, lastError, profiles.ToArray(), Events()); }
    public async Task<BootStatus> Status()
    {
        await gate.WaitAsync(); try { return Snapshot(); } finally { gate.Release(); }
    }
    private void EnsureStopped() { CheckProcess(); if (state is "RUNNING" or "STARTING") throw new ApiException(409, "Pare o boot antes de alterar configuração/perfis."); }
    public async Task<NetworkBootConfig> Configure(NetworkBootConfig value)
    {
        await gate.WaitAsync(); try
        {
            EnsureStopped(); var validation = NetworkTopologyValidator.Validate(value);
            if (!validation.Valid) throw new ApiException(400, string.Join(" ", validation.Errors));
            var temp = ConfigPath + ".tmp"; File.WriteAllText(temp, BootJson.Serialize(value)); File.Move(temp, ConfigPath, true);
            config = value; lastError = null; Record("CONFIGURED", $"Interface {value.InterfaceId}, {value.BindIPv4}, {value.AllowedCidr}, {value.Mode}."); return config;
        }
        finally { gate.Release(); }
    }
    private BootValidation ValidateLocked(bool ports)
    {
        var initial = NetworkTopologyValidator.Validate(config); var errors = initial.Errors.ToList();
        if (!File.Exists(executable)) errors.Add("Executável do serviço Network Boot ausente no pacote.");
        if (profiles.Count(p => p.Enabled && p.Kind == "IPXE_BOOTSTRAP") != 1) errors.Add("Habilite exatamente um perfil iPXE BIOS/UEFI.");
        if (!profiles.Any(p => p.Enabled && p.Kind == "RESCUE_LINUX")) errors.Add("Importe/habilite um perfil Rescue Linux.");
        if (profiles.Any(p => p.Enabled && p.ValidationStatus != "NETWORK_BOOT_LAB")) errors.Add("Perfil de desenvolvimento bloqueado: importe o pacote netboot que passou pelo laboratório da mesma release.");
        try { BootArtifactStore.VerifyAll(Store, profiles.Where(p => p.Enabled)); }
        catch (Exception e) when (e is IOException or ArgumentException) { errors.Add("Artefatos: " + e.Message); }
        if (ports && initial.Valid)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(config.BindIPv4), 69));
                using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind(new IPEndPoint(IPAddress.Parse(config.BindIPv4), config.HttpPort));
            }
            catch (SocketException e) { errors.Add("Conflito/permissão de portas TFTP69/HTTP: " + e.SocketErrorCode); }
            if (config.Mode == "PROXY_DHCP")
            {
                try
                {
                    // Exclusive on purpose: another DHCP or PXE service on this computer must make the start fail.
                    using var dhcp = new UdpClient(new IPEndPoint(IPAddress.Any, 67));
                    using var boot = new UdpClient(new IPEndPoint(IPAddress.Parse(config.BindIPv4), 4011));
                }
                catch (SocketException e) { errors.Add("ProxyDHCP: portas UDP 67/4011 ocupadas ou sem permissão (" + e.SocketErrorCode + "). Há outro servidor DHCP/PXE neste computador?"); }
            }
        }
        return new(errors.Count == 0, errors.ToArray(), initial.Warnings);
    }
    public async Task<BootValidation> Validate()
    {
        await gate.WaitAsync(); try { CheckProcess(); var result = ValidateLocked(state != "RUNNING"); Record("VALIDATED", result.Valid ? "Pré-verificação aprovada; não iniciou listeners." : string.Join(" ", result.Errors)); return result; } finally { gate.Release(); }
    }
    public async Task<BootProfile> Import(string directory)
    {
        await gate.WaitAsync(); try
        {
            EnsureStopped(); if (profiles.Length >= 32) throw new ApiException(409, "Limite de 32 perfis.");
            var p = trusted.Classify(BootArtifactStore.Import(Path.GetFullPath(directory), Store)); profiles = [.. profiles, p]; Record("IMPORTED", p.Id); return p;
        }
        finally { gate.Release(); }
    }
    public async Task<BootProfile> Enable(string id, bool enabled)
    {
        await gate.WaitAsync(); try
        {
            EnsureStopped(); var p = profiles.SingleOrDefault(p => p.Id == id) ?? throw new ApiException(404, "Perfil inexistente.");
            p = p with { Enabled = enabled }; if (enabled) BootArtifactStore.VerifyAll(Store, [p]);
            File.WriteAllText(BootArtifactStore.SafeFile(Store, p.Id + "/netboot-manifest.json"), BootJson.Serialize(p));
            profiles = profiles.Select(old => old.Id == id ? p : old).ToArray(); Record("PROFILE", id + " enabled=" + enabled); return p;
        }
        finally { gate.Release(); }
    }
    public async Task<BootStatus> Start(bool confirm)
    {
        await gate.WaitAsync(); try
        {
            EnsureStopped(); if (!confirm) throw new ApiException(400, "Confirme explicitamente a rede de bancada isolada.");
            var validation = ValidateLocked(true); if (!validation.Valid) throw new ApiException(400, string.Join(" ", validation.Errors));
            state = "STARTING"; lastError = null;
            try
            {
                process?.Dispose();
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { info.FileName = "dotnet"; info.ArgumentList.Add(executable); }
                process = Process.Start(info) ?? throw new IOException("Não foi possível iniciar serviço de boot.");
                // Drain stderr without retaining attacker-controlled unbounded output.
                process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
                await process.StandardInput.WriteLineAsync(BootJson.Serialize(new BootLaunch(config, Store, profiles.Where(p => p.Enabled).ToArray()))); await process.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var reply = await process.StandardOutput.ReadLineAsync(timeout.Token);
                using var json = System.Text.Json.JsonDocument.Parse(reply ?? "{}");
                if (!json.RootElement.TryGetProperty("state", out var response) || response.GetString() != "RUNNING") throw new IOException(json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Processo não confirmou os listeners.");
                state = "RUNNING"; started = DateTimeOffset.UtcNow; Record("STARTED", $"{config.BindIPv4}:69/{config.HttpPort}; {config.Mode}; rede isolada confirmada localmente.");
                var reader = process.StandardOutput;
                eventReader = Task.Run(async () =>
                {
                    try
                    {
                        while (await reader.ReadLineAsync() is { } item)
                        {
                            if (item.Length > 2048) continue;
                            var value = BootJson.Parse<BootEvent>(item);
                            if (value.Event is "TFTP" or "HTTP" or "HTTP_BEGIN" or "PROXY" && value.Message.Length <= 1024) AddEvent(value);
                        }
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or System.Text.Json.JsonException or InvalidDataException) { }
                });
            }
            catch (Exception e)
            {
                if (process is { HasExited: false }) { process.Kill(true); await process.WaitForExitAsync(); }
                state = "FAILED"; lastError = e.Message; Record("FAILED", lastError); throw new ApiException(409, "Boot não iniciou: " + lastError);
            }
            return Snapshot();
        }
        finally { gate.Release(); }
    }
    public async Task<BootStatus> Stop()
    {
        await gate.WaitAsync(); try
        {
            if (process is { HasExited: false })
            {
                process.StandardInput.Close(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(deadline.Token); } catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
            }
            if (eventReader != null) { await eventReader; eventReader = null; }
            process?.Dispose(); process = null; state = "STOPPED"; started = null; Record("STOPPED", "Listeners encerrados; jobs do Controller preservados."); return Snapshot();
        }
        finally { gate.Release(); }
    }
    // Read without the gate: callers hold the Controller lock, and Record() takes that lock while holding the gate.
    public (RescueBootSource? Source, string Reason) RescueSource()
    {
        var current = state; var c = config; var list = profiles;
        if (current != "RUNNING" || process is not { HasExited: false }) return (null, "Inicie o Boot pela rede (aba Boot pela rede): o modo SLT carrega o sistema a partir do servidor.");
        var ipxe = list.SingleOrDefault(p => p.Enabled && p.Kind == "IPXE_BOOTSTRAP"); var loader = ipxe?.Artifacts.SingleOrDefault(a => a.Id == "uefi-local");
        if (ipxe == null || loader == null) return (null, "O perfil iPXE importado não traz o carregador do modo SLT. Importe o pacote netboot desta versão.");
        var rescue = list.Where(p => p.Enabled && p.Kind == "RESCUE_LINUX").OrderBy(p => p.Id, StringComparer.Ordinal).LastOrDefault();
        if (rescue == null) return (null, "Habilite um perfil SLT Rescue no Boot pela rede.");
        return (new($"http://{c.BindIPv4}:{c.HttpPort}", rescue.Id, BootArtifactStore.SafeFile(Store, ipxe.Id + "/" + loader.RelativePath), loader.Sha256.ToLowerInvariant(), loader.SizeBytes), "");
    }
    public async ValueTask DisposeAsync() { await Stop(); gate.Dispose(); }
}
