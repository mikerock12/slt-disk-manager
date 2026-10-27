[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer)
$ErrorActionPreference='Stop'
foreach($hive in @('HKCU:','HKLM:')){
 if(Test-Path "$hive\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A21348C4-4BD9-4533-9F63-B99F3F80D0F1}_is1"){throw 'Há instalação SLT existente; execute o smoke em ambiente descartável para preservar seu registro.'}
}
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture=Join-Path $root ('runtime/installer-smoke-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$setup=(Resolve-Path -LiteralPath $Installer).Path
$target=Join-Path $fixture 'app'
$process=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/SP-',('/DIR="'+$target+'"'),'/TYPE=client',('/LOG="'+(Join-Path $fixture 'setup.log')+'"')) -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0){throw 'Instalação silenciosa do cliente falhou.'}
if(!(Test-Path -LiteralPath (Join-Path $target 'client/SLT.WindowsAgent.exe')) -or (Test-Path -LiteralPath (Join-Path $target 'server'))){throw 'Seleção de componentes incorreta.'}
& (Join-Path $target 'client/SLT.WindowsAgent.exe') --help
if($LASTEXITCODE -ne 0){throw 'Cliente instalado não iniciou.'}
$state=Join-Path $target 'state/client'
New-Item -ItemType Directory -Path $state -Force | Out-Null
$sentinel=Join-Path $state 'synthetic-preserve.txt'
'SYNTHETIC OPERATING STATE' | Set-Content -LiteralPath $sentinel
$uninstaller=Join-Path $target 'unins000.exe'
$process=Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0){throw 'Desinstalação de fixture falhou.'}
if(!(Test-Path -LiteralPath $sentinel) -or (Test-Path -LiteralPath (Join-Path $target 'client/SLT.WindowsAgent.exe'))){throw 'Desinstalação removeu estado gerado ou preservou executável instalado.'}
Write-Output 'PASS installer: per-user client component, executable readiness, uninstall preserves generated operating state.'
Write-Output "Evidência sintética preservada: $fixture"
