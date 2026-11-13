[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('create','screen','type','key','remove','state')][string]$Action,
    [string]$Name = 'SLT-Kamikaze-Lab',
    [string]$Lab = (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) 'runtime\kamikaze-lab'),
    [string]$BaseDisk,
    [string]$Out,
    [string]$Text,
    [int]$Key = 13
)
# Kamikaze laboratory in a disposable Hyper-V VM. The VM boots a differencing disk of a lab
# Windows (the base image is never written) plus synthetic data disks. Nothing here touches the
# host disks or its boot configuration. 'screen' saves the VM screen; 'type' types at its console.
$ErrorActionPreference = 'Stop'
function Get-LabVm { $vm = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$Name'"; if (-not $vm) { throw "VM $Name não existe." }; $vm }
switch ($Action) {
    'create' {
        if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "VM $Name já existe." }
        if (-not $BaseDisk) { $BaseDisk = Join-Path $Lab 'server-uefi.vhdx' }
        $os = Join-Path $Lab 'os-diff.vhdx'
        Remove-Item -LiteralPath $os -ErrorAction SilentlyContinue
        New-VHD -Path $os -ParentPath $BaseDisk -Differencing | Out-Null
        $vm = New-VM -Name $Name -Generation 2 -MemoryStartupBytes 4GB -VHDPath $os
        Set-VM -VM $vm -StaticMemory -ProcessorCount 2 -CheckpointType Disabled -AutomaticStopAction TurnOff -Notes 'SLT kamikaze lab'
        Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
        Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
        # Distinct sizes: virtual disks have no serial, the identity is model + size.
        foreach ($disk in @(@{n='src'; s=1GB}, @{n='fmt'; s=1536MB}, @{n='dst'; s=2GB})) {
            $path = Join-Path $Lab "$($disk.n).vhdx"
            Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
            New-VHD -Path $path -SizeBytes $disk.s -Dynamic | Out-Null
            Add-VMHardDiskDrive -VM $vm -Path $path
        }
        Set-VMFirmware -VM $vm -FirstBootDevice (Get-VMHardDiskDrive -VM $vm | Where-Object Path -eq $os)
        Start-VM -VM $vm
        "VM $Name criada e ligada (Secure Boot ligado, sem rede)."
    }
    'state' { $v = Get-VM -Name $Name; "{0} | heartbeat {1} | uptime {2}" -f $v.State, $v.Heartbeat, $v.Uptime }
    'screen' {
        Add-Type -AssemblyName System.Drawing
        $vm = Get-LabVm
        $service = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemManagementService
        $settings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData | Where-Object VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized'
        $w = 1024; $h = 768
        $r = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $settings; WidthPixels = [uint16]$w; HeightPixels = [uint16]$h }
        $bitmap = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
        $data = $bitmap.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h), 'WriteOnly', $bitmap.PixelFormat)
        [System.Runtime.InteropServices.Marshal]::Copy([byte[]]$r.ImageData, 0, $data.Scan0, $w * $h * 2)
        $bitmap.UnlockBits($data)
        $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        "Tela salva em $Out"
    }
    'type' {
        $keyboard = Get-CimAssociatedInstance -InputObject (Get-LabVm) -ResultClassName Msvm_Keyboard
        Invoke-CimMethod -InputObject $keyboard -MethodName TypeText -Arguments @{ asciiText = $Text } | Out-Null
        Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = [uint32]13 } | Out-Null
        "Digitado + Enter"
    }
    'key' {
        $keyboard = Get-CimAssociatedInstance -InputObject (Get-LabVm) -ResultClassName Msvm_Keyboard
        Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = [uint32]$Key } | Out-Null
        "Tecla $Key"
    }
    'remove' {
        if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { Stop-VM -Name $Name -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -Name $Name -Force }
        foreach ($f in 'os-diff.vhdx', 'src.vhdx', 'fmt.vhdx', 'dst.vhdx') { Remove-Item -LiteralPath (Join-Path $Lab $f) -ErrorAction SilentlyContinue }
        "VM e discos do laboratório removidos (imagem base preservada)."
    }
}
