[CmdletBinding()]
param(
    # The "server" folder of the new package (the one holding SLT.Controller.Host.exe).
    [Parameter(Mandatory)][string]$PackageDirectory,
    [ValidateRange(1024,65535)][int]$DiscoveryPort=7444,
    # Prints what would change; stops and changes nothing. Needs no elevation.
    [switch]$ValidateOnly,
    # For tests only: analyse this command line instead of the installed service.
    [string]$TestImagePath
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServerSetup.Common.ps1')
if(Invoke-Native64Bit $PSCommandPath $PSBoundParameters){exit $script:Native64ExitCode}
if($TestImagePath -and -not $ValidateOnly){throw '-TestImagePath só é aceito com -ValidateOnly.'}
if(-not $ValidateOnly -and -not (Test-SltAdministrator)){throw 'Execute como Administrador.'}

# Updates the programs of an installed service. Certificates, pin, token, paired computers,
# images and settings stay where they are: only the program folder is replaced.
$imagePath=if($TestImagePath){$TestImagePath}else{Get-SltServiceImagePath $script:SltServiceName}
if(-not $imagePath){throw 'O serviço SLTDiskManagerController não está instalado. Use Install-Server.ps1.'}
$tokens=Split-ServiceCommandLine $imagePath
$currentExe=$tokens[0]
if((Split-Path -Leaf $currentExe) -ne 'SLT.Controller.Host.exe'){throw 'O serviço instalado não aponta para SLT.Controller.Host.exe.'}
$currentDirectory=Split-Path -Parent $currentExe
$source=Join-Path $PackageDirectory 'SLT.Controller.Host.exe'
if(-not(Test-Path -LiteralPath $source)){throw 'PackageDirectory deve apontar à pasta server publicada.'}
$packageRoot=Split-Path -Parent ([IO.Path]::GetFullPath($PackageDirectory))
$bootSource=Join-Path $packageRoot 'network-boot'
if(-not(Test-Path -LiteralPath (Join-Path $bootSource 'SLT.NetworkBoot.Service.exe'))){throw 'Pacote incompleto: network-boot ausente.'}
$bind=Get-ServiceOption $tokens '--bind'; $subnet=Get-ServiceOption $tokens '--allowed-subnet'; $port=Get-ServiceOption $tokens '--port'
if(-not $bind -or -not $subnet){throw 'Linha de comando do serviço sem --bind/--allowed-subnet; reinstale com Install-Server.ps1.'}
if(-not $port){$port='7443'}

# Earlier setups ran a 32-bit PowerShell and placed the service under "Program Files (x86)".
$programFiles=Get-NativeProgramFiles
$targetDirectory=$currentDirectory
$x86=${env:ProgramFiles(x86)}
if($x86 -and $currentDirectory.StartsWith([IO.Path]::GetFullPath($x86).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){
    $targetDirectory=Join-Path $programFiles (Split-Path -Leaf $currentDirectory)
}
$relocate=-not $targetDirectory.Equals($currentDirectory,[StringComparison]::OrdinalIgnoreCase)
$exe=Join-Path $targetDirectory 'SLT.Controller.Host.exe'
$tokens[0]=$exe
Set-ServiceOption $tokens '--network-boot-executable' (Join-Path $targetDirectory 'network-boot\SLT.NetworkBoot.Service.exe')
if(-not (Get-ServiceOption $tokens '--discovery-port')){Set-ServiceOption $tokens '--discovery-port' ([string]$DiscoveryPort)}
$newImagePath=Join-ServiceCommandLine $tokens
if($ValidateOnly){
    [pscustomobject]@{currentDirectory=$currentDirectory;targetDirectory=$targetDirectory;relocated=$relocate;imagePath=$newImagePath;stateDirectory=(Get-ServiceOption $tokens '--state-dir');repositoryDirectory=(Get-ServiceOption $tokens '--repository')} | ConvertTo-Json -Compress
    return
}

Assert-NoReparseChain $targetDirectory
$service=Get-Service $script:SltServiceName
if($service.Status -ne 'Stopped'){Stop-Service $script:SltServiceName -Force; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))}
$backup=$null
try {
    if(-not $relocate -and (Test-Path -LiteralPath $targetDirectory)){
        # Keep the previous programs until the new ones start.
        $backup=$targetDirectory+'.anterior-'+(Get-Date -Format 'yyyyMMddHHmmss')
        Rename-Item -LiteralPath $targetDirectory -NewName (Split-Path -Leaf $backup)
    }
    if((Test-Path -LiteralPath $targetDirectory) -and (Get-ChildItem -LiteralPath $targetDirectory -Force | Select-Object -First 1)){throw "A pasta de destino não está vazia: $targetDirectory"}
    New-ProtectedDirectory $targetDirectory
    Copy-Item -Path (Join-Path $PackageDirectory '*') -Destination $targetDirectory -Recurse -Force
    Copy-Item -LiteralPath $bootSource -Destination (Join-Path $targetDirectory 'network-boot') -Recurse
    Set-SltServiceImagePath $script:SltServiceName $newImagePath
    foreach($rule in @('SLTDiskManagerControllerLAN','SLTDiskManagerDiscovery')){Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule}
    New-NetFirewallRule -Name SLTDiskManagerControllerLAN -DisplayName 'SLT Disk Manager LAN privada' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -LocalAddress $bind -RemoteAddress $subnet -Profile Private -Program $exe | Out-Null
    New-NetFirewallRule -Name SLTDiskManagerDiscovery -DisplayName 'SLT Disk Manager busca do servidor na rede privada' -Direction Inbound -Action Allow -Protocol UDP -LocalPort (Get-ServiceOption $tokens '--discovery-port') -RemoteAddress $subnet -Profile Private -Program $exe | Out-Null
    Start-Service $script:SltServiceName
    (Get-Service $script:SltServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(60))
} catch {
    Write-Warning "Atualização interrompida: $($_.Exception.Message)"
    if($backup -and (Test-Path -LiteralPath $backup)){
        # Put the previous programs back so the bench keeps working.
        if(Test-Path -LiteralPath $targetDirectory){Remove-Item -LiteralPath $targetDirectory -Recurse -Force}
        Rename-Item -LiteralPath $backup -NewName (Split-Path -Leaf $targetDirectory)
        Set-SltServiceImagePath $script:SltServiceName $imagePath
        Start-Service $script:SltServiceName -ErrorAction SilentlyContinue
        Write-Warning 'Os programas anteriores foram restaurados.'
    } elseif($relocate){
        Set-SltServiceImagePath $script:SltServiceName $imagePath
        Start-Service $script:SltServiceName -ErrorAction SilentlyContinue
        Write-Warning 'O serviço voltou a usar a pasta anterior.'
    }
    throw
}
if($backup -and (Test-Path -LiteralPath $backup)){Remove-Item -LiteralPath $backup -Recurse -Force}
Write-Host "Serviço atualizado em $targetDirectory."
if($relocate){Write-Host "A pasta antiga não é mais usada e pode ser apagada: $currentDirectory"}
Write-Host 'Certificados, pin, token, computadores conectados e imagens não foram alterados.'
