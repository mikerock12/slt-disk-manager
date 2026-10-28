using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLT.WindowsAgent;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("SLT Windows Agent 0.1.0\n  --server https://IP-LAN:7443 --server-pin SHA256 [--state-dir DIR] [--once]\n  [--smartctl C:\\caminho\\smartctl.exe] [--poll-seconds 10] [--file-roots C:\\scopes.json]\n  --collect-only: imprime inventário local somente leitura, sem conectar ao servidor.\n  Para serviço, use o instalador em deploy/. Nunca inclua token administrativo neste cliente.");
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
