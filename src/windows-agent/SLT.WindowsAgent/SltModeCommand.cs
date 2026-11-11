using System.Net;

namespace SLT.WindowsAgent;

// "Reiniciar em modo SLT Disk Manager" asked at this computer (shortcut or command line).
// Uses the identity of the already paired client; the running service keeps working meanwhile.
public static class SltModeCommand
{
    public static async Task<int> Run(AgentOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Exige Windows 10/11 x64.");
        var support = RescueBoot.Probe();
        if (!support.Available) { Console.Error.WriteLine("Este computador não pode reiniciar em modo SLT: " + support.Reason); return 2; }
        var state = new SecureState(options.StateDirectory);
        if (!Enrollment.HasIdentity(state)) { Console.Error.WriteLine("Este cliente ainda não foi conectado ao servidor. Abra o cliente e faça o pareamento primeiro."); return 2; }
        var resolved = await ServerLocator.Resolve(options, state, Console.WriteLine, ct);
        if (!resolved.PinVerified) { Console.Error.WriteLine("Servidor deste cliente não está registrado; abra o cliente normalmente uma vez."); return 2; }
        if (!options.AssumeYes)
        {
            Console.WriteLine("\nEste computador vai REINICIAR no modo SLT Disk Manager.");
            Console.WriteLine("Nesse modo o servidor da assistência controla os discos (clonar, formatar, instalar sistema).");
            Console.WriteLine("Salve e feche tudo antes de continuar.");
            Console.Write("Digite REINICIAR para confirmar: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "REINICIAR", StringComparison.Ordinal)) { Console.WriteLine("Cancelado. Nada foi alterado."); return 3; }
        }
        var identity = await Enrollment.GetIdentity(resolved, state, Console.WriteLine, ct);
        using var certificate = identity.Certificate;
        using var transport = new PinnedTransport(resolved, certificate);
        var commands = new WindowsSystemCommands();
        SignedAssignment assignment;
        try { assignment = await transport.Post<SignedAssignment>("/agent/v1/rescue-boot", new { }, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Conflict)
        {
            Console.Error.WriteLine("O servidor não pode iniciar o modo SLT agora. No painel, confira a aba Boot pela rede (perfil SLT Rescue ativo) e se não há tarefa em andamento neste computador.");
            return 4;
        }
        var plan = transport.VerifyAssignment(assignment, identity.DeviceId, DateTimeOffset.UtcNow);
        var jobPath = "/agent/v1/jobs/" + Uri.EscapeDataString(plan.Id);
        if (plan.Kind != "REBOOT_RESCUE" || !state.TryClaimJob(plan.Id)) throw new InvalidOperationException("Plano inesperado recebido do servidor.");
        JobResult result;
        try { result = await RescueBoot.Execute(plan, transport, state, commands, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { result = new(false, "Preparação do modo SLT falhou.", ex.Message); }
        await transport.Post<object>(jobPath + "/result", result, ct);
        if (!result.Success) { Console.Error.WriteLine(result.Message + " " + result.Details); return 5; }
        await RescueBoot.Restart(commands, Math.Min(plan.RescueBoot!.RebootDelaySeconds, 15), ct);
        Console.WriteLine("Pronto. O computador reinicia em instantes no modo SLT Disk Manager. Para desistir agora: shutdown /a");
        return 0;
    }
}
