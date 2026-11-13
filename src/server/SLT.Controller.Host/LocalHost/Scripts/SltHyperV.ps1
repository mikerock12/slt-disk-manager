[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('status','create','start','stop','turn-off','delete','set-iso')][string]$Operation)
# Hyper-V virtual machines created by the SLT Controller (HyperV.cs). The Controller validates
# names, sizes and disks; only machines tagged "SLT-Disk-Manager" in Notes are touched here.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::InputEncoding = [Text.Encoding]::UTF8
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$p = [Console]::In.ReadToEnd() | ConvertFrom-Json
function Done($value) { Write-Output ('SLT-RESULT:' + (ConvertTo-Json -InputObject $value -Compress -Depth 8)); exit 0 }
function Fail([string]$message) { Done @{ error = $message } }
function Say([string]$message) { Write-Output $message }
function Get-SltVM([string]$name) {
    $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
    if (-not $vm -or "$($vm.Notes)" -notlike 'SLT-Disk-Manager*') { throw "Máquina virtual $name não encontrada ou não criada pelo SLT Disk Manager." }
    return $vm
}

try {
    if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
        if ($Operation -eq 'status') { Done @{ available = $false; reason = 'Hyper-V não está instalado. Ative "Hyper-V" em Recursos do Windows (Windows 10/11 Pro) e reinicie.' } }
        Fail 'Hyper-V não está instalado neste Windows.'
    }
    switch ($Operation) {
        'status' {
            $service = Get-Service vmms -ErrorAction SilentlyContinue
            if (-not $service -or $service.Status -ne 'Running') { Done @{ available = $false; reason = 'O serviço do Hyper-V (vmms) não está em execução. Reinicie o servidor após ativar o Hyper-V.' } }
            $computer = Get-CimInstance Win32_ComputerSystem
            $vms = @(Get-VM | Where-Object { "$($_.Notes)" -like 'SLT-Disk-Manager*' } | ForEach-Object {
                $vm = $_
                $cpu = Get-VMProcessor -VMName $vm.Name
                $dvd = @(Get-VMDvdDrive -VMName $vm.Name -ErrorAction SilentlyContinue | Where-Object { $_.Path } | ForEach-Object { [string]$_.Path })
                $drives = @(Get-VMHardDiskDrive -VMName $vm.Name | ForEach-Object { [ordered]@{ path = [string]$_.Path; diskNumber = $(if ($null -ne $_.DiskNumber) { [int]$_.DiskNumber } else { -1 }) } })
                $secure = ''
                if ($vm.Generation -eq 2) { $secure = [string](Get-VMFirmware -VMName $vm.Name).SecureBoot }
                [ordered]@{ name = $vm.Name; notes = [string]$vm.Notes; state = [string]$vm.State; generation = [int]$vm.Generation; memoryBytes = [int64]$vm.MemoryStartup
                    processorCount = [int]$cpu.Count; cpuMaximumPercent = [int]$cpu.Maximum; uptimeSeconds = [int64]$vm.Uptime.TotalSeconds
                    cpuUsagePercent = [int]$vm.CPUUsage; iso = ($dvd -join ';'); drives = $drives; secureBoot = $secure }
            })
            Done @{ available = $true; logicalProcessors = [int]$computer.NumberOfLogicalProcessors; memoryBytes = [int64]$computer.TotalPhysicalMemory
                switches = @(Get-VMSwitch | ForEach-Object { [string]$_.Name }); machines = $vms; vmConnect = (Test-Path -LiteralPath (Join-Path $env:windir 'System32\vmconnect.exe')) }
        }
        'create' {
            if (Get-VM -Name $p.name -ErrorAction SilentlyContinue) { Fail "Já existe uma máquina virtual chamada $($p.name)." }
            $arguments = @{ Name = $p.name; Generation = [int]$p.generation; MemoryStartupBytes = ([int64]$p.memoryMiB * 1MB); NoVHD = $true }
            if ($p.switchName) { $arguments.SwitchName = $p.switchName }
            Say "Criando a máquina virtual $($p.name) (geração $($p.generation))..."
            $vm = New-VM @arguments
            $created = @()
            try {
                Set-VM -VM $vm -StaticMemory -Notes "SLT-Disk-Manager id=$($p.id)" -CheckpointType Disabled -AutomaticStopAction ShutDown
                Set-VMProcessor -VM $vm -Count ([int]$p.cpuCount) -Maximum ([int]$p.cpuPercent)
                if ($p.disk.kind -eq 'NEW') {
                    Say "Criando o disco virtual $($p.disk.path)..."
                    New-Item -ItemType Directory -Path (Split-Path -Parent $p.disk.path) -Force | Out-Null
                    New-VHD -Path $p.disk.path -SizeBytes ([int64]$p.disk.sizeGiB * 1GB) -Dynamic | Out-Null
                    $created += $p.disk.path
                    Add-VMHardDiskDrive -VM $vm -Path $p.disk.path
                } elseif ($p.disk.kind -eq 'EXISTING') {
                    Add-VMHardDiskDrive -VM $vm -Path $p.disk.path
                } elseif ($p.disk.kind -eq 'PHYSICAL') {
                    Say "Disco físico $($p.disk.number): offline no servidor e entregue à máquina virtual."
                    $disk = Get-Disk -Number $p.disk.number
                    if (-not $disk.IsOffline) { Set-Disk -Number $p.disk.number -IsOffline $true }
                    Add-VMHardDiskDrive -VM $vm -DiskNumber $p.disk.number
                }
                if ($p.isoPath) {
                    if ($vm.Generation -eq 2) { Add-VMDvdDrive -VM $vm -Path $p.isoPath } else { Set-VMDvdDrive -VMName $vm.Name -ControllerNumber 1 -ControllerLocation 0 -Path $p.isoPath }
                }
                if ($vm.Generation -eq 2) {
                    if ($p.secureBoot) { Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate $p.secureBootTemplate } else { Set-VMFirmware -VM $vm -EnableSecureBoot Off }
                    $dvd = Get-VMDvdDrive -VM $vm | Select-Object -First 1
                    if ($dvd) { Set-VMFirmware -VM $vm -FirstBootDevice $dvd }
                    if ($p.tpm) { Set-VMKeyProtector -VM $vm -NewLocalKeyProtector; Enable-VMTPM -VM $vm }
                } else {
                    Set-VMBios -VM $vm -StartupOrder @('CD', 'IDE', 'LegacyNetworkAdapter', 'Floppy')
                }
            } catch {
                $message = $_.Exception.Message
                Remove-VM -VM $vm -Force -ErrorAction SilentlyContinue
                foreach ($file in $created) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
                if ($p.disk.kind -eq 'PHYSICAL') { Set-Disk -Number $p.disk.number -IsOffline $false -ErrorAction SilentlyContinue }
                Fail "Criação desfeita: $message"
            }
            Done @{ name = $vm.Name; created = $created }
        }
        'start' { $vm = Get-SltVM $p.name; Start-VM -VM $vm; Done @{ state = [string](Get-VM -Name $p.name).State } }
        'stop' { $vm = Get-SltVM $p.name; Stop-VM -VM $vm -Force; Done @{ state = [string](Get-VM -Name $p.name).State } }
        'turn-off' { $vm = Get-SltVM $p.name; Stop-VM -VM $vm -TurnOff -Force; Done @{ state = [string](Get-VM -Name $p.name).State } }
        'set-iso' {
            $vm = Get-SltVM $p.name
            $dvd = Get-VMDvdDrive -VM $vm | Select-Object -First 1
            if (-not $dvd) { if ($p.isoPath) { Add-VMDvdDrive -VM $vm -Path $p.isoPath } }
            else { Set-VMDvdDrive -VMDvdDrive $dvd -Path $(if ($p.isoPath) { $p.isoPath } else { $null }) }
            Done @{ iso = [string]$p.isoPath }
        }
        'delete' {
            $vm = Get-SltVM $p.name
            if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
            $physical = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $null -ne $_.DiskNumber } | ForEach-Object { [int]$_.DiskNumber })
            Remove-VM -VM $vm -Force
            $removed = @()
            foreach ($file in @($p.deletePaths)) { if ($file -and (Test-Path -LiteralPath $file)) { Remove-Item -LiteralPath $file -Force; $removed += $file } }
            if ($p.onlineDisks) { foreach ($n in $physical) { Set-Disk -Number $n -IsOffline $false } }
            Done @{ removedFiles = $removed; physicalDisks = $physical }
        }
    }
} catch {
    Fail $_.Exception.Message
}
