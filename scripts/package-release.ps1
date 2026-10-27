[CmdletBinding()]
param([string]$Version='0.1.0-beta.1')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$output=Join-Path $projectRoot "artifacts/SLT-Disk-Manager-$Version-win-x64"
if(Test-Path -LiteralPath $output){throw "Destino existente; preserve a versão anterior ou escolha outra: $output"}
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $projectRoot
try {
    $env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools/dotnet-home'
    $env:NUGET_PACKAGES=Join-Path $projectRoot '.tools/nuget'
    $projects=@{
        server='src/server/SLT.Controller.Host/SLT.Controller.Host.csproj'
        desktop='src/desktop/SLT.Desktop.Wpf/SLT.Desktop.Wpf.csproj'
        client='src/windows-agent/SLT.WindowsAgent/SLT.WindowsAgent.csproj'
    }
    foreach($name in $projects.Keys){
        dotnet publish $projects[$name] -c Release -r win-x64 --self-contained true -o (Join-Path $output $name) --nologo
        if($LASTEXITCODE -ne 0){throw "Falha no publish $name"}
    }
    Copy-Item -LiteralPath README.md -Destination $output
    Copy-Item -LiteralPath docs/QUICKSTART.md -Destination $output
    Copy-Item -LiteralPath docs -Destination (Join-Path $output 'docs') -Recurse
    foreach($document in @('AGENTS.md','contexto.md','SECURITY.md','CHANGELOG.md','SLT_Disk_Manager_Especificacao_Tecnica.md')){Copy-Item -LiteralPath $document -Destination $output}
    Copy-Item -LiteralPath LICENSE -Destination $output
    Copy-Item -Path deploy/portable/* -Destination $output -Recurse
    Copy-Item -LiteralPath deploy/windows-agent-installer -Destination (Join-Path $output 'install-agent') -Recurse
    Copy-Item -LiteralPath deploy/server-installer -Destination (Join-Path $output 'install-server') -Recurse
    New-Item -ItemType Directory -Path (Join-Path $output 'rescue') | Out-Null
    $rescueBinary=if(Test-Path artifacts/slt-rescue-agent-linux-amd64){'artifacts/slt-rescue-agent-linux-amd64'}else{'artifacts/slt-rescue-agent'}
    Copy-Item -LiteralPath $rescueBinary -Destination (Join-Path $output 'rescue/slt-rescue-agent')
    Copy-Item -LiteralPath rescue/README.md -Destination (Join-Path $output 'rescue/README.md')
    Copy-Item -LiteralPath rescue/scripts -Destination (Join-Path $output 'rescue/scripts') -Recurse
    Copy-Item -LiteralPath rescue/profiles -Destination (Join-Path $output 'rescue/profiles') -Recurse
    Get-ChildItem -LiteralPath $output -Recurse -File | Get-FileHash -Algorithm SHA256 | ForEach-Object {
        '{0}  {1}' -f $_.Hash.ToLowerInvariant(), $_.Path.Substring($output.Length+1).Replace('\','/')
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Compress-Archive -LiteralPath $output -DestinationPath "$output.zip"
    Write-Output "Pacote: $output.zip"
    Get-FileHash -LiteralPath "$output.zip" -Algorithm SHA256
} finally {Pop-Location}
