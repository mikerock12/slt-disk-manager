[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    # Optional: without an address the client looks for the server on the local network and
    # shows a pairing code of 8 digits to be typed in the server panel.
    [string]$Server,
    [ValidatePattern('^([A-Fa-f0-9]{64})?$')][string]$ServerPin,
    [string]$InstallDirectory,
    [string]$StateDirectory = (Join-Path $env:ProgramData 'SLT\WindowsAgent'),
    [string]$SmartctlPath,
    [string]$FileRootsPath,
    [switch]$SkipStart,
    # Checks the parameters and prints the planned folders; installs nothing. Needs no elevation.
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The setup program is 32-bit; a 32-bit PowerShell reports "Program Files (x86)" as Program Files.
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    $native = Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Value -is [switch]) { if ($entry.Value.IsPresent) { $forward += "-$($entry.Key)" } }
        else { $forward += @("-$($entry.Key)", [string]$entry.Value) }
    }
    & $native @forward
    exit $LASTEXITCODE
}
if (-not $InstallDirectory) {
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $InstallDirectory = Join-Path $programFiles 'SLT Disk Manager\WindowsAgent'
}
if ($ServerPin -and -not $Server) { throw 'ServerPin exige Server.' }
if ($ValidateOnly) {
    [pscustomobject]@{ installDirectory = [IO.Path]::GetFullPath($InstallDirectory); stateDirectory = [IO.Path]::GetFullPath($StateDirectory); server = $Server; discovery = (-not $Server); is64BitProcess = [Environment]::Is64BitProcess } | ConvertTo-Json -Compress
    return
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Execute como Administrador.' }

function Assert-SafePath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Contains('"') -or $Value.Contains("`r") -or $Value.Contains("`n")) {
        throw 'Caminho invalido para instalacao de servico.'
    }
    $absolute = [IO.Path]::GetFullPath($Value)
    if ($absolute.TrimEnd('\') -eq [IO.Path]::GetPathRoot($absolute).TrimEnd('\')) { throw 'Nao use a raiz de um disco.' }
    return $absolute.TrimEnd('\')
}

function Assert-NoReparseAncestors([string]$Value) {
    $candidate = [IO.Path]::GetFullPath($Value)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            if ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Caminho de estado/configuracao nao pode atravessar links ou junctions.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($candidate)
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
}

if (Get-Service -Name 'SLTWindowsAgent' -ErrorAction SilentlyContinue) {
    throw 'SLTWindowsAgent ja existe. Para atualizar os programas use Update-Agent.ps1; o pareamento e preservado.'
}
$source = (Resolve-Path -LiteralPath (Assert-SafePath $PackageDirectory)).Path
$installPath = Assert-SafePath $InstallDirectory
$statePath = Assert-SafePath $StateDirectory
$agentName = 'SLT.WindowsAgent.exe'
if (-not (Test-Path -LiteralPath (Join-Path $source $agentName) -PathType Leaf)) { throw 'Pacote publicado nao contem SLT.WindowsAgent.exe.' }
if (-not (Test-Path -LiteralPath (Join-Path $source 'Hardware\Collect-Inventory.ps1') -PathType Leaf)) { throw 'Pacote publicado sem coletor de hardware.' }
if ($statePath.Equals($installPath, [StringComparison]::OrdinalIgnoreCase) -or
    $statePath.StartsWith($installPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $installPath.StartsWith($statePath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use diretorio de estado separado dos binarios.' }
$scopeJson = $null
if ($FileRootsPath) {
    $scopeSource = (Resolve-Path -LiteralPath (Assert-SafePath $FileRootsPath)).Path
    Assert-NoReparseAncestors $scopeSource
    $scopeInfo = Get-Item -LiteralPath $scopeSource
    if ($scopeInfo.PSIsContainer -or $scopeInfo.Length -gt 65536) { throw 'Escopos exigem arquivo JSON de ate 64 KiB.' }
    $scopeJson = [IO.File]::ReadAllText($scopeSource)
    $scopeDefinitions = @($scopeJson | ConvertFrom-Json)
    if (-not $scopeJson.TrimStart().StartsWith('[') -or $scopeDefinitions.Count -gt 32) { throw 'Escopos exigem array JSON com ate 32 entradas.' }
    # O agente valida todos os caminhos, permissoes, conflitos e IDs antes de parear.
}
if ($SmartctlPath) {
    $SmartctlPath = (Resolve-Path -LiteralPath (Assert-SafePath $SmartctlPath)).Path
    if (-not (Test-Path -LiteralPath $SmartctlPath -PathType Leaf)) { throw 'smartctl.exe nao encontrado.' }
}
if ($Server) {
    $uri = [Uri]$Server
    $address = $null
    if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or
        -not [Net.IPAddress]::TryParse($uri.Host, [ref]$address)) { throw 'Server exige HTTPS com IP literal da LAN.' }
}
# O executavel tambem valida LAN, pin, certificados e caminhos antes de parear.

New-Item -ItemType Directory -Path $installPath -Force | Out-Null
if ((Get-Item -LiteralPath $installPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Diretorio de instalacao nao pode ser link.' }
if (-not $source.Equals($installPath, [StringComparison]::OrdinalIgnoreCase)) {
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $installPath -Recurse -Force }
}
$installAcl = New-Object Security.AccessControl.DirectorySecurity
$installAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $principal = New-Object Security.Principal.SecurityIdentifier($sid)
    $installAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($principal, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -LiteralPath $installPath -AclObject $installAcl
Assert-NoReparseAncestors $statePath
New-Item -ItemType Directory -Path $statePath -Force | Out-Null
Assert-NoReparseAncestors $statePath
$stateAcl = New-Object Security.AccessControl.DirectorySecurity
$stateAcl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $principal = New-Object Security.Principal.SecurityIdentifier($sid)
    $stateAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($principal, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -LiteralPath $statePath -AclObject $stateAcl
$agent = Join-Path $installPath $agentName
# Address and pin are remembered in the private state after pairing; the service needs only the state folder.
$agentArguments = @('--state-dir', $statePath)
$pairingArguments = @()
if ($Server) { $pairingArguments += @('--server', $Server) }
if ($ServerPin) { $pairingArguments += @('--server-pin', $ServerPin) }
if ($SmartctlPath) { $agentArguments += @('--smartctl', $SmartctlPath) }
if ($null -ne $scopeJson) {
    $privateScopes = Join-Path $statePath 'file-roots.json'
    Assert-NoReparseAncestors $privateScopes
    $temporaryScopes = Join-Path $statePath ('scopes-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporaryScopes, $scopeJson, (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporaryScopes -Destination $privateScopes -Force
    } finally {
        if (Test-Path -LiteralPath $temporaryScopes) { Remove-Item -LiteralPath $temporaryScopes -Force }
    }
    $agentArguments += @('--file-roots', $privateScopes)
}

Write-Host 'Conectando ao servidor. O codigo de 8 digitos aparece abaixo: digite-o no painel do servidor (Conectar computador).'
& $agent @agentArguments @pairingArguments --once
if ($LASTEXITCODE -ne 0) { throw 'Pareamento/inventario inicial falhou. Nenhum servico foi criado.' }

$commandParts = @('"' + $agent + '"')
foreach ($argument in $agentArguments) {
    if ($argument.Contains('"') -or $argument.Contains("`r") -or $argument.Contains("`n")) { throw 'Argumento invalido para servico.' }
    $commandParts += '"' + $argument + '"'
}
New-Service -Name 'SLTWindowsAgent' -DisplayName 'SLT Disk Manager Windows Agent' -BinaryPathName ($commandParts -join ' ') -StartupType Automatic |
    Out-Null
& sc.exe failure SLTWindowsAgent reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Servico criado, mas a politica de recuperacao nao foi configurada.' }
if (-not $SkipStart) { Start-Service -Name 'SLTWindowsAgent' }
Write-Host ('Agente instalado. Estado privado em: ' + $statePath)
