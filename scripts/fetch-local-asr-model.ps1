param(
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$modelName = 'sherpa-onnx-streaming-zipformer-en-20M-2023-02-17'
$archiveUrl = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-streaming-zipformer-en-20M-2023-02-17.tar.bz2'
$archiveSha256 = '9C559283E8498D3FE95913C79CA1CB454BB26281AC2B102B41306C7D752765D9'

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repoRoot ".artifacts\$modelName"
}

$destinationPath = [IO.Path]::GetFullPath($Destination)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts'))
$downloadRoot = Join-Path $artifactRoot 'downloads'
$extractRoot = Join-Path $artifactRoot ('model-extract-' + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $downloadRoot "$modelName.tar.bz2"

New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

try {
    if (-not (Test-Path -LiteralPath $archivePath) -or
        (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $archiveSha256) {
        Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actualHash -ne $archiveSha256) {
        throw "Local ASR model archive hash mismatch: $actualHash"
    }

    & tar -xjf $archivePath -C $extractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Model extraction failed with exit code $LASTEXITCODE"
    }

    $sourceRoot = Join-Path $extractRoot $modelName
    $requiredFiles = @(
        'encoder-epoch-99-avg-1.int8.onnx',
        'decoder-epoch-99-avg-1.onnx',
        'joiner-epoch-99-avg-1.int8.onnx',
        'tokens.txt',
        'README.md'
    )
    foreach ($file in $requiredFiles) {
        $source = Join-Path $sourceRoot $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required model file is missing after extraction: $file"
        }
    }

    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    foreach ($file in $requiredFiles) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination (Join-Path $destinationPath $file) -Force
    }

    $validationSource = Join-Path $sourceRoot 'test_wavs'
    if (Test-Path -LiteralPath $validationSource -PathType Container) {
        $validationDestination = Join-Path $destinationPath 'validation-wavs'
        New-Item -ItemType Directory -Path $validationDestination -Force | Out-Null
        foreach ($file in @('0.wav', '1.wav', 'trans.txt')) {
            $source = Join-Path $validationSource $file
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                Copy-Item -LiteralPath $source -Destination (Join-Path $validationDestination $file) -Force
            }
        }
    }

    @"
Model: $modelName
Source: https://huggingface.co/csukuangfj/$modelName
Upstream model: https://huggingface.co/desh2608/icefall-asr-librispeech-pruned-transducer-stateless7-streaming-small
Model license: Apache License 2.0
Training corpus: LibriSpeech, CC BY 4.0
Archive SHA-256: $archiveSha256
This model is English-only and is distributed separately from source control.
"@ | Set-Content -LiteralPath (Join-Path $destinationPath 'MODEL-NOTICE.txt') -Encoding UTF8

    Write-Output $destinationPath
}
finally {
    $resolvedExtract = [IO.Path]::GetFullPath($extractRoot)
    if ($resolvedExtract.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedExtract)) {
        Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
    }
}
