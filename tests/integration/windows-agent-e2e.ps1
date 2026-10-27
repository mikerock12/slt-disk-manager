#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RepositoryRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))),
    [int]$Port = 17444,
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Este E2E exige Windows e faz somente inventario local em leitura.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Escolha uma porta de laboratorio entre 1024 e 65535.' }
$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$runtime = Join-Path $repository ('runtime\windows-agent-e2e-' + [Guid]::NewGuid().ToString('N'))
$serverState = Join-Path $runtime 'server'
$agentState = Join-Path $runtime 'agent'
$serverDll = Join-Path $repository 'src\server\SLT.Controller.Host\bin\Debug\net10.0\SLT.Controller.Host.dll'
$agentDll = Join-Path $repository 'src\windows-agent\SLT.WindowsAgent\bin\Debug\net10.0\SLT.WindowsAgent.dll'
foreach ($binary in @($serverDll, $agentDll)) {
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw ('Compile primeiro: ' + $binary) }
}
New-Item -ItemType Directory -Path $runtime -Force | Out-Null

# C# keeps certificate validation on the TLS callback thread, independent of a PowerShell runspace.
# There is intentionally no SkipCertificateCheck or accept-all callback.
if (-not ('SltWindowsE2E.PinnedAdmin' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
namespace SltWindowsE2E {
  public sealed class PinnedAdmin : IDisposable {
    private readonly HttpClient client;
    public PinnedAdmin(string url, string token, string pin) {
      var expected = Convert.FromHexString(pin);
      var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
      handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        certificate != null && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
        certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), expected);
      client = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
      client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public string Get(string path) {
      using var response = client.GetAsync(path).GetAwaiter().GetResult();
      response.EnsureSuccessStatusCode(); return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
    public string Post(string path, string body) {
      using var content = new StringContent(body, Encoding.UTF8, "application/json");
      using var response = client.PostAsync(path, content).GetAwaiter().GetResult();
      response.EnsureSuccessStatusCode(); return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
    public void Dispose() { client.Dispose(); }
  }
}
'@
}

function Wait-Condition([scriptblock]$Check, [string]$Message) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (& $Check) { return }
        Start-Sleep -Milliseconds 500
    }
    throw $Message
}
function Start-DotnetProcess([string[]]$ProcessArguments, [string]$LogName) {
    $quoted = foreach ($value in $ProcessArguments) {
        if ($value.Contains('"') -or $value.Contains("`n") -or $value.Contains("`r")) { throw 'Argumento de processo invalido.' }
        '"' + $value + '"'
    }
    Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $quoted -WorkingDirectory $repository `
        -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtime ($LogName + '.stdout.log')) `
        -RedirectStandardError (Join-Path $runtime ($LogName + '.stderr.log')) -PassThru
}
function Wait-AgentExit([Diagnostics.Process]$Process) {
    Wait-Condition { $Process.Refresh(); $Process.HasExited } 'Agente nao encerrou no prazo de laboratorio.'
    $Process.WaitForExit()
    $Process.Refresh()
    return $Process.ExitCode
}
$serverProcess = $null
$agentProcess = $null
$admin = $null
try {
    $serverProcess = Start-DotnetProcess @($serverDll, '--bind', '127.0.0.1', '--port', [string]$Port, '--state-dir', $serverState) 'controller'
    $connectionPath = Join-Path $serverState 'connection.json'
    Wait-Condition {
        $serverProcess.Refresh()
        if ($serverProcess.HasExited) { throw 'Controller encerrou durante inicializacao. Consulte log privado do teste.' }
        if (-not (Test-Path -LiteralPath $connectionPath)) { return $false }
        try { $script:connection = Get-Content -Raw -LiteralPath $connectionPath | ConvertFrom-Json; return [bool]$script:connection.serverPin } catch { return $false }
    } 'Controller nao criou connection.json.'
    $admin = [SltWindowsE2E.PinnedAdmin]::new($connection.url, $connection.token, $connection.serverPin)
    Wait-Condition { try { return (($admin.Get('/health') | ConvertFrom-Json).status -eq 'ok') } catch { return $false } } 'HTTPS do Controller indisponivel.'
    Write-Output 'PASS Controller HTTPS loopback com pin SHA256 validado'

    $agentArguments = @($agentDll, '--server', $connection.url, '--server-pin', $connection.serverPin, '--state-dir', $agentState, '--once')
    $agentProcess = Start-DotnetProcess $agentArguments 'agent-enrollment'
    $pendingPath = Join-Path $agentState 'enrollment.json'
    Wait-Condition {
        $agentProcess.Refresh()
        if ($agentProcess.HasExited) { throw 'Agente encerrou antes do pareamento. Consulte logs privados do teste.' }
        return (Test-Path -LiteralPath $pendingPath)
    } 'Agente nao criou solicitacao de pareamento.'
    $pending = Get-Content -Raw -LiteralPath $pendingPath | ConvertFrom-Json
    $enrollments = @($admin.Get('/api/v1/enrollments') | ConvertFrom-Json)
    $request = @($enrollments | Where-Object { $_.id -eq $pending.id })
    if ($request.Count -ne 1 -or $request[0].code -ne $pending.code -or $request[0].mode -ne 'WINDOWS') { throw 'Codigo/identidade de pareamento divergente.' }
    $approved = $admin.Post('/api/v1/enrollments/' + $pending.id + '/approve', (@{ code = $pending.code } | ConvertTo-Json -Compress)) | ConvertFrom-Json
    if ($approved.status -ne 'APPROVED') { throw 'Pareamento nao aprovado.' }
    $exitCode = Wait-AgentExit $agentProcess
    if ($exitCode -ne 0) { throw ('Agente falhou apos pareamento: exit ' + $exitCode + '. Consulte logs privados.') }
    $identity = Get-Content -Raw -LiteralPath (Join-Path $agentState 'identity.json') | ConvertFrom-Json
    $device = $admin.Get('/api/v1/devices/' + $identity.deviceId) | ConvertFrom-Json
    if ($device.mode -ne 'WINDOWS' -or $device.trustStatus -ne 'TRUSTED' -or $null -eq $device.inventory -or
        $device.inventory.deviceId -ne $identity.deviceId -or $device.inventory.agentVersion -ne '0.1.0') { throw 'Inventario autenticado nao foi persistido.' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile(
        (Join-Path $agentState 'client-cert.pem'), (Join-Path $agentState 'client-key.pem'))
    try {
        if ($certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne $identity.deviceId -or
            $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw 'Certificado cliente invalido.' }
    } finally { $certificate.Dispose() }
    Write-Output ('PASS Pareamento CSR + certificado individual + inventario via mTLS; discos=' + @($device.inventory.disks).Count)

    $plan = $admin.Post('/api/v1/jobs/plan', (@{ kind = 'INVENTORY'; deviceId = $identity.deviceId; diskFingerprint = ''; imageId = ''; consent = $true } | ConvertTo-Json -Compress)) | ConvertFrom-Json
    $null = $admin.Post('/api/v1/jobs/' + $plan.id + '/approve', (@{ challenge = $plan.plan.challenge } | ConvertTo-Json -Compress))
    $agentProcess = Start-DotnetProcess $agentArguments 'agent-signed-job'
    if ((Wait-AgentExit $agentProcess) -ne 0) { throw 'Agente falhou ao reutilizar certificado e executar job assinado.' }
    $jobs = @($admin.Get('/api/v1/jobs') | ConvertFrom-Json)
    $job = @($jobs | Where-Object { $_.id -eq $plan.id })
    if ($job.Count -ne 1 -or $job[0].state -ne 'COMPLETED' -or -not $job[0].result.success) { throw 'Job INVENTORY assinado nao foi concluido.' }
    Write-Output 'PASS Certificado persistido reutilizado e job INVENTORY RSA concluido'

    $null = $admin.Post('/api/v1/devices/' + $identity.deviceId + '/revoke', '{}')
    $agentProcess = Start-DotnetProcess $agentArguments 'agent-revoked'
    if ((Wait-AgentExit $agentProcess) -eq 0) { throw 'Agente revogado encerrou com sucesso indevido.' }
    Write-Output 'PASS Certificado revogado recusado sem novo pareamento automatico'
    Write-Output 'Windows Agent E2E: 4/4 cenarios aprovados. Nenhuma gravacao de disco executada.'
    Write-Output ('Logs e estado sensiveis somente em diretorio runtime ignorado pelo Git: ' + $runtime)
}
finally {
    if ($admin) { $admin.Dispose() }
    foreach ($process in @($agentProcess, $serverProcess)) {
        if ($process) { $process.Refresh(); if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } }
    }
}
