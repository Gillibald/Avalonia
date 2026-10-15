<#
.SYNOPSIS
Runs a TextStress campaign: the scenes a change can affect, base against tip, interleaved, and
summarizes it with keep-rule verdicts.

.DESCRIPTION
A campaign compares two variants, base and tip. Each is a build plus extra arguments:

- Two commits (-Base, optionally -Tip): each is checked out into a worktree under the repository's
  .claude/worktrees (ts-<commit>, reused when it is already there) and built in Release. Without
  -Tip the tip is this checkout as it is, built in place.
- One build with a switch (-BaseArgs, no -Base): base runs the tip build with -BaseArgs added, for
  switches such as --motion-budget-us 0 or --mode backend. One build means no build-order drift and
  half the build time; prefer it whenever a switch reproduces the base.
- Neither: only the tip runs, for a measurement without a comparison.

Every run is its own TextStress process and no two ever run at once: before each run the script
waits until no other TextStress or test process runs and the CPU load stays under -IdleCpu.
Loop order is pass, render path, job, variant; the variant order flips on even passes so slow drift
of the machine favours neither side. Each run gets --label base|tip (the status strip's colour),
--job, --preset, --progress i/n and --eta.

Scene sets (-Set) name the scenes a code area can affect; a change touching several areas takes
several sets. Each set has default render paths (-Renders overrides them) and
frame presets per job (-Frames overrides them):

  batching    atlas, batching, clips: list-fling, mixed-ui, sweep-states mixed n 64/1000,
              sweep-glyphs n 1000 static; angle, vulkan, wgl
  motion      motion policy: Inter zoom and rotate (400 glyphs), mixed12 zoom and rotate, mixed48
              zoom, CJK zoom n 1000; angle
  colour      colour glyphs: grid static and churn (TextBlock and glyph runs), emoji list fling; angle, vulkan
  memory      glyph cache budget and eviction: CJK sawtooth and monotonic, mixed48 sawtooth and
              monotonic, 1800 frames; angle
  regression  broad check: list-fling, code-scroll, mixed-ui; angle
  cpu         CPU raster blitters and SIMD: code-scroll, list-fling; software

Frame presets: steady = 300 measured frames after 300 warm-up frames, first = 180 frames without
warm-up (cold frames, first gestures), long = 1800 frames without warm-up (memory growth).
-CountersOnly runs one pass of every job (counters are deterministic) and judges counters only.

Results go to <OutDir>/<render>/<variant>.<job>-p<pass>.tsv with the run's console output in a .log
next to it, the campaign log to <OutDir>/run.log and the summary to <OutDir>/summary.md. Phone runs
(-Phone) go through run-android.ps1, one call per variant and scenario, and land in <OutDir>/phone-<render>/ in the
same layout.

Machine specifics are parameters with today's defaults: -BuildProperties (the SDK 10.0.402
NU1603 workaround), -Serial (the Galaxy S21 Ultra), -IdleCpu.

.EXAMPLE
# A policy change measured with its switch in one build: motion scenes on ANGLE and WGL, two passes.
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\budget -Set motion -Renders angle,wgl `
    -BaseArgs '--motion-budget-us 0'

.EXAMPLE
# Two commits, counters first: one pass of the batching scenes, judged on counters only.
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\batch-counters -Set batching -Base 008a8e0f8b -CountersOnly

.EXAMPLE
# The same scenes on the phone (CoreCLR), desktop runs skipped.
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\motion-phone -Set motion -Base 008a8e0f8b -Phone -NoDesktop

.EXAMPLE
# A milestone: every set on every desktop path and the phone, three passes.
pwsh samples/TextStress/scripts/campaign.ps1 -OutDir C:\results\milestone -Set batching,motion,colour,memory,regression `
    -Base 008a8e0f8b -Renders angle,wgl,vulkan,software -Passes 3 -Phone
#>
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [ValidateSet('batching', 'motion', 'colour', 'memory', 'regression', 'cpu')] [string[]] $Set = @(),
    # Extra jobs, name = command line without --frames/--warmup, run with the steady frame preset.
    [System.Collections.IDictionary] $Jobs = $null,
    [string] $Base = '',
    [string] $Tip = '',
    [string] $BaseArgs = '',
    [string] $TipArgs = '',
    [string] $Mode = 'managed',
    [ValidateSet('angle', 'wgl', 'vulkan', 'software')] [string[]] $Renders = @(),
    [int] $Passes = 2,
    [ValidateSet('', 'steady', 'first', 'long')] [string] $Frames = '',
    [switch] $CountersOnly,
    [ValidateSet('desktop', 'phone')] [string] $Preset = 'desktop',
    # A content area of its own, WIDTHxHEIGHT in DIP, instead of the preset's; for comparing with
    # builds that predate the presets (1280x800 matches their window).
    [string] $ContentSize = '',
    [switch] $Phone,
    [switch] $NoDesktop,
    [ValidateSet('mono-paot', 'mono-faot', 'mono-jit', 'coreclr')] [string] $PhoneBuild = 'coreclr',
    [string[]] $PhoneRenders = @('egl'),
    [string] $Serial = 'R5CR61DXTLA',
    [string[]] $BuildProperties = @('-p:WarningsNotAsErrors=NU1603'),
    [switch] $NoBuild,
    [int] $IdleCpu = 25,
    # Removes the worktrees this campaign created once it has finished.
    [switch] $RemoveWorktrees,
    # Prints the runs without building or running anything.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
# Worktrees belong to the checkout the work started in, also when this script runs from one of its
# .claude/worktrees checkouts.
$owner = $repo -replace '\\\.claude\\worktrees\\.*$', ''
$worktrees = Join-Path $owner '.claude\worktrees'
$exeRelative = 'samples\TextStress\bin\Release\net10.0\TextStress.exe'
$apkRelative = 'samples\TextStress.Android\bin\Release\net10.0-android36.0\android-arm64\com.avalonia.textstress-Signed.apk'

$framePresets = @{
    steady = '--frames 300 --warmup 300'
    first  = '--frames 180 --warmup 0'
    long   = '--frames 1800 --warmup 0'
}

# name = command line, frame preset. Scene parameters follow the change-to-scene map.
$sets = @{
    batching   = [ordered]@{
        'list-fling'    = 'list-fling', 'steady'
        'mixed-ui'      = 'mixed-ui', 'steady'
        'states-mixed'  = 'sweep-states --kind mixed --n 64,1000', 'steady'
        'glyphs-static' = 'sweep-glyphs --n 1000', 'steady'
    }
    motion     = [ordered]@{
        'inter-zoom'     = 'sweep-glyphs --n 400 --zoom 60 --zoom-step 0.01 --zoom-font inter --motion zoom', 'steady'
        'inter-rotate'   = 'sweep-glyphs --n 400 --zoom 60 --zoom-font inter --motion rotate', 'steady'
        'mixed12-zoom'   = 'mixed-ui --cards 12 --changes 0 --motion zoom', 'steady'
        'mixed12-rotate' = 'mixed-ui --cards 12 --changes 0 --motion rotate', 'steady'
        'mixed48-zoom'   = 'mixed-ui --cards 48 --changes 0 --motion zoom', 'steady'
        'cjk-zoom'       = 'sweep-glyphs --n 1000 --zoom 60 --zoom-step 0.01', 'steady'
    }
    colour     = [ordered]@{
        'color-tb-static' = 'color-glyphs --layout grid --motion static', 'steady'
        'color-tb-churn'  = 'color-glyphs --layout grid --motion churn', 'steady'
        'color-gr-static' = 'color-glyphs --layout grid --motion static --draw glyphrun', 'steady'
        'color-gr-churn'  = 'color-glyphs --layout grid --motion churn --draw glyphrun', 'steady'
        'color-list'      = 'color-glyphs --layout list --motion fling', 'steady'
    }
    memory     = [ordered]@{
        'cjk-saw'      = 'sweep-glyphs --n 1000 --zoom 60 --zoom-step 0.01', 'long'
        'cjk-mono'     = 'sweep-glyphs --n 1000 --zoom 1800 --zoom-step 0.002', 'long'
        'mixed48-saw'  = 'mixed-ui --cards 48 --changes 0 --motion zoom', 'long'
        'mixed48-mono' = 'mixed-ui --cards 48 --changes 0 --motion zoom --zoom-period 400', 'long'
    }
    regression = [ordered]@{
        'list-fling'  = 'list-fling', 'steady'
        'code-scroll' = 'code-scroll', 'steady'
        'mixed-ui'    = 'mixed-ui', 'steady'
    }
    cpu        = [ordered]@{
        'code-scroll' = 'code-scroll', 'steady'
        'list-fling'  = 'list-fling', 'steady'
    }
}

$setRenders = @{
    batching = @('angle', 'vulkan', 'wgl'); motion = @('angle'); colour = @('angle', 'vulkan'); memory = @('angle')
    regression = @('angle'); cpu = @('software')
}

$allJobs = [ordered]@{}
$defaultRenders = [Collections.Generic.List[string]]::new()
foreach ($name in $Set) {
    foreach ($job in $sets[$name].Keys) { $allJobs[$job] = $sets[$name][$job] }
    foreach ($r in $setRenders[$name]) { if (-not $defaultRenders.Contains($r)) { $defaultRenders.Add($r) } }
}
if ($Jobs) { foreach ($job in $Jobs.Keys) { $allJobs[$job] = $Jobs[$job], 'steady' } }
if ($allJobs.Count -eq 0) { throw 'Nothing to run: pass -Set and/or -Jobs.' }
if ($Renders.Count -eq 0) { $Renders = if ($defaultRenders.Count) { @($defaultRenders) } else { @('angle') } }
if ($NoDesktop) { $Renders = @() }
if ($CountersOnly) { $Passes = 1 }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$log = Join-Path $OutDir 'run.log'

function Write-Log([string] $line) {
    $stamped = '{0:HH:mm:ss} {1}' -f (Get-Date), $line
    Write-Host $stamped
    Add-Content -LiteralPath $log $stamped
}

$createdWorktrees = [Collections.Generic.List[string]]::new()

# A checkout of $commit under .claude/worktrees, reused when it is already there.
function Get-Checkout([string] $commit) {
    $sha = (git -C $repo rev-parse --verify "$commit^{commit}").Trim()
    if ($LASTEXITCODE -ne 0) { throw "unknown commit '$commit'" }
    $path = Join-Path $worktrees ('ts-' + $sha.Substring(0, 10))
    if (Test-Path $path) {
        $head = (git -C $path rev-parse HEAD).Trim()
        if ($head -ne $sha) { throw "$path holds $head, not $sha; remove it first" }
        return $path
    }
    Write-Log "worktree $path at $sha"
    $output = git -C $repo worktree add --detach $path $sha 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed for ${commit}: $output" }
    git -C $path submodule update --init --recursive 2>&1 | Out-Null
    $createdWorktrees.Add($path)
    return $path
}

function Build-Checkout([string] $path, [string] $project, [string[]] $extra) {
    if ($NoBuild) { return }
    Write-Log "build $project in $path"
    dotnet build (Join-Path $path $project) -c Release -nologo -v q @BuildProperties @extra | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed in $path" }
}

$phoneProperties = @{
    'mono-paot' = @(); 'mono-faot' = @('-p:AndroidEnableProfiledAot=false')
    'mono-jit' = @('-p:RunAOTCompilation=false', '-p:AndroidEnableProfiledAot=false')
    'coreclr' = @('-p:UseMonoRuntime=false')
}

# The variants: build checkout plus extra arguments. In-build A/B shares the tip's checkout.
$tipPath = if ($Tip) { if ($DryRun) { "<worktree of $Tip>" } else { Get-Checkout $Tip } } else { $repo }
$variants = [ordered]@{}
if ($Base) {
    $basePath = if ($DryRun) { "<worktree of $Base>" } else { Get-Checkout $Base }
    $variants['base'] = @{ Path = $basePath; Args = $BaseArgs }
}
elseif ($BaseArgs) {
    $variants['base'] = @{ Path = $tipPath; Args = $BaseArgs }
}
$variants['tip'] = @{ Path = $tipPath; Args = $TipArgs }
$names = @($variants.Keys)

if (-not $DryRun) {
    $paths = @($variants.Values | ForEach-Object { $_.Path } | Select-Object -Unique)
    foreach ($path in $paths) {
        if ($Renders.Count) { Build-Checkout $path 'samples\TextStress\TextStress.csproj' @() }
        if ($Phone) { Build-Checkout $path 'samples\TextStress.Android\TextStress.Android.csproj' $phoneProperties[$PhoneBuild] }
    }
    if ($Phone) {
        # Each variant's APK is copied out, so two variants never share an output folder.
        $apkDir = Join-Path $OutDir 'apks'
        New-Item -ItemType Directory -Force $apkDir | Out-Null
        foreach ($v in $names) {
            $apk = Join-Path $apkDir "$v-$PhoneBuild.apk"
            Copy-Item (Join-Path $variants[$v].Path $apkRelative) $apk -Force
            $variants[$v].Apk = $apk
        }
    }
}

function Wait-Idle {
    $waited = 0
    while ($true) {
        $busy = @(Get-Process | Where-Object {
                $_.Name -match '^(TextStress|testhost.*|.*UnitTests|.*RenderTests|.*IntegrationTests|.*LeakTests|dotnet-trace)$' })
        $load = (1..2 | ForEach-Object {
                (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
                Start-Sleep -Milliseconds 500 }) | Measure-Object -Average
        if ($busy.Count -eq 0 -and $load.Average -lt $IdleCpu) { return }
        if ($waited % 60 -eq 0) {
            Write-Log ('  waiting for an idle machine: {0} cpu {1:N0} %' -f (($busy | ForEach-Object Name) -join ','), $load.Average)
        }
        Start-Sleep -Seconds 5
        $waited += 5
    }
}

function Get-FrameArgs([string] $jobPreset) {
    $name = if ($Frames) { $Frames } else { $jobPreset }
    $framePresets[$name]
}

$contentArgs = @('--preset', $Preset)
if ($ContentSize) {
    $w, $h = $ContentSize -split 'x'
    $contentArgs += @('--width', $w, '--height', $h)
}

$phoneTargets = if ($Phone) { @($PhoneRenders) } else { @() }
$total = $Passes * ($Renders.Count + $phoneTargets.Count) * $allJobs.Count * $names.Count
$run = 0
$started = Get-Date
$runTime = [TimeSpan]::Zero
$executed = 0
Write-Log ("campaign: {0} runs; variants {1}; jobs {2}; renders {3}{4}" -f $total, ($names -join ','),
    (@($allJobs.Keys) -join ','), ($Renders -join ','), $(if ($Phone) { "; phone $($PhoneRenders -join ',') $PhoneBuild" } else { '' }))

function Get-Eta {
    if ($executed -eq 0) { return '' }
    $left = $runTime.TotalMinutes / $executed * ($total - $run + 1)
    '{0:N0} min left' -f [Math]::Ceiling($left)
}

for ($pass = 1; $pass -le $Passes; $pass++) {
    $order = if ($pass % 2 -eq 1) { $names } else { @($names)[($names.Count - 1)..0] }

    foreach ($render in $Renders) {
        $dir = Join-Path $OutDir $render
        New-Item -ItemType Directory -Force $dir | Out-Null

        foreach ($job in $allJobs.Keys) {
            $command, $jobFrames = $allJobs[$job]
            foreach ($v in $order) {
                $run++
                $out = Join-Path $dir "$v.$job-p$pass.tsv"
                $words = -split $command
                $arguments = @('--scenario', $words[0]) + @($words | Select-Object -Skip 1) + (-split (Get-FrameArgs $jobFrames)) +
                    @('--mode', $Mode, '--render', $render, '--pass', $pass, '--label', $v, '--tag', $v, '--job', $job) +
                    $contentArgs + @('--progress', "$run/$total")
                $eta = Get-Eta
                if ($eta) { $arguments += @('--eta', $eta) }
                # -split of an empty string yields one empty word, which TextStress rejects.
                $arguments += @('--out', $out) + @(-split $variants[$v].Args | Where-Object { $_ })

                if ($DryRun) { Write-Host "run $run/$total $render $v $job : $($arguments -join ' ')"; continue }

                Wait-Idle
                Write-Log "run $run/$total p$pass $render $job $v"
                $runStarted = Get-Date
                & (Join-Path $variants[$v].Path $exeRelative) @arguments *> ($out -replace '\.tsv$', '.log')
                $exit = $LASTEXITCODE
                $runTime += (Get-Date) - $runStarted
                $executed++
                if ($exit -eq 4) { throw "the content area does not fit on this screen; see $($out -replace '\.tsv$', '.log')" }
                if ($exit -ne 0) { Write-Log "  FAILED with exit $exit" }
            }
        }
    }

    # The phone runs every job of one variant in a row: each switch of variants reinstalls the APK,
    # and the first runs after an install run slow, so each install gets one discarded warm-up run.
    # run-android.ps1 crosses one scenario with named argument sets, so jobs go to it grouped by
    # scenario and frame preset.
    foreach ($render in $phoneTargets) {
        $dir = Join-Path $OutDir "phone-$render"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $groups = [ordered]@{}
        foreach ($job in $allJobs.Keys) {
            $command, $jobFrames = $allJobs[$job]
            $words = -split $command
            $key = "$($words[0])|$(Get-FrameArgs $jobFrames)"
            if (-not $groups.Contains($key)) { $groups[$key] = [ordered]@{} }
            $groups[$key][$job] = @($words | Select-Object -Skip 1) -join ' '
        }

        foreach ($v in $order) {
            $raw = Join-Path $OutDir "phone-raw\$v-p$pass"
            $first = $true

            foreach ($key in $groups.Keys) {
                $scenario, $frameText = $key -split '\|'
                $frameWords = -split $frameText
                $named = [ordered]@{}
                foreach ($job in $groups[$key].Keys) {
                    $named[$job] = (@($groups[$key][$job], '--job', $job, '--label', $v, '--preset', 'phone') +
                        (-split $variants[$v].Args) | Where-Object { $_ }) -join ' '
                }

                if ($DryRun) {
                    foreach ($job in $named.Keys) { $run++; Write-Host "run $run/$total phone $render $v $job : $scenario $($named[$job]) $frameText" }
                    continue
                }

                Write-Log "runs $($run + 1)-$($run + $named.Count)/$total p$pass phone $render $v $scenario"
                $runStarted = Get-Date
                & (Join-Path $PSScriptRoot 'run-android.ps1') -OutDir $raw -NoBuild -NoInstall:(-not $first) -NoWarmupRun:(-not $first) `
                    -ApkPath $variants[$v].Apk -RunTag $v -Serial $Serial -Renders $render -Passes 1 -Jobs $scenario -Modes $Mode `
                    -Sweeps @{} -Variants $named -Frames $frameWords[1] -Warmup $frameWords[3] `
                    -ProgressBase $run -ProgressTotal $total -Eta (Get-Eta) -NoSummary
                $runTime += (Get-Date) - $runStarted
                $executed += $named.Count
                $run += $named.Count
                $first = $false

                foreach ($job in $named.Keys) {
                    $tsv = Join-Path $raw "$render\$scenario-$job-$Mode-p1.tsv"
                    if (Test-Path $tsv) {
                        Copy-Item $tsv (Join-Path $dir "$v.$job-p$pass.tsv")
                        Copy-Item ($tsv -replace '\.tsv$', '.log') (Join-Path $dir "$v.$job-p$pass.log") -ErrorAction SilentlyContinue
                    }
                    else { Write-Log "  FAILED: no result for $job" }
                }
            }
        }
    }
}

if ($DryRun) { return }
if ($Phone) { & (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe') -s $Serial uninstall com.avalonia.textstress | Out-Null }
Write-Log ('finished after {0:N1} min' -f ((Get-Date) - $started).TotalMinutes)

$summaryArgs = @((Join-Path $PSScriptRoot 'summarize.py'), $OutDir, (Join-Path $OutDir 'summary.md'))
if ($CountersOnly) { $summaryArgs += '--counters-only' }
python @summaryArgs

if ($RemoveWorktrees) {
    foreach ($path in $createdWorktrees) {
        git -C $repo worktree remove --force $path 2>&1 | Out-Null
        # git cannot delete paths past MAX_PATH (the build outputs); the extended-length prefix can.
        if (Test-Path $path) { Remove-Item -LiteralPath ('\\?\' + $path) -Recurse -Force; git -C $repo worktree prune }
        Write-Log "removed worktree $path"
    }
}
