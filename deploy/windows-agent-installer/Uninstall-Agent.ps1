#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name 'SLTWindowsAgent' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name 'SLTWindowsAgent' }
    & sc.exe delete SLTWindowsAgent | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows nao conseguiu remover o servico.' }
}
Write-Host 'Servico removido. Credenciais/estado foram preservados. Revogue a maquina no servidor antes de descartar credenciais.'
Write-Host 'Binarios e dados nao foram apagados automaticamente.'
