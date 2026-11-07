param([string]$CliPath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = if ($CliPath) { [IO.Path]::GetFullPath($CliPath) } else { Join-Path $projectRoot 'src/Sevo.Mod.Cli/bin/Debug/net8.0/Sevo.Mod.Cli.exe' }
$evidence = Join-Path $projectRoot ('local-evidence/core-tests/cli-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($evidence)
$store = Join-Path $evidence 'store'
$game = Join-Path $evidence 'game'
[void][IO.Directory]::CreateDirectory((Join-Path $game 'BepInEx/plugins'))
[void][IO.Directory]::CreateDirectory((Join-Path $game 'Starship EVO_Data/Managed'))
[IO.File]::WriteAllText((Join-Path $game 'Starship EVO_Data/Managed/Assembly-CSharp.dll'), 'inert fixture assembly')
[IO.File]::WriteAllText((Join-Path $game 'Starship EVO.exe'), 'inert fixture, never launched')
$log = Join-Path $evidence 'commands.log'
function Invoke-Platform([string[]]$CommandArguments, [int]$ExpectedExit = 0) {
    $output = & $exe --root $store @CommandArguments 2>&1
    $code = $LASTEXITCODE
    Add-Content -LiteralPath $log -Value ('> ' + ($CommandArguments -join ' ') + ' [exit ' + $code + ']')
    Add-Content -LiteralPath $log -Value ($output -join [Environment]::NewLine)
    if ($code -ne $ExpectedExit) { throw ('CLI exit mismatch: ' + ($output -join ' ')) }
    if ($ExpectedExit -eq 0) { return ($output -join [Environment]::NewLine | ConvertFrom-Json) }
}
foreach ($version in @('1.0.0','2.0.0')) {
    $zipPath = Join-Path $evidence ($version + '.zip')
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = @{'sevo.mod.json' = ('{"schemaVersion":1,"id":"cli.sample","name":"CLI sample","version":"' + $version + '","payloadRoot":"payload"}'); 'payload/Mod.dll' = $version}
        foreach ($entry in $files.GetEnumerator()) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($entry.Key).Open())
            try { $writer.Write($entry.Value) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    $null = Invoke-Platform @('import', $zipPath)
}
$packages = Invoke-Platform @('packages')
if ($packages.Count -ne 2) { throw 'Expected two imported versions.' }
$profile = Invoke-Platform @('create-profile', 'CLI smoke', $game)
$null = Invoke-Platform @('profiles')
$null = Invoke-Platform @('install', $profile.id, 'cli.sample', '1.0.0')
$dll = Join-Path $game 'BepInEx/plugins/SevoManaged/cli.sample/Mod.dll'
if ([IO.File]::ReadAllText($dll) -ne '1.0.0') { throw 'Initial deployment mismatch.' }
$null = Invoke-Platform @('disable', $profile.id, 'cli.sample')
if (Test-Path -LiteralPath $dll) { throw 'Disabled DLL remains scanned.' }
$null = Invoke-Platform @('enable', $profile.id, 'cli.sample')
$null = Invoke-Platform @('pin', $profile.id, 'cli.sample')
$null = Invoke-Platform @('install', $profile.id, 'cli.sample', '2.0.0') 2
$null = Invoke-Platform @('unpin', $profile.id, 'cli.sample')
$null = Invoke-Platform @('install', $profile.id, 'cli.sample', '2.0.0')
$null = Invoke-Platform @('rollback', $profile.id, 'cli.sample')
if ([IO.File]::ReadAllText($dll) -ne '1.0.0') { throw 'Rollback deployment mismatch.' }
$issues = Invoke-Platform @('validate', $profile.id)
if ($issues.Count -gt 0) { throw 'Profile validation failed.' }
$null = Invoke-Platform @('remove', $profile.id, 'cli.sample')
if (Test-Path -LiteralPath $dll) { throw 'Removed DLL remains deployed.' }
'PASS CLI: all 13 commands plus pinned rejection and file outcomes; evidence ' + $evidence
