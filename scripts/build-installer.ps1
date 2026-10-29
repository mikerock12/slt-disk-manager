[CmdletBinding()]
param([string]$Version='0.1.0-beta.5.1',[string]$PackageDirectory,[string]$Compiler,[ValidateSet('server','client')][string[]]$Edition=@('server','client'))
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if($Version -notmatch '^[0-9A-Za-z.-]+$'){throw 'Versão inválida.'}
if(!$PackageDirectory){$PackageDirectory=Join-Path $root "artifacts/SLT-Disk-Manager-$Version-win-x64"}
$PackageDirectory=(Resolve-Path -LiteralPath $PackageDirectory).Path
# Separate installers: the server/panel for the bench and the Windows client for serviced PCs.
$required=@{
 server=@('server/SLT.Controller.Host.exe','desktop/SLT.Desktop.exe','windows-pe/slt-winpe-agent.exe','server/tools/7-Zip/7z.exe','server/tools/7-Zip/7z.dll','server/tools/7-Zip/License.txt','install-server/Install-Server.ps1')
 client=@('client/SLT.WindowsAgent.exe','client/tools/smartmontools/smartctl.exe','client/tools/smartmontools/COPYING.txt','install-agent/Install-Agent.ps1')
}
$names=@{server='Servidor';client='Cliente'}
foreach($item in @('SHA256SUMS.txt')+($Edition|ForEach-Object {$required[$_]})){
 if(!(Test-Path -LiteralPath (Join-Path $PackageDirectory $item) -PathType Leaf)){throw "Pacote incompleto: $item"}
}
if(!$Compiler){$Compiler=@((Join-Path $root '.tools/inno/ISCC.exe'),"${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe","$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1}
if(!$Compiler){throw 'Compile com Inno Setup 6 oficial; o CI Windows já disponibiliza o compilador.'}
foreach($e in $Edition){
 & $Compiler /Qp "/DPackageDir=$PackageDirectory" "/DAppVersion=$Version" "/DEdition=$e" (Join-Path $root 'deploy/installer/slt-disk-manager.iss')
 if($LASTEXITCODE -ne 0){throw "Compilação do instalador $e falhou."}
 $installer=Join-Path $root "artifacts/SLT-Disk-Manager-$($names[$e])-Setup-$Version.exe"
 $hash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
 "$hash  $(Split-Path $installer -Leaf)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
 Write-Output $installer
 Write-Output "SHA256 $hash"
}
