# 先完成构建，再覆盖既有目录；不按版本新建目录，不删除便携配置及额外用户文件。
param([ValidateSet('fast', 'small', 'both')][string]$Mode = 'both')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\FlashTrans\FlashTrans.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
$version = [string]($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '工程版本必须是 x.y.z。' }
$dist = Join-Path $root 'dist'
$flavours = if ($Mode -eq 'both') { @('fast', 'small') } else { @($Mode) }
$targets = @{}
foreach ($flavour in $flavours) { $targets[$flavour] = Get-ReleaseDirectory $dist $flavour }
$stage = Assert-ReleasePath (Join-Path $dist ('.publish-' + [guid]::NewGuid().ToString('N'))) $dist
$applied = [Collections.Generic.List[object]]::new()
$preserveStage = $false
try {
    foreach ($flavour in $flavours) {
        $output = Join-Path $stage $flavour
        Write-Host "=== 构建 FlashTrans $version ($flavour) ==="
        $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-o', $output, '--nologo', '-v', 'minimal', '-p:DebugType=none')
        if ($flavour -eq 'fast') {
            $publishArgs += @('--self-contained', 'true', '-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=false', '-p:PublishSingleFile=true')
        } else { $publishArgs += @('--self-contained', 'false') }
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "发布构建失败：$flavour，尚未覆盖原目录。" }
        $binary = Join-Path $output 'FlashTrans.exe'
        if ((Get-Item -LiteralPath $binary).VersionInfo.FileVersion -ne "$version.0") { throw "程序版本错误：$binary" }
        $files = @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
            $relative = $_.FullName.Substring($output.Length + 1)
            Assert-ReleaseFile $relative
            [pscustomobject]@{ Path = $relative; SHA256 = (Get-ReleaseHash $_.FullName) }
        })
        [pscustomobject]@{ Version = $version; Files = $files } | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $output '.flashtrans-release.json') -Encoding UTF8
    }

    # 构建全部成功后才关占用实例；此时原目录尚未改动。
    foreach ($flavour in $flavours) {
        $target = $targets[$flavour]
        Stop-ReleaseProcesses $target
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $stage $flavour) -File -Recurse -Force) {
            $relative = $file.FullName.Substring((Join-Path $stage $flavour).Length + 1)
            $destination = Assert-ReleasePath (Join-Path $target $relative) $dist
            if (Test-Path -LiteralPath $destination) {
                # 不结束其他目录的软件；仍有第三方占用时在覆盖前明确失败。
                $handle = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $handle.Dispose()
            }
        }
    }
    foreach ($flavour in $flavours) {
        $output = Join-Path $stage $flavour
        $target = $targets[$flavour]
        foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse -Force) {
            $relative = $file.FullName.Substring($output.Length + 1)
            $destination = Assert-ReleasePath (Join-Path $target $relative) $dist
            $backup = $null
            if (Test-Path -LiteralPath $destination) {
                $backup = Join-Path (Join-Path $stage "backup-$flavour") $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
                Copy-Item -LiteralPath $destination -Destination $backup
            }
            $applied.Add([pscustomobject]@{ Path = $destination; Backup = $backup })
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
        Write-Host "已原位更新为 $version：$target"
    }
} catch {
    $failure = $_
    foreach ($item in $applied) {
        try {
            $safe = Assert-ReleasePath $item.Path $dist
            if ($item.Backup) { Copy-Item -LiteralPath $item.Backup -Destination $safe -Force }
            elseif (Test-Path -LiteralPath $safe) { Remove-Item -LiteralPath $safe -Force }
        } catch { $preserveStage = $true; Write-Warning "恢复失败：$($item.Path)，备份保留在 $stage" }
    }
    throw $failure
} finally {
    if (-not $preserveStage -and (Test-Path -LiteralPath $stage)) {
        $safeStage = Assert-ReleasePath $stage $dist
        Remove-Item -LiteralPath $safeStage -Recurse -Force
    }
}
