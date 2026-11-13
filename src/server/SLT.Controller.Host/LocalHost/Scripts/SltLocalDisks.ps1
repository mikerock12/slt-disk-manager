[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('inventory','format-disk','convert-table','format-volume','set-offline','partition-sizes','resize-partition','check-disk','repair-volume','mbr2gpt','boot-repair','system-repair')][string]$Operation)
# Typed operations on the server's own disks, called by the SLT Controller (LocalDisks.cs).
# The Controller has already validated the request, the typed confirmation and the disk
# identity; this script only executes one operation and answers with one SLT-RESULT line.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$p = [Console]::In.ReadToEnd() | ConvertFrom-Json

function Done($value) { Write-Output ('SLT-RESULT:' + (ConvertTo-Json -InputObject $value -Compress -Depth 8)); exit 0 }
function Fail([string]$message) { Done @{ error = $message } }
function Say([string]$message) { Write-Output $message }
function Letter($partition) { if ($partition.DriveLetter -and [char]$partition.DriveLetter -ne [char]0) { [string]$partition.DriveLetter } else { '' } }
function Get-SltPartition([int]$disk, [int]$number) { Get-Partition -DiskNumber $disk -PartitionNumber $number }
# Temporary drive letters for partitions without one (EFI, offline Windows); removed in finally.
$script:temporary = @()
function Ensure-Letter([int]$disk, [int]$number) {
    $part = Get-SltPartition $disk $number
    $letter = Letter $part
    if ($letter) { return $letter }
    Add-PartitionAccessPath -DiskNumber $disk -PartitionNumber $number -AssignDriveLetter
    $letter = Letter (Get-SltPartition $disk $number)
    if (-not $letter) { throw "Não foi possível atribuir letra à partição $number do disco $disk." }
    $script:temporary += @{ disk = $disk; number = $number; letter = $letter }
    return $letter
}
function Remove-TemporaryLetters {
    foreach ($t in $script:temporary) {
        try { Remove-PartitionAccessPath -DiskNumber $t.disk -PartitionNumber $t.number -AccessPath "$($t.letter):\" } catch { Say "Aviso: letra temporária $($t.letter): não removida: $($_.Exception.Message)" }
    }
}
function Prepare-Writable([int]$number) {
    $d = Get-Disk -Number $number
    if ($d.IsOffline) { Set-Disk -Number $number -IsOffline $false }
    if ($d.IsReadOnly) { Set-Disk -Number $number -IsReadOnly $false }
}
function Reset-Table([int]$number, [string]$style) {
    Prepare-Writable $number
    $d = Get-Disk -Number $number
    if ($d.PartitionStyle -ne 'RAW') { Say "Apagando a tabela de partições do disco $number..."; Clear-Disk -Number $number -RemoveData -RemoveOEM -Confirm:$false }
    $d = Get-Disk -Number $number
    if ($d.PartitionStyle -eq 'RAW') { Initialize-Disk -Number $number -PartitionStyle $style }
    elseif ($d.PartitionStyle -ne $style) { Set-Disk -Number $number -PartitionStyle $style }
    Say "Disco $number inicializado em $style."
}
function Invoke-Tool([string]$path, [string[]]$arguments) {
    Say ("> " + (Split-Path -Leaf $path) + ' ' + ($arguments -join ' '))
    & $path @arguments 2>&1 | ForEach-Object { "$_".Replace([string][char]0, '') } | Where-Object { $_.Trim() } | ForEach-Object { Say $_ }
    return $LASTEXITCODE
}

try {
    switch ($Operation) {
        'inventory' {
            $pagefile = @(Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue | ForEach-Object { $_.Name.Substring(0, 1).ToUpperInvariant() })
            $physical = @(Get-PhysicalDisk -ErrorAction SilentlyContinue)
            $disks = @(foreach ($d in (Get-Disk | Sort-Object Number)) {
                $pd = $physical | Where-Object { "$($_.DeviceId)" -eq "$($d.Number)" } | Select-Object -First 1
                $parts = @(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Sort-Object PartitionNumber | ForEach-Object {
                    $v = $null; try { $v = $_ | Get-Volume -ErrorAction Stop } catch { }
                    $letter = Letter $_
                    $windows = $false; if ($letter) { $windows = Test-Path -LiteralPath "$($letter):\Windows\System32\config\SYSTEM" }
                    [ordered]@{ number = [int]$_.PartitionNumber; offsetBytes = [int64]$_.Offset; sizeBytes = [int64]$_.Size; type = [string]$_.Type; gptType = [string]$_.GptType
                        isActive = [bool]$_.IsActive; isBoot = [bool]$_.IsBoot; isSystem = [bool]$_.IsSystem; driveLetter = $letter
                        fileSystem = $(if ($v) { [string]$v.FileSystem } else { '' }); label = $(if ($v) { [string]$v.FileSystemLabel } else { '' })
                        freeBytes = $(if ($v) { [int64]$v.SizeRemaining } else { [int64]0 }); hasWindows = $windows }
                })
                [ordered]@{ number = [int]$d.Number; model = ([string]$d.FriendlyName).Trim(); serial = ([string]$d.SerialNumber).Trim(); sizeBytes = [int64]$d.Size
                    logicalSectorBytes = [int]$d.LogicalSectorSize; physicalSectorBytes = [int]$d.PhysicalSectorSize; busType = [string]$d.BusType
                    partitionStyle = [string]$d.PartitionStyle; isSystem = [bool]$d.IsSystem; isBoot = [bool]$d.IsBoot; isOffline = [bool]$d.IsOffline
                    isReadOnly = [bool]$d.IsReadOnly; uniqueId = [string]$d.UniqueId; health = [string]$d.HealthStatus
                    mediaType = $(if ($pd) { [string]$pd.MediaType } else { '' }); location = [string]$d.Location; partitions = $parts }
            })
            Done @{ disks = $disks; pagefileDrives = $pagefile; systemDrive = $env:SystemDrive.Substring(0, 1).ToUpperInvariant() }
        }
        'format-disk' {
            Reset-Table $p.number $p.tableType
            $part = New-Partition -DiskNumber $p.number -UseMaximumSize
            Say "Formatando em $($p.fileSystem)..."
            # Formatted before it gets a letter: Explorer does not pop up "format this disk?".
            $volume = Format-Volume -Partition $part -FileSystem $p.fileSystem -NewFileSystemLabel $p.label -Confirm:$false -Force
            Add-PartitionAccessPath -DiskNumber $p.number -PartitionNumber $part.PartitionNumber -AssignDriveLetter
            $letter = Letter (Get-SltPartition $p.number $part.PartitionNumber)
            Done @{ driveLetter = $letter; fileSystem = [string]$volume.FileSystem; sizeBytes = [int64]$volume.Size }
        }
        'convert-table' {
            Reset-Table $p.number $p.tableType
            Done @{ partitionStyle = [string](Get-Disk -Number $p.number).PartitionStyle }
        }
        'format-volume' {
            $part = Get-SltPartition $p.number $p.partition
            Say "Formatando a partição $($p.partition) do disco $($p.number) em $($p.fileSystem)..."
            $volume = Format-Volume -Partition $part -FileSystem $p.fileSystem -NewFileSystemLabel $p.label -Confirm:$false -Force
            Done @{ fileSystem = [string]$volume.FileSystem; sizeBytes = [int64]$volume.Size; driveLetter = (Letter $part) }
        }
        'set-offline' {
            Set-Disk -Number $p.number -IsOffline ([bool]$p.offline)
            Done @{ isOffline = [bool](Get-Disk -Number $p.number).IsOffline }
        }
        'partition-sizes' {
            $s = Get-PartitionSupportedSize -DiskNumber $p.number -PartitionNumber $p.partition
            Done @{ minimumBytes = [int64]$s.SizeMin; maximumBytes = [int64]$s.SizeMax; currentBytes = [int64](Get-SltPartition $p.number $p.partition).Size }
        }
        'resize-partition' {
            $s = Get-PartitionSupportedSize -DiskNumber $p.number -PartitionNumber $p.partition
            $size = if ([int64]$p.sizeBytes -eq 0) { [int64]$s.SizeMax } else { [int64]$p.sizeBytes }
            if ($size -lt $s.SizeMin -or $size -gt $s.SizeMax) { Fail ("Tamanho fora do permitido pelo Windows: entre {0:N1} GB e {1:N1} GB." -f ($s.SizeMin / 1e9), ($s.SizeMax / 1e9)) }
            Say ("Redimensionando para {0:N2} GB..." -f ($size / 1e9))
            Resize-Partition -DiskNumber $p.number -PartitionNumber $p.partition -Size $size
            Done @{ sizeBytes = [int64](Get-SltPartition $p.number $p.partition).Size }
        }
        'check-disk' {
            $d = Get-Disk -Number $p.number
            $pd = Get-PhysicalDisk | Where-Object { "$($_.DeviceId)" -eq "$($p.number)" } | Select-Object -First 1
            $report = [ordered]@{ health = [string]$d.HealthStatus; operational = [string]($d.OperationalStatus -join ','); volumes = @() }
            if ($pd) {
                $report.mediaType = [string]$pd.MediaType
                try {
                    $r = $pd | Get-StorageReliabilityCounter -ErrorAction Stop
                    $report.temperatureCelsius = $r.Temperature; $report.powerOnHours = $r.PowerOnHours; $report.wearPercent = $r.Wear
                    $report.readErrorsTotal = $r.ReadErrorsTotal; $report.writeErrorsTotal = $r.WriteErrorsTotal
                } catch { Say "Contadores de confiabilidade indisponíveis: $($_.Exception.Message)" }
            }
            Say "Saúde informada pelo Windows: $($report.health) ($($report.operational))"
            if ($p.smartctl -and (Test-Path -LiteralPath $p.smartctl)) { $report.smartExitCode = Invoke-Tool $p.smartctl @('-H', '-A', "/dev/pd$($p.number)") }
            foreach ($part in @(Get-Partition -DiskNumber $p.number -ErrorAction SilentlyContinue)) {
                $letter = Letter $part
                if (-not $letter) { continue }
                $v = $part | Get-Volume
                $item = [ordered]@{ driveLetter = $letter; fileSystem = [string]$v.FileSystem; result = '' }
                if ($v.FileSystem -in 'NTFS', 'ReFS') { Say "Verificando $($letter): (somente leitura)..."; $item.result = [string](Repair-Volume -DriveLetter $letter -Scan) }
                elseif ($v.FileSystem -in 'FAT32', 'FAT', 'exFAT') { $code = Invoke-Tool (Join-Path $env:windir 'System32\chkdsk.exe') @("$($letter):"); $item.result = if ($code -eq 0) { 'NoErrorsFound' } else { "chkdsk código $code" } }
                else { $item.result = 'Sistema de arquivos não verificado' }
                Say "$($letter): $($item.result)"
                $report.volumes += $item
            }
            Done $report
        }
        'repair-volume' {
            $part = Get-SltPartition $p.number $p.partition
            $v = $part | Get-Volume
            $letter = Letter $part
            if ($v.FileSystem -in 'NTFS', 'ReFS') { Say 'Corrigindo o volume (fica indisponível durante a correção)...'; $result = [string]($v | Repair-Volume -OfflineScanAndFix) }
            elseif ($letter) { $code = Invoke-Tool (Join-Path $env:windir 'System32\chkdsk.exe') @("$($letter):", '/f', '/x'); $result = if ($code -eq 0) { 'NoErrorsFound' } else { "chkdsk código $code" } }
            else { Fail 'Volume FAT sem letra: atribua uma letra antes de corrigir.' }
            Say "Resultado: $result"
            Done @{ result = $result }
        }
        'mbr2gpt' {
            $tool = Join-Path $env:windir 'System32\mbr2gpt.exe'
            if (-not (Test-Path -LiteralPath $tool)) { Fail 'MBR2GPT.exe não existe neste Windows.' }
            $code = Invoke-Tool $tool @('/validate', "/disk:$($p.number)", '/allowFullOS')
            if ($code -ne 0) { Fail "MBR2GPT recusou o disco (código $code). Veja o registro: o disco precisa ter Windows instalado e no máximo 3 partições primárias." }
            if ($p.validateOnly) { Done @{ validated = $true; converted = $false } }
            $code = Invoke-Tool $tool @('/convert', "/disk:$($p.number)", '/allowFullOS')
            if ($code -ne 0) { Fail "MBR2GPT falhou ao converter (código $code)." }
            Done @{ validated = $true; converted = $true; partitionStyle = [string](Get-Disk -Number $p.number).PartitionStyle }
        }
        'boot-repair' {
            $disk = Get-Disk -Number $p.number
            $windows = Ensure-Letter $p.number $p.partition
            if (-not (Test-Path -LiteralPath "$($windows):\Windows\System32\config\SYSTEM")) { Fail "A partição $($p.partition) não contém uma instalação do Windows." }
            if ($disk.PartitionStyle -eq 'GPT') {
                $system = Get-Partition -DiskNumber $p.number | Where-Object { $_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' } | Select-Object -First 1
                if (-not $system) { Fail 'Disco GPT sem partição de sistema EFI: crie-a antes ou converta com MBR2GPT.' }
                $firmware = 'UEFI'
            } else {
                $system = Get-Partition -DiskNumber $p.number | Where-Object { $_.IsActive } | Select-Object -First 1
                if (-not $system) { Set-Partition -DiskNumber $p.number -PartitionNumber $p.partition -IsActive $true; $system = Get-SltPartition $p.number $p.partition; Say 'Partição do Windows marcada como ativa.' }
                $firmware = 'BIOS'
            }
            $letter = Ensure-Letter $p.number $system.PartitionNumber
            $code = Invoke-Tool (Join-Path $env:windir 'System32\bcdboot.exe') @("$($windows):\Windows", '/s', "$($letter):", '/f', $firmware)
            if ($code -ne 0) { Fail "BCDBoot falhou (código $code)." }
            Done @{ firmware = $firmware; windowsLetter = $windows; systemPartition = [int]$system.PartitionNumber }
        }
        'system-repair' {
            $dism = Join-Path $env:windir 'System32\Dism.exe'; $sfc = Join-Path $env:windir 'System32\sfc.exe'
            if ($p.mode -eq 'ONLINE') {
                $dismCode = Invoke-Tool $dism @('/Online', '/Cleanup-Image', '/RestoreHealth')
                $sfcCode = Invoke-Tool $sfc @('/scannow')
            } else {
                $letter = Ensure-Letter $p.number $p.partition
                if (-not (Test-Path -LiteralPath "$($letter):\Windows\System32\config\SYSTEM")) { Fail "A partição $($p.partition) não contém uma instalação do Windows." }
                $dismCode = Invoke-Tool $dism @("/Image:$($letter):\", '/Cleanup-Image', '/RestoreHealth')
                $sfcCode = Invoke-Tool $sfc @('/scannow', "/offbootdir=$($letter):\", "/offwindir=$($letter):\Windows")
            }
            Done @{ dismExitCode = $dismCode; sfcExitCode = $sfcCode }
        }
    }
} catch {
    Fail $_.Exception.Message
} finally {
    Remove-TemporaryLetters
}
