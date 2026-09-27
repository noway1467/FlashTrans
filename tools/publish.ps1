# 版本号只从主工程读取；新版本输出到独立目录，绝不清空用户旧版本或便携数据。
param([ValidateSet('fast', 'small', 'both')][string]$Mode = 'both')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'src\FlashTrans\FlashTrans.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
$version = [string]($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '工程版本必须是 x.y.z。' }
$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
$flavours = if ($Mode -eq 'both') { @('fast', 'small') } else { @($Mode) }

# 两种产物先一起检查，避免第二个目录存在时只发布了一半。
foreach ($flavour in $flavours) {
    $output = [IO.Path]::GetFullPath((Join-Path $dist "FlashTrans-$version-win-x64-$flavour"))
    if (-not $output.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '输出路径越界。' }
    if (Test-Path -LiteralPath $output) { throw "输出目录已存在，未覆盖：$output。请先检查其中的便携数据，再手动移走或更换版本。" }
}
foreach ($flavour in $flavours) {
    $output = Join-Path $dist "FlashTrans-$version-win-x64-$flavour"
    Write-Host "=== 发布 FlashTrans-$version-win-x64-$flavour ==="
    $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-o', $output, '--nologo', '-v', 'minimal', '-p:DebugType=none')
    if ($flavour -eq 'fast') {
        $publishArgs += @('--self-contained', 'true', '-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=false', '-p:PublishSingleFile=true')
    } else { $publishArgs += @('--self-contained', 'false') }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$flavour。保留该目录便于检查，未删除或覆盖旧版本。" }
    $binary = Join-Path $output 'FlashTrans.exe'
    if ((Get-Item -LiteralPath $binary).VersionInfo.FileVersion -ne "$version.0") { throw "程序版本与目录名不一致：$binary" }
    Write-Host "已生成：$binary"
}
