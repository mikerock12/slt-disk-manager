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
    # Third-party tools the app calls, shipped with their licenses (docs/12-licensing-third-party.md).
    $sevenSource=Join-Path $env:ProgramFiles '7-Zip'
    foreach($file in '7z.exe','7z.dll','License.txt'){if(-not(Test-Path -LiteralPath (Join-Path $sevenSource $file))){throw "7-Zip ausente em $sevenSource; instale-o para empacotar o servidor."}}
    $sevenOut=Join-Path $output 'server/tools/7-Zip'
    New-Item -ItemType Directory -Path $sevenOut -Force | Out-Null
    foreach($file in '7z.exe','7z.dll','License.txt','readme.txt'){$path=Join-Path $sevenSource $file;if(Test-Path -LiteralPath $path){Copy-Item -LiteralPath $path -Destination $sevenOut}}
    $sevenVersion=(Get-Item -LiteralPath (Join-Path $sevenOut '7z.exe')).VersionInfo.ProductVersion
    Set-Content -LiteralPath (Join-Path $sevenOut 'LEIA-ME-SLT.txt') -Encoding utf8 -Value "7-Zip $sevenVersion (Igor Pavlov), GNU LGPL com cláusulas BSD e restrição unRAR descritas em License.txt.`r`nUsado pelo servidor para ler ISO/WIM/ESD do operador. Fonte e binários oficiais: https://www.7-zip.org/"
    $smartUrl='https://github.com/smartmontools/smartmontools/releases/download/RELEASE_7_5/smartmontools-7.5.win32-setup.exe'
    $smartSha='896337fcc253220614cf8cdbd5cf2321c5aa326a37a04160a672a281e6104c70'
    $cache=Join-Path $projectRoot 'artifacts/third-party'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $smartSetup=Join-Path $cache 'smartmontools-7.5.win32-setup.exe'
    if(-not(Test-Path -LiteralPath $smartSetup)){Invoke-WebRequest -Uri $smartUrl -OutFile $smartSetup -UseBasicParsing}
    if((Get-FileHash -LiteralPath $smartSetup -Algorithm SHA256).Hash.ToLowerInvariant() -ne $smartSha){Remove-Item -LiteralPath $smartSetup;throw 'Instalador do smartmontools não confere com o SHA256 fixado.'}
    $smartOut=Join-Path $output 'client/tools/smartmontools'
    New-Item -ItemType Directory -Path $smartOut -Force | Out-Null
    & (Join-Path $sevenOut '7z.exe') e $smartSetup 'bin\smartctl.exe' 'doc\COPYING.txt' 'doc\AUTHORS.txt' "-o$smartOut" -y -bso0 | Out-Null
    if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $smartOut 'smartctl.exe'))){throw 'Extração do smartctl falhou.'}
    Set-Content -LiteralPath (Join-Path $smartOut 'LEIA-ME-SLT.txt') -Encoding utf8 -Value "smartmontools 7.5, GNU GPL versão 2 ou posterior (COPYING.txt).`r`nUsado pelo cliente para o diagnóstico SMART somente leitura. Código-fonte correspondente: https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5`r`nInstalador oficial de origem: $smartUrl`r`nSHA256 conferido: $smartSha"
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
    Copy-Item -LiteralPath rescue/windows-pe -Destination (Join-Path $output 'windows-pe') -Recurse
    Copy-Item -LiteralPath artifacts/slt-winpe-agent.exe -Destination (Join-Path $output 'windows-pe/slt-winpe-agent.exe')
    Get-ChildItem -LiteralPath $output -Recurse -File | Get-FileHash -Algorithm SHA256 | ForEach-Object {
        '{0}  {1}' -f $_.Hash.ToLowerInvariant(), $_.Path.Substring($output.Length+1).Replace('\','/')
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Compress-Archive -LiteralPath $output -DestinationPath "$output.zip"
    Write-Output "Pacote: $output.zip"
    Get-FileHash -LiteralPath "$output.zip" -Algorithm SHA256
} finally {Pop-Location}
