<#
.SYNOPSIS
    Builds PixelMan from a local checkout and makes the Avalonia PixelMan backend use it.

.DESCRIPTION
    Builds the native library for the current platform with cargo, packs Avalonia.PixelMan
    with it under a fresh version into the packages folder next to this script, and writes
    PixelMan.local.props, which Directory.Packages.props imports to select that version.
    Every run gets a new version, so NuGet never serves a stale build from its cache.

    This is how the PixelMan backend gets its package: the backend and its tests restore only
    from the packages folder, and fail to restore until this script has run once. The
    package holds the native library of the current platform only (plus the browser-wasm
    archive with -Browser); it is not for distribution.

.PARAMETER Browser
    Also builds the static library for browser-wasm, so browser apps that reference the
    package link PixelMan into dotnet.native.wasm. Needs the wasm32-unknown-emscripten Rust
    target and, in the consuming app, the .NET 11 browser workload (Emscripten 6): the
    Emscripten of the .NET 10 workload is too old for the objects Rust emits.

.PARAMETER PixelManPath
    The PixelMan checkout (its submodules initialized).

.EXAMPLE
    ./BuildLocal.ps1 -PixelManPath ../../../.claude/worktrees/pixelman-local
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PixelManPath,

    [switch] $Browser
)

$ErrorActionPreference = 'Stop'

$pixelMan = (Resolve-Path $PixelManPath).Path
$here = $PSScriptRoot
$packages = Join-Path $here 'packages'

$cargo = Get-Command cargo -ErrorAction SilentlyContinue
$cargoPath = if ($cargo) { $cargo.Source } else { Join-Path $HOME '.cargo/bin/cargo' }

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()

if ($IsWindows -or $env:OS -eq 'Windows_NT') {
    $rid = "win-$architecture"; $native = 'pixelman_ffi.dll'
}
elseif ($IsMacOS) {
    $rid = "osx-$architecture"; $native = 'libpixelman_ffi.dylib'
}
else {
    $rid = "linux-$architecture"; $native = 'libpixelman_ffi.so'
}

# PixelMan has no Directory.Build.props, Directory.Build.targets or Directory.Packages.props of
# its own, so MSBuild would look further up and apply those of a repository the checkout sits
# in (a worktree inside the Avalonia folder picks up Avalonia's). Empty boundary files stop
# the lookup; they stay out of PixelMan's git status.
$boundaries = @{
    'Directory.Build.props'   = '<Project />'
    'Directory.Build.targets' = '<Project />'
    'Directory.Packages.props' = '<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>'
}

$exclude = Join-Path (git -C $pixelMan rev-parse --git-common-dir) 'info/exclude'

foreach ($name in $boundaries.Keys) {
    $file = Join-Path $pixelMan $name

    if (-not (git -C $pixelMan ls-files $name)) {
        Set-Content $file $boundaries[$name] -Encoding utf8

        if (-not (Select-String -Path $exclude -SimpleMatch "/$name" -Quiet -ErrorAction SilentlyContinue)) {
            Add-Content $exclude "/$name"
        }
    }
}

Push-Location (Join-Path $pixelMan 'rust')
try {
    & $cargoPath build --release -p pixelman-ffi
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE." }

    if ($Browser) {
        # pixelman-ffi's cdylib output needs emcc on PATH for this target; the browser only
        # needs the archive, so build that crate type alone.
        & $cargoPath rustc --release -p pixelman-ffi --target wasm32-unknown-emscripten --crate-type staticlib
        if ($LASTEXITCODE -ne 0) {
            throw "cargo rustc for wasm32-unknown-emscripten failed with exit code $LASTEXITCODE. Install the target with 'rustup target add wasm32-unknown-emscripten' if it is missing."
        }
    }
}
finally {
    Pop-Location
}

$project = Join-Path $pixelMan 'managed/src/Avalonia.PixelMan'
$runtime = Join-Path $project "runtimes/$rid/native"

New-Item -ItemType Directory -Force $runtime | Out-Null
Copy-Item (Join-Path $pixelMan "rust/target/release/$native") $runtime -Force

$browserRuntime = Join-Path $project 'runtimes/browser-wasm/native'

if ($Browser) {
    # The archive is named after the LibraryImport name, which the browser link requires.
    New-Item -ItemType Directory -Force $browserRuntime | Out-Null
    Copy-Item (Join-Path $pixelMan 'rust/target/wasm32-unknown-emscripten/release/libpixelman_ffi.a') (Join-Path $browserRuntime 'pixelman_ffi.a') -Force
}
elseif (Test-Path (Split-Path $browserRuntime)) {
    # An archive from an earlier -Browser run would be packed as if it matched this build.
    Remove-Item -Recurse -Force (Split-Path $browserRuntime)
}

$version = '0.9.0-local.' + (Get-Date -Format 'yyyyMMddHHmmss')

New-Item -ItemType Directory -Force $packages | Out-Null

dotnet pack (Join-Path $project 'Avalonia.PixelMan.csproj') -c Release -p:IncludeNativeRuntimes=true "-p:Version=$version" -o $packages
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE." }

@"
<Project>
  <!-- Written by BuildLocal.ps1: the Avalonia PixelMan backend uses the local build $version. -->
  <PropertyGroup>
    <PixelManVersion>$version</PixelManVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $here 'PixelMan.local.props') -Encoding utf8

$rids = if ($Browser) { "$rid, browser-wasm" } else { $rid }
Write-Host "Avalonia.PixelMan $version ($rids) is selected."
