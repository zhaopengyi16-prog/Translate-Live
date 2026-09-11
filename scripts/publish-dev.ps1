param(
    [string]$DotnetPath = $env:LECTURE_COPILOT_DOTNET
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1') -DotnetPath $DotnetPath
$output = Join-Path $repoRoot 'artifacts\dev-win-x64'

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
}
finally {
    Pop-Location
}
