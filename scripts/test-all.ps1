[CmdletBinding()]
param([switch]$Integration)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools/dotnet-home'
    $env:NUGET_PACKAGES=Join-Path $projectRoot '.tools/nuget'
    foreach($project in @('tests/SLT.Controller.Tests','tests/SLT.WindowsAgent.Tests')) {
        dotnet run --project $project
        if($LASTEXITCODE -ne 0) { throw "Falha: $project" }
    }
    $go=Get-Command go -ErrorAction SilentlyContinue
    $goPath=if($go){$go.Source}else{Join-Path $projectRoot '.tools/go/bin/go.exe'}
    Push-Location src/rescue-agent
    try { & $goPath test ./...; if($LASTEXITCODE -ne 0){throw 'Testes Go falharam.'} } finally {Pop-Location}
    if($Integration) {
        python tests/integration/rescue-e2e.py
        if($LASTEXITCODE -ne 0){throw 'Integração Rescue falhou.'}
        & tests/integration/windows-agent-e2e.ps1
    }
} finally {Pop-Location}
