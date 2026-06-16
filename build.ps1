#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build, pack, and deploy the ccpick global tool — the `ccp` command.

.DESCRIPTION
  Packs ccpick in Release into ./bin/<Configuration>, then ALWAYS reinstalls it
  as a .NET global tool from that local package. Every run deploys the freshly
  built `ccp` shim into ~/.dotnet/tools (%USERPROFILE%\.dotnet\tools on Windows),
  the folder on your PATH, so the `ccp` you invoke anywhere reflects this build.
  Run it from anywhere; it operates on its own folder.

.PARAMETER Configuration
  Build configuration. Default: Release.

.PARAMETER Clean
  Remove bin/ and obj/ before building (drops stale .nupkg versions too).

.EXAMPLE
  ./build.ps1              # pack, then deploy `ccp` to ~/.dotnet/tools
.EXAMPLE
  ./build.ps1 -Clean       # clean first, then pack + deploy
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$pkgId = 'ccpick'                                    # package id (the command is `ccp`)
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root "bin/$Configuration"

# Where `dotnet tool install --global` drops the launcher shim. $HOME resolves to
# %USERPROFILE% on Windows; the shim is ccp.exe there, bare `ccp` elsewhere.
$toolsDir = Join-Path $HOME '.dotnet/tools'
$shim     = Join-Path $toolsDir ($IsWindows ? 'ccp.exe' : 'ccp')

Push-Location $root
try {
    if ($Clean) {
        Write-Host '==> Cleaning bin/ and obj/' -ForegroundColor Cyan
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue bin, obj
    }

    Write-Host "==> Packing $pkgId ($Configuration)" -ForegroundColor Cyan
    dotnet pack -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed (exit $LASTEXITCODE)" }

    # Reinstall so `ccp` reflects this build. Uninstall first (unconditionally:
    # `dotnet tool install` no-ops when the version is unchanged, and `update`
    # from a local --add-source is finicky). A "not installed" failure here is
    # expected on a clean machine, so its exit code is intentionally ignored.
    Write-Host "==> Removing any existing global tool $pkgId" -ForegroundColor Cyan
    dotnet tool uninstall --global $pkgId 2>$null | Out-Null

    Write-Host "==> Installing $pkgId from $outDir" -ForegroundColor Cyan
    # --ignore-failed-sources: a machine-level nuget.config private feed may be
    # unreachable. We only need the local package, so don't let that abort install.
    dotnet tool install --global --add-source $outDir --ignore-failed-sources $pkgId
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool install failed (exit $LASTEXITCODE)" }

    # Confirm the shim actually landed in ~/.dotnet/tools — that's the deploy.
    if (-not (Test-Path -LiteralPath $shim)) {
        throw "install reported success but the shim is missing: $shim"
    }
    Write-Host "==> Deployed: $shim" -ForegroundColor Green
    Write-Host '==> Done. Run `ccp --help` to verify.' -ForegroundColor Green
}
finally {
    Pop-Location
}
