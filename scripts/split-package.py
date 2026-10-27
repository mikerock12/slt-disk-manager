"""Split a release ZIP for connectors with a 100 MB per-file transport limit."""
import argparse
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("package")
args = parser.parse_args()
package = Path(args.package).resolve(strict=True)
destination = package.parent / (package.stem + "-drive-parts")
destination.mkdir(exist_ok=False)
manifest = {"packageName": package.name, "sizeBytes": package.stat().st_size, "sha256": hashlib.sha256(package.read_bytes()).hexdigest(), "parts": []}
with package.open("rb") as source:
    sequence = 1
    while data := source.read(64 * 1024 * 1024):
        name = package.name + f".part{sequence:03d}"
        (destination / name).write_bytes(data)
        manifest["parts"].append({"name": name, "sizeBytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        sequence += 1
(destination / "package-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
(destination / "Reunir-Pacote.ps1").write_text(r'''$ErrorActionPreference='Stop'
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-manifest.json') -Raw | ConvertFrom-Json
if($manifest.packageName -notmatch '^[a-zA-Z0-9_.-]+\.zip$'){throw 'Nome de pacote invalido.'}
$output=Join-Path $PSScriptRoot $manifest.packageName
$temporary=$output+'.partial'
if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $temporary)){throw 'Destino ja existe. Preserve ou mova o arquivo anterior antes de reunir.'}
$stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
    foreach($part in $manifest.parts){
        if($part.name -notmatch '^[a-zA-Z0-9_.-]+\.zip\.part\d{3}$'){throw 'Nome de parte invalido.'}
        $path=Join-Path $PSScriptRoot $part.name
        if((Get-Item -LiteralPath $path).Length -ne $part.sizeBytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $part.sha256){throw "Parte ausente/corrompida: $($part.name)"}
        $inputStream=[IO.File]::OpenRead($path)
        try {$inputStream.CopyTo($stream)} finally {$inputStream.Dispose()}
    }
    $stream.Flush($true)
} finally {$stream.Dispose()}
if((Get-Item -LiteralPath $temporary).Length -ne $manifest.sizeBytes -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256){throw 'SHA256 final divergente; nao extraia o pacote.'}
Move-Item -LiteralPath $temporary -Destination $output
Write-Host "ZIP reunido e validado: $output"
''', encoding="utf-8")
(destination / "Reunir-Pacote.cmd").write_bytes(b'@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Reunir-Pacote.ps1"\r\npause\r\n')
(destination / "LEIA-ME.txt").write_text("Baixe todas as partes listadas no package-manifest.json, mais package-manifest.json, Reunir-Pacote.ps1 e Reunir-Pacote.cmd na mesma pasta. Execute Reunir-Pacote.cmd. O script verifica SHA256 das partes e do ZIP reunido. Depois extraia o ZIP e leia QUICKSTART.md. Nao execute restauração em dados reais antes da homologação indicada.\n", encoding="utf-8")
print(destination)
print(json.dumps(manifest, indent=2))
