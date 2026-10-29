[CmdletBinding()]
param([Parameter(Mandatory)][string]$ServerInstaller,[Parameter(Mandatory)][string]$ClientInstaller)
$ErrorActionPreference='Stop'
# Server keeps the former combined AppId; the client has its own.
$ids=@{server='{A21348C4-4BD9-4533-9F63-B99F3F80D0F1}';client='{6B3F0D52-8E4A-4C71-9A2D-5F1C7E83B904}'}
foreach($id in $ids.Values){foreach($hive in @('HKCU:','HKLM:')){
 if(Test-Path "$hive\Software\Microsoft\Windows\CurrentVersion\Uninstall\${id}_is1"){throw 'Há instalação SLT existente; execute o smoke em ambiente descartável para preservar seu registro.'}
}}
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture=Join-Path $root ('runtime/installer-smoke-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null

function Install-Fixture([string]$Installer,[string]$Name){
 $setup=(Resolve-Path -LiteralPath $Installer).Path
 $target=Join-Path $fixture $Name
 $process=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/SP-',('/DIR="'+$target+'"'),('/LOG="'+(Join-Path $fixture "$Name-setup.log")+'"')) -WindowStyle Hidden -Wait -PassThru
 if($process.ExitCode -ne 0){throw "Instalação silenciosa do $Name falhou."}
 $target
}
function Uninstall-Fixture([string]$Target,[string]$Name,[string]$StateDir,[string]$Executable){
 $state=Join-Path $Target $StateDir
 New-Item -ItemType Directory -Path $state -Force | Out-Null
 $sentinel=Join-Path $state 'synthetic-preserve.txt'
 'SYNTHETIC OPERATING STATE' | Set-Content -LiteralPath $sentinel
 $process=Start-Process -FilePath (Join-Path $Target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
 if($process.ExitCode -ne 0){throw "Desinstalação de fixture do $Name falhou."}
 if(!(Test-Path -LiteralPath $sentinel) -or (Test-Path -LiteralPath (Join-Path $Target $Executable))){throw "Desinstalação do $Name removeu estado gerado ou preservou executável instalado."}
}

$client=Install-Fixture $ClientInstaller 'cliente'
foreach($absent in @('server','desktop','windows-pe','rescue')){if(Test-Path -LiteralPath (Join-Path $client $absent)){throw "Instalador do cliente trouxe $absent."}}
& (Join-Path $client 'client/SLT.WindowsAgent.exe') --help
if($LASTEXITCODE -ne 0){throw 'Cliente instalado não iniciou.'}
$smartctl=Join-Path $client 'client/tools/smartmontools/smartctl.exe'
if(!(Test-Path -LiteralPath $smartctl) -or !(Test-Path -LiteralPath (Join-Path $client 'client/tools/smartmontools/COPYING.txt'))){throw 'smartctl embutido ou sua licença ausente no cliente instalado.'}
$version=& $smartctl --version
if($LASTEXITCODE -ne 0 -or -not ($version -match 'smartctl 7\.5')){throw 'smartctl embutido não executou.'}

$server=Install-Fixture $ServerInstaller 'servidor'
if(Test-Path -LiteralPath (Join-Path $server 'client')){throw 'Instalador do servidor trouxe o cliente.'}
foreach($present in @('server/SLT.Controller.Host.exe','desktop/SLT.Desktop.exe','server/tools/7-Zip/7z.exe','server/tools/7-Zip/License.txt','windows-pe/slt-winpe-agent.exe','windows-pe/New-WinPEKit.ps1','install-server/Install-Server.ps1')){
 if(!(Test-Path -LiteralPath (Join-Path $server $present))){throw "Servidor instalado sem $present."}
}
& (Join-Path $server 'server/tools/7-Zip/7z.exe') i | Out-Null
if($LASTEXITCODE -ne 0){throw '7-Zip embutido não executou.'}

Uninstall-Fixture $client 'cliente' 'state/client' 'client/SLT.WindowsAgent.exe'
Uninstall-Fixture $server 'servidor' 'state/server' 'server/SLT.Controller.Host.exe'
Write-Output 'PASS installers: separate per-user server and client, bundled 7-Zip and smartctl run, uninstall preserves generated operating state.'
Write-Output "Evidência sintética preservada: $fixture"
