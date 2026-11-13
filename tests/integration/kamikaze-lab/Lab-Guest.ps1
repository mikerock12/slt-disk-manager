[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('wait','info','copy','prepare','stage','status','repair','verify')][string]$Action,
    [string]$Name = 'SLT-Kamikaze-Lab',
    [string]$Lab = (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) 'runtime\kamikaze-lab'),
    [string]$UserName = 'tecnico'
)
# Runs the kamikaze laboratory steps INSIDE the lab VM through PowerShell Direct (no network).
# The lab account was created by the earlier VM laboratory; its password is read from the local,
# git-ignored runtime\lab-full\lab.json and never printed.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$secret = (Get-Content -LiteralPath (Join-Path $root 'runtime\lab-full\lab.json') -Raw | ConvertFrom-Json).passwords.'client2-windows'
$credential = New-Object System.Management.Automation.PSCredential($UserName, (ConvertTo-SecureString $secret -AsPlainText -Force))
function Session { New-PSSession -VMName $Name -Credential $credential }

# Shared by the guest blocks: admin API of the Controller running in the VM.
$api = {
    # curl.exe (Windows 10): Invoke-RestMethod of PowerShell 5.1 fails the TLS handshake with Kestrel here.
    # -k only inside the lab VM, on loopback, against the Controller it just started.
    function Api([string]$Method, [string]$Path, $Body) {
        $token = (Get-Content 'C:\SLT\state\admin.token' -Raw).Trim()
        $arguments = @('-sk', '--max-time', '600', '-X', $Method, '-H', "Authorization: Bearer $token", '-H', 'Content-Type: application/json')
        if ($null -ne $Body) { $file = [IO.Path]::GetTempFileName(); [IO.File]::WriteAllText($file, ($Body | ConvertTo-Json -Depth 8 -Compress)); $arguments += @('--data-binary', "@$file") }
        $text = & curl.exe @arguments "https://127.0.0.1:7443$Path"
        if ($file) { Remove-Item $file }
        $result = ($text -join "`n") | ConvertFrom-Json
        if ($result.error) { throw "API $Path : $($result.error)" }
        $result
    }
}

switch ($Action) {
    'wait' {
        $until = (Get-Date).AddMinutes(15)
        while ((Get-Date) -lt $until) {
            try { $s = Session; Invoke-Command -Session $s { "$env:COMPUTERNAME pronto" }; Remove-PSSession $s; return } catch { Start-Sleep 10 }
        }
        throw 'Windows da VM não respondeu ao PowerShell Direct.'
    }
    'info' {
        $s = Session
        Invoke-Command -Session $s {
            "Windows: " + (Get-CimInstance Win32_OperatingSystem).Caption + " | firmware: " + $env:firmware_type
            "Admin: " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            reagentc /info | Select-String 'Status|Local|Location'
            "Winre em System32: " + (Test-Path C:\Windows\System32\Recovery\Winre.wim)
            Get-Disk | Select-Object Number, FriendlyName, @{n='GB';e={[math]::Round($_.Size/1GB,2)}}, PartitionStyle, IsSystem | Format-Table | Out-String
            "SecureBoot: " + (Confirm-SecureBootUEFI)
        }
        Remove-PSSession $s
    }
    'copy' {
        $s = Session
        Invoke-Command -Session $s { Remove-Item C:\SLT -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory C:\SLT | Out-Null }
        Copy-Item -ToSession $s -Path (Join-Path $Lab 'server') -Destination 'C:\SLT\server' -Recurse
        Invoke-Command -Session $s { "Copiado: " + (Get-ChildItem C:\SLT\server -Recurse -File).Count + " arquivos" }
        Remove-PSSession $s
    }
    'prepare' {
        $s = Session
        Invoke-Command -Session $s {
            $ErrorActionPreference = 'Stop'
            # Source disk (1 GB): GPT + NTFS + a file whose SHA256 is kept for the check after the clone.
            $src = Get-Disk | Where-Object { $_.Size -eq 1GB } | Select-Object -First 1
            if ($src.PartitionStyle -eq 'RAW') { Initialize-Disk -Number $src.Number -PartitionStyle GPT }
            $part = New-Partition -DiskNumber $src.Number -UseMaximumSize -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel ORIGEM -Confirm:$false | Out-Null
            $letter = (Get-Partition -DiskNumber $src.Number -PartitionNumber $part.PartitionNumber).DriveLetter
            $bytes = New-Object byte[] (64MB); (New-Object Random 42).NextBytes($bytes)
            [IO.File]::WriteAllBytes("$($letter):\dados.bin", $bytes)
            (Get-FileHash "$($letter):\dados.bin").Hash | Set-Content C:\SLT\dados.sha256
            # Controller as a SYSTEM task that also starts after reboots (collects the kamikaze result).
            $action = New-ScheduledTaskAction -Execute 'C:\SLT\server\SLT.Controller.Host.exe' -Argument '--bind 127.0.0.1 --state-dir C:\SLT\state --discovery off' -WorkingDirectory 'C:\SLT\server'
            $trigger = New-ScheduledTaskTrigger -AtStartup
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries
            Register-ScheduledTask -TaskName 'SLT Controller Lab' -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
            Start-ScheduledTask -TaskName 'SLT Controller Lab'
            $until = (Get-Date).AddMinutes(3)
            while (-not (Test-Path C:\SLT\state\admin.token) -and (Get-Date) -lt $until) { Start-Sleep 2 }
            "Origem: disco $($src.Number) letra $letter; Controller iniciado: " + (Test-Path C:\SLT\state\admin.token)
        }
        Remove-PSSession $s
    }
    'stage' {
        $s = Session
        Invoke-Command -Session $s -ArgumentList $api {
            param($api)
            . ([scriptblock]::Create($api))
            $status = Api GET '/api/v1/server-local/kamikaze'
            "Firmware $($status.firmware) | Secure Boot $($status.secureBoot) | imagem: $($status.bootImage.detail)"
            $status.disks | ForEach-Object { "  disco $($_.number) $($_.model) $([math]::Round($_.sizeBytes/1GB,2)) GB $($_.notes -join ',')" }
            $id = { param($gb) ($status.disks | Where-Object { $_.sizeBytes -eq [int64]($gb * 1GB) }).identity }
            $plan = Api POST '/api/v1/server-local/kamikaze/plan' @{ rebootNow = $true; operations = @(
                @{ kind = 'CLONE_DISK'; sourceIdentity = (& $id 1); targetIdentity = (& $id 2) },
                @{ kind = 'FORMAT_DISK'; targetIdentity = (& $id 1.5); tableType = 'MBR'; fileSystem = 'EXFAT'; label = 'KAMIKAZE' }) }
            "Plano: " + ($plan.summary -join ' | ')
            "Avisos: " + ($plan.warnings -join ' | ')
            "Frases no console: " + ($plan.consoleChallenges -join ' | ')
            $plan.consoleChallenges -join "`n" | Set-Content C:\SLT\frases.txt
            Api POST "/api/v1/server-local/kamikaze/$($plan.id)/confirm" @{ challenge = $plan.challenge } | Out-Null
            $until = (Get-Date).AddMinutes(20)
            do { Start-Sleep 5; $session = (Api GET '/api/v1/server-local/kamikaze').sessions | Where-Object id -eq $plan.id } while ($session.state -eq 'STAGING' -and (Get-Date) -lt $until)
            "Estado: $($session.state) | $($session.message)"
            $session.log
        }
        Remove-PSSession $s
    }
    'status' {
        $s = Session
        Invoke-Command -Session $s -ArgumentList $api {
            param($api)
            . ([scriptblock]::Create($api))
            $until = (Get-Date).AddMinutes(3)
            while (-not (Test-NetConnection 127.0.0.1 -Port 7443 -InformationLevel Quiet -WarningAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep 3 }
            $session = (Api GET '/api/v1/server-local/kamikaze').sessions | Select-Object -First 1
            "Estado: $($session.state) | $($session.message)"
            $session.resultJson
            "Entradas de boot restantes com SLT: " + ((bcdedit /enum all) -match 'SLT').Count
        }
        Remove-PSSession $s
    }
    'repair' {
        # Real BCDBoot and disk check on the VM's own Windows disk through the Controller API.
        $s = Session
        Invoke-Command -Session $s -ArgumentList $api {
            param($api)
            . ([scriptblock]::Create($api))
            function RunOp($request) {
                $plan = Api POST '/api/v1/server-local/disk-operations' $request
                Api POST "/api/v1/server-local/disk-operations/$($plan.id)/confirm" @{ challenge = $plan.challenge } | Out-Null
                do { Start-Sleep 3; $op = Api GET "/api/v1/server-local/disk-operations/$($plan.id)" } while ($op.state -eq 'RUNNING')
                "$($op.title) => $($op.state) | $($op.message)"
                if ($op.state -ne 'COMPLETED') { $op.log }
                $op.resultJson
            }
            $inventory = Api GET '/api/v1/server-local/disks'
            $system = $inventory.disks | Where-Object { $_.isSystem } | Select-Object -First 1
            "Disco do sistema $($system.number): proteções = $($system.protections -join ', ')"
            RunOp @{ kind = 'CHECK_DISK'; diskNumber = $system.number; fingerprint = $system.fingerprint }
            $windows = $system.partitions | Where-Object { $_.hasWindows } | Select-Object -First 1
            RunOp @{ kind = 'BOOT_REPAIR'; diskNumber = $system.number; fingerprint = $system.fingerprint; partitionNumber = $windows.number }
            try { Api POST '/api/v1/server-local/disk-operations' @{ kind = 'FORMAT_DISK'; diskNumber = $system.number; fingerprint = $system.fingerprint; tableType = 'GPT'; fileSystem = 'NTFS' } | Out-Null; 'ERRO: formatação do disco do sistema foi aceita' } catch { "Formatar o disco do sistema recusado: $($_.Exception.Message)" }
        }
        Remove-PSSession $s
    }
    'verify' {
        $s = Session
        Invoke-Command -Session $s {
            $dst = Get-Disk | Where-Object { $_.Size -eq 2GB }
            if ($dst.IsOffline) { Set-Disk -Number $dst.Number -IsOffline $false }
            $src = Get-Disk | Where-Object { $_.Size -eq 1GB }
            "Origem: $($src.PartitionStyle) offline=$($src.IsOffline) | Destino: $($dst.PartitionStyle) offline=$((Get-Disk -Number $dst.Number).IsOffline)"
            $fmt = Get-Disk | Where-Object { $_.Size -eq 1536MB }
            $fmtVolume = Get-Partition -DiskNumber $fmt.Number -ErrorAction SilentlyContinue | Get-Volume -ErrorAction SilentlyContinue
            "Formatado: tabela $($fmt.PartitionStyle) | sistema $($fmtVolume.FileSystem) | rótulo $($fmtVolume.FileSystemLabel)"
            $part = Get-Partition -DiskNumber $dst.Number | Where-Object Type -eq 'Basic' | Select-Object -First 1
            if (-not $part.DriveLetter -or [char]$part.DriveLetter -eq [char]0) { $part | Add-PartitionAccessPath -AssignDriveLetter; $part = Get-Partition -DiskNumber $dst.Number -PartitionNumber $part.PartitionNumber }
            $hash = (Get-FileHash "$($part.DriveLetter):\dados.bin").Hash
            "Arquivo no clone confere com a origem: " + ($hash -eq (Get-Content C:\SLT\dados.sha256).Trim())
            "Partição do clone: " + [math]::Round($part.Size / 1MB) + " MB; maior suportado: " + [math]::Round((Get-PartitionSupportedSize -DiskNumber $dst.Number -PartitionNumber $part.PartitionNumber).SizeMax / 1MB) + " MB (GPT de reserva no fim do disco de 2 GB)"
        }
        Remove-PSSession $s
    }
}
