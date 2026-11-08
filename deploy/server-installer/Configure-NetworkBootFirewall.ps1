#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Bind,
    [Parameter(Mandatory)][string]$AllowedCidr,
    [Parameter(Mandatory)][string]$InterfaceAlias,
    [ValidateRange(1024,65535)][int]$HttpPort=8088,
    [switch]$Remove
)
$ErrorActionPreference='Stop'
# Explicit elevated operator action only. Never invoked automatically by setup/start.
$names=@('SLTDiskManagerBootHTTP','SLTDiskManagerBootTFTP')
if($Remove){foreach($name in $names){Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule};return}
$Executable=[IO.Path]::GetFullPath($Executable)
if((Split-Path -Leaf $Executable) -ne 'SLT.NetworkBoot.Service.exe' -or -not(Test-Path -LiteralPath $Executable -PathType Leaf)){throw 'Selecione o executável Network Boot instalado.'}
$ancestor=$Executable
while($ancestor){if((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Links/reparse recusados.'};$ancestor=Split-Path -Parent $ancestor}
$adapter=Get-NetAdapter -Name $InterfaceAlias
if($adapter.Virtual -or $adapter.Status -ne 'Up' -or $adapter.PhysicalMediaType -notin @('802.3','Unspecified')){throw 'Selecione a Ethernet física ativa da bancada.'}
if(-not(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 | Where-Object IPAddress -eq $Bind)){throw 'IP não pertence à interface selecionada.'}
if($AllowedCidr -notmatch '^(\d{1,3}(?:\.\d{1,3}){3})/(\d{1,2})$'){throw 'CIDR inválido.'}
$prefix=[int]$Matches[2];$base=[Net.IPAddress]::Parse($Matches[1]).GetAddressBytes();$ip=[Net.IPAddress]::Parse($Bind).GetAddressBytes()
if($base.Length -ne 4 -or $ip.Length -ne 4 -or $prefix -lt 8 -or $prefix -gt 32 -or $HttpPort -eq 7443){throw 'IPv4/CIDR/porta inválidos.'}
function Test-Private([byte[]]$b){return $b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168)}
$last=New-Object byte[] 4
for($i=0;$i -lt 4;$i++){
    $bits=[Math]::Min(8,[Math]::Max(0,$prefix-8*$i));$mask=[byte](256-[Math]::Pow(2,8-$bits))
    if(($base[$i] -band $mask) -ne $base[$i] -or ($ip[$i] -band $mask) -ne $base[$i]){throw 'CIDR não canônico ou IP fora da rede.'}
    $last[$i]=$base[$i] -bor (255-$mask)
}
if(-not(Test-Private $base) -or -not(Test-Private $last)){throw 'CIDR deve permanecer integralmente em RFC1918.'}
if(Get-NetFirewallRule -Name $names -ErrorAction SilentlyContinue){throw 'Regras já existem; remova explicitamente antes de substituir.'}
try {
    New-NetFirewallRule -Name $names[0] -DisplayName 'SLT Network Boot HTTP bancada' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpPort -LocalAddress $Bind -RemoteAddress $AllowedCidr -InterfaceAlias $InterfaceAlias -Profile Private -Program $Executable | Out-Null
    # TFTP uses UDP69 + ephemeral transfer sockets; constrained to this executable/NIC/CIDR.
    New-NetFirewallRule -Name $names[1] -DisplayName 'SLT Network Boot TFTP bancada' -Direction Inbound -Action Allow -Protocol UDP -LocalAddress $Bind -RemoteAddress $AllowedCidr -InterfaceAlias $InterfaceAlias -Profile Private -Program $Executable | Out-Null
} catch {foreach($name in $names){Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule};throw}
Write-Host 'Regras privadas criadas. PXE continua desligado até ação explícita no painel. Use -Remove para reverter.'
