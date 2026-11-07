param([Parameter(Mandatory)][string]$KeelDirectory)
. (Join-Path $PSScriptRoot 'Environment.ps1')

$buildProps = [xml](Get-Content -LiteralPath (Join-Path $PlatformRoot 'Directory.Build.props') -Raw)
$keelVersion = $buildProps.Project.PropertyGroup.BundledKeelVersion
if ($keelVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Set BundledKeelVersion in Directory.Build.props.' }
$keel = (Resolve-Path -LiteralPath $KeelDirectory).Path
$keelChecksums = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $keel 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw 'Keel checksum list is invalid.' }
    $keelChecksums.Add($Matches[2], $Matches[1])
}
$keelCommit = (Get-Content -LiteralPath (Join-Path $keel 'SOURCE-COMMIT.txt') -Raw).Trim()
if ($keelCommit -notmatch '^[0-9a-f]{40}$') { throw 'Keel source commit is invalid.' }
$frameworkNames = @('Sevo.Framework.Runtime.dll', 'Sevo.Framework.Sdk.dll')
foreach ($name in $frameworkNames) {
    $dll = Join-Path $keel $name
    if ((Get-FileHash -LiteralPath $dll).Hash -ne $keelChecksums[$name]) {
        throw "Keel payload checksum mismatch for $name."
    }
    $actualVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -split '\+', 2)[0]
    if ($actualVersion -ne $keelVersion) {
        throw "Expected Starship Keel $keelVersion in $name but found $actualVersion."
    }
}

Push-Location -LiteralPath $PlatformRoot
try {
    $status = @(git status --porcelain)
    if ($LASTEXITCODE -or $status.Count) { throw 'Build the release from a clean Git checkout.' }
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Uplink source commit is unavailable.' }
    $launcher = 'src/Sevo.Mod.Launcher/Sevo.Mod.Launcher.csproj'
    $project = [xml](Get-Content -LiteralPath $launcher -Raw)
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Uplink project requires a stable three-part version.' }
    $output = Assert-PlatformChild (Join-Path $PlatformRoot "artifacts/releases/Starship-Uplink-$version-win-x64")
    if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output + '.zip')) -or
        (Test-Path -LiteralPath ($output + '.sha256.txt'))) {
        throw 'Release output already exists. Preserve it before building again.'
    }

    $publish = @('-c', 'Release', '--self-contained', 'false', '-p:DebugType=None', '--nologo', '-m:1', '-nr:false', '--disable-build-servers')
    & $PlatformDotnet publish $launcher @publish -o $output
    if ($LASTEXITCODE) { throw 'Uplink publish failed.' }
    & $PlatformDotnet publish 'src/Sevo.Mod.Cli/Sevo.Mod.Cli.csproj' @publish -o (Join-Path $output 'cli')
    if ($LASTEXITCODE) { throw 'Uplink CLI publish failed.' }
    $framework = Join-Path $output 'framework'
    New-Item -ItemType Directory -Path $framework | Out-Null
    foreach ($name in $frameworkNames) {
        Copy-Item -LiteralPath (Join-Path $keel $name) -Destination $framework
    }
    Copy-Item -LiteralPath (Join-Path $PlatformRoot 'README.md') -Destination $output
    $commit | Set-Content -LiteralPath (Join-Path $output 'SOURCE-COMMIT.txt') -Encoding utf8NoBOM
    @("Starship Keel $keelVersion", "https://github.com/MadTheDog/Starship-Keel/releases/tag/v$keelVersion", "Source commit $keelCommit") |
        Set-Content -LiteralPath (Join-Path $output 'KEEL-RELEASE.txt') -Encoding utf8NoBOM
    Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\','/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
    $archive = $output + '.zip'
    Compress-Archive -LiteralPath $output -DestinationPath $archive
    '{0}  {1}' -f (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant(), (Split-Path -Leaf $archive) |
        Set-Content -LiteralPath ($output + '.sha256.txt') -Encoding utf8NoBOM
    "Packaged $archive"
} finally { Pop-Location }
