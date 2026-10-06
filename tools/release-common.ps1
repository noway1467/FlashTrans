# 发布和压缩共用同一套路径规则，避免升级后覆盖、打包指向不同目录。
function Get-ReleaseHash([string]$Path) {
    # 直接用 .NET，避免 Windows PowerShell 模块自动加载不可用时发布到一半才失败。
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Assert-ReleasePath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布路径越界：$full"
    }
    $current = $full
    while ($current.Length -ge $base.Length) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "发布路径含链接，停止：$current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Get-ReleaseDirectory([string]$Dist, [string]$Flavour) {
    $existing = @(if (Test-Path -LiteralPath $Dist) {
        Get-ChildItem -LiteralPath $Dist -Directory | Where-Object {
            $_.Name -match "^FlashTrans(?:-\d+\.\d+\.\d+)?-win-x64-$Flavour`$"
        }
    })
    if ($existing.Count -gt 1) { throw "发现多个 $Flavour 发布目录，请先明确保留哪个；不会自行选择或新增目录。" }
    $path = if ($existing.Count -eq 1) { $existing[0].FullName } else { Join-Path $Dist "FlashTrans-win-x64-$Flavour" }
    return Assert-ReleasePath $path $Dist
}

function Stop-ReleaseProcesses([string]$Directory) {
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($process in Get-Process) {
        try { $path = $process.Path } catch { continue }
        if (-not $path -or -not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        Write-Host "解除发布目录占用：$($process.ProcessName)（PID $($process.Id)）"
        try {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(1500)) {
                # 强制结束前重新核对 PID 和可执行文件路径，防止 PID 已被其他进程复用。
                $live = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
                if ($live -and $live.Path -eq $path) { Stop-Process -Id $live.Id -Force }
                if (-not $process.WaitForExit(5000)) { throw '进程未退出' }
            }
        } catch {
            if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) { throw "无法解除占用：$path。$($_.Exception.Message)" }
        }
    }
}

function Assert-ReleaseFile([string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)' -or
        $Relative -match '(^|[\\/])(data|portable\.txt|settings\.json|document-history\.json)([\\/]|$)' -or
        $Relative.EndsWith('.log', [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布清单包含非程序文件：$Relative"
    }
}
