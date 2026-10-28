[CmdletBinding()]
param([string]$Version='0.1.0-beta.3.1',[string]$PackageDirectory,[string]$Compiler)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if($Version -notmatch '^[0-9A-Za-z.-]+$'){throw 'Versão inválida.'}
if(!$PackageDirectory){$PackageDirectory=Join-Path $root "artifacts/SLT-Disk-Manager-$Version-win-x64"}
$PackageDirectory=(Resolve-Path -LiteralPath $PackageDirectory).Path
foreach($required in @('server/SLT.Controller.Host.exe','desktop/SLT.Desktop.exe','client/SLT.WindowsAgent.exe','windows-pe/slt-winpe-agent.exe','SHA256SUMS.txt','server/tools/7-Zip/7z.exe','server/tools/7-Zip/7z.dll','server/tools/7-Zip/License.txt','client/tools/smartmontools/smartctl.exe','client/tools/smartmontools/COPYING.txt')){
 if(!(Test-Path -LiteralPath (Join-Path $PackageDirectory $required) -PathType Leaf)){throw "Pacote incompleto: $required"}
}
if(!$Compiler){$Compiler=@((Join-Path $root '.tools/inno/ISCC.exe'),"${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe","$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1}
if(!$Compiler){throw 'Compile com Inno Setup 6 oficial; o CI Windows já disponibiliza o compilador.'}
& $Compiler /Qp "/DPackageDir=$PackageDirectory" "/DAppVersion=$Version" (Join-Path $root 'deploy/installer/slt-disk-manager.iss')
if($LASTEXITCODE -ne 0){throw 'Compilação do instalador falhou.'}
$installer=Join-Path $root "artifacts/SLT-Disk-Manager-Setup-$Version.exe"
$hash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $installer -Leaf)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Output $installer
Write-Output "SHA256 $hash"
