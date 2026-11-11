[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Bind,
    [Parameter(Mandatory)][string]$AllowedSubnet,
    [string]$InstallDirectory,
    [string]$StateDirectory,
    # Images, imported media, clone batches and boot profiles. Default: <state>\repository.
    [string]$RepositoryDirectory,
    [ValidateRange(1024,65535)][int]$Port=7443,
    [ValidateRange(1024,65535)][int]$DiscoveryPort=7444,
    # Checks everything and prints the planned service command line; creates nothing. Needs no elevation.
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServerSetup.Common.ps1')
if(Invoke-Native64Bit $PSCommandPath $PSBoundParameters){exit $script:Native64ExitCode}
if(-not $ValidateOnly -and -not (Test-SltAdministrator)){throw 'Execute como Administrador.'}

$programFiles=Get-NativeProgramFiles
if(-not $InstallDirectory){$InstallDirectory=Join-Path $programFiles 'SLT Disk Manager Controller'}
if(-not $StateDirectory){$StateDirectory=Join-Path $env:ProgramData 'SLT Disk Manager\Controller'}
if(-not $ValidateOnly -and (Get-Service $script:SltServiceName -ErrorAction SilentlyContinue)){throw 'Serviço já instalado. Para atualizar os programas use Update-Server.ps1; para mudar a pasta do repositório use Set-ServerRepository.ps1.'}
$ip=$null
if(-not [System.Net.IPAddress]::TryParse($Bind,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork'){throw 'IPv4 inválido.'}
if($AllowedSubnet -notmatch '^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$'){throw 'CIDR inválido.'}
if($DiscoveryPort -eq $Port){throw 'A porta da busca automática deve ser diferente da porta HTTPS.'}
$InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory)
$StateDirectory=[IO.Path]::GetFullPath($StateDirectory)
$programRoot=$programFiles.TrimEnd('\')+'\'
if(-not $InstallDirectory.StartsWith($programRoot,[StringComparison]::OrdinalIgnoreCase) -or $InstallDirectory.TrimEnd('\') -eq $programRoot.TrimEnd('\')){throw "Instale o serviço em uma subpasta dedicada de $programFiles."}
$dataRoot=[IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')+'\'
if(-not $StateDirectory.StartsWith($dataRoot,[StringComparison]::OrdinalIgnoreCase) -or $StateDirectory.TrimEnd('\') -eq $dataRoot.TrimEnd('\')){throw 'Estado do serviço deve ficar em uma subpasta dedicada de ProgramData.'}
if((Test-Path -LiteralPath $InstallDirectory) -and (Get-ChildItem -LiteralPath $InstallDirectory -Force | Select-Object -First 1)){throw 'Pasta de programa não está vazia. Use Update-Server.ps1 para atualizar uma instalação existente.'}
$source=Join-Path $PackageDirectory 'SLT.Controller.Host.exe'
if(-not(Test-Path -LiteralPath $source)){throw 'PackageDirectory deve apontar à pasta server publicada.'}
$packageRoot=Split-Path -Parent ([IO.Path]::GetFullPath($PackageDirectory))
$bootSource=Join-Path $packageRoot 'network-boot'
if(-not(Test-Path -LiteralPath (Join-Path $bootSource 'SLT.NetworkBoot.Service.exe'))){throw 'Pacote incompleto: network-boot ausente.'}
foreach($path in @($InstallDirectory,$StateDirectory)){
    if($path.Contains('"')){throw 'Caminho inválido.'}
    Assert-NoReparseChain $path
}
$repository=$null
if($RepositoryDirectory){$repository=Resolve-RepositoryDirectory $RepositoryDirectory @($InstallDirectory,$packageRoot)}

$exe=Join-Path $InstallDirectory 'SLT.Controller.Host.exe'
$bootExe=Join-Path $InstallDirectory 'network-boot\SLT.NetworkBoot.Service.exe'
$tokens=New-Object Collections.Generic.List[string]
foreach($value in @($exe,'--bind',$Bind,'--allowed-subnet',$AllowedSubnet,'--port',[string]$Port,'--discovery-port',[string]$DiscoveryPort,'--state-dir',$StateDirectory,'--network-boot-executable',$bootExe)){$tokens.Add($value)}
if($repository){$tokens.Add('--repository');$tokens.Add($repository)}
$binary=Join-ServiceCommandLine $tokens
if($ValidateOnly){
    [pscustomobject]@{installDirectory=$InstallDirectory;stateDirectory=$StateDirectory;repositoryDirectory=$repository;imagePath=$binary;is64BitProcess=[Environment]::Is64BitProcess} | ConvertTo-Json -Compress
    return
}

foreach($path in @($InstallDirectory,$StateDirectory)){New-ProtectedDirectory $path}
if($repository){New-ProtectedDirectory $repository}
Copy-Item -Path (Join-Path $PackageDirectory '*') -Destination $InstallDirectory -Recurse -Force
Copy-Item -LiteralPath $bootSource -Destination (Join-Path $InstallDirectory 'network-boot') -Recurse
New-Service -Name $script:SltServiceName -DisplayName 'SLT Disk Manager Controller' -BinaryPathName $binary -StartupType Automatic | Out-Null
New-NetFirewallRule -Name SLTDiskManagerControllerLAN -DisplayName 'SLT Disk Manager LAN privada' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -LocalAddress $Bind -RemoteAddress $AllowedSubnet -Profile Private -Program $exe | Out-Null
# Discovery only answers "there is an SLT server at this address"; it carries no credential.
New-NetFirewallRule -Name SLTDiskManagerDiscovery -DisplayName 'SLT Disk Manager busca do servidor na rede privada' -Direction Inbound -Action Allow -Protocol UDP -LocalPort $DiscoveryPort -RemoteAddress $AllowedSubnet -Profile Private -Program $exe | Out-Null
Start-Service $script:SltServiceName
Write-Host "Serviço iniciado em $InstallDirectory."
if($repository){Write-Host "Repositório (imagens e mídias): $repository"}
Write-Host "Abra o atalho 'SLT Disk Manager - Painel' (ele pede permissão de administrador)."
