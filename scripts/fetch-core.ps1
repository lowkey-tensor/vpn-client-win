<#
  Скачивает ядра sing-box и Xray-core с официальных страниц релизов GitHub в папку core\.
  Каждый архив проверяется: SHA256 должен совпасть с digest, который публикует GitHub.
  Запуск:  powershell -ExecutionPolicy Bypass -File scripts\fetch-core.ps1
#>
param(
    [string]$SingBoxTag = 'latest',
    [string]$XrayTag = 'latest'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$core = Join-Path $root 'core'
New-Item -ItemType Directory -Force $core | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('vpnclient-core-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function Get-Release($repo, $tag) {
    $url = if ($tag -eq 'latest') { "https://api.github.com/repos/$repo/releases/latest" } else { "https://api.github.com/repos/$repo/releases/tags/$tag" }
    Invoke-RestMethod $url -Headers @{ 'User-Agent' = 'vpnclient-fetch-core' }
}

function Get-Verified($release, $pattern, $dest) {
    $asset = $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
    if (-not $asset) { throw "В релизе $($release.tag_name) нет файла по маске $pattern" }
    if (-not $asset.digest -or $asset.digest -notlike 'sha256:*') { throw "У $($asset.name) нет SHA256 digest — отказ" }
    $zip = Join-Path $work $asset.name
    Write-Host "Скачиваю $($asset.name) ($([math]::Round($asset.size/1MB,1)) МБ), $($release.tag_name)"
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    $want = $asset.digest.Substring(7).ToLower()
    if ($hash -ne $want) { throw "SHA256 не совпал для $($asset.name): $hash != $want" }
    Write-Host "  SHA256 совпал: $hash"
    Expand-Archive $zip $dest -Force
}

$sb = Join-Path $work 'sb'
$xr = Join-Path $work 'xr'
Get-Verified (Get-Release 'SagerNet/sing-box' $SingBoxTag) 'windows-amd64\.zip$' $sb
Get-Verified (Get-Release 'XTLS/Xray-core' $XrayTag) '^Xray-windows-64\.zip$' $xr

Copy-Item (Get-ChildItem $sb -Recurse -Filter sing-box.exe | Select-Object -First 1).FullName (Join-Path $core 'sing-box.exe') -Force
Get-ChildItem $sb -Recurse -Filter libcronet.dll | Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName $core -Force }
Copy-Item (Join-Path $xr 'xray.exe') (Join-Path $core 'xray.exe') -Force

Remove-Item $work -Recurse -Force
& (Join-Path $core 'sing-box.exe') version | Select-Object -First 1
& (Join-Path $core 'xray.exe') version | Select-Object -First 1
Write-Host 'Готово: ядра лежат в core\'
