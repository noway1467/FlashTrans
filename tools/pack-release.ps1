# 从最近一次构建清单打包，跳过原目录里保留的用户数据；允许替换同版本压缩包。
param([string]$Version = '1.9.1')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本格式必须是 x.y.z。' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $root 'dist'
$out = Assert-ReleasePath (Join-Path $dist 'release') $dist
$packages = @()
foreach ($flavour in @('fast', 'small')) {
    $source = Get-ReleaseDirectory $dist $flavour
    $binary = Join-Path $source 'FlashTrans.exe'
    if (-not (Test-Path -LiteralPath $binary)) { throw "缺少 $flavour，请先执行 tools\publish.cmd both。" }
    if ((Get-Item -LiteralPath $binary).VersionInfo.FileVersion -ne "$Version.0") { throw "程序版本与待打包版本不一致：$binary" }
    $manifestPath = Assert-ReleasePath (Join-Path $source '.flashtrans-release.json') $dist
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw '缺少本次发布清单，请先执行 tools\publish.cmd both。' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Version -ne $Version -or @($manifest.Files).Count -eq 0) { throw '发布清单版本错误或为空。' }
    foreach ($file in $manifest.Files) {
        Assert-ReleaseFile $file.Path
        $path = Assert-ReleasePath (Join-Path $source $file.Path) $source
        if ((Get-ReleaseHash $path) -ne $file.SHA256) { throw "程序文件与本次构建不一致：$path" }
    }
    $zip = Assert-ReleasePath (Join-Path $out "FlashTrans-$Version-win-x64-$flavour.zip") $dist
    $packages += [pscustomobject]@{ Source = $source; Zip = $zip; Files = $manifest.Files }
}
New-Item -ItemType Directory -Path $out -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $temp = Assert-ReleasePath (Join-Path $out ('.pack-' + [guid]::NewGuid().ToString('N') + '.tmp')) $dist
    try {
        $archive = [IO.Compression.ZipFile]::Open($temp, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $package.Files) {
                $path = Join-Path $package.Source $file.Path
                # 包内仍使用原目录名，解压升级不会另建一套程序目录。
                $entry = ((Split-Path -Leaf $package.Source) + '/' + $file.Path.Replace('\', '/'))
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $path, $entry, [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $archive.Dispose() }
        if (Test-Path -LiteralPath $package.Zip) {
            # Windows PowerShell 5.1 会把 $null 字符串参数绑定为空路径，使用显式临时备份。
            $previous = Assert-ReleasePath ($temp + '.previous') $dist
            [IO.File]::Replace($temp, $package.Zip, $previous)
            Remove-Item -LiteralPath $previous
        }
        else { [IO.File]::Move($temp, $package.Zip) }
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
    $hash = Get-ReleaseHash $package.Zip
    '{0}  {1:N1} MB  SHA256 {2}' -f $package.Zip, ((Get-Item -LiteralPath $package.Zip).Length / 1MB), $hash
}
