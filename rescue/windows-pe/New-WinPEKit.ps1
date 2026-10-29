[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$AgentPath,
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$ServerPin,
    [Parameter(Mandatory)][string]$ScratchDirectory
)
$ErrorActionPreference='Stop'
# This creates a NEW kit folder only. It never formats USB, mounts WIM or alters boot.
if($Server -notmatch '^https://[A-Za-z0-9.-]+:[0-9]{1,5}$'){throw 'Servidor deve ser HTTPS LAN com porta explícita e hostname/IP simples.'}
$uri=[uri]$Server
if($uri.Port -lt 1 -or $uri.Port -gt 65535){throw 'Porta inválida.'}
$ServerPin=$ServerPin.Replace(':','').ToLowerInvariant()
if($ServerPin -notmatch '^[a-f0-9]{64}$'){throw 'Pin deve conter SHA256 hexadecimal de 64 caracteres.'}
if($ScratchDirectory -notmatch '^[A-WY-Za-wy-z]:\\[A-Za-z0-9 _.-]+(\\[A-Za-z0-9 _.-]+)*$' -or $ScratchDirectory -match '(^|\\)\.\.?($|\\)' -or $ScratchDirectory -match '[ .]($|\\)'){throw 'Escolha pasta dedicada no armazenamento físico NTFS do WinPE, fora do destino. RAM X: não é aceita.'}
$agent=(Get-Item -LiteralPath $AgentPath -ErrorAction Stop)
if($agent.PSIsContainer -or $agent.Extension -ne '.exe' -or ($agent.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Informe executável WinPE regular.'}
$bytes=[IO.File]::ReadAllBytes($agent.FullName)
if($bytes.Length -lt 512 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a){throw 'Executável PE inválido.'}
$peOffset=[BitConverter]::ToInt32($bytes,0x3c)
if($peOffset -lt 64 -or $peOffset+6 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes,$peOffset) -ne 0x4550 -or [BitConverter]::ToUInt16($bytes,$peOffset+4) -ne 0x8664){throw 'O agente deve ser Windows AMD64.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Destino já existe. Escolha uma pasta nova para o kit.'}
$parent=Split-Path -Parent $output
if(!(Test-Path -LiteralPath $parent -PathType Container)){throw 'A pasta pai do kit deve existir.'}
$walk=Get-Item -LiteralPath $parent
while($walk){if($walk.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Pasta de saída não pode atravessar reparse/junction.'};$walk=$walk.Parent}
New-Item -ItemType Directory -Path $output | Out-Null
$slt=New-Item -ItemType Directory -Path (Join-Path $output 'SLT')
$marker='kit-'+[guid]::NewGuid().ToString('N')+'.marker'
[IO.File]::WriteAllText((Join-Path $slt.FullName $marker),'SLT WinPE kit locator; public, no credentials.',[Text.Encoding]::ASCII)
Copy-Item -LiteralPath $agent.FullName -Destination (Join-Path $slt.FullName 'slt-winpe-agent.exe')
$start=@'
@echo off
setlocal DisableDelayedExpansion
reg query HKLM\SYSTEM\CurrentControlSet\Control\MiniNT >nul 2>&1
if errorlevel 1 goto blocked
if not exist "%SystemRoot%\System32\wpeutil.exe" goto blocked
"%SystemRoot%\System32\wpeutil.exe" InitializeNetwork
if errorlevel 1 goto blocked
if not exist "__SCRATCH__" mkdir "__SCRATCH__"
if errorlevel 1 goto blocked
set "SLT_BOOTSECT="
if exist "%~dp0..\boot\bootsect.exe" set "SLT_BOOTSECT=--bootsect %~d0\boot\bootsect.exe"
rem WinPE under a hypervisor (Hyper-V/WHPX) can hand Go's async preemption an inconsistent
rem thread context and crash the agent at start; cooperative preemption is enough here.
set "GODEBUG=asyncpreemptoff=1"
"%~dp0slt-winpe-agent.exe" --server "__SERVER__" --server-pin "__PIN__" --state-dir "X:\SLT-State" --scratch-dir "__SCRATCH__" --allow-deployment %SLT_BOOTSECT%
echo.
echo O agente encerrou. Confira o resultado no servidor; primeiro boot/OOBE ainda precisa ser validado.
goto stopped
:blocked
echo Falha no preflight WinPE/rede/scratch. Nenhuma instalacao automatica sera iniciada.
:stopped
echo Para sair, desligue manualmente ou use Ctrl+C e wpeutil Shutdown.
:hold
pause >nul
goto hold
'@
$start=$start.Replace('__SERVER__',$Server).Replace('__PIN__',$ServerPin).Replace('__SCRATCH__',$ScratchDirectory)
[IO.File]::WriteAllText((Join-Path $slt.FullName 'Start-SLT.cmd'),$start.Replace("`r`n","`n").Replace("`n","`r`n"),[Text.Encoding]::ASCII)
$unattend=@'
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
 <settings pass="windowsPE">
  <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
   <RunSynchronous>
    <RunSynchronousCommand wcm:action="add">
     <Order>1</Order><Description>SLT WinPE - somente jobs aprovados</Description>
     <Path>cmd.exe /d /c "for %d in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\SLT\__MARKER__ if exist %d:\sources\boot.wim if exist %d:\SLT\Start-SLT.cmd call %d:\SLT\Start-SLT.cmd &amp; cmd.exe /d /k echo SLT: Setup automatico bloqueado; confira a midia e desligue manualmente."</Path>
    </RunSynchronousCommand>
   </RunSynchronous>
  </component>
 </settings>
</unattend>
'@
[IO.File]::WriteAllText((Join-Path $output 'Autounattend.xml'),$unattend.Replace('__MARKER__',$marker),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $output 'LEIA-ME.md')
$manifest=[ordered]@{schemaVersion=1;agent='slt-winpe-agent.exe';locatorMarker=$marker;sha256=(Get-FileHash -LiteralPath $agent.FullName -Algorithm SHA256).Hash.ToLowerInvariant();server=$Server;serverPin=$ServerPin;scratchDirectoryInWinPE=$ScratchDirectory;containsMicrosoftMedia=$false}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $slt.FullName 'kit-manifest.json') -Encoding utf8
Write-Output "Kit criado: $output"
Write-Output 'Copie SLT/ e Autounattend.xml para a raiz da mídia oficial de instalação Windows AMD64, após revisar LEIA-ME.'
