# Prints the volume path (\\?\Volume{GUID}\) of the EFI System Partition on the disk Windows
# starts from. Read only: nothing is mounted, assigned a letter or changed.
$ErrorActionPreference = 'Stop'
$esp = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
$systemDisks = @(Get-Disk | Where-Object { $_.IsSystem -or $_.IsBoot } | ForEach-Object { $_.Number })
Get-Partition | Where-Object { $_.GptType -eq $esp -and $systemDisks -contains $_.DiskNumber } | ForEach-Object {
    $_.AccessPaths | Where-Object { $_ -like '\\?\Volume{*' }
} | Sort-Object -Unique
