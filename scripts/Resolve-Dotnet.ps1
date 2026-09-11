param(
    [string]$DotnetPath = $env:LECTURE_COPILOT_DOTNET
)

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $projectLocalDotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $projectLocalDotnet) {
        $DotnetPath = $projectLocalDotnet
    }
}

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $cursor = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $cursor -and $cursor.Name -ne 'Codex') {
        $cursor = $cursor.Parent
    }

    if ($null -ne $cursor) {
        $DotnetPath = Join-Path $cursor.FullName 'Tools\dotnet-sdk-10\dotnet.exe'
    }
}

if ([string]::IsNullOrWhiteSpace($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath)) {
    $systemDotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if ($null -ne $systemDotnet) {
        $DotnetPath = $systemDotnet.Source
    }
}

if (-not (Test-Path -LiteralPath $DotnetPath)) {
    throw "Translate Live .NET SDK was not found. Set LECTURE_COPILOT_DOTNET or pass -DotnetPath."
}

return (Resolve-Path -LiteralPath $DotnetPath).Path
