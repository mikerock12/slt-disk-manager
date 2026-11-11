[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(ValueFromRemainingArguments)][string[]]$Rest
)
# Runs one of the service scripts for the setup program and keeps the window open when
# something fails, so the reason can be read instead of the window just closing.
$ErrorActionPreference='Stop'
$target=Join-Path $PSScriptRoot $Script
if(-not (Test-Path -LiteralPath $target -PathType Leaf) -or [IO.Path]::GetExtension($target) -ne '.ps1'){
    Write-Host "Script não encontrado: $target" -ForegroundColor Red
    Read-Host 'Enter para fechar' | Out-Null
    exit 2
}
$arguments=@{}
for($i=0;$i -lt @($Rest).Count;$i++){
    $name=$Rest[$i]
    if($name -notmatch '^-[A-Za-z]+$'){ Write-Host "Argumento inesperado: $name" -ForegroundColor Red; Read-Host 'Enter para fechar' | Out-Null; exit 2 }
    if($i+1 -lt $Rest.Count -and $Rest[$i+1] -notmatch '^-[A-Za-z]+$'){ $arguments[$name.Substring(1)]=$Rest[$i+1]; $i++ }
    else { $arguments[$name.Substring(1)]=$true }
}
try {
    & $target @arguments
    if($LASTEXITCODE){ throw "O script terminou com o código $LASTEXITCODE." }
    Write-Host ''
    Write-Host 'Concluído.' -ForegroundColor Green
    Start-Sleep -Seconds 4
    exit 0
} catch {
    Write-Host ''
    Write-Host 'NÃO FOI POSSÍVEL CONCLUIR:' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Write-Host 'Nada além do que aparece acima foi alterado. Corrija e execute o instalador de novo.'
    Read-Host 'Enter para fechar' | Out-Null
    exit 1
}
