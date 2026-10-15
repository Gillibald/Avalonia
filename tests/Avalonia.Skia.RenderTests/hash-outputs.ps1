<#
.SYNOPSIS
Hashes every render test output (*.out.png under tests/TestFiles), or compares two hash lists.

.DESCRIPTION
A change that should only make text rendering faster must leave every rendered image byte for
byte the same. Hash the outputs of a run on the base and on the tip, then compare the lists: any
changed, missing or extra output is listed and the script exits with 1.

The list has one "<sha256> *<path>" line per output, sorted by path (the format sha256sum -b
writes), with paths relative to tests/TestFiles.

-Run first deletes the old outputs, so stale images from an earlier run cannot hide a missing one,
then builds and runs the Skia render tests of the checkout the script lives in.

.EXAMPLE
pwsh tests/Avalonia.Skia.RenderTests/hash-outputs.ps1 -Run -Out C:\results\tip.sha256

.EXAMPLE
pwsh tests/Avalonia.Skia.RenderTests/hash-outputs.ps1 -Compare C:\results\base.sha256, C:\results\tip.sha256
#>
param(
    [string] $Out = '',
    [switch] $Run,
    [string] $Configuration = 'Debug',
    [string[]] $BuildProperties = @('-p:WarningsNotAsErrors=NU1603'),
    [string[]] $Compare = @()
)

$ErrorActionPreference = 'Stop'

function Read-Hashes([string] $path) {
    $map = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $path) {
        # Lists written by find | sha256sum carry a leading ./ on every path.
        if ($line -match '^([0-9a-fA-F]{64}) [ *](?:\./)?(.+)$') { $map[$Matches[2]] = $Matches[1].ToLowerInvariant() }
    }
    $map
}

if ($Compare.Count) {
    if ($Compare.Count -ne 2) { throw '-Compare takes two hash lists.' }
    $a = Read-Hashes $Compare[0]
    $b = Read-Hashes $Compare[1]
    $changed = @($a.Keys | Where-Object { $b.Contains($_) -and $a[$_] -ne $b[$_] })
    $missing = @($a.Keys | Where-Object { -not $b.Contains($_) })
    $extra = @($b.Keys | Where-Object { -not $a.Contains($_) })
    $changed | ForEach-Object { "changed  $_" }
    $missing | ForEach-Object { "only in $($Compare[0]): $_" }
    $extra | ForEach-Object { "only in $($Compare[1]): $_" }
    "{0} outputs compared: {1} changed, {2} only in the first list, {3} only in the second" -f $a.Count, $changed.Count, $missing.Count, $extra.Count
    exit $(if ($changed.Count + $missing.Count + $extra.Count) { 1 } else { 0 })
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$files = Join-Path $repo 'tests\TestFiles'

if ($Run) {
    Get-ChildItem $files -Recurse -Filter '*.out.png' | Remove-Item
    $project = Join-Path $PSScriptRoot 'Avalonia.Skia.RenderTests.csproj'
    dotnet build $project -c $Configuration -nologo -v q @BuildProperties | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    # A failing render test still writes its output; the hashes show what changed.
    dotnet test $project -c $Configuration --no-build | Select-Object -Last 5 | Out-Host
}

$lines = Get-ChildItem $files -Recurse -Filter '*.out.png' |
    ForEach-Object { [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($files, $_.FullName).Replace('\', '/'); File = $_.FullName } } |
    Sort-Object Path -CaseSensitive |
    ForEach-Object { '{0} *{1}' -f (Get-FileHash -LiteralPath $_.File -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Path }

if (-not $lines) { throw "no *.out.png under $files; run the render tests first (-Run)" }
if ($Out) {
    Set-Content -LiteralPath $Out -Value $lines -Encoding ascii
    "{0} outputs hashed to {1}" -f @($lines).Count, $Out
}
else { $lines }
