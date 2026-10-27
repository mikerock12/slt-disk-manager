[CmdletBinding()]
param([Parameter(Mandatory)][string]$AgentPath,[string]$ArtifactsDirectory=(Join-Path $PSScriptRoot '../../artifacts'))
$ErrorActionPreference='Stop'
$parent=[IO.Path]::GetFullPath($ArtifactsDirectory)
if(!(Test-Path -LiteralPath $parent -PathType Container)){throw 'Pasta de artifacts deve existir.'}
$fixture=Join-Path $parent ('winpe-kit-fixture-'+[guid]::NewGuid().ToString('N'))
$generator=Join-Path $PSScriptRoot 'New-WinPEKit.ps1'
$pin='a'*64
& $generator -OutputDirectory $fixture -AgentPath $AgentPath -Server https://192.168.50.10:7443 -ServerPin $pin -ScratchDirectory 'E:\SLT-Scratch'
[xml]$xml=Get-Content -LiteralPath (Join-Path $fixture 'Autounattend.xml') -Raw
$manager=[Xml.XmlNamespaceManager]::new($xml.NameTable)
$manager.AddNamespace('u','urn:schemas-microsoft-com:unattend')
$command=$xml.SelectSingleNode('//u:RunSynchronousCommand/u:Path',$manager).InnerText
if(!$command.Contains('Start-SLT.cmd') -or !$command.Contains('cmd.exe /d /k') -or $xml.SelectSingleNode('//u:DiskConfiguration|//u:ImageInstall',$manager)){throw 'Answer file não preserva as barreiras esperadas.'}
$manifest=Get-Content -LiteralPath (Join-Path $fixture 'SLT/kit-manifest.json') -Raw | ConvertFrom-Json
if($manifest.containsMicrosoftMedia -or $manifest.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $fixture 'SLT/slt-winpe-agent.exe')).Hash.ToLowerInvariant()){throw 'Manifesto incorreto.'}
if(!$command.Contains($manifest.locatorMarker) -or !$command.Contains('sources\boot.wim') -or !(Test-Path -LiteralPath (Join-Path $fixture ('SLT/'+$manifest.locatorMarker)))){throw 'Localizador único da mídia ausente.'}
$cmd=Get-Content -LiteralPath (Join-Path $fixture 'SLT/Start-SLT.cmd') -Raw
if(!$cmd.Contains('MiniNT') -or !$cmd.Contains('--allow-deployment') -or !$cmd.Contains(':hold') -or !$cmd.Contains('X:\SLT-State')){throw 'Bootstrap incorreto.'}
$rejected=$false
try{& $generator -OutputDirectory $fixture -AgentPath $AgentPath -Server https://192.168.50.10:7443 -ServerPin $pin -ScratchDirectory 'E:\SLT-Scratch'}catch{$rejected=$true}
if(!$rejected){throw 'Sobrescrita de kit existente foi aceita.'}
foreach($badScratch in @('X:\SLT-Scratch','E:\..\customer','E:\scratch&whoami')){
 $rejected=$false
 try{& $generator -OutputDirectory ($fixture+'-invalid') -AgentPath $AgentPath -Server https://192.168.50.10:7443 -ServerPin $pin -ScratchDirectory $badScratch}catch{$rejected=$true}
 if(!$rejected){throw "Scratch inválido aceito: $badScratch"}
}
Write-Output 'PASS WinPE kit: PE AMD64, XML, hash, hold, no overwrite, RAM/traversal/injection rejected.'
Write-Output "Fixture preservado para inspeção: $fixture"
