[CmdletBinding()]
param([string]$Server, [string]$ServerPin)
$ErrorActionPreference='Stop'
if(-not $Server){$Server=Read-Host 'URL HTTPS do servidor (ex.: https://192.168.1.10:7443)'}
if(-not $ServerPin){$ServerPin=Read-Host 'Pin SHA256 exibido no servidor (64 caracteres)'}
$state=Join-Path $PSScriptRoot 'state/client'
& (Join-Path $PSScriptRoot 'client/SLT.WindowsAgent.exe') --server $Server --server-pin $ServerPin --state-dir $state
if($LASTEXITCODE -ne 0){throw 'Agente encerrou com erro. Confira a mensagem acima.'}
