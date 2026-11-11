[CmdletBinding()]
param(
    # The "client" folder of the new package (the one holding SLT.WindowsAgent.exe).
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    # Prints what would change; changes nothing. Needs no elevation.
    [switch]$ValidateOnly,
    # For tests only: analyse this command line instead of the installed service.
    [string]$TestImagePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Replaces the programs of the installed client service. The pairing with the server
# (certificate, key and server address in the private state folder) is kept.
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
if ($TestImagePath -and -not $ValidateOnly) { throw '-TestImagePath so e aceito com -ValidateOnly.' }
$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\SLTWindowsAgent'
$imagePath = if ($TestImagePath) { $TestImagePath } elseif (Test-Path -LiteralPath $key) { (Get-ItemProperty -LiteralPath $key -Name ImagePath).ImagePath } else { $null }
if (-not $imagePath) { throw 'O servico SLTWindowsAgent nao esta instalado. Use Install-Agent.ps1.' }
$tokens = New-Object Collections.Generic.List[string]
foreach ($match in [regex]::Matches($imagePath, '"([^"]*)"|(\S+)')) { $tokens.Add($(if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value })) }
$currentExe = $tokens[0]
if ((Split-Path -Leaf $currentExe) -ne 'SLT.WindowsAgent.exe') { throw 'O servico instalado nao aponta para SLT.WindowsAgent.exe.' }
$currentDirectory = Split-Path -Parent $currentExe
$source = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $source 'SLT.WindowsAgent.exe') -PathType Leaf)) { throw 'PackageDirectory deve apontar a pasta client publicada.' }
if (-not (Test-Path -LiteralPath (Join-Path $source 'Hardware\Find-EfiSystemPartition.ps1') -PathType Leaf)) { throw 'Pacote sem os scripts de hardware desta versao.' }

# Earlier setups ran a 32-bit PowerShell and placed the service under "Program Files (x86)".
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$targetDirectory = $currentDirectory
$x86 = ${env:ProgramFiles(x86)}
if ($x86 -and $currentDirectory.StartsWith([IO.Path]::GetFullPath($x86).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    $targetDirectory = Join-Path $programFiles $currentDirectory.Substring([IO.Path]::GetFullPath($x86).TrimEnd('\').Length + 1)
}
$relocate = -not $targetDirectory.Equals($currentDirectory, [StringComparison]::OrdinalIgnoreCase)
$tokens[0] = Join-Path $targetDirectory 'SLT.WindowsAgent.exe'
# The bundled smartctl moves with the programs.
for ($i = 1; $i -lt $tokens.Count; $i++) {
    if ($tokens[$i].StartsWith($currentDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { $tokens[$i] = Join-Path $targetDirectory $tokens[$i].Substring($currentDirectory.Length + 1) }
}
$newImagePath = (@($tokens | ForEach-Object { if ($_.Contains('"')) { throw 'Argumento invalido.' }; '"' + $_ + '"' }) -join ' ')
if ($ValidateOnly) {
    [pscustomobject]@{ currentDirectory = $currentDirectory; targetDirectory = $targetDirectory; relocated = $relocate; imagePath = $newImagePath } | ConvertTo-Json -Compress
    return
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Execute como Administrador.' }

$service = Get-Service -Name 'SLTWindowsAgent'
if ($service.Status -ne 'Stopped') { Stop-Service -Name 'SLTWindowsAgent' -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
$backup = $null
try {
    if (-not $relocate -and (Test-Path -LiteralPath $targetDirectory)) {
        $backup = $targetDirectory + '.anterior-' + (Get-Date -Format 'yyyyMMddHHmmss')
        Rename-Item -LiteralPath $targetDirectory -NewName (Split-Path -Leaf $backup)
    }
    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    if ((Get-Item -LiteralPath $targetDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Diretorio de instalacao nao pode ser link.' }
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $targetDirectory -Recurse -Force }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $principal = New-Object Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($principal, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $targetDirectory -AclObject $acl
    Set-ItemProperty -LiteralPath $key -Name ImagePath -Value $newImagePath -Type ExpandString
    Start-Service -Name 'SLTWindowsAgent'
} catch {
    Write-Warning ('Atualizacao interrompida: ' + $_.Exception.Message)
    if ($backup -and (Test-Path -LiteralPath $backup)) {
        if (Test-Path -LiteralPath $targetDirectory) { Remove-Item -LiteralPath $targetDirectory -Recurse -Force }
        Rename-Item -LiteralPath $backup -NewName (Split-Path -Leaf $targetDirectory)
    }
    Set-ItemProperty -LiteralPath $key -Name ImagePath -Value $imagePath -Type ExpandString
    Start-Service -Name 'SLTWindowsAgent' -ErrorAction SilentlyContinue
    throw
}
if ($backup -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Recurse -Force }
Write-Host ('Cliente atualizado em ' + $targetDirectory + '. O pareamento com o servidor foi preservado.')
if ($relocate) { Write-Host ('A pasta antiga nao e mais usada e pode ser apagada: ' + $currentDirectory) }
