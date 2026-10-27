using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SLT.WindowsAgent;

public sealed class AgentWorker(AgentOptions options, ILogger<AgentWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await ExecuteCore(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { Environment.ExitCode = 1; throw; }
    }

    private async Task ExecuteCore(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SLT Windows Agent exige Windows 10/11 x64.");
        var state = new SecureState(options.StateDirectory);
        using var singleInstance = new FileStream(state.FilePath("agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var roots = new FileRootCatalog(options);
        var collector = new InventoryCollector(options, roots);
        var identity = await Enrollment.GetIdentity(options, state, message => logger.LogInformation("{Pairing}", message), stoppingToken);
        using var certificate = identity.Certificate;
        using var transport = new PinnedTransport(options, certificate);
        logger.LogInformation("Agente pareado {DeviceId}; servidor {Server}.", identity.DeviceId, options.Server);
        Inventory? cachedInventory = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Heartbeats reuse the last snapshot; CIM enumeration every poll made file browsing unnecessarily slow.
                if (cachedInventory is null || cachedInventory.CapturedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-1))
                    cachedInventory = await collector.Collect(identity.DeviceId, stoppingToken);
                await transport.Post<object>("/agent/v1/inventory", cachedInventory, stoppingToken);
                var assignment = await transport.Get<SignedAssignment>("/agent/v1/jobs/next", stoppingToken);
                if (assignment is not null) await Execute(assignment, transport, collector, roots, state, identity.DeviceId, stoppingToken);
                if (options.Once) { lifetime.StopApplication(); return; }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogError("Credencial recusada ou revogada (HTTP {Status}). Novo pareamento requer intervenção local.", ex.StatusCode);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Ciclo do agente falhou: {Error}", ex.Message);
                if (options.Once) throw;
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
        }
    }

    private async Task Execute(SignedAssignment assignment, PinnedTransport transport, InventoryCollector collector, FileRootCatalog roots,
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
                if (plan.Kind.StartsWith("FILE_", StringComparison.Ordinal)) result = await new FileJobExecutor(roots, transport).Execute(plan, ct);
                else
                {
                    // Refresh hardware immediately before running a diagnostic.
                    var current = await collector.Collect(deviceId, ct);
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
    }
}
