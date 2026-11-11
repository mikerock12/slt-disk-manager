using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SLT.WindowsAgent;

public sealed class AgentWorker(AgentOptions options, ILogger<AgentWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    private sealed class ServerMovedException(Uri address) : Exception { public Uri Address { get; } = address; }
    private readonly ISystemCommands commands = new WindowsSystemCommands();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var current = options;
            while (true)
            {
                try { await ExecuteCore(current, stoppingToken); return; }
                catch (ServerMovedException moved)
                {
                    logger.LogWarning("Servidor mudou de endereço para {Server}; o certificado é o mesmo. Reconectando.", moved.Address);
                    current = current with { Server = moved.Address };
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // The console window stays open (Cliente.cmd pauses), so the reason must be readable there.
            logger.LogError("Cliente encerrado: {Error}", ex.Message);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    private async Task ExecuteCore(AgentOptions requested, CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SLT Windows Agent exige Windows 10/11 x64.");
        var state = new SecureState(requested.StateDirectory);
        using var singleInstance = new FileStream(state.FilePath("agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (await RescueBoot.Cleanup(state, commands, force: false, stoppingToken))
                logger.LogInformation("Entrada temporária do modo SLT removida; a inicialização do Windows segue como antes.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Não foi possível remover a entrada temporária do modo SLT agora: {Error}", ex.Message);
        }
        var resolved = await ServerLocator.Resolve(requested, state, message => logger.LogInformation("{Discovery}", message), stoppingToken);
        if (requested.Server is not null && resolved.Server != requested.Server) resolved = resolved with { Server = requested.Server };
        var roots = new FileRootCatalog(resolved);
        var collector = new InventoryCollector(resolved, roots);
        var identity = await Enrollment.GetIdentity(resolved, state, message => logger.LogInformation("{Pairing}", message), stoppingToken);
        using var certificate = identity.Certificate;
        using var transport = new PinnedTransport(resolved, certificate);
        ServerLocator.Save(state, resolved, "");
        logger.LogInformation("Agente pareado {DeviceId}; servidor {Server}.", identity.DeviceId, resolved.Server);
        var interactive = Environment.UserInteractive && !Console.IsInputRedirected && !resolved.Once;
        if (interactive) Console.WriteLine("\n  Conectado. Deixe esta janela aberta.\n  Tecla R : reiniciar este computador em modo SLT Disk Manager\n  Ctrl+C  : encerrar o cliente\n");
        Inventory? cachedInventory = null;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Heartbeats reuse the last snapshot; CIM enumeration every poll made file browsing unnecessarily slow.
                if (cachedInventory is null || cachedInventory.CapturedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-1))
                    cachedInventory = await collector.Collect(identity.DeviceId, stoppingToken);
                await transport.Post<object>("/agent/v1/inventory", cachedInventory, stoppingToken);
                failures = 0;
                var assignment = await transport.Get<SignedAssignment>("/agent/v1/jobs/next", stoppingToken);
                if (assignment is not null) await Execute(assignment, transport, collector, roots, state, identity.DeviceId, stoppingToken);
                if (resolved.Once) { lifetime.StopApplication(); return; }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogError("Credencial recusada ou revogada (HTTP {Status}). Novo pareamento requer intervenção local.", ex.StatusCode);
                throw;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ex is not ServerMovedException)
            {
                logger.LogError("Ciclo do agente falhou: {Error}", ex.Message);
                if (resolved.Once) throw;
                // No HTTP status means the server did not answer at all; after a few rounds look for it elsewhere.
                if (ex is HttpRequestException { StatusCode: null } or TaskCanceledException && ++failures % 3 == 0 && requested.Server is null &&
                    await ServerLocator.Relocate(resolved, stoppingToken) is { } moved)
                {
                    ServerLocator.Save(state, resolved with { Server = moved }, "");
                    throw new ServerMovedException(moved);
                }
            }
            if (interactive ? await WaitForKey(TimeSpan.FromSeconds(resolved.PollSeconds), stoppingToken) : await Pause(resolved.PollSeconds, stoppingToken))
                await RequestSltMode(transport, state, identity.DeviceId, stoppingToken);
        }
    }

    private static async Task<bool> Pause(int seconds, CancellationToken ct) { await Task.Delay(TimeSpan.FromSeconds(seconds), ct); return false; }

    // True when the technician pressed R and then confirmed with S.
    private static async Task<bool> WaitForKey(TimeSpan time, CancellationToken ct)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.R)
            {
                Console.Write("\n  Reiniciar ESTE computador em modo SLT Disk Manager? O servidor passará a controlar os discos. [S/N]: ");
                var answer = Console.ReadKey(false).Key; Console.WriteLine();
                if (answer == ConsoleKey.S) return true;
                Console.WriteLine("  Cancelado.");
            }
            await Task.Delay(200, ct);
        }
        return false;
    }

    private async Task RequestSltMode(PinnedTransport transport, SecureState state, string deviceId, CancellationToken ct)
    {
        try
        {
            var assignment = await transport.Post<SignedAssignment>("/agent/v1/rescue-boot", new { }, ct);
            await Execute(assignment, transport, null, null, state, deviceId, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Conflict)
        {
            logger.LogWarning("O servidor não pode iniciar o modo SLT agora. No painel, confira a aba Boot pela rede (perfil SLT Rescue ativo) e se não há tarefa em andamento neste computador.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Reinício em modo SLT não foi preparado: {Error}", ex.Message);
        }
    }

    private async Task Execute(SignedAssignment assignment, PinnedTransport transport, InventoryCollector? collector, FileRootCatalog? roots,
        SecureState state, string deviceId, CancellationToken ct)
    {
        var plan = transport.VerifyAssignment(assignment, deviceId, DateTimeOffset.UtcNow);
        var jobPath = "/agent/v1/jobs/" + Uri.EscapeDataString(plan.Id);
        if (!state.TryClaimJob(plan.Id))
        {
            logger.LogWarning("Job {JobId} repetido foi rejeitado pelo diário local.", plan.Id);
            await transport.Post<object>(jobPath + "/result", new JobResult(false, "Job já recebido anteriormente; execução repetida bloqueada.", "REPLAY_BLOCKED"), ct);
            return;
        }
        var progress = await transport.Post<ProgressResponse>(jobPath + "/progress", new JobProgress("VALIDATING", 0, 1), ct);
        JobResult result;
        if (progress.CancelRequested) result = new(false, "Cancelado antes da execução.", "CANCELLED");
        else
        {
            try
            {
                if (plan.Kind == "REBOOT_RESCUE") result = await RescueBoot.Execute(plan, transport, state, commands, ct);
                else if (plan.Kind.StartsWith("FILE_", StringComparison.Ordinal)) result = await new FileJobExecutor(roots ?? throw new InvalidOperationException("Escopos de arquivos indisponíveis."), transport).Execute(plan, ct);
                else
                {
                    // Refresh hardware immediately before running a diagnostic.
                    var current = await (collector ?? throw new InvalidOperationException("Inventário indisponível.")).Collect(deviceId, ct);
                    if (plan.Kind == "INVENTORY")
                    {
                        await transport.Post<object>("/agent/v1/inventory", current, ct);
                        result = new(true, "Inventário atualizado em modo somente leitura.", "INVENTORY_UPDATED");
                    }
                    else result = await collector.Smart(plan, current, ct);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = new(false, "Transferência cancelada; nenhuma pasta existente foi sobrescrita.", "CANCELLED");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new(false, "Operação do agente falhou.", ex.Message);
            }
        }
        // Persist the outcome before acknowledgement, preserving evidence across network failure.
        state.Write("job-" + Guid.Parse(plan.Id).ToString("N") + ".result.json", result);
        await transport.Post<object>(jobPath + "/result", result, ct);
        logger.LogInformation("Job {JobId} {Kind}: {Success} — {Message}", plan.Id, plan.Kind, result.Success, result.Message);
        if (plan.Kind == "REBOOT_RESCUE" && result.Success)
        {
            // The server already knows; only now the restart is scheduled, giving the user time to save work.
            try { await RescueBoot.Restart(commands, plan.RescueBoot!.RebootDelaySeconds, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Reinício não foi agendado ({Error}). Reinicie o computador manualmente: o modo SLT já está selecionado para a próxima inicialização.", ex.Message);
            }
        }
    }
}
