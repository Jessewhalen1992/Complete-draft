[CmdletBinding()]
param(
    [string]$ManifestPath = "data/regression-baselines/twp43-8-5/baseline.json",
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,
    [string]$Label
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedManifestPath = (Resolve-Path -LiteralPath (Join-Path $repoRoot $ManifestPath)).Path
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json

function Resolve-RepoPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    return (Resolve-Path -LiteralPath (Join-Path $repoRoot $RelativePath)).Path
}

$resolvedOutputDir = Join-Path $repoRoot $OutputDir
if (-not (Test-Path -LiteralPath $resolvedOutputDir)) {
    New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null
}
$resolvedOutputDir = (Resolve-Path -LiteralPath $resolvedOutputDir).Path

$acceptedDxf = Resolve-RepoPath -RelativePath $manifest.accepted_dxf
$correctedReferenceDxf = Resolve-RepoPath -RelativePath $manifest.corrected_reference_dxf
$seedDwg = Resolve-RepoPath -RelativePath $manifest.seed_dwg
$workbook = Resolve-RepoPath -RelativePath $manifest.workbook
$runLabel = if ([string]::IsNullOrWhiteSpace($Label)) { Split-Path -Leaf $resolvedOutputDir } else { $Label }
$fullAutoCadProfileName = if ($manifest.PSObject.Properties["full_autocad_profile"] -and -not [string]::IsNullOrWhiteSpace([string]$manifest.full_autocad_profile)) {
    [string]$manifest.full_autocad_profile
}
else {
    "ATSBUILD_TEST"
}

function Get-ManifestBool {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not $Object.PSObject.Properties[$Name]) {
        return $false
    }

    return [bool]$Object.$Name
}

$harnessEnvironment = @{}
if (Get-ManifestBool -Object $manifest -Name "ats_fabric_only") {
    $harnessEnvironment["ATSBUILD_XLS_ATS_FABRIC_ONLY"] = "1"
}
if (Get-ManifestBool -Object $manifest -Name "quarter_view") {
    $harnessEnvironment["ATSBUILD_QUATERVIEW"] = "1"
}
if (Get-ManifestBool -Object $manifest -Name "preserve_final_usec_variant_layers") {
    $harnessEnvironment["ATSBUILD_PRESERVE_FINAL_USEC_VARIANT_LAYERS"] = "1"
}

$previousEnvironment = @{}
foreach ($name in $harnessEnvironment.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    [Environment]::SetEnvironmentVariable($name, $harnessEnvironment[$name], "Process")
}

try {
    & (Join-Path $repoRoot "scripts\atsbuild_harness.ps1") `
        -Runner FullAutoCAD `
        -DwgPath $seedDwg `
        -WorkbookPath $workbook `
        -OutputDir $resolvedOutputDir `
        -FullAutoCadProfileName $fullAutoCadProfileName | Out-Null
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }
}

$artifactsDir = Join-Path $resolvedOutputDir "artifacts"
$actualDxf = Join-Path $artifactsDir "output.dxf"
$compareJson = Join-Path $artifactsDir "compare-baseline.json"
$metricsJson = Join-Path $artifactsDir "compare-metrics.json"

python (Join-Path $repoRoot "scripts\compare_dxf_entities.py") `
    --before $acceptedDxf `
    --after $actualDxf `
    --precision 3 `
    --sample-limit 40 | Set-Content -LiteralPath $compareJson

python (Join-Path $repoRoot "scripts\track_township_dxf_metrics.py") `
    --label $runLabel `
    --scope $manifest.scope `
    --actual $actualDxf `
    --expected $correctedReferenceDxf `
    --previous $acceptedDxf `
    --summary-json $metricsJson | Out-Null

$compare = Get-Content -LiteralPath $compareJson -Raw | ConvertFrom-Json
$metrics = Get-Content -LiteralPath $metricsJson -Raw | ConvertFrom-Json
$logPath = Join-Path $artifactsDir "AtsBackgroundBuilder.run.log"
$importLine = Select-String -Path $logPath -Pattern "Imported dispositions:" | Select-Object -Last 1

[pscustomobject]@{
    scope = $manifest.scope
    label = $runLabel
    outputDir = $resolvedOutputDir
    actualDxf = $actualDxf
    baselineDxf = $acceptedDxf
    baselineComparePassed = $compare.passed
    baselineAdded = $compare.addedSample.Count
    baselineMissing = $compare.missingSample.Count
    importedDispositions = if ($importLine) { $importLine.Line.Trim() } else { "" }
    metricsJson = $metricsJson
    compareJson = $compareJson
    expected3dpMismatchCount = $metrics.vsExpected.precisionSummaries.'3dp'.mismatchCount
} | ConvertTo-Json -Depth 6
