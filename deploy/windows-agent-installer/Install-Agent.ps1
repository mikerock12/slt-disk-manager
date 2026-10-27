#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ServerPin,
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'SLT Disk Manager\WindowsAgent'),
    [string]$StateDirectory = (Join-Path $env:ProgramData 'SLT\WindowsAgent'),
    [string]$SmartctlPath,
    [string]$FileRootsPath,
    [switch]$SkipStart
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
    throw 'SLTWindowsAgent ja existe. Desinstale explicitamente o servico antes de instalar nova versao; o estado de pareamento sera preservado.'
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
$uri = [Uri]$Server
$address = $null
if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or
    -not [Net.IPAddress]::TryParse($uri.Host, [ref]$address)) { throw 'Server exige HTTPS com IP literal da LAN.' }
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
$agentArguments = @('--server', $Server, '--server-pin', $ServerPin, '--state-dir', $statePath)
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

Write-Host 'Iniciando pareamento local. Confira o codigo no servidor e aprove a maquina correta.'
& $agent @agentArguments --once
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
