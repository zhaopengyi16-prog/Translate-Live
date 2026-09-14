param(
    [string]$DotnetPath = $env:LECTURE_COPILOT_DOTNET,
    [string]$LocalAsrModelRoot = $env:TRANSLATE_LIVE_LOCAL_ASR_MODEL_ROOT,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1') -DotnetPath $DotnetPath
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot 'artifacts\dev-win-x64'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}

Push-Location $repoRoot
try {
    & $dotnet publish 'LiveCaptionsTranslator.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        -m:1 `
        -nodeReuse:false `
        -p:PublishSingleFile=false `
        -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        $source = Join-Path $repoRoot $notice
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $output $notice) -Force
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($LocalAsrModelRoot)) {
        $modelSource = [IO.Path]::GetFullPath($LocalAsrModelRoot)
        $modelName = 'sherpa-onnx-streaming-zipformer-en-20M-2023-02-17'
        $modelDestination = Join-Path $output "Models\$modelName"
        $requiredFiles = @(
            'encoder-epoch-99-avg-1.int8.onnx',
            'decoder-epoch-99-avg-1.onnx',
            'joiner-epoch-99-avg-1.int8.onnx',
            'tokens.txt',
            'MODEL-NOTICE.txt'
        )
        foreach ($file in $requiredFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $modelSource $file) -PathType Leaf)) {
                throw "Local ASR model is incomplete: $file"
            }
        }
        New-Item -ItemType Directory -Path $modelDestination -Force | Out-Null
        foreach ($file in $requiredFiles) {
            Copy-Item -LiteralPath (Join-Path $modelSource $file) -Destination $modelDestination -Force
        }
    }
}
finally {
    Pop-Location
}
