#Requires -RunAsAdministrator
$ErrorActionPreference='Stop'
$service=Get-Service SLTDiskManagerController -ErrorAction SilentlyContinue
if($service){Stop-Service SLTDiskManagerController -Force; & sc.exe delete SLTDiskManagerController; if($LASTEXITCODE -ne 0){throw 'Falha ao excluir serviço.'}}
foreach($name in @('SLTDiskManagerControllerLAN','SLTDiskManagerDiscovery')){Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule}
foreach($name in @('SLTDiskManagerBootHTTP','SLTDiskManagerBootTFTP')){Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule}
Write-Host 'Serviço e regras de firewall removidos. Programas, estado e repositório foram preservados. Revogue agentes antes de descartar o servidor.'
