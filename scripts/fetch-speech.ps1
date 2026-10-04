$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $taskRoot 'artifacts/speech'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$sources = @(
    @{ Name='sensevoice.tar.bz2'; Url='https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17.tar.bz2'; Hash='7d1efa2138a65b0b488df37f8b89e3d91a60676e416f515b952358d83dfd347e' },
    @{ Name='linux.tar.bz2'; Url='https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/sherpa-onnx-v1.13.8-linux-x64-shared-no-tts.tar.bz2'; Hash='d0f96c8b65c6cd0974fada22737e337de81bc8cd2abbec2e39caf358b1eec5fc' },
    @{ Name='windows.tar.bz2'; Url='https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts.tar.bz2'; Hash='4b0a94f7b5c606b1b64a19a831c2127559e4b3d34e195465ebc7be73d9ed4783' },
    @{ Name='embedding.onnx'; Url='https://huggingface.co/Xenova/multilingual-e5-small/resolve/761b726dd34fb83930e26aab4e9ac3899aa1fa78/onnx/model_quantized.onnx'; Hash='f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193' },
    @{ Name='sentencepiece.bpe.model'; Url='https://huggingface.co/intfloat/multilingual-e5-small/resolve/fd1525a9fd15316a2d503bf26ab031a61d056e98/sentencepiece.bpe.model'; Hash='cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865' },
    @{ Name='silero-vad.onnx'; Url='https://raw.githubusercontent.com/snakers4/silero-vad/1e261b036686cd0017d500ee96acd1c4ba572a9d/src/silero_vad/data/silero_vad_16k_op15.onnx'; Hash='7ed98ddbad84ccac4cd0aeb3099049280713df825c610a8ed34543318f1b2c49' }
)
foreach ($source in $sources) {
    $path = Join-Path $assets $source.Name
    if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $source.Url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $source.Hash) { throw "资源校验失败：$($source.Name)" }
}
$payload = Join-Path $assets 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($name in @('sensevoice','linux','windows')) {
    $extract = Join-Path $assets $name
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    tar -xf (Join-Path $assets "$name.tar.bz2") -C $extract
    if ($LASTEXITCODE -ne 0) { throw "解压失败：$name" }
}
$model = Join-Path $assets 'sensevoice/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17'
Copy-Item -LiteralPath (Join-Path $model 'model.int8.onnx') -Destination (Join-Path $payload 'sensevoice.onnx') -Force
Copy-Item -LiteralPath (Join-Path $model 'tokens.txt') -Destination $payload -Force
foreach ($name in @('embedding.onnx','sentencepiece.bpe.model')) { Copy-Item -LiteralPath (Join-Path $assets $name) -Destination $payload -Force }
$linux = Join-Path $assets 'linux/sherpa-onnx-v1.13.8-linux-x64-shared-no-tts'
$windows = Join-Path $assets 'windows/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts'
New-Item -ItemType Directory -Path (Join-Path $payload 'linux-x64/bin'), (Join-Path $payload 'linux-x64/lib'), (Join-Path $payload 'win-x64') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $linux 'bin/sherpa-onnx-offline') -Destination (Join-Path $payload 'linux-x64/bin') -Force
Copy-Item -LiteralPath (Join-Path $linux 'lib/libonnxruntime.so') -Destination (Join-Path $payload 'linux-x64/lib') -Force
foreach ($name in @('sherpa-onnx-offline.exe','onnxruntime.dll','onnxruntime_providers_shared.dll')) {
    Copy-Item -LiteralPath (Join-Path $windows "bin/$name") -Destination (Join-Path $payload 'win-x64') -Force
}
$manifest = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[IO.File]::WriteAllText((Join-Path $assets 'manifest.json'), (ConvertTo-Json -InputObject $manifest -Depth 4), [Text.UTF8Encoding]::new($false))
[IO.Compression.ZipFile]::CreateFromDirectory($payload, (Join-Path $assets 'speech-assets.new.zip'), [IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath (Join-Path $assets 'speech-assets.new.zip') -Destination (Join-Path $assets 'speech-assets.zip') -Force
Write-Output 'CPU 识别和候选筛选资源校验通过'
