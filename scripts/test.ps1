param(
    [string]$DotnetPath = $env:LECTURE_COPILOT_DOTNET
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1') -DotnetPath $DotnetPath

Push-Location $repoRoot
try {
    & $dotnet test 'LiveCaptionsTranslator.sln' `
        -c Release `
        --no-restore `
        -m:1 `
        -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
