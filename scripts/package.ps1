$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'fetch-speech.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src/Jellyfin.Plugin.SubtitlesTool/Jellyfin.Plugin.SubtitlesTool.csproj'
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw '插件版本格式无效' }
dotnet build $projectFile -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw '插件构建失败' }
$artifactDirectory = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
$archivePath = Join-Path $artifactDirectory "jav-jellyfin-subtitles-tool_$version.zip"
$outputDirectory = Join-Path $projectRoot 'src/Jellyfin.Plugin.SubtitlesTool/bin/Release/net9.0'
$files = @('Jellyfin.Plugin.SubtitlesTool.dll', 'Jellyfin.Plugin.SubtitlesTool.deps.json', 'AngleSharp.dll', 'UtfUnknown.dll') | ForEach-Object { Join-Path $outputDirectory $_ }
$files += @(Join-Path $outputDirectory 'Microsoft.ML.OnnxRuntime.dll')
$files += @(Join-Path $outputDirectory 'System.Numerics.Tensors.dll')
$files += @(Join-Path $outputDirectory 'Microsoft.ML.Tokenizers.dll'), @(Join-Path $outputDirectory 'Google.Protobuf.dll')
$files += @(Join-Path $projectRoot 'artifacts/speech/speech-assets.zip')
$files += @((Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'), (Join-Path $projectRoot 'third-party'))
Compress-Archive -LiteralPath $files -DestinationPath $archivePath -Force
$entry = @{
    guid = 'c4b75732-8527-4f58-9cdf-18efca21a9e5'
    name = 'JAV Subtitles Tool'
    description = '聚合迅雷与 SubtitleCat，按番号和分段搜索字幕，支持下载替换及字幕校准。'
    overview = '预览后校准当前唯一字幕，视频始终只读。'
    owner = 'yexi-by'
    category = 'Metadata'
    versions = @(@{
        version = $version
        changelog = 'LLM 设置支持自定义 HTTP 请求头，随分析及重试发送 x-opencode-session、User-Agent 等字段，可连接 OpenCode Go。保留关闭思考的自定义参数，改善设置页多行输入框样式。'
        targetAbi = '10.11.11.0'
        sourceUrl = "https://github.com/yexi-by/jav-jellyfin-subtitles-tool/releases/download/v$version/jav-jellyfin-subtitles-tool_$version.zip"
        checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm MD5).Hash.ToUpperInvariant()
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    })
}
$manifest = ConvertTo-Json -InputObject @($entry) -Depth 8
[IO.File]::WriteAllText((Join-Path $artifactDirectory 'manifest.json'), $manifest + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "安装包：$archivePath"
