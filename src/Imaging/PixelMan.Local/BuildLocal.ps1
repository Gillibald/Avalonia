<#
.SYNOPSIS
    Builds PixelMan and makes the Avalonia PixelMan backend use it, through the PixelManLocal build target.

.DESCRIPTION
    The work is done by `./build.sh PixelManLocal` (`build.cmd PixelManLocal` on Windows), which
    needs no PowerShell; this script only forwards to it. See nukebuild/Build.Imaging.cs.

.PARAMETER PixelManPath
    A PixelMan checkout (its submodules initialized). Without it the target clones PixelMan at the
    commit in PixelMan.commit.

.PARAMETER Browser
    Also builds the static library for browser-wasm.

.PARAMETER Android
    Also builds the shared library for android-arm64.

.EXAMPLE
    ./BuildLocal.ps1 -PixelManPath ../../../.claude/worktrees/pixelman-local
#>
param(
    [string] $PixelManPath,
    [switch] $Browser,
    [switch] $Android
)

$ErrorActionPreference = 'Stop'

$arguments = @('PixelManLocal')
if ($PixelManPath) { $arguments += @('--pixelman', (Resolve-Path $PixelManPath).Path) }
if ($Browser) { $arguments += '--pixelman-browser' }
if ($Android) { $arguments += '--pixelman-android' }

& (Join-Path $PSScriptRoot '../../../build.ps1') @arguments
if ($LASTEXITCODE -ne 0) { throw "PixelManLocal failed with exit code $LASTEXITCODE." }
