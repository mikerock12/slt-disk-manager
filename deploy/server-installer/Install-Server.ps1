#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Bind,
    [Parameter(Mandatory)][string]$AllowedSubnet,
    [string]$InstallDirectory="$env:ProgramFiles\SLT Disk Manager Controller",
    [string]$StateDirectory="$env:ProgramData\SLT Disk Manager\Controller",
    [ValidateRange(1024,65535)][int]$Port=7443
)
$ErrorActionPreference='Stop'
if(Get-Service SLTDiskManagerController -ErrorAction SilentlyContinue){throw 'Serviço já instalado. Pare-o e preserve backup antes de atualizar.'}
$ip=$null
if(-not [System.Net.IPAddress]::TryParse($Bind,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork'){throw 'IPv4 inválido.'}
if($AllowedSubnet -notmatch '^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$'){throw 'CIDR inválido.'}
$InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory)
$StateDirectory=[IO.Path]::GetFullPath($StateDirectory)
$programRoot=[IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')+'\'
if(-not $InstallDirectory.StartsWith($programRoot,[StringComparison]::OrdinalIgnoreCase) -or $InstallDirectory.TrimEnd('\') -eq $programRoot.TrimEnd('\')){throw 'Instale o serviço em uma subpasta dedicada de Program Files.'}
$dataRoot=[IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')+'\'
if(-not $StateDirectory.StartsWith($dataRoot,[StringComparison]::OrdinalIgnoreCase) -or $StateDirectory.TrimEnd('\') -eq $dataRoot.TrimEnd('\')){throw 'Estado do serviço deve ficar em uma subpasta dedicada de ProgramData.'}
if((Test-Path -LiteralPath $InstallDirectory) -and (Get-ChildItem -LiteralPath $InstallDirectory -Force | Select-Object -First 1)){throw 'Pasta de programa não está vazia. Preserve a instalação anterior e escolha outra pasta dedicada.'}
$source=Join-Path $PackageDirectory 'SLT.Controller.Host.exe'
if(-not(Test-Path -LiteralPath $source)){throw 'PackageDirectory deve apontar à pasta server publicada.'}
foreach($path in @($InstallDirectory,$StateDirectory)){
    if($path.Contains('"')){throw 'Caminho inválido.'}
    $ancestor=$path
    while($ancestor){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Reparse point recusado: $ancestor"}
        $ancestor=Split-Path -Parent $ancestor
    }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){
        $identity=New-Object Security.Principal.SecurityIdentifier($sid)
        $rule=New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}
Copy-Item -Path (Join-Path $PackageDirectory '*') -Destination $InstallDirectory -Recurse -Force
$exe=Join-Path $InstallDirectory 'SLT.Controller.Host.exe'
$binary='"'+$exe+'" --bind '+$Bind+' --allowed-subnet '+$AllowedSubnet+' --port '+$Port+' --state-dir "'+$StateDirectory+'"'
New-Service -Name SLTDiskManagerController -DisplayName 'SLT Disk Manager Controller' -BinaryPathName $binary -StartupType Automatic | Out-Null
New-NetFirewallRule -Name SLTDiskManagerControllerLAN -DisplayName 'SLT Disk Manager LAN privada' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -LocalAddress $Bind -RemoteAddress $AllowedSubnet -Profile Private -Program $exe | Out-Null
Start-Service SLTDiskManagerController
Write-Host "Serviço iniciado. Abra desktop/SLT.Desktop.exe --connection `"$StateDirectory\connection.json`" como Administrador."
