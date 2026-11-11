# Shared by Install-Server.ps1, Update-Server.ps1 and Set-ServerRepository.ps1 (dot-sourced).
# Pure helpers: nothing here creates a service, a firewall rule or touches a disk by itself.
Set-StrictMode -Version Latest

$script:SltServiceName = 'SLTDiskManagerController'
$script:SltSids = @('S-1-5-18', 'S-1-5-32-544') # SYSTEM and Administrators only

# The setup program is 32-bit. Started from it, Windows PowerShell is 32-bit too and reports
# "Program Files (x86)" as Program Files. Re-run the same script in the 64-bit PowerShell.
function Invoke-Native64Bit([string]$ScriptPath, [hashtable]$BoundParameters) {
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::Is64BitProcess) { return $false }
    $native = Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $native)) { throw 'PowerShell de 64 bits não encontrado; execute este script em um PowerShell de 64 bits.' }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath)
    foreach ($entry in $BoundParameters.GetEnumerator()) {
        if ($entry.Value -is [switch]) { if ($entry.Value.IsPresent) { $arguments += "-$($entry.Key)" } }
        else { $arguments += @("-$($entry.Key)", [string]$entry.Value) }
    }
    # Inside a function the child's output would become the return value: send it to the console instead.
    & $native @arguments | ForEach-Object { [Console]::Out.WriteLine("$_") }
    $script:Native64ExitCode = $LASTEXITCODE
    return $true
}

function Test-SltAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-NativeProgramFiles {
    # ProgramW6432 is the real "Program Files" even inside a 32-bit process.
    if ($env:ProgramW6432) { return [IO.Path]::GetFullPath($env:ProgramW6432) }
    return [IO.Path]::GetFullPath($env:ProgramFiles)
}

function Assert-NoReparseChain([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Link ou junção recusado no caminho: $candidate"
        }
        $parent = [IO.Path]::GetDirectoryName($candidate)
        if (-not $parent -or $parent -eq $candidate) { break }
        $candidate = $parent
    }
}

# Repository = images, imported ISO/WIM/ESD, clone batches and boot profiles. It needs space,
# so it may live on another local disk; it must never sit inside the program folder.
function Resolve-RepositoryDirectory([string]$Path, [string[]]$ProgramDirectories) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Informe a pasta do repositório.' }
    if ($Path.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) { throw 'A pasta do repositório não pode conter aspas nem quebras de linha.' }
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path -notmatch '^[A-Za-z]:\\') { throw 'Informe um caminho absoluto em disco local, por exemplo D:\SLT Disk Manager Repositorio.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [IO.Path]::GetPathRoot($full)
    if ($full.Length -le $root.TrimEnd('\').Length) { throw 'Não use a raiz da unidade como repositório; crie uma pasta dedicada.' }
    $drive = New-Object IO.DriveInfo($root)
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { throw "A unidade $root não é um disco local fixo (rede, removível ou inexistente)." }
    foreach ($program in $ProgramDirectories) {
        if (-not $program) { continue }
        $base = [IO.Path]::GetFullPath($program).TrimEnd('\')
        if ($full.Equals($base, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "O repositório não pode ficar dentro da pasta do programa ($base)."
        }
    }
    Assert-NoReparseChain $full
    return $full
}

function New-ProtectedDirectory([string]$Path) {
    Assert-NoReparseChain $Path
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Assert-NoReparseChain $Path
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in $script:SltSids) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# Service command line -> ordered list of tokens (quotes removed).
function Split-ServiceCommandLine([string]$ImagePath) {
    $tokens = New-Object Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($ImagePath, '"([^"]*)"|(\S+)')) {
        $tokens.Add($(if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }))
    }
    if ($tokens.Count -lt 1) { throw 'Linha de comando do serviço vazia.' }
    return ,$tokens
}

function Get-ServiceOption([Collections.Generic.List[string]]$Tokens, [string]$Name) {
    for ($i = 1; $i -lt $Tokens.Count - 1; $i++) { if ($Tokens[$i] -eq $Name) { return $Tokens[$i + 1] } }
    return $null
}

function Set-ServiceOption([Collections.Generic.List[string]]$Tokens, [string]$Name, [string]$Value) {
    for ($i = 1; $i -lt $Tokens.Count - 1; $i++) { if ($Tokens[$i] -eq $Name) { $Tokens[$i + 1] = $Value; return } }
    $Tokens.Add($Name); $Tokens.Add($Value)
}

function Join-ServiceCommandLine([Collections.Generic.List[string]]$Tokens) {
    $parts = foreach ($token in $Tokens) {
        if ($token.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) { throw 'Argumento inválido para o serviço.' }
        if ($token -match '[\s]' -or $token -eq '') { '"' + $token + '"' } else { $token }
    }
    # The executable is always quoted: an unquoted path with spaces is a classic service weakness.
    $parts = @($parts)
    if (-not $parts[0].StartsWith('"')) { $parts[0] = '"' + $parts[0] + '"' }
    return ($parts -join ' ')
}

function Get-SltServiceImagePath([string]$ServiceName) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    return (Get-ItemProperty -LiteralPath $key -Name ImagePath).ImagePath
}

# Written straight to the registry: passing a command line with inner quotes through sc.exe from
# Windows PowerShell 5.1 loses the quotes. The Service Control Manager reads it at the next start.
function Set-SltServiceImagePath([string]$ServiceName, [string]$ImagePath) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    if (-not (Test-Path -LiteralPath $key)) { throw "Serviço $ServiceName não está instalado." }
    Set-ItemProperty -LiteralPath $key -Name ImagePath -Value $ImagePath -Type ExpandString
    if ((Get-ItemProperty -LiteralPath $key -Name ImagePath).ImagePath -ne $ImagePath) { throw 'A linha de comando do serviço não foi gravada.' }
}

function Get-TreeManifest([string]$Root) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $manifest = @{}
    if (-not (Test-Path -LiteralPath $base)) { return $manifest }
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File -Force) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link recusado no repositório: $($file.FullName)" }
        $manifest[$file.FullName.Substring($base.Length + 1)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $manifest
}

# Copies a tree, re-reads every copied file and only then removes the source.
function Move-VerifiedTree([string]$Source, [string]$Destination, [switch]$KeepSource) {
    $sourceFull = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    $destinationFull = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $sourceFull)) { return 0 }
    if ($destinationFull.Equals($sourceFull, [StringComparison]::OrdinalIgnoreCase) -or $destinationFull.StartsWith($sourceFull + '\', [StringComparison]::OrdinalIgnoreCase) -or $sourceFull.StartsWith($destinationFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Origem e destino da migração se sobrepõem.'
    }
    Assert-NoReparseChain $sourceFull
    $expected = Get-TreeManifest $sourceFull
    $needed = (Get-ChildItem -LiteralPath $sourceFull -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
    $free = (New-Object IO.DriveInfo([IO.Path]::GetPathRoot($destinationFull))).AvailableFreeSpace
    if ($needed -and $free -lt ($needed + 64MB)) { throw "Espaço insuficiente no destino: precisa de $([math]::Ceiling($needed / 1MB)) MiB livres." }
    New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null
    foreach ($directory in Get-ChildItem -LiteralPath $sourceFull -Recurse -Directory -Force) {
        New-Item -ItemType Directory -Path (Join-Path $destinationFull $directory.FullName.Substring($sourceFull.Length + 1)) -Force | Out-Null
    }
    foreach ($relative in $expected.Keys) {
        $target = Join-Path $destinationFull $relative
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected[$relative]) { throw "Já existe arquivo diferente no destino: $target" }
            continue
        }
        Copy-Item -LiteralPath (Join-Path $sourceFull $relative) -Destination $target
    }
    $copied = Get-TreeManifest $destinationFull
    foreach ($relative in $expected.Keys) {
        if (-not $copied.ContainsKey($relative) -or $copied[$relative] -ne $expected[$relative]) { throw "Conferência SHA256 falhou em $relative. A origem foi preservada." }
    }
    if (-not $KeepSource) { Remove-Item -LiteralPath $sourceFull -Recurse -Force }
    return $expected.Count
}
