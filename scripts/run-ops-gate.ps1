param(
    [int]$MinTwp = 50,
    [int]$MinSections = 30,
    [double]$MaxUnmatchedRatio = 0.30,
    [int]$MaxSectionsWithGt2Unmatched = 22,
    [string]$RoadWidthTargets = "20.11,30.17",
    [string]$OutRoot = ".\\out-validate",
    [switch]$SkipZ11,
    [switch]$SkipZ12
)

$ErrorActionPreference = "Stop"

function Invoke-Validator {
    param(
        [Parameter(Mandatory = $true)][string]$Zone,
        [Parameter(Mandatory = $true)][string]$OutDir,
        [Parameter(Mandatory = $true)][string]$RoadTargets,
        [Parameter(Mandatory = $true)][int]$MaxGt2
    )

    Write-Host "Running validator for zone $Zone..."
    & py -m ats_viewer.validator `
        --all-townships `
        --zone $Zone `
        --road-width-targets $RoadTargets `
        --max-sections-with-gt2-unmatched $MaxGt2 `
        --allow-partial-townships `
        --out $OutDir

    $exitCode = $LASTEXITCODE
    Write-Host "Validator zone $Zone exit code: $exitCode"
}

function Get-TownshipsFromSummary {
    param([Parameter(Mandatory = $true)][string]$SummaryPath)
    if (-not (Test-Path $SummaryPath)) {
        throw "Missing validation summary: $SummaryPath"
    }
    $payload = Get-Content $SummaryPath -Raw | ConvertFrom-Json
    return @($payload.townships)
}

if (-not (Get-Command py -ErrorAction SilentlyContinue)) {
    throw "'py' launcher was not found on PATH."
}

$z11Out = Join-Path $OutRoot "z11-gate"
$z12Out = Join-Path $OutRoot "z12-gate"

if (-not $SkipZ11) {
    Invoke-Validator -Zone "11" -OutDir $z11Out -RoadTargets $RoadWidthTargets -MaxGt2 $MaxSectionsWithGt2Unmatched
}

if (-not $SkipZ12) {
    Invoke-Validator -Zone "12" -OutDir $z12Out -RoadTargets $RoadWidthTargets -MaxGt2 $MaxSectionsWithGt2Unmatched
}

$allTownships = @()
if (Test-Path (Join-Path $z11Out "validation_summary.json")) {
    $allTownships += Get-TownshipsFromSummary -SummaryPath (Join-Path $z11Out "validation_summary.json")
}
if (Test-Path (Join-Path $z12Out "validation_summary.json")) {
    $allTownships += Get-TownshipsFromSummary -SummaryPath (Join-Path $z12Out "validation_summary.json")
}

if ($allTownships.Count -eq 0) {
    throw "No validator summary files were found. Nothing to evaluate."
}

$opsTownships = @(
    $allTownships | Where-Object {
        [int]$_.key.twp -ge $MinTwp -and [int]$_.sections -ge $MinSections
    }
)

$opsFailures = @(
    $opsTownships | Where-Object {
        ([double]$_.unmatched_ratio -gt $MaxUnmatchedRatio) -or
        ([int]$_.accepted_pairs -eq 0) -or
        ([int]$_.sections_with_gt2_unmatched -gt $MaxSectionsWithGt2Unmatched)
    }
)

$reportPath = Join-Path $OutRoot "ops-gate-failures.csv"
$opsFailures |
    Select-Object label, sections, unmatched_ratio, accepted_pairs, sections_with_gt2_unmatched, failures |
    Export-Csv $reportPath -NoTypeInformation

Write-Host "Ops checked: $($opsTownships.Count)"
Write-Host "Ops failed:  $($opsFailures.Count)"
Write-Host "Report:      $reportPath"

if ($opsFailures.Count -gt 0) {
    exit 1
}

exit 0
