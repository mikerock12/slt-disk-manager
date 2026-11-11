[CmdletBinding()]
param([string]$Bind, [string]$AllowedSubnet, [int]$Port=7443, [string]$RepositoryDirectory)
$ErrorActionPreference='Stop'

# With the Windows service installed, a second (portable) server on the same port would only
# fail. Open the panel of the service instead; it asks for administrator permission.
$serviceKey='HKLM:\SYSTEM\CurrentControlSet\Services\SLTDiskManagerController'
if(Test-Path -LiteralPath $serviceKey){
    $imagePath=(Get-ItemProperty -LiteralPath $serviceKey -Name ImagePath).ImagePath
    $state=if($imagePath -match '--state-dir\s+"([^"]+)"'){$Matches[1]}elseif($imagePath -match '--state-dir\s+(\S+)'){$Matches[1]}else{Join-Path $env:ProgramData 'SLT Disk Manager\Controller'}
    Write-Host 'O servidor já está instalado como serviço do Windows neste computador.'
    Write-Host 'Abrindo o painel do serviço (o Windows pedirá permissão de administrador)...'
    Start-Process -FilePath (Join-Path $PSScriptRoot 'desktop/SLT.Desktop.exe') -ArgumentList @('--connection',('"'+(Join-Path $state 'connection.json')+'"')) -Verb RunAs
    return
}

if(-not $Bind){$Bind=Read-Host 'IP privado deste computador servidor (ex.: 192.168.1.10)'}
if(-not $AllowedSubnet){$AllowedSubnet=Read-Host 'Sub-rede autorizada da bancada (ex.: 192.168.1.0/24)'}
$ip=$null
if(-not [System.Net.IPAddress]::TryParse($Bind,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork'){throw 'IPv4 inválido.'}
if($AllowedSubnet -notmatch '^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$'){throw 'Sub-rede CIDR inválida.'}
if($Port -lt 1024 -or $Port -gt 65535){throw 'Porta inválida.'}
$state=Join-Path $PSScriptRoot 'state/server'
New-Item -ItemType Directory -Path $state -Force | Out-Null
$exe=Join-Path $PSScriptRoot 'server/SLT.Controller.Host.exe'
$serverArguments=@('--bind',$Bind,'--allowed-subnet',$AllowedSubnet,'--port',$Port,'--state-dir',('"'+$state+'"'))
if($RepositoryDirectory){
    # Images and imported media need space: this folder may be on another local disk.
    if($RepositoryDirectory.Contains('"') -or -not [IO.Path]::IsPathRooted($RepositoryDirectory)){throw 'Informe um caminho absoluto para a pasta do repositório, sem aspas.'}
    $repository=[IO.Path]::GetFullPath($RepositoryDirectory)
    if($repository.TrimEnd('\').Length -le [IO.Path]::GetPathRoot($repository).TrimEnd('\').Length){throw 'Não use a raiz da unidade como repositório; crie uma pasta dedicada.'}
    New-Item -ItemType Directory -Path $repository -Force | Out-Null
    $serverArguments+=@('--repository',('"'+$repository+'"'))
}
$process=Start-Process -FilePath $exe -ArgumentList $serverArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $state 'server.log') -RedirectStandardError (Join-Path $state 'server-error.log')
$process.Id | Set-Content (Join-Path $state 'controller.pid')
$connection=Join-Path $state 'connection.json'
for($i=0;$i -lt 60;$i++){
    $process.Refresh()
    if($process.HasExited){throw "Servidor encerrou. Leia $state/server-error.log"}
    if(Test-Path -LiteralPath $connection){
        try {
            $config=Get-Content -LiteralPath $connection -Raw | ConvertFrom-Json
            if($config.processId -eq $process.Id){
                $socket=New-Object System.Net.Sockets.TcpClient
                try { $pending=$socket.ConnectAsync('127.0.0.1',$Port); if($pending.Wait(300) -and $socket.Connected){break} } finally {$socket.Dispose()}
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 500
}
if($i -ge 60){throw 'Servidor não iniciou em 30 segundos. Consulte os logs locais.'}
Get-Content -LiteralPath (Join-Path $state 'server-info.txt')
Write-Host 'Os clientes desta versão encontram o servidor sozinhos e mostram um código de 8 dígitos.'
Write-Host 'Se os clientes não encontrarem o servidor, o firewall do Windows está bloqueando: instale o serviço (install-server) como Administrador.'
Start-Process -FilePath (Join-Path $PSScriptRoot 'desktop/SLT.Desktop.exe') -ArgumentList @('--connection',('"'+$connection+'"'))
