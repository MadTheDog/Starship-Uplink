$ErrorActionPreference = 'Stop'
$PlatformRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$env:DOTNET_ROOT = Join-Path $PlatformRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $PlatformRoot '.cache\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PlatformRoot '.cache\nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$PlatformDotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (!(Test-Path -LiteralPath $PlatformDotnet)) {
    $candidate = Get-Command dotnet -ErrorAction SilentlyContinue
    if (!$candidate) { throw 'Install a .NET 8 SDK, or provide the private .tools/dotnet toolchain. See docs/DEVELOPMENT.md.' }
    $PlatformDotnet = $candidate.Source
}

function Assert-PlatformChild([string]$Path, [string]$Parent = $PlatformRoot) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path must be beneath $Parent : $full" }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $entry = Get-Item -LiteralPath $current -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $current" }
        }
        $parentInfo = [IO.Directory]::GetParent($current)
        $current = if ($parentInfo) { $parentInfo.FullName } else { $null }
    }
    return $full
}
