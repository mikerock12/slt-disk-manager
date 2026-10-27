#Requires -RunAsAdministrator
$ErrorActionPreference='Stop'
$service=Get-Service SLTDiskManagerController -ErrorAction SilentlyContinue
if($service){Stop-Service SLTDiskManagerController -Force; & sc.exe delete SLTDiskManagerController; if($LASTEXITCODE -ne 0){throw 'Falha ao excluir serviço.'}}
Get-NetFirewallRule -Name SLTDiskManagerControllerLAN -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host 'Serviço e regra removidos. Programas e dados foram preservados. Revogue agentes antes de descartar o servidor.'
