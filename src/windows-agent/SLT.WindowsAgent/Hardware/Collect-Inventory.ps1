$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$notes = New-Object 'System.Collections.Generic.List[string]'
$disks = @()
try {
    $physicalDisks = @(Get-Disk -ErrorAction Stop)
    foreach ($disk in $physicalDisks) {
        $partitions = @()
        try {
            foreach ($partition in @(Get-Partition -DiskNumber $disk.Number -ErrorAction Stop)) {
                $fileSystem = ''
                try { $volume = $partition | Get-Volume -ErrorAction Stop; $fileSystem = [string]$volume.FileSystem } catch { }
                $partitions += [ordered]@{
                    name = [string]$partition.PartitionNumber
                    path = $(if ($partition.AccessPaths) { [string]$partition.AccessPaths[0] } else { '' })
                    sizeBytes = [long]$partition.Size
                    fileSystem = $fileSystem
                    mountPoint = $(if ($partition.DriveLetter) { [string]$partition.DriveLetter + ':\' } else { '' })
                }
            }
        } catch { $notes.Add('PARTITION_INVENTORY_UNAVAILABLE disk=' + [string]$disk.Number) }
        $disks += [ordered]@{
            id = 'windows-disk-' + [string]$disk.Number
            path = '\\.\PhysicalDrive' + [string]$disk.Number
            model = ([string]$disk.FriendlyName).Trim()
            serial = ([string]$disk.SerialNumber).Trim()
            sizeBytes = [long]$disk.Size
            logicalSectorBytes = [int]$disk.LogicalSectorSize
            physicalSectorBytes = [int]$disk.PhysicalSectorSize
            fingerprint = ''
            isSystem = [bool]($disk.IsSystem -or $disk.IsBoot)
            isReadOnly = [bool]$disk.IsReadOnly
            mounted = [bool](@($partitions | Where-Object { $_.mountPoint -ne '' }).Count -gt 0)
            health = [string]$disk.HealthStatus
            partitions = @($partitions)
        }
        if ([string]::IsNullOrWhiteSpace([string]$disk.SerialNumber)) { $notes.Add('DISK_SERIAL_UNAVAILABLE disk=' + [string]$disk.Number) }
    }
} catch { $notes.Add('DISK_INVENTORY_UNAVAILABLE: execute como administrador para inventario completo.') }
$osName = [Environment]::OSVersion.VersionString
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $osName = [string]$os.Caption + ' build ' + [string]$os.BuildNumber
} catch { $notes.Add('OS_CIM_UNAVAILABLE') }
$notes.Add('Windows ativo: nenhuma captura de disco ou gravacao de setores e permitida por este agente.')
[ordered]@{ os = $osName; disks = @($disks); notes = @($notes) } | ConvertTo-Json -Depth 8 -Compress
