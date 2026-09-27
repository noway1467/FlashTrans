# 将带版本号的发布目录压缩成同名包，包内也保留同名顶层目录。
param([string]$Version = '1.9.1')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本格式必须是 x.y.z。' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'release'
$packages = @()

foreach ($flavour in @('fast', 'small')) {
    $name = "FlashTrans-$Version-win-x64-$flavour"
    $source = Join-Path $dist $name
    $binary = Join-Path $source 'FlashTrans.exe'
    if (-not (Test-Path -LiteralPath $binary)) { throw "缺少 $name，请先执行 tools\publish.cmd both。" }
    if ((Get-Item -LiteralPath $binary).VersionInfo.FileVersion -ne "$Version.0") { throw "包名与程序版本不一致：$binary" }
    $zip = Join-Path $out "$name.zip"
    if (Test-Path -LiteralPath $zip) { throw "压缩包已存在，未覆盖：$zip" }
    # 不能把便携数据、日志或链接到其他目录的内容打包发布。
    $items = @((Get-Item -LiteralPath $source)) + @(Get-ChildItem -LiteralPath $source -Recurse -Force)
    if ($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) { throw "发布目录含链接，停止：$source" }
    if ($items | Where-Object { $_.Name -in @('data', 'settings.json', 'document-history.json', 'portable.txt') -or $_.Extension -eq '.log' }) {
        throw "发布目录含用户配置或日志，停止：$source"
    }
    $packages += [pscustomobject]@{ Source = $source; Zip = $zip; Name = $name }
}
New-Item -ItemType Directory -Path $out -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $temp = Join-Path $out ('.pack-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($package.Source, $temp, [IO.Compression.CompressionLevel]::Optimal, $true)
        [IO.File]::Move($temp, $package.Zip)
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
    $hash = (Get-FileHash -LiteralPath $package.Zip -Algorithm SHA256).Hash
    '{0}  {1:N1} MB  SHA256 {2}' -f $package.Zip, ((Get-Item -LiteralPath $package.Zip).Length / 1MB), $hash
}
