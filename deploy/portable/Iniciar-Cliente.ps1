[CmdletBinding()]
param([string]$Server, [string]$ServerPin, [string]$FileRootsPath)
$ErrorActionPreference='Stop'
if(-not $Server){$Server=Read-Host 'URL HTTPS do servidor (ex.: https://192.168.1.10:7443)'}
if(-not $ServerPin){$ServerPin=Read-Host 'Pin SHA256 exibido no servidor (64 caracteres)'}
$state=Join-Path $PSScriptRoot 'state/client'
$clientArgs=@('--server',$Server,'--server-pin',$ServerPin,'--state-dir',$state)
if(-not $FileRootsPath){$FileRootsPath=Read-Host 'Caminho do JSON de pastas autorizadas (Enter para somente inventário)'}
if($FileRootsPath){$clientArgs+=@('--file-roots',(Resolve-Path -LiteralPath $FileRootsPath).Path)}
& (Join-Path $PSScriptRoot 'client/SLT.WindowsAgent.exe') @clientArgs
if($LASTEXITCODE -ne 0){throw 'Agente encerrou com erro. Confira a mensagem acima.'}
