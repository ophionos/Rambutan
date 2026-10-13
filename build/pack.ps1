# Publishes the NativeAOT build and packs a Velopack release (F8.3).
# Usage: ./build/pack.ps1 [-Channel <name>] [-Rid win-x64|linux-x64] [-OutputDir <dir>]
# Channels are per OS (win, win-beta, linux, linux-beta); the default is the
# stable channel of the RID's OS, which is also Velopack's own default name.
param(
    [string]$Channel,
    [string]$Rid = "win-x64",
    [string]$OutputDir = "$PSScriptRoot/releases"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

# Version comes from Directory.Build.props — the single source of truth.
$props = [xml](Get-Content "$repo/Directory.Build.props")
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in Directory.Build.props" }

# VS 2026 quirk: vcvarsall resolves vswhere.exe from PATH; without the VS
# Installer dir the ilcompiler link step fails with a garbled MSB3073.
$vsInstaller = "C:\Program Files (x86)\Microsoft Visual Studio\Installer"
if ($env:OS -eq "Windows_NT" -and (Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) {
    $env:PATH += ";$vsInstaller"
}

$os = $Rid.Split("-")[0]
if (-not $Channel) { $Channel = $os }
$mainExe = if ($os -eq "win") { "Rambutan.App.exe" } else { "Rambutan.App" }

$publishDir = "$PSScriptRoot/publish/$Rid"
dotnet publish "$repo/src/Rambutan.App" -c Release -r $Rid -o $publishDir
if ($LASTEXITCODE) { exit $LASTEXITCODE }

vpk pack --packId Rambutan.Desktop --packTitle "Rambutan Music Player" `
    --packVersion $version --packDir $publishDir `
    --mainExe $mainExe --channel $Channel --outputDir $OutputDir
exit $LASTEXITCODE
