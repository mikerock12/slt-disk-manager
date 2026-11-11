[CmdletBinding()]
param(
    # New folder for images, imported media, clone batches and boot profiles (needs free space).
    [Parameter(Mandatory)][string]$RepositoryDirectory,
    # Also move what is already in the current repository (checked by SHA256 before the source is removed).
    [switch]$MoveExisting,
    # Prints what would change; stops and changes nothing. Needs no elevation.
    [switch]$ValidateOnly,
    # For tests only: analyse this command line instead of the installed service.
    [string]$TestImagePath
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServerSetup.Common.ps1')
if(Invoke-Native64Bit $PSCommandPath $PSBoundParameters){exit $script:Native64ExitCode}
if(-not $TestImagePath -and -not $ValidateOnly -and -not (Test-SltAdministrator)){throw 'Execute como Administrador.'}

# Points the installed server to another repository folder. Certificates, pin, token and
# paired computers live in the state folder and are not touched.
$imagePath=if($TestImagePath){$TestImagePath}else{Get-SltServiceImagePath $script:SltServiceName}
if(-not $imagePath){throw 'O serviço SLTDiskManagerController não está instalado.'}
$tokens=Split-ServiceCommandLine $imagePath
$installDirectory=Split-Path -Parent $tokens[0]
$stateDirectory=Get-ServiceOption $tokens '--state-dir'
if(-not $stateDirectory){throw 'Linha de comando do serviço sem --state-dir.'}
$current=Get-ServiceOption $tokens '--repository'
if(-not $current){$current=Join-Path $stateDirectory 'repository'}
$repository=Resolve-RepositoryDirectory $RepositoryDirectory @($installDirectory)
$stateFull=[IO.Path]::GetFullPath($stateDirectory).TrimEnd('\')
if($repository.Equals($stateFull,[StringComparison]::OrdinalIgnoreCase)){throw 'Use uma pasta separada da pasta de estado do servidor.'}
Set-ServiceOption $tokens '--repository' $repository
$newImagePath=Join-ServiceCommandLine $tokens
# Boot profiles (hundreds of MiB) follow the repository; older servers kept them in the state folder.
$moves=@(
    @{from=[IO.Path]::GetFullPath($current).TrimEnd('\');to=$repository},
    @{from=(Join-Path $stateFull 'network-boot');to=(Join-Path $repository 'network-boot')}
) | Where-Object { -not $_.from.Equals($_.to,[StringComparison]::OrdinalIgnoreCase) -and -not $_.to.StartsWith($_.from+'\',[StringComparison]::OrdinalIgnoreCase) -and -not $_.from.StartsWith($repository+'\',[StringComparison]::OrdinalIgnoreCase) }
if($ValidateOnly){
    [pscustomobject]@{currentRepository=$current;repositoryDirectory=$repository;imagePath=$newImagePath;moveExisting=[bool]$MoveExisting;wouldMove=@($moves | ForEach-Object { $_.from })} | ConvertTo-Json -Compress
    return
}

$service=$null
if(-not $TestImagePath){
    $service=Get-Service $script:SltServiceName
    if($service.Status -ne 'Stopped'){Stop-Service $script:SltServiceName -Force; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))}
}
try {
    New-ProtectedDirectory $repository
    $moved=0
    if($MoveExisting){foreach($move in $moves){$moved+=Move-VerifiedTree $move.from $move.to}}
    if(-not $TestImagePath){Set-SltServiceImagePath $script:SltServiceName $newImagePath}
} finally {
    if($service){Start-Service $script:SltServiceName -ErrorAction SilentlyContinue}
}
Write-Host "Repositório do servidor: $repository"
if($MoveExisting){Write-Host "$moved arquivo(s) movidos e conferidos por SHA256."}
else{Write-Warning "O conteúdo anterior continua em $current. Imagens, mídias e perfis de boot já cadastrados ficam indisponíveis até serem levados para a pasta nova: execute de novo com -MoveExisting."}
Write-Host 'Certificados, pin, token e computadores conectados não foram alterados.'
if($TestImagePath){$newImagePath}
