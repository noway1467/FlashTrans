# 在临时工作区验证覆盖/保留/打包流程，模拟构建产物但真实启动并解除文件占用。
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$exe = Join-Path $repo 'src\FlashTrans\bin\Release\net9.0-windows\FlashTrans.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '请先构建当前 Release 主工程。' }
$version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion -replace '\.0$', ''
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixture = Join-Path $tempRoot ('FlashTrans-release-test-' + [guid]::NewGuid().ToString('N'))
$lockProcess = $null
function Need($condition, [string]$message) { if (-not $condition) { throw $message } }
try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'tools'), (Join-Path $fixture 'src\FlashTrans') -Force | Out-Null
    foreach ($script in 'publish.ps1','pack-release.ps1','release-common.ps1') {
        Copy-Item -LiteralPath (Join-Path $repo "tools\$script") -Destination (Join-Path $fixture "tools\$script")
    }
    "<Project><PropertyGroup><Version>$version</Version></PropertyGroup></Project>" |
        Set-Content -LiteralPath (Join-Path $fixture 'src\FlashTrans\FlashTrans.csproj')
    $dist = Join-Path $fixture 'dist'
    foreach ($flavour in 'fast','small') {
        $target = Join-Path $dist "FlashTrans-1.8.0-win-x64-$flavour"
        New-Item -ItemType Directory -Path (Join-Path $target 'data'), (Join-Path $target 'legacy'), (Join-Path $target 'legacy-user') -Force | Out-Null
        Copy-Item -LiteralPath $exe -Destination (Join-Path $target 'FlashTrans.exe')
        'old-program' | Set-Content -LiteralPath (Join-Path $target 'payload.txt')
        'old-obsolete' | Set-Content -LiteralPath (Join-Path $target 'obsolete.dll')
        'old-nested' | Set-Content -LiteralPath (Join-Path $target 'legacy\old.dll')
        'test-only-user-nested' | Set-Content -LiteralPath (Join-Path $target 'legacy-user\keep.txt')
        'test-only-private-data' | Set-Content -LiteralPath (Join-Path $target 'data\settings.json')
        'test-only-notes' | Set-Content -LiteralPath (Join-Path $target 'notes.txt')
        'test-only-log' | Set-Content -LiteralPath (Join-Path $target 'private.log')
        '' | Set-Content -LiteralPath (Join-Path $target 'portable.txt')
        [pscustomobject]@{
            Version = '1.8.0'
            Files = @(
                [pscustomobject]@{ Path = 'FlashTrans.exe'; SHA256 = 'OLD' }
                [pscustomobject]@{ Path = 'payload.txt'; SHA256 = 'OLD' }
                [pscustomobject]@{ Path = 'obsolete.dll'; SHA256 = 'OLD' }
                [pscustomobject]@{ Path = 'legacy\old.dll'; SHA256 = 'OLD' }
            )
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target '.flashtrans-release.json') -Encoding UTF8
    }
    $fast = Join-Path $dist 'FlashTrans-1.8.0-win-x64-fast'
    $helper = Join-Path $fixture 'lock-helper'
    New-Item -ItemType Directory -Path $helper -Force | Out-Null
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>' |
        Set-Content -LiteralPath (Join-Path $helper 'ReleaseLockProbe.csproj')
    'using var file = System.IO.File.Open(args[0], System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read); System.IO.File.WriteAllText(args[1], "ready"); System.Threading.Thread.Sleep(-1);' |
        Set-Content -LiteralPath (Join-Path $helper 'Program.cs')
    & (Get-Command dotnet).Source build (Join-Path $helper 'ReleaseLockProbe.csproj') -c Release --nologo -o (Join-Path $fast 'test-helper')
    if ($LASTEXITCODE -ne 0) { throw '占用测试辅助程序构建失败' }
    $ready = Join-Path $fixture 'ready.txt'
    $lockProcess = Start-Process -FilePath (Join-Path $fast 'test-helper\ReleaseLockProbe.exe') -ArgumentList @(('"' + (Join-Path $fast 'payload.txt') + '"'), ('"' + $ready + '"')) -PassThru -WindowStyle Hidden
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $ready)) {
        if ($wait.Elapsed.TotalSeconds -gt 10 -or $lockProcess.HasExited) { throw '占用辅助程序未就绪' }
        Start-Sleep -Milliseconds 50
    }

    # 只替换构建步骤，文件复制、进程关闭、校验和 ZIP 替换都走真实发布脚本。
    $global:FlashTransReleaseProbeFailBuild = $true
    function dotnet {
        $output = [string]$args[[Array]::IndexOf($args, '-o') + 1]
        if ($global:FlashTransReleaseProbeFailBuild -and (Split-Path -Leaf $output) -eq 'small') { $global:LASTEXITCODE = 1; return }
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Copy-Item -LiteralPath $exe -Destination (Join-Path $output 'FlashTrans.exe')
        'new-program' | Set-Content -LiteralPath (Join-Path $output 'payload.txt')
        $global:LASTEXITCODE = 0
    }
    $failed = $false
    try { & (Join-Path $fixture 'tools\publish.ps1') both } catch { $failed = $true }
    Need $failed '模拟构建失败未传播'
    Need (-not $lockProcess.HasExited) '构建失败却关闭了原程序'
    Need ((Get-Content -LiteralPath (Join-Path $fast 'payload.txt') -Raw).Trim() -eq 'old-program') '构建失败却覆盖原文件'
    Need (Test-Path -LiteralPath (Join-Path $fast 'obsolete.dll')) '构建失败却删除了旧程序文件'
    Need (Test-Path -LiteralPath (Join-Path $fast 'legacy\old.dll')) '构建失败却删除了旧子目录程序文件'
    Write-Output 'OK 构建失败不覆盖、不关闭现有实例'

    $global:FlashTransReleaseProbeFailBuild = $false
    $small = Join-Path $dist 'FlashTrans-1.8.0-win-x64-small'
    $externalLock = [IO.File]::Open((Join-Path $small 'obsolete.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $failed = $false
        try { & (Join-Path $fixture 'tools\publish.ps1') both } catch { $failed = $true }
        Need $failed '第三方占用待清理旧文件时未前置失败'
        Need ((Get-Content -LiteralPath (Join-Path $fast 'payload.txt') -Raw).Trim() -eq 'old-program') '第三方占用失败却提前改动了 fast 目录'
        Need (Test-Path -LiteralPath (Join-Path $small 'obsolete.dll')) '第三方占用失败却删除了旧文件'
    } finally { $externalLock.Dispose() }
    Write-Output 'OK 第三方占用待清理旧文件时前置失败且零破坏'

    & (Join-Path $fixture 'tools\publish.ps1') both
    Need ($lockProcess.WaitForExit(3000)) '发布没有解除目标目录内的实际文件占用'
    foreach ($dir in $fast, $small) {
        Need ((Get-Content -LiteralPath (Join-Path $dir 'payload.txt') -Raw).Trim() -eq 'new-program') '程序文件未覆盖'
        Need (-not (Test-Path -LiteralPath (Join-Path $dir 'obsolete.dll'))) '旧清单废弃程序文件未清理'
        Need (-not (Test-Path -LiteralPath (Join-Path $dir 'legacy\old.dll'))) '旧清单废弃子目录程序文件未清理'
        Need (-not (Test-Path -LiteralPath (Join-Path $dir 'legacy'))) '旧程序文件清空后的空子目录未清理'
        Need ((Get-Content -LiteralPath (Join-Path $dir 'data\settings.json') -Raw).Trim() -eq 'test-only-private-data') '便携数据被改动'
        Need ((Get-Content -LiteralPath (Join-Path $dir 'legacy-user\keep.txt') -Raw).Trim() -eq 'test-only-user-nested') '用户子目录文件被改动'
        Need (Test-Path -LiteralPath (Join-Path $dir 'portable.txt')) '便携标记被删除'
        Need (Test-Path -LiteralPath (Join-Path $dir 'notes.txt')) '额外用户文件被删除'
        Need (Test-Path -LiteralPath (Join-Path $dir 'private.log')) '日志文件被删除'
    }
    Need (@(Get-ChildItem -LiteralPath $dist -Directory).Count -eq 2) '发布新增了版本目录或残留临时目录'
    Write-Output 'OK 原位覆盖、清理旧程序文件、解除实际占用、保留便携配置及额外文件'

    & (Join-Path $fixture 'tools\pack-release.ps1') -Version $version
    & (Join-Path $fixture 'tools\pack-release.ps1') -Version $version
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($flavour in 'fast','small') {
        $zip = Join-Path $dist "release\FlashTrans-$version-win-x64-$flavour.zip"
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName })
            Need ($names.Count -eq 2) '包内混入用户文件或辅助程序'
            Need ($names -contains "FlashTrans-1.8.0-win-x64-$flavour/FlashTrans.exe") '包内目录不是原发布目录'
        } finally { $archive.Dispose() }
    }
    Write-Output 'OK 同版本压缩包可覆盖、包内保留原目录名、不含用户数据'

    'tampered' | Set-Content -LiteralPath (Join-Path $fast 'payload.txt')
    $failed = $false
    try { & (Join-Path $fixture 'tools\pack-release.ps1') -Version $version } catch { $failed = $true }
    Need $failed '修改后的程序文件绕过了构建哈希检查'
    . (Join-Path $fixture 'tools\release-common.ps1')
    $failed = $false
    try { Assert-ReleasePath (Join-Path $fixture 'outside') $dist } catch { $failed = $true }
    Need $failed '路径越界未拒绝'
    Write-Output 'OK 构建哈希和目录越界保护'
} finally {
    Remove-Variable -Name FlashTransReleaseProbeFailBuild -Scope Global -ErrorAction SilentlyContinue
    if ($lockProcess -and -not $lockProcess.HasExited) { Stop-Process -Id $lockProcess.Id -Force }
    $safe = [IO.Path]::GetFullPath($fixture)
    if (-not $safe.StartsWith($tempRoot + '\FlashTrans-release-test-', [StringComparison]::OrdinalIgnoreCase)) { throw '测试清理路径越界' }
    if (Test-Path -LiteralPath $safe) { Remove-Item -LiteralPath $safe -Recurse -Force }
}
