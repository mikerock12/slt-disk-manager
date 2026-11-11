[CmdletBinding()]
param([Parameter(Mandatory)][string]$ServerInstaller,[Parameter(Mandatory)][string]$ClientInstaller)
$ErrorActionPreference='Stop'
# Server keeps the former combined AppId; the client has its own.
$ids=@{server='{A21348C4-4BD9-4533-9F63-B99F3F80D0F1}';client='{6B3F0D52-8E4A-4C71-9A2D-5F1C7E83B904}'}
foreach($id in $ids.Values){foreach($hive in @('HKCU:','HKLM:')){
 if(Test-Path "$hive\Software\Microsoft\Windows\CurrentVersion\Uninstall\${id}_is1"){throw 'Há instalação SLT existente; execute o smoke em ambiente descartável para preservar seu registro.'}
}}
# Never run next to a real installation: the setup would see its services and offer to update them.
foreach($service in 'SLTDiskManagerController','SLTWindowsAgent'){
 if(Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$service"){throw "O serviço $service existe neste computador; execute o smoke em ambiente descartável."}
}
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture=Join-Path $root ('runtime/installer-smoke-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$desktop=[Environment]::GetFolderPath('Desktop')
$group=Join-Path ([Environment]::GetFolderPath('Programs')) 'SLT Disk Manager'
$shell=New-Object -ComObject WScript.Shell

function Install-Fixture([string]$Installer,[string]$Name,[string[]]$Extra=@()){
 $setup=(Resolve-Path -LiteralPath $Installer).Path
 $target=Join-Path $fixture $Name
 # Only the desktop shortcut task: the service tasks stay off, so no service, firewall rule or elevation.
 $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/TASKS="desktopicon"',('/DIR="'+$target+'"'),('/LOG="'+(Join-Path $fixture "$Name-setup.log")+'"'))+$Extra
 $process=Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
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
# Shortcut must exist with this target and arguments; -Elevated checks the "run as administrator" flag of the .lnk.
function Assert-Shortcut([string]$Path,[string]$Target,[string]$Arguments,[switch]$Elevated){
 if(!(Test-Path -LiteralPath $Path)){throw "Atalho ausente: $Path"}
 $link=$shell.CreateShortcut($Path)
 if(-not $link.TargetPath.Equals($Target,[StringComparison]::OrdinalIgnoreCase)){throw "Atalho $Path aponta para $($link.TargetPath), esperado $Target"}
 if($link.Arguments -ne $Arguments){throw "Atalho $Path tem argumentos [$($link.Arguments)], esperado [$Arguments]"}
 $flag=([IO.File]::ReadAllBytes($Path)[0x15] -band 0x20) -ne 0
 if($flag -ne [bool]$Elevated){throw "Atalho $Path com elevação=$flag, esperado $([bool]$Elevated)"}
}
function Assert-NoShortcut([string]$Path){ if(Test-Path -LiteralPath $Path){throw "Atalho não deveria existir: $Path"} }

$client=Install-Fixture $ClientInstaller 'cliente'
foreach($absent in @('server','desktop','windows-pe','rescue','network-boot')){if(Test-Path -LiteralPath (Join-Path $client $absent)){throw "Instalador do cliente trouxe $absent."}}
foreach($present in @('client/SLT.WindowsAgent.exe','client/Hardware/Find-EfiSystemPartition.ps1','Cliente.cmd','Modo-SLT.cmd','Iniciar-Cliente.ps1','Executar-Instalacao.ps1','install-agent/Install-Agent.ps1','install-agent/Update-Agent.ps1')){
 if(!(Test-Path -LiteralPath (Join-Path $client $present))){throw "Cliente instalado sem $present."}
}
& (Join-Path $client 'client/SLT.WindowsAgent.exe') --help
if($LASTEXITCODE -ne 0){throw 'Cliente instalado não iniciou.'}
$smartctl=Join-Path $client 'client/tools/smartmontools/smartctl.exe'
if(!(Test-Path -LiteralPath $smartctl) -or !(Test-Path -LiteralPath (Join-Path $client 'client/tools/smartmontools/COPYING.txt'))){throw 'smartctl embutido ou sua licença ausente no cliente instalado.'}
$version=& $smartctl --version
if($LASTEXITCODE -ne 0 -or -not ($version -match 'smartctl 7\.5')){throw 'smartctl embutido não executou.'}
Assert-Shortcut (Join-Path $desktop 'SLT Disk Manager - Cliente.lnk') (Join-Path $client 'Cliente.cmd') ''
Assert-Shortcut (Join-Path $group 'SLT Disk Manager - Cliente.lnk') (Join-Path $client 'Cliente.cmd') ''
Assert-Shortcut (Join-Path $group 'Reiniciar em modo SLT Disk Manager.lnk') (Join-Path $client 'Modo-SLT.cmd') ''
# The installed service script, called by the setup from a 32-bit process, must still choose "Program Files".
$wow=Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
if(Test-Path -LiteralPath $wow){
 $plan=(& $wow -NoProfile -ExecutionPolicy Bypass -File (Join-Path $client 'install-agent/Install-Agent.ps1') -PackageDirectory (Join-Path $client 'client') -ValidateOnly | Select-Object -Last 1) | ConvertFrom-Json
 if($plan.installDirectory -match '\(x86\)' -or -not $plan.installDirectory.StartsWith($env:ProgramW6432,[StringComparison]::OrdinalIgnoreCase) -or -not $plan.discovery){throw "Serviço do cliente iria para $($plan.installDirectory)."}
}

$server=Install-Fixture $ServerInstaller 'servidor'
if(Test-Path -LiteralPath (Join-Path $server 'client')){throw 'Instalador do servidor trouxe o cliente.'}
foreach($present in @('server/SLT.Controller.Host.exe','network-boot/SLT.NetworkBoot.Service.exe','desktop/SLT.Desktop.exe','server/tools/7-Zip/7z.exe','server/tools/7-Zip/License.txt','windows-pe/slt-winpe-agent.exe','windows-pe/New-WinPEKit.ps1','install-server/Install-Server.ps1','install-server/Update-Server.ps1','install-server/Set-ServerRepository.ps1','install-server/ServerSetup.Common.ps1','install-server/Configure-NetworkBootFirewall.ps1','Executar-Instalacao.ps1')){
 if(!(Test-Path -LiteralPath (Join-Path $server $present))){throw "Servidor instalado sem $present."}
}
& (Join-Path $server 'server/tools/7-Zip/7z.exe') i | Out-Null
if($LASTEXITCODE -ne 0){throw '7-Zip embutido não executou.'}
# Without the service: portable shortcut, no elevation, and no panel shortcut that would find no service.
Assert-Shortcut (Join-Path $desktop 'SLT Disk Manager - Servidor e painel.lnk') (Join-Path $server 'Servidor.cmd') ''
Assert-Shortcut (Join-Path $group 'Servidor e painel.lnk') (Join-Path $server 'Servidor.cmd') ''
Assert-NoShortcut (Join-Path $desktop 'SLT Disk Manager - Painel.lnk')
Assert-NoShortcut (Join-Path $group 'SLT Disk Manager - Painel.lnk')
if(Test-Path -LiteralPath $wow){
 $plan=(& $wow -NoProfile -ExecutionPolicy Bypass -File (Join-Path $server 'install-server/Install-Server.ps1') -PackageDirectory (Join-Path $server 'server') -Bind 192.168.77.10 -AllowedSubnet 192.168.77.0/24 -RepositoryDirectory (Join-Path $fixture 'Repositorio SLT') -ValidateOnly | Select-Object -Last 1) | ConvertFrom-Json
 if($plan.installDirectory -ne (Join-Path $env:ProgramW6432 'SLT Disk Manager Controller') -or -not $plan.imagePath.Contains('--repository "'+(Join-Path $fixture 'Repositorio SLT')+'"')){throw "Serviço do servidor iria para $($plan.installDirectory) com [$($plan.imagePath)]."}
 if(Test-Path -LiteralPath (Join-Path $fixture 'Repositorio SLT')){throw 'Modo de validação criou a pasta do repositório.'}
}
Uninstall-Fixture $client 'cliente' 'state/client' 'client/SLT.WindowsAgent.exe'
Uninstall-Fixture $server 'servidor' 'state/server' 'server/SLT.Controller.Host.exe'
foreach($gone in @((Join-Path $desktop 'SLT Disk Manager - Cliente.lnk'),(Join-Path $desktop 'SLT Disk Manager - Servidor e painel.lnk'),(Join-Path $group 'Servidor e painel.lnk'))){Assert-NoShortcut $gone}

# With the Windows service (simulated by the setup's test parameter: no service is created or read):
# the panel shortcut opens the service's connection.json and asks for administrator permission.
$serviceState=Join-Path $fixture 'estado do servico'
$server=Install-Fixture $ServerInstaller 'servidor-servico' @(('/SLTTESTSERVICESTATE="'+$serviceState+'"'))
$panel=Join-Path $server 'desktop\SLT.Desktop.exe'
$connection='--connection "'+(Join-Path $serviceState 'connection.json')+'"'
Assert-Shortcut (Join-Path $desktop 'SLT Disk Manager - Painel.lnk') $panel $connection -Elevated
Assert-Shortcut (Join-Path $group 'SLT Disk Manager - Painel.lnk') $panel $connection -Elevated
Assert-NoShortcut (Join-Path $desktop 'SLT Disk Manager - Servidor e painel.lnk')
Assert-NoShortcut (Join-Path $group 'Servidor e painel.lnk')
if(Test-Path -LiteralPath $serviceState){throw 'O instalador criou ou tocou a pasta de estado do serviço.'}
Uninstall-Fixture $server 'servidor-servico' 'state/server' 'server/SLT.Controller.Host.exe'
Assert-NoShortcut (Join-Path $desktop 'SLT Disk Manager - Painel.lnk')
Write-Output 'PASS installers: separate per-user server and client, desktop and Start menu shortcuts with expected targets (panel shortcut elevated when the service is used), 64-bit service paths, bundled 7-Zip and smartctl run, uninstall preserves generated operating state.'
Write-Output "Evidência sintética preservada: $fixture"
