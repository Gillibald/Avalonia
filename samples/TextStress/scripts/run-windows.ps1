<#
.SYNOPSIS
Runs the TextStress scenarios and sweeps on Windows in both text rasterization modes and
summarizes the results.

.DESCRIPTION
Every scenario and sweep runs once per mode, per render path and per pass, each in its own
process. Modes are interleaved (managed/backend, then backend/managed on the next pass) so
slow drift of the machine does not favour one mode. Results go to
<OutDir>/<render>/<job>-<mode>-p<pass>.tsv; summary.md is written next to them.

Render paths are Win32PlatformOptions.RenderingMode values: angle (AngleEgl, the Win32
default, D3D11 through ANGLE with WinUI composition), wgl (desktop OpenGL on a redirection
surface), software (Skia raster into a framebuffer), vulkan.

.EXAMPLE
pwsh samples/TextStress/scripts/run-windows.ps1 -OutDir C:\results\text-stress\windows\2026-10-02
#>
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [int] $Passes = 3,
    [string[]] $Renders = @('angle', 'wgl', 'software'),
    [string[]] $Jobs = @('code-scroll', 'list-fling', 'mixed-ui', 'sweep-runs', 'sweep-glyphs', 'sweep-states'),
    [int] $Frames = 600,
    [int] $Warmup = 60,
    [int] $SweepFrames = 180,
    [int] $SweepWarmup = 30,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$project = Join-Path $repo 'samples\TextStress\TextStress.csproj'
$exe = Join-Path $repo 'samples\TextStress\bin\Release\net10.0\TextStress.exe'

if (-not $NoBuild) {
    dotnet build $project -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

$sweepValues = @{
    'sweep-runs'   = '10,32,100,316,1000,3162,10000,31623,100000'
    'sweep-glyphs' = '100,200,500,1000,2000,5000,10000,20000'
    'sweep-states' = '0,1,4,16,64,256,1000,2000'
}

$tag = (git -C $repo rev-parse --short HEAD).Trim()
$status = git -C $repo status --porcelain -- src samples/TextStress
if ($status) { $tag = "$tag+dirty" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir 'run.log'
$started = Get-Date

for ($pass = 1; $pass -le $Passes; $pass++) {
    $modes = if ($pass % 2 -eq 1) { @('managed', 'backend') } else { @('backend', 'managed') }

    foreach ($render in $Renders) {
        $renderDir = Join-Path $OutDir $render
        New-Item -ItemType Directory -Force $renderDir | Out-Null

        foreach ($job in $Jobs) {
            foreach ($mode in $modes) {
                $out = Join-Path $renderDir "$job-$mode-p$pass.tsv"
                $arguments = @('--scenario', $job, '--mode', $mode, '--render', $render, '--pass', $pass,
                    '--tag', $tag, '--out', $out)

                if ($sweepValues.ContainsKey($job)) {
                    $arguments += @('--n', $sweepValues[$job], '--frames', $SweepFrames, '--warmup', $SweepWarmup)
                }
                else {
                    $arguments += @('--frames', $Frames, '--warmup', $Warmup)
                }

                $line = "{0:HH:mm:ss} pass {1} {2} {3} {4}" -f (Get-Date), $pass, $render, $job, $mode
                Write-Host $line
                Add-Content $log $line

                & $exe @arguments 2>&1 | Tee-Object -FilePath $log -Append | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    $failure = "  FAILED with exit code $LASTEXITCODE"
                    Write-Warning $failure
                    Add-Content $log $failure
                }
            }
        }
    }
}

Add-Content $log ("finished after {0:N1} min" -f ((Get-Date) - $started).TotalMinutes)
python (Join-Path $PSScriptRoot 'summarize.py') $OutDir (Join-Path $OutDir 'summary.md')
