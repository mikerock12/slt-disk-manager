[CmdletBinding()]
param([string]$Server, [string]$ServerPin, [string]$FileRootsPath, [switch]$SltMode, [switch]$NoElevate)
$ErrorActionPreference='Stop'
$agent=Join-Path $PSScriptRoot 'client/SLT.WindowsAgent.exe'

function Test-Administrator {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Get-ServiceStateDirectory {
    $key='HKLM:\SYSTEM\CurrentControlSet\Services\SLTWindowsAgent'
    if(-not (Test-Path -LiteralPath $key)){return $null}
    $imagePath=(Get-ItemProperty -LiteralPath $key -Name ImagePath).ImagePath
    if($imagePath -match '"--state-dir"\s+"([^"]+)"'){return $Matches[1]}
    if($imagePath -match '--state-dir\s+"([^"]+)"'){return $Matches[1]}
    return (Join-Path $env:ProgramData 'SLT\WindowsAgent')
}

# SMART, the disk inventory and the SLT mode need administrator rights: ask once, here.
if(-not $NoElevate -and -not (Test-Administrator)){
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'))
    if($Server){$arguments+=@('-Server',$Server)}
    if($ServerPin){$arguments+=@('-ServerPin',$ServerPin)}
    if($FileRootsPath){$arguments+=@('-FileRootsPath',('"'+$FileRootsPath+'"'))}
    if($SltMode){$arguments+='-SltMode'}
    try { Start-Process -FilePath powershell.exe -ArgumentList $arguments -Verb RunAs; return }
    catch { Write-Warning 'Sem permissão de administrador: o cliente abre com funções limitadas (sem SMART e sem o modo SLT).' }
}

$serviceState=Get-ServiceStateDirectory
if($serviceState){
    # Installed as a Windows service: it is already running in the background.
    Write-Host ''
    Write-Host '  SLT DISK MANAGER - CLIENTE (instalado como serviço)'
    Write-Host '  O cliente já está em execução e conectado ao servidor em segundo plano.'
    Write-Host ''
    if(-not $SltMode){
        Write-Host '  1 - Reiniciar este computador em modo SLT Disk Manager'
        Write-Host '  0 - Fechar'
        $choice=Read-Host '  Escolha'
        if($choice -ne '1'){return}
    }
    & $agent --reboot-to-slt --state-dir $serviceState
    if($LASTEXITCODE -ne 0){Write-Host ''; Read-Host '  Não foi possível preparar o modo SLT (veja a mensagem acima). Enter para fechar' | Out-Null}
    return
}

$state=Join-Path $PSScriptRoot 'state/client'
$clientArgs=@('--state-dir',$state)
# Without an address the client looks for the server on the local network by itself.
if($Server){$clientArgs+=@('--server',$Server)}
if($ServerPin){$clientArgs+=@('--server-pin',$ServerPin)}
if($FileRootsPath){$clientArgs+=@('--file-roots',(Resolve-Path -LiteralPath $FileRootsPath).Path)}
if($SltMode){$clientArgs+='--reboot-to-slt'}
& $agent @clientArgs
if($LASTEXITCODE -ne 0){Write-Host ''; Read-Host 'O cliente encerrou (veja a mensagem acima). Enter para fechar' | Out-Null}
