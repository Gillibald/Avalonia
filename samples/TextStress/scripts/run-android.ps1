<#
.SYNOPSIS
Runs the TextStress scenarios on an Android device over adb in both text rasterization modes
and summarizes the results.

.DESCRIPTION
Builds and installs samples/TextStress.Android once, then runs every job once per render path,
variant, mode and pass, each in a fresh app process. Modes are interleaved and their order
alternates per pass (managed/backend, then backend/managed) so slow drift of the device does not
favour one mode. Before every run the script waits until the AP temperature is below
-CoolDownC (up to -CoolDownMaxSec); while a run measures, a background job samples the CPU
cluster and GPU clocks. Results go to <OutDir>/<render>/<job>-<variant>-<mode>-p<pass>.tsv, the
app's log lines next to them as .log; per-run thermals and clocks go to runs.txt (tab-separated), everything else
to run.log; summary.md is written at the end. The TSV schema is the desktop one, so summarize.py
and compare.py read the files unchanged.

The window holds the display at -Refresh Hz (the app requests the display mode itself, no
device setting changes); a run whose rate does not hold exits with code 3 and is reported as
failed.

Builds (-Build) are runtime configurations of the app: mono-paot (the SDK's Release default,
Mono with profiled AOT), mono-faot (full AOT), mono-jit (no AOT), coreclr (CoreCLR, experimental
in the .NET 10 Android SDK).

.EXAMPLE
pwsh samples/TextStress/scripts/run-android.ps1 -OutDir C:\results\android\mono-paot -Jobs list-fling,code-scroll `
    -Variants ([ordered]@{ default = ''; faces1 = '--faces 1' })
#>
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [ValidateSet('mono-paot', 'mono-faot', 'mono-jit', 'coreclr')] [string] $Build = 'mono-paot',
    [int] $Passes = 3,
    [string[]] $Renders = @('egl', 'vulkan'),
    [string[]] $Jobs = @('code-scroll', 'list-fling', 'mixed-ui'),
    [int] $Frames = 600,
    [int] $Warmup = 60,
    [int] $SweepFrames = 180,
    [int] $SweepWarmup = 30,
    [System.Collections.IDictionary] $Sweeps = @{
        'sweep-runs'   = '10,100,1000,10000'
        'sweep-glyphs' = '1000,10000'
        'sweep-states' = '64,1000'
    },
    [System.Collections.IDictionary] $Variants = $null,
    [int] $Refresh = 60,
    [double] $CoolDownC = 38,
    [int] $CoolDownMaxSec = 300,
    [int] $RunTimeoutSec = 600,
    [string] $Serial = $env:ANDROID_SERIAL,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
# Temperatures and clocks go to tab-separated files read by scripts; keep the decimal point.
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$project = Join-Path $repo 'samples\TextStress.Android\TextStress.Android.csproj'
$apk = Join-Path $repo 'samples\TextStress.Android\bin\Release\net10.0-android36.0\android-arm64\com.avalonia.textstress-Signed.apk'
$package = 'com.avalonia.textstress'
$deviceDir = "/storage/emulated/0/Android/data/$package/files"
$adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
if (-not (Test-Path $adb)) { $adb = 'adb' }

$buildProperties = @{
    'mono-paot' = @()
    'mono-faot' = @('-p:AndroidEnableProfiledAot=false')
    'mono-jit'  = @('-p:RunAOTCompilation=false', '-p:AndroidEnableProfiledAot=false')
    'coreclr'   = @('-p:UseMonoRuntime=false')
}

function Invoke-Adb {
    $arguments = if ($Serial) { @('-s', $Serial) + $args } else { $args }
    & $adb @arguments
}

function Get-Thermal {
    $text = (Invoke-Adb shell 'dumpsys thermalservice') -join "`n"
    $status = if ($text -match 'Thermal Status: (\d+)') { [int]$Matches[1] } else { -1 }
    $temps = @{}
    $cached = $text.Substring([Math]::Max(0, $text.IndexOf('Cached temperatures')))
    foreach ($m in [regex]::Matches($cached, 'mValue=([0-9.]+), mType=\d+, mName=(\w+)')) {
        if (-not $temps.ContainsKey($m.Groups[2].Value)) { $temps[$m.Groups[2].Value] = [double]$m.Groups[1].Value }
    }
    [pscustomobject]@{ Status = $status; Ap = $temps['AP']; Skin = $temps['SKIN']; Bat = $temps['BAT'] }
}

function Wait-CoolDown {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $thermal = Get-Thermal
    while (($thermal.Ap -ge $CoolDownC -or $thermal.Status -gt 0) -and $watch.Elapsed.TotalSeconds -lt $CoolDownMaxSec) {
        Start-Sleep -Seconds 5
        $thermal = Get-Thermal
    }
    [pscustomobject]@{ Thermal = $thermal; WaitedSec = [int]$watch.Elapsed.TotalSeconds }
}

# Samples the three CPU clusters (cpu0-3 A55, cpu4-6 A78, cpu7 X1 on Exynos 2100) and the GPU
# clock from the host, so nothing runs on the device besides the app.
$clockSampler = {
    param($adb, $serial, $path)
    $paths = '/sys/devices/system/cpu/cpufreq/policy0/scaling_cur_freq /sys/devices/system/cpu/cpufreq/policy4/scaling_cur_freq ' +
        '/sys/devices/system/cpu/cpufreq/policy7/scaling_cur_freq /sys/kernel/gpu/gpu_clock'
    $prefix = if ($serial) { @('-s', $serial) } else { @() }
    while ($true) {
        $values = (& $adb @prefix shell "cat $paths 2>/dev/null") -join ' '
        Add-Content -Path $path -Value $values
        Start-Sleep -Milliseconds 250
    }
}

function Measure-Clocks($path) {
    if (-not (Test-Path $path)) { return 'nan', 'nan', 'nan', 'nan' }
    $rows = Get-Content $path | ForEach-Object { , ($_ -split '\s+' | Where-Object { $_ }) } | Where-Object { $_.Count -ge 4 }
    if (-not $rows) { return 'nan', 'nan', 'nan', 'nan' }
    0..3 | ForEach-Object {
        $i = $_
        $values = $rows | ForEach-Object { [double]$_[$i] }
        $mean = ($values | Measure-Object -Average).Average
        # cpufreq and the Mali clock both report kHz.
        '{0:F0}' -f ($mean / 1000)
    }
}

if (-not $NoBuild) {
    dotnet build $project -c Release -nologo -v q @($buildProperties[$Build])
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

Invoke-Adb install -r $apk | Out-Null
if ($LASTEXITCODE -ne 0) { throw "install failed" }

$tag = (git -C $repo rev-parse --short HEAD).Trim()
$status = git -C $repo status --porcelain -- src samples/TextStress samples/TextStress.Android
if ($status) { $tag = "$tag+dirty" }
$tag = "$tag-$Build"

$variantNames = if ($Variants) { @($Variants.Keys) } else { @('default') }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'run.log'
$runs = Join-Path $OutDir 'runs.txt'
if (-not (Test-Path $runs)) {
    Set-Content $runs "render`tjob`tvariant`tmode`tpass`texit`twaited_s`tstatus_before`tap_before`tap_after`tstatus_after`tlittle_mhz`tmid_mhz`tbig_mhz`tgpu_mhz`tseconds"
}
$started = Get-Date
$failures = 0

for ($pass = 1; $pass -le $Passes; $pass++) {
    $modes = if ($pass % 2 -eq 1) { @('managed', 'backend') } else { @('backend', 'managed') }

    foreach ($render in $Renders) {
        $renderDir = Join-Path $OutDir $render
        New-Item -ItemType Directory -Force $renderDir | Out-Null

        foreach ($job in $Jobs) {
            foreach ($variant in $variantNames) {
                foreach ($mode in $modes) {
                    $name = "$job-$variant-$mode-p$pass"
                    $file = "$name.tsv"
                    $arguments = @('--scenario', $job, '--mode', $mode, '--render', $render, '--pass', $pass,
                        '--tag', $tag, '--refresh', $Refresh, '--out', $file)

                    if ($Variants -and $Variants[$variant]) {
                        $arguments += -split $Variants[$variant]
                    }

                    if ($Sweeps.Contains($job)) {
                        $arguments += @('--n', $Sweeps[$job], '--frames', $SweepFrames, '--warmup', $SweepWarmup)
                    }
                    else {
                        $arguments += @('--frames', $Frames, '--warmup', $Warmup)
                    }

                    $cool = Wait-CoolDown
                    $line = "{0:HH:mm:ss} pass {1} {2} {3} {4} {5} (waited {6} s, AP {7} C, status {8})" -f (Get-Date), $pass,
                        $render, $job, $variant, $mode, $cool.WaitedSec, $cool.Thermal.Ap, $cool.Thermal.Status
                    Write-Host $line
                    Add-Content $log $line

                    Invoke-Adb shell am force-stop $package | Out-Null
                    Invoke-Adb shell rm -f "$deviceDir/$file" "$deviceDir/$file.done" | Out-Null
                    Invoke-Adb logcat -c | Out-Null

                    $clockFile = Join-Path ([IO.Path]::GetTempPath()) "textstress-clocks-$PID.txt"
                    Remove-Item $clockFile -ErrorAction SilentlyContinue
                    $sampler = Start-Job -ScriptBlock $clockSampler -ArgumentList $adb, $Serial, $clockFile
                    $watch = [Diagnostics.Stopwatch]::StartNew()

                    Invoke-Adb shell am start -n "$package/.MainActivity" --es args "'$($arguments -join ' ')'" | Out-Null

                    $exit = 'timeout'
                    while ($watch.Elapsed.TotalSeconds -lt $RunTimeoutSec) {
                        $done = Invoke-Adb shell "cat $deviceDir/$file.done 2>/dev/null"
                        if ($done) { $exit = ($done -join '').Trim(); break }
                        Start-Sleep -Milliseconds 500
                    }

                    $seconds = [int]$watch.Elapsed.TotalSeconds
                    Stop-Job $sampler; Remove-Job $sampler -Force
                    $clocks = Measure-Clocks $clockFile
                    Remove-Item $clockFile -ErrorAction SilentlyContinue
                    $after = Get-Thermal

                    Invoke-Adb logcat -d -s 'TextStress:*' 'AndroidRuntime:E' | Set-Content (Join-Path $renderDir "$name.log")
                    if ($exit -eq '0') {
                        Invoke-Adb pull "$deviceDir/$file" (Join-Path $renderDir $file) | Out-Null
                    }
                    else {
                        $failures++
                        $failure = "  FAILED with exit $exit (3 = refresh rate did not hold)"
                        Write-Warning $failure
                        Add-Content $log $failure
                    }

                    Add-Content $runs (@($render, $job, $variant, $mode, $pass, $exit, $cool.WaitedSec, $cool.Thermal.Status,
                            $cool.Thermal.Ap, $after.Ap, $after.Status) + $clocks + @($seconds) -join "`t")
                }
            }
        }
    }
}

Invoke-Adb shell am force-stop $package | Out-Null
Add-Content $log ("finished after {0:N1} min, {1} failed runs" -f ((Get-Date) - $started).TotalMinutes, $failures)
python (Join-Path $PSScriptRoot 'summarize.py') $OutDir (Join-Path $OutDir 'summary.md')
