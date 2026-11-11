[CmdletBinding()]
param()
# Service scripts of the installers, exercised without any service: validation mode (-ValidateOnly),
# command lines given by the test (-TestImagePath) and synthetic folders. Nothing here reads or
# changes SLTDiskManagerController, SLTWindowsAgent, ProgramData or a real installation.
# Runs in Windows PowerShell 5.1 (what the setup uses) and in PowerShell 7.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$server=Join-Path $root 'deploy/server-installer'
$agent=Join-Path $root 'deploy/windows-agent-installer'
. (Join-Path $server 'ServerSetup.Common.ps1')
$script:passed=0
function Check([bool]$Condition,[string]$Name){ if(-not $Condition){throw "FAIL $Name"}; $script:passed++; Write-Output "PASS $Name" }
function Rejects([scriptblock]$Action,[string]$Name){ $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Check $failed $Name }
# Runs a script in a child PowerShell and returns its JSON line (or throws with its error text).
function Invoke-Json([string]$Shell,[string]$Script,[string[]]$Arguments){
    $output=& $Shell -NoProfile -ExecutionPolicy Bypass -File $Script @Arguments 2>&1
    if($LASTEXITCODE -ne 0){throw ("Script falhou: "+($output -join ' '))}
    return (($output | Where-Object { "$_".TrimStart().StartsWith('{') } | Select-Object -Last 1) | ConvertFrom-Json)
}
# Scripts that receive a whole service command line (-TestImagePath) are called in this process:
# Windows PowerShell 5.1 cannot pass inner quotes to a child process intact.
function Invoke-Plan([string]$Script,[hashtable]$Parameters){
    return ((& $Script @Parameters | Where-Object { "$_".TrimStart().StartsWith('{') } | Select-Object -Last 1) | ConvertFrom-Json)
}
$work=Join-Path ([IO.Path]::GetTempPath()) ('slt-setup-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $package=Join-Path $work 'package'
    foreach($file in 'server/SLT.Controller.Host.exe','network-boot/SLT.NetworkBoot.Service.exe','client/SLT.WindowsAgent.exe','client/Hardware/Collect-Inventory.ps1','client/Hardware/Find-EfiSystemPartition.ps1'){
        $path=Join-Path $package $file; New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null; 'synthetic' | Set-Content -LiteralPath $path
    }
    $native=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $wow=Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    $programFiles=Get-NativeProgramFiles
    $repository=Join-Path $work 'Repositorio SLT'

    # Repository folder rules.
    Check ((Resolve-RepositoryDirectory $repository @($package)) -eq $repository) 'repositório em disco local fixo e fora do programa é aceito'
    Rejects { Resolve-RepositoryDirectory ([IO.Path]::GetPathRoot($work)) @() } 'raiz da unidade recusada como repositório'
    Rejects { Resolve-RepositoryDirectory (Join-Path $package 'dados') @($package) } 'repositório dentro da pasta do programa recusado'
    Rejects { Resolve-RepositoryDirectory $package @($package) } 'a própria pasta do programa recusada como repositório'
    Rejects { Resolve-RepositoryDirectory 'repositorio\relativo' @() } 'caminho relativo recusado'
    Rejects { Resolve-RepositoryDirectory '\\servidor\compartilhamento\slt' @() } 'compartilhamento de rede recusado'
    Rejects { Resolve-RepositoryDirectory ($repository+'"') @() } 'aspas no caminho recusadas'
    $link=Join-Path $work 'juncao'; $real=Join-Path $work 'real'; New-Item -ItemType Directory -Path $real | Out-Null
    cmd /c mklink /J "$link" "$real" | Out-Null
    if(Test-Path -LiteralPath $link){ Rejects { Resolve-RepositoryDirectory (Join-Path $link 'repo') @() } 'junção no caminho do repositório recusada' }

    # Service command line parsing and rewriting.
    $old='"C:\Program Files (x86)\SLT Disk Manager Controller\SLT.Controller.Host.exe" --bind 192.168.1.23 --allowed-subnet 192.168.1.0/24 --port 7443 --state-dir "C:\ProgramData\SLT Disk Manager\Controller" --network-boot-executable "C:\Program Files (x86)\SLT Disk Manager Controller\network-boot/SLT.NetworkBoot.Service.exe"'
    $tokens=Split-ServiceCommandLine $old
    Check ($tokens.Count -eq 11 -and (Get-ServiceOption $tokens '--state-dir') -eq 'C:\ProgramData\SLT Disk Manager\Controller' -and (Get-ServiceOption $tokens '--bind') -eq '192.168.1.23') 'linha de comando do serviço é lida com caminhos entre aspas'
    Set-ServiceOption $tokens '--repository' 'D:\SLT Disk Manager Repositorio'
    Set-ServiceOption $tokens '--repository' 'E:\Outro Repositorio'
    $joined=Join-ServiceCommandLine $tokens
    Check ($joined.EndsWith('--repository "E:\Outro Repositorio"') -and ([regex]::Matches($joined,'--repository')).Count -eq 1 -and $joined.StartsWith('"C:\Program Files (x86)\SLT Disk Manager Controller\SLT.Controller.Host.exe" --bind 192.168.1.23 ')) '--repository é acrescentado uma vez e substituído depois, com o executável entre aspas'
    Rejects { $bad=New-Object Collections.Generic.List[string]; $bad.Add('a.exe'); $bad.Add('x"y'); Join-ServiceCommandLine $bad } 'aspas dentro de argumento do serviço recusadas'

    # Verified move of a repository tree.
    $source=Join-Path $work 'antigo'; $destination=Join-Path $work 'novo'
    New-Item -ItemType Directory -Path (Join-Path $source 'images/a') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $source 'media/vazia') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $source 'images/a/00000000.bin'),(New-Object byte[] 70000))
    'manifesto' | Set-Content -LiteralPath (Join-Path $source 'images/a/manifest.json')
    $before=Get-TreeManifest $source
    Check ((Move-VerifiedTree $source $destination) -eq 2 -and -not (Test-Path -LiteralPath $source)) 'migração move os arquivos e só então remove a origem'
    $after=Get-TreeManifest $destination
    Check ($after.Count -eq 2 -and $after['images\a\00000000.bin'] -eq $before['images\a\00000000.bin'] -and (Test-Path -LiteralPath (Join-Path $destination 'media/vazia'))) 'destino confere por SHA256 e preserva pastas vazias'
    New-Item -ItemType Directory -Path (Join-Path $source 'images/a') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $source 'images/a/00000000.bin'),[byte[]](1..200))
    Rejects { Move-VerifiedTree $source $destination } 'arquivo diferente já existente no destino interrompe a migração'
    Check (Test-Path -LiteralPath (Join-Path $source 'images/a/00000000.bin')) 'origem é preservada quando a migração falha'
    Rejects { Move-VerifiedTree $destination (Join-Path $destination 'dentro') } 'destino dentro da origem recusado'
    Check ((Move-VerifiedTree (Join-Path $work 'inexistente') (Join-Path $work 'x')) -eq 0) 'origem inexistente não é erro (servidor novo)'

    # Install-Server: validation only, started from the 32-bit PowerShell like the setup does.
    $installArguments=@('-PackageDirectory',(Join-Path $package 'server'),'-Bind','192.168.1.23','-AllowedSubnet','192.168.1.0/24','-ValidateOnly')
    $shells=@($native); if(Test-Path -LiteralPath $wow){$shells+=$wow}
    foreach($shell in $shells){
        $label=if($shell -eq $wow){'PowerShell de 32 bits'}else{'PowerShell de 64 bits'}
        $plan=Invoke-Json $shell (Join-Path $server 'Install-Server.ps1') ($installArguments+@('-RepositoryDirectory',$repository))
        Check ($plan.installDirectory -eq (Join-Path $programFiles 'SLT Disk Manager Controller') -and $plan.installDirectory -notmatch '\(x86\)' -and ($plan.is64BitProcess -or -not [Environment]::Is64BitOperatingSystem)) "Install-Server a partir do ${label}: serviço vai para Program Files, em processo de 64 bits"
        Check ($plan.repositoryDirectory -eq $repository -and $plan.imagePath.Contains('--repository "'+$repository+'"') -and $plan.imagePath.Contains('--discovery-port 7444') -and $plan.imagePath.StartsWith('"'+$plan.installDirectory+'\SLT.Controller.Host.exe" ')) "Install-Server a partir do ${label}: repositório e busca automática entram na linha de comando do serviço"
    }
    $plain=Invoke-Json $native (Join-Path $server 'Install-Server.ps1') $installArguments
    Check ($null -eq $plain.repositoryDirectory -and -not $plain.imagePath.Contains('--repository')) 'sem pasta informada o repositório continua na pasta de estado'
    Check (-not (Test-Path -LiteralPath $repository)) 'modo de validação não cria a pasta do repositório'
    Rejects { Invoke-Json $native (Join-Path $server 'Install-Server.ps1') ($installArguments+@('-RepositoryDirectory',[IO.Path]::GetPathRoot($work))) } 'Install-Server recusa a raiz da unidade'
    Rejects { Invoke-Json $native (Join-Path $server 'Install-Server.ps1') ($installArguments+@('-RepositoryDirectory',(Join-Path $programFiles 'SLT Disk Manager Controller\repo'))) } 'Install-Server recusa repositório dentro da pasta do serviço'
    Rejects { Invoke-Json $native (Join-Path $server 'Install-Server.ps1') @('-PackageDirectory',(Join-Path $package 'server'),'-Bind','192.168.1.23','-AllowedSubnet','192.168.1.0/24','-InstallDirectory',(Join-Path $work 'fora'),'-ValidateOnly') } 'Install-Server recusa instalar o serviço fora de Program Files'

    # Update-Server: an installation left in "Program Files (x86)" by an earlier 32-bit setup.
    if($true){
        $update=Invoke-Plan (Join-Path $server 'Update-Server.ps1') @{PackageDirectory=(Join-Path $package 'server');ValidateOnly=$true;TestImagePath=$old}
        Check ($update.relocated -and $update.targetDirectory -eq (Join-Path $programFiles 'SLT Disk Manager Controller') -and $update.stateDirectory -eq 'C:\ProgramData\SLT Disk Manager\Controller') 'Update-Server corrige a instalação de Program Files (x86) e mantém a pasta de estado'
        Check ($update.imagePath.Contains('--bind 192.168.1.23 --allowed-subnet 192.168.1.0/24 --port 7443') -and $update.imagePath.Contains('--discovery-port 7444') -and $update.imagePath -notmatch '\(x86\)') 'Update-Server preserva rede e porta, liga a busca automática e não deixa caminho (x86)'
    }
    $current='"'+(Join-Path $programFiles 'SLT Disk Manager Controller\SLT.Controller.Host.exe')+'" --bind 10.0.0.5 --allowed-subnet 10.0.0.0/24 --port 7443 --discovery-port 7555 --state-dir "C:\ProgramData\SLT Disk Manager\Controller" --repository "D:\Repo SLT"'
    $same=Invoke-Plan (Join-Path $server 'Update-Server.ps1') @{PackageDirectory=(Join-Path $package 'server');ValidateOnly=$true;TestImagePath=$current}
    Check (-not $same.relocated -and $same.imagePath.Contains('--discovery-port 7555') -and $same.repositoryDirectory -eq 'D:\Repo SLT') 'Update-Server mantém pasta, porta de busca e repositório já configurados'
    Rejects { Invoke-Plan (Join-Path $server 'Update-Server.ps1') @{PackageDirectory=(Join-Path $package 'server');ValidateOnly=$true;TestImagePath='"C:\Outro\outro.exe" --bind 1.2.3.4'} } 'Update-Server recusa serviço que não é o Controller'

    # Set-ServerRepository: plan only.
    $change=Invoke-Plan (Join-Path $server 'Set-ServerRepository.ps1') @{RepositoryDirectory=$repository;ValidateOnly=$true;MoveExisting=$true;TestImagePath=$old}
    Check ($change.currentRepository -eq 'C:\ProgramData\SLT Disk Manager\Controller\repository' -and $change.repositoryDirectory -eq $repository -and $change.imagePath.EndsWith('--repository "'+$repository+'"') -and @($change.wouldMove).Count -eq 2) 'Set-ServerRepository planeja mover o repositório e os perfis de boot da pasta de estado'
    Check ($change.imagePath.Contains('--state-dir "C:\ProgramData\SLT Disk Manager\Controller"')) 'Set-ServerRepository não altera a pasta de estado (certificados, pin, token)'
    $replace=Invoke-Plan (Join-Path $server 'Set-ServerRepository.ps1') @{RepositoryDirectory=$repository;ValidateOnly=$true;TestImagePath=$current}
    Check (([regex]::Matches($replace.imagePath,'--repository')).Count -eq 1 -and $replace.currentRepository -eq 'D:\Repo SLT') 'Set-ServerRepository substitui um --repository existente'
    Rejects { Invoke-Plan (Join-Path $server 'Set-ServerRepository.ps1') @{RepositoryDirectory='C:\ProgramData\SLT Disk Manager\Controller';ValidateOnly=$true;TestImagePath=$old} } 'Set-ServerRepository recusa a própria pasta de estado'
    Check (-not (Test-Path -LiteralPath $repository)) 'planos de validação não criaram nada'

    # Real move through the script (ACL included) needs elevation: CI runners have it, a normal desktop session does not.
    if(Test-SltAdministrator){
        $state=Join-Path $work 'estado'; $oldRepository=Join-Path $state 'repository'
        New-Item -ItemType Directory -Path (Join-Path $oldRepository 'images/b') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $state 'network-boot/store/rescue-x') -Force | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $oldRepository 'images/b/00000000.bin'),(New-Object byte[] 4096))
        'perfil' | Set-Content -LiteralPath (Join-Path $state 'network-boot/store/rescue-x/rootfs')
        'segredo' | Set-Content -LiteralPath (Join-Path $state 'admin.token')
        $synthetic='"'+(Join-Path $package 'server\SLT.Controller.Host.exe')+'" --bind 127.0.0.1 --state-dir "'+$state+'"'
        $output=& (Join-Path $server 'Set-ServerRepository.ps1') -RepositoryDirectory $repository -MoveExisting -TestImagePath $synthetic 6>$null
        Check ((Test-Path -LiteralPath (Join-Path $repository 'images/b/00000000.bin')) -and (Test-Path -LiteralPath (Join-Path $repository 'network-boot/store/rescue-x/rootfs')) -and -not (Test-Path -LiteralPath $oldRepository) -and -not (Test-Path -LiteralPath (Join-Path $state 'network-boot'))) 'Set-ServerRepository -MoveExisting leva imagens e perfis de boot para a pasta nova'
        Check ((Test-Path -LiteralPath (Join-Path $state 'admin.token')) -and ("$($output | Select-Object -Last 1)").EndsWith('--repository "'+$repository+'"')) 'pasta de estado permanece intacta e a nova linha de comando aponta para o repositório'
        $acl=Get-Acl -LiteralPath $repository
        $sids=@($acl.Access | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique)
        Check ($acl.AreAccessRulesProtected -and ($sids -join ',') -eq 'S-1-5-18,S-1-5-32-544') 'pasta do repositório fica com ACL protegida só para SISTEMA e Administradores'
    } else { Write-Output 'SKIP migração real com ACL: exige sessão elevada (executada no CI).' }

    # Client service scripts.
    foreach($shell in $shells){
        $client=Invoke-Json $shell (Join-Path $agent 'Install-Agent.ps1') @('-PackageDirectory',(Join-Path $package 'client'),'-ValidateOnly')
        Check ($client.installDirectory -eq (Join-Path $programFiles 'SLT Disk Manager\WindowsAgent') -and $client.discovery -and $client.installDirectory -notmatch '\(x86\)') 'Install-Agent sem endereço usa a busca automática e instala em Program Files'
    }
    $typed=Invoke-Json $native (Join-Path $agent 'Install-Agent.ps1') @('-PackageDirectory',(Join-Path $package 'client'),'-Server','https://192.168.1.23:7443','-ValidateOnly')
    Check (-not $typed.discovery -and $typed.server -eq 'https://192.168.1.23:7443') 'Install-Agent aceita só o endereço (pin confirmado pelo código de 8 dígitos)'
    Rejects { Invoke-Json $native (Join-Path $agent 'Install-Agent.ps1') @('-PackageDirectory',(Join-Path $package 'client'),'-ServerPin',('a'*64),'-ValidateOnly') } 'Install-Agent recusa pin sem endereço'
    $oldAgent='"C:\Program Files (x86)\SLT Disk Manager\WindowsAgent\SLT.WindowsAgent.exe" "--server" "https://192.168.1.23:7443" "--server-pin" "'+('ab'*32)+'" "--state-dir" "C:\ProgramData\SLT\WindowsAgent" "--smartctl" "C:\Program Files (x86)\SLT Disk Manager\WindowsAgent\tools\smartmontools\smartctl.exe"'
    $agentUpdate=Invoke-Plan (Join-Path $agent 'Update-Agent.ps1') @{PackageDirectory=(Join-Path $package 'client');ValidateOnly=$true;TestImagePath=$oldAgent}
    Check ($agentUpdate.relocated -and $agentUpdate.targetDirectory -eq (Join-Path $programFiles 'SLT Disk Manager\WindowsAgent') -and $agentUpdate.imagePath -notmatch '\(x86\)' -and $agentUpdate.imagePath.Contains('"--state-dir" "C:\ProgramData\SLT\WindowsAgent"') -and $agentUpdate.imagePath.Contains('"--server" "https://192.168.1.23:7443"')) 'Update-Agent corrige a pasta (x86), leva o smartctl junto e preserva o pareamento'

    Write-Output "$script:passed/$script:passed verificações dos scripts de serviço passaram (somente validação e pastas sintéticas)."
} finally {
    if(Test-Path -LiteralPath (Join-Path $work 'juncao')){ cmd /c rmdir (Join-Path $work 'juncao') | Out-Null }
    if(Test-SltAdministrator){ & icacls.exe $work /reset /T /C /Q | Out-Null }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
