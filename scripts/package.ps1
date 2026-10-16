<#
  Собирает автономный архив для Windows x64: .NET внутри, ничего ставить не нужно.
  Перед запуском в core\ должны лежать ядра (scripts\fetch-core.ps1).
  Запуск:  powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Version 4.0
  Результат: _release\VpnClient-<версия>-win-x64.zip
#>
param([string]$Version = 'dev')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root '_release'
$pub = Join-Path $out 'VpnClient'

foreach ($f in 'sing-box.exe', 'xray.exe') {
    if (-not (Test-Path (Join-Path $root "core\$f"))) { throw "Нет core\$f — сначала запустите scripts\fetch-core.ps1" }
}

if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish (Join-Path $root 'src\VpnClient') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $pub
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish завершился с ошибкой' }

$zip = Join-Path $out "VpnClient-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip -CompressionLevel Optimal
$h = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Set-Content (Join-Path $out "VpnClient-$Version-win-x64.zip.sha256") "$h  VpnClient-$Version-win-x64.zip" -Encoding ascii
"{0}  {1:N1} МБ  sha256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $h
