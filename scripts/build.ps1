param(
    [string]$DotnetPath = $env:LECTURE_COPILOT_DOTNET
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = & (Join-Path $PSScriptRoot 'Resolve-Dotnet.ps1') -DotnetPath $DotnetPath

Push-Location $repoRoot
try {
    & $dotnet restore 'LiveCaptionsTranslator.sln' --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    # WPF writes shared markup-compiler caches under obj. Keep solution builds
    # single-node and disable node reuse so interrupted builds do not leave a
    # worker holding LectureCopilot.Dev_MarkupCompile.cache.
    & $dotnet build 'LiveCaptionsTranslator.sln' `
        -c Release `
        --no-restore `
        -m:1 `
        -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
