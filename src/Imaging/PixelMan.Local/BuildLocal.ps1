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
    target, Python, and the .NET 10 browser workload (wasm-tools-net10), whose Emscripten
    3.1.56 rewrites a second archive for net8.0 to net10.0 apps (Emscripten 3); net11.0 apps
    link the archive Rust writes with the .NET 11 workload's Emscripten 6.

.PARAMETER Android
    Also builds the shared library for android-arm64, so Android apps that reference the
    package load it. Needs the aarch64-linux-android Rust target and an Android NDK, found
    through ANDROID_NDK_ROOT or the newest one under the Android SDK (ANDROID_HOME, or the
    default SDK folder).

.PARAMETER PixelManPath
    The PixelMan checkout (its submodules initialized).

.EXAMPLE
    ./BuildLocal.ps1 -PixelManPath ../../../.claude/worktrees/pixelman-local
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PixelManPath,

    [switch] $Browser,

    [switch] $Android
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

$exclude = Join-Path (git -C $pixelMan rev-parse --path-format=absolute --git-common-dir) 'info/exclude'

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

    if ($Android) {
        $ndk = $env:ANDROID_NDK_ROOT
        if (-not $ndk) {
            $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($IsMacOS) { Join-Path $HOME 'Library/Android/sdk' } elseif ($IsLinux) { Join-Path $HOME 'Android/Sdk' } else { Join-Path $env:LOCALAPPDATA 'Android/Sdk' }
            $ndk = Get-ChildItem (Join-Path $sdk 'ndk') -Directory -ErrorAction SilentlyContinue | Sort-Object { [version]$_.Name } | Select-Object -Last 1 -ExpandProperty FullName
        }
        if (-not $ndk) { throw 'No Android NDK found; set ANDROID_NDK_ROOT.' }

        $ndkHost = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'windows-x86_64' } elseif ($IsMacOS) { 'darwin-x86_64' } else { 'linux-x86_64' }
        $bin = Join-Path $ndk "toolchains/llvm/prebuilt/$ndkHost/bin"
        $clang = Get-ChildItem $bin -Filter 'clang*' | Where-Object { $_.BaseName -eq 'clang' } | Select-Object -First 1 -ExpandProperty FullName
        $ar = Get-ChildItem $bin -Filter 'llvm-ar*' | Select-Object -First 1 -ExpandProperty FullName

        # The NDK's per-API .cmd wrappers break on paths with spaces, so clang gets the target
        # triple itself. Android 15 needs 16 KB page alignment.
        $env:CC_aarch64_linux_android = $clang
        $env:CFLAGS_aarch64_linux_android = '--target=aarch64-linux-android24'
        $env:AR_aarch64_linux_android = $ar
        $env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER = $clang
        $env:CARGO_TARGET_AARCH64_LINUX_ANDROID_RUSTFLAGS = '-C link-arg=--target=aarch64-linux-android24 -C link-arg=-Wl,-z,max-page-size=16384'

        & $cargoPath build --release -p pixelman-ffi --target aarch64-linux-android
        if ($LASTEXITCODE -ne 0) {
            throw "cargo build for aarch64-linux-android failed with exit code $LASTEXITCODE. Install the target with 'rustup target add aarch64-linux-android' if it is missing."
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
    $archive = Join-Path $pixelMan 'rust/target/wasm32-unknown-emscripten/release/libpixelman_ffi.a'
    Copy-Item $archive (Join-Path $browserRuntime 'pixelman_ffi.a') -Force

    # .NET 8 to 10 apps link with Emscripten 3, which needs the archive rewritten; Emscripten
    # 3.1.56's llvm-ar from the .NET 10 browser workload (wasm-tools-net10) writes its index.
    $packs = Join-Path (Split-Path (Get-Command dotnet).Source) 'packs'
    $ar = Get-ChildItem (Join-Path $packs 'Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.*') -Directory -ErrorAction SilentlyContinue |
        Get-ChildItem -Directory | Sort-Object { [version]$_.Name } |
        ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'tools/bin') -Filter 'llvm-ar*' -ErrorAction SilentlyContinue } |
        Select-Object -Last 1 -ExpandProperty FullName
    $emscripten3 = Join-Path $browserRuntime 'emscripten3'
    if ($ar) {
        New-Item -ItemType Directory -Force $emscripten3 | Out-Null
        & python (Join-Path $pixelMan 'scripts/emscripten3-archive.py') $archive (Join-Path $emscripten3 'pixelman_ffi.a') --ar $ar
        if ($LASTEXITCODE -ne 0) { throw "emscripten3-archive.py failed with exit code $LASTEXITCODE." }
    }
    else {
        Write-Warning 'No Emscripten 3.1.56 found (dotnet workload install wasm-tools-net10); the package will not link in net8.0 to net10.0 browser apps.'
        if (Test-Path $emscripten3) { Remove-Item -Recurse -Force $emscripten3 }
    }
}
elseif (Test-Path (Split-Path $browserRuntime)) {
    # An archive from an earlier -Browser run would be packed as if it matched this build.
    Remove-Item -Recurse -Force (Split-Path $browserRuntime)
}

$androidRuntime = Join-Path $project 'runtimes/android-arm64/native'

if ($Android) {
    New-Item -ItemType Directory -Force $androidRuntime | Out-Null
    Copy-Item (Join-Path $pixelMan 'rust/target/aarch64-linux-android/release/libpixelman_ffi.so') $androidRuntime -Force
}
elseif (Test-Path (Split-Path $androidRuntime)) {
    # A library from an earlier -Android run would be packed as if it matched this build.
    Remove-Item -Recurse -Force (Split-Path $androidRuntime)
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

$rids = (@($rid) + @(if ($Browser) { 'browser-wasm' }) + @(if ($Android) { 'android-arm64' })) -join ', '
Write-Host "Avalonia.PixelMan $version ($rids) is selected."
