<#
.SYNOPSIS
  Builds pax-media-manager-setup.exe: one file that installs PAX Media Manager for the current user.

.DESCRIPTION
  PAX Media Manager runs from a folder (its video player's native libraries must be real files), so the setup program
  carries that folder inside it: the app is published self-contained for win-x64 (no .NET needed on the PC), zipped,
  and built into Setup\ as its payload. The setup program is a single self-contained file too.
  It installs to %LOCALAPPDATA%\Programs\PAX Media Manager with Start menu (and optional desktop) shortcuts and an
  Apps & features entry, without admin rights. Silent: pax-media-manager-setup.exe --quiet [--dir <folder>].

  Output: %LOCALAPPDATA%\PaxMediaManager\installer\pax-media-manager-setup.exe (plus a .sha256 beside it).

.PARAMETER Version
  Optional version (e.g. 1.1.0). Defaults to the <Version> in Setup\PaxMediaManager.Setup.csproj.
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot '..\PaxMediaManager.csproj' | Resolve-Path
$setup = Join-Path $PSScriptRoot 'Setup\PaxMediaManager.Setup.csproj' | Resolve-Path
if (-not $Version) { $Version = ([xml](Get-Content $setup)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
# Built on this PC's own disk: the project lives on a NAS share whose clock drifts, so outputs there can look stale.
$stage = Join-Path $env:TEMP 'pax-media-manager-setup-build'
$outDir = Join-Path $env:LOCALAPPDATA 'PaxMediaManager\installer'
$out = Join-Path $outDir 'pax-media-manager-setup.exe'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, $outDir | Out-Null

Write-Host "1/3 Publishing PAX Media Manager $Version (self-contained, win-x64)…"
dotnet publish $app -c Release -r win-x64 --self-contained true -p:DebugType=none "-p:Version=$Version" -o (Join-Path $stage 'app') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed ($LASTEXITCODE)" }

Write-Host "2/3 Packing it…"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = Join-Path $stage 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $stage 'app'), $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "3/3 Building the setup program around it…"
dotnet publish $setup -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none "-p:Version=$Version" "-p:PayloadZip=$payload" -o (Join-Path $stage 'setup') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Building the setup program failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $stage 'setup\pax-media-manager-setup.exe') $out -Force
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  pax-media-manager-setup.exe" | Set-Content -Encoding ascii "$out.sha256"
Remove-Item $stage -Recurse -Force

Write-Host ("Built {0} ({1:N1} MB), version {2}" -f $out, ((Get-Item $out).Length / 1MB), $Version)
Write-Host "SHA-256 $hash"
