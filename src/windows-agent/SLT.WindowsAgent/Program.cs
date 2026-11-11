using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLT.WindowsAgent;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("""
        SLT Windows Agent 0.1.0
          Sem opções: procura o servidor SLT Disk Manager na rede local e mostra o código de pareamento.
          [--server https://IP-LAN:7443] [--server-pin SHA256]  endereço e pin, quando a busca automática não serve
          [--state-dir DIR] [--once] [--poll-seconds 10] [--smartctl C:\caminho\smartctl.exe] [--file-roots C:\scopes.json]
          [--discovery-port 7444] [--discovery-address IP-LAN]
          --reboot-to-slt [--yes]: reinicia este computador em modo SLT Disk Manager (cliente já pareado, administrador).
          --collect-only: imprime inventário local somente leitura, sem conectar ao servidor.
          Para serviço, use o instalador em deploy/. Nunca inclua token administrativo neste cliente.
        """);
    return 0;
}
try
{
    var options = AgentOptions.Parse(args);
    if (options.CollectOnly)
    {
        var inventory = await new InventoryCollector(options).Collect(Guid.NewGuid().ToString(), CancellationToken.None);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inventory, Wire.Json));
        return 0;
    }
    if (options.RebootToSlt) return await SltModeCommand.Run(options, CancellationToken.None);
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton(options);
    builder.Services.AddWindowsService(settings => settings.ServiceName = "SLTWindowsAgent");
    builder.Services.AddHostedService<AgentWorker>();
    builder.Logging.AddSimpleConsole(settings => { settings.SingleLine = true; settings.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
    await builder.Build().RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine("SLT Windows Agent: " + ex.Message);
    return 1;
}
