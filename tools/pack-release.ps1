# Zips the published folders in dist\ into release archives.
#   powershell -NoProfile -File tools\pack-release.ps1 [-Version 1.7.6]
#
# Why zip the folder instead of shrinking the exe: single-file compression
# (EnableCompressionInSingleFile) gets the exe to ~62MB but pushes cold start
# from ~0.4s to 1.8-2.8s, because the whole bundle has to be inflated to a temp
# dir on every launch. A zip costs the user one extraction and keeps the fast
# start. Downloads are the same size either way.
param([string]$Version = '1.7.6')

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty when the script is piped in on stdin - fall back to cwd,
# which is the repo root in that case.
$root = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
$out = Join-Path $root 'dist\release'
New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($flavour in 'fast', 'small') {
    $src = Join-Path $root "dist\FlashTrans-win-x64-$flavour"
    if (-not (Test-Path (Join-Path $src 'FlashTrans.exe'))) {
        Write-Host "skip $flavour - not published"
        continue
    }
    $zip = Join-Path $out "FlashTrans-$Version-win-x64-$flavour.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # A running instance keeps Assets\app.ico open (the tray icon loads from it) and
    # Compress-Archive refuses to read a file that is open elsewhere, even though a
    # plain copy of it succeeds. So stage a copy and zip that - no need to make the
    # user close the app, which may be mid-edit on a screenshot.
    $stage = Join-Path $root "dist\_zipsrc\$flavour"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $stage -Recurse -Force

    $seconds = Measure-Command {
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    }
    $raw = (Get-ChildItem $src -Recurse -File | Measure-Object -Sum Length).Sum
    $packed = (Get-Item $zip).Length
    '{0,-6} {1,4}MB -> {2,6}MB zip ({3}% of original, {4}s)' -f `
        $flavour, [math]::Round($raw / 1MB), [math]::Round($packed / 1MB, 1),
        [math]::Round(100 * $packed / $raw), [math]::Round($seconds.TotalSeconds)

    Remove-Item $stage -Recurse -Force
}

$zipsrc = Join-Path $root 'dist\_zipsrc'
if (Test-Path $zipsrc) { Remove-Item $zipsrc -Recurse -Force }

Write-Host ''
Get-ChildItem $out -Filter *.zip | ForEach-Object {
    '{0}  {1}MB  SHA256 {2}' -f $_.Name, [math]::Round($_.Length / 1MB, 1),
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16)
}
Write-Host ''
Write-Host "release archives in $out"
