[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$GameRoot)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameRoot).Path
$versionFile = Join-Path $game 'BeatSaberVersion.txt'
if ((Get-Content -LiteralPath $versionFile -Raw).Trim() -ne '1.45.2_28848') {
    throw 'Expected Beat Saber 1.45.2_28848'
}
$sharedProject = Join-Path $root 'vendor/BeatSage/scripts/shared_song_import/SongPackageIO.csproj'
if (-not (Test-Path -LiteralPath $sharedProject)) {
    throw 'Initialize the BeatSage submodule with git submodule update --init'
}
$inputRoot = Join-Path $root '.tmp/game-refs'
foreach ($directory in @('Beat Saber_Data/Managed', 'Plugins', 'Libs')) {
    $destination = Join-Path $inputRoot $directory
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $game "$directory/*.dll") -Destination $destination -Force
}
$sharedObj = Join-Path $root '.tmp/shared-obj/'
$sharedOut = Join-Path $root '.tmp/shared/'
$common = @("-p:GameRoot=$inputRoot", "-p:BeatSaberDir=$inputRoot", "-p:GameDirectory=$inputRoot",
    '-p:ImportDirectoryBuildTargets=False', '-p:DisableCopyToGame=True',
    '-p:DisableCopyToPlugins=True', '-p:DisableZipRelease=True')
dotnet build $sharedProject -c Release @common "-p:RepoRoot=$root" `
    "-p:BaseIntermediateOutputPath=$sharedObj" "-p:OutputPath=$sharedOut"
if ($LASTEXITCODE -ne 0) { throw "SongPackageIO build failed ($LASTEXITCODE)" }
$sharedDll = Join-Path $sharedOut 'SongPackageIO.dll'
if (-not (Test-Path -LiteralPath $sharedDll)) { throw 'SongPackageIO output is missing' }
$obj = Join-Path $root '.tmp/obj/'
$out = Join-Path $root '.tmp/out/'
dotnet build (Join-Path $root 'BetterSongSearch.csproj') -c Release @common `
    "-p:SongPackageIOPath=$sharedDll" "-p:BaseIntermediateOutputPath=$obj" "-p:OutputPath=$out"
if ($LASTEXITCODE -ne 0) { throw "BetterSongSearch build failed ($LASTEXITCODE)" }
