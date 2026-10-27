[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    dotnet build SLT.DiskManager.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build .NET falhou.' }
    $go = Get-Command go -ErrorAction SilentlyContinue
    $goPath = if ($go) { $go.Source } else { Join-Path $projectRoot '.tools/go/bin/go.exe' }
    if (-not (Test-Path -LiteralPath $goPath)) { throw 'Instale Go 1.26+ do site oficial para compilar Rescue.' }
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    Push-Location src/rescue-agent
    try {
        & $goPath build -trimpath -o ../../artifacts/slt-rescue-agent-fixture.exe ./cmd/slt-rescue-agent
        if ($LASTEXITCODE -ne 0) { throw 'Build Go Windows falhou.' }
        $env:GOOS='linux'; $env:GOARCH='amd64'; $env:CGO_ENABLED='0'
        & $goPath build -trimpath -o ../../artifacts/slt-rescue-agent-linux-amd64 ./cmd/slt-rescue-agent
        if ($LASTEXITCODE -ne 0) { throw 'Build Go Linux falhou.' }
    } finally { Remove-Item Env:GOOS,Env:GOARCH,Env:CGO_ENABLED -ErrorAction SilentlyContinue; Pop-Location }
} finally { Pop-Location }
