[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DwgPath,

    [string]$WorkbookPath,

    [string]$SpecPath,

    [string]$ReviewConfigPath,

    [switch]$DefaultBlindMidpoints,

    [string]$OutputDir,

    [string]$AccoreConsolePath = "C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe",

    [string]$PluginDllPath = "",

    [string]$PythonExe = "python",

    [switch]$KeepWorkingDrawing
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    try {
        return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    }
    catch {
        throw "$Label not found: $Path"
    }
}

function New-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Invoke-ExternalProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [hashtable]$EnvironmentVariables,
        [string]$StdOutPath,
        [string]$StdErrPath
    )

    $savedEnvironment = @{}
    if ($EnvironmentVariables) {
        foreach ($key in $EnvironmentVariables.Keys) {
            $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
            [Environment]::SetEnvironmentVariable($key, [string]$EnvironmentVariables[$key], "Process")
        }
    }

    try {
        $argumentString = (($Arguments | ForEach-Object {
            if ($_ -match '[\s"]') {
                '"' + (($_ -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
            }
            else {
                $_
            }
        }) -join ' ')

        $startInfo = @{
            FilePath = $FilePath
            ArgumentList = $argumentString
            WorkingDirectory = $WorkingDirectory
            Wait = $true
            PassThru = $true
            RedirectStandardOutput = $StdOutPath
            RedirectStandardError = $StdErrPath
        }

        $process = Start-Process @startInfo
        $stdout = if ($StdOutPath -and (Test-Path -LiteralPath $StdOutPath)) { Get-Content -LiteralPath $StdOutPath -Raw } else { "" }
        $stderr = if ($StdErrPath -and (Test-Path -LiteralPath $StdErrPath)) { Get-Content -LiteralPath $StdErrPath -Raw } else { "" }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        if ($EnvironmentVariables) {
            foreach ($key in $EnvironmentVariables.Keys) {
                [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], "Process")
            }
        }
    }
}

function Copy-AppendedFileSegment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [long]$StartOffset = 0
    )

    if (-not (Test-Path -LiteralPath $SourcePath)) {
        return $null
    }

    $sourceInfo = Get-Item -LiteralPath $SourcePath
    $resolvedStart = [Math]::Max(0, [Math]::Min($StartOffset, $sourceInfo.Length))
    $sourceStream = [System.IO.File]::Open($SourcePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)

    try {
        $destinationDir = Split-Path -Parent $DestinationPath
        if ($destinationDir) {
            New-Directory -Path $destinationDir | Out-Null
        }

        $destinationStream = [System.IO.File]::Create($DestinationPath)
        try {
            [void]$sourceStream.Seek($resolvedStart, [System.IO.SeekOrigin]::Begin)
            $sourceStream.CopyTo($destinationStream)
        }
        finally {
            $destinationStream.Dispose()
        }
    }
    finally {
        $sourceStream.Dispose()
    }

    return $DestinationPath
}

$workspaceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PluginDllPath)) {
    $PluginDllPath = Join-Path $workspaceRoot "src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll"
}

if ([string]::IsNullOrWhiteSpace($WorkbookPath) -and [string]::IsNullOrWhiteSpace($SpecPath)) {
    throw "Provide either -WorkbookPath or -SpecPath."
}

if (-not [string]::IsNullOrWhiteSpace($WorkbookPath) -and -not [string]::IsNullOrWhiteSpace($SpecPath)) {
    throw "Use either -WorkbookPath or -SpecPath, not both."
}

$resolvedDwgPath = Resolve-ExistingPath -Path $DwgPath -Label "DWG"
$resolvedAccoreConsolePath = Resolve-ExistingPath -Path $AccoreConsolePath -Label "AcCoreConsole"
$resolvedPluginDllPath = Resolve-ExistingPath -Path $PluginDllPath -Label "Plugin DLL"
$pluginFolder = Split-Path -Parent $resolvedPluginDllPath
$pluginLogPath = Join-Path $pluginFolder "AtsBackgroundBuilder.log"
$pluginLogOffset = if (Test-Path -LiteralPath $pluginLogPath) { (Get-Item -LiteralPath $pluginLogPath).Length } else { 0L }

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $workspaceRoot ("data\atsbuild-harness\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$runDirectory = New-Directory -Path $OutputDir
$artifactDirectory = New-Directory -Path (Join-Path $runDirectory "artifacts")
$workingDirectory = New-Directory -Path (Join-Path $runDirectory "working")
$isolateDirectory = New-Directory -Path (Join-Path $runDirectory "accore-isolate")
$scriptPath = Join-Path $workingDirectory "run.scr"
$consoleStdOutPath = Join-Path $artifactDirectory "accoreconsole.stdout.log"
$consoleStdErrPath = Join-Path $artifactDirectory "accoreconsole.stderr.log"
$pluginRunLogPath = Join-Path $artifactDirectory "AtsBackgroundBuilder.run.log"
$pluginFullLogCopyPath = Join-Path $artifactDirectory "AtsBackgroundBuilder.full.log"
$reviewReportPath = Join-Path $artifactDirectory "review-report.json"
$reviewStdOutPath = Join-Path $artifactDirectory "review.stdout.log"
$workingDwgPath = Join-Path $workingDirectory ([System.IO.Path]::GetFileNameWithoutExtension($resolvedDwgPath) + ".working.dwg")
$dxfPath = Join-Path $artifactDirectory "output.dxf"

$resolvedWorkbookPath = $null
if (-not [string]::IsNullOrWhiteSpace($WorkbookPath)) {
    $resolvedWorkbookPath = Resolve-ExistingPath -Path $WorkbookPath -Label "Workbook"
}
else {
    $resolvedSpecPath = Resolve-ExistingPath -Path $SpecPath -Label "Spec"
    $generatorScriptPath = Resolve-ExistingPath -Path (Join-Path $PSScriptRoot "atsbuild_generate_workbook.py") -Label "Workbook generator"
    $resolvedWorkbookPath = Join-Path $artifactDirectory "generated-input.xlsx"
    $generatorResult = Invoke-ExternalProcess `
        -FilePath $PythonExe `
        -Arguments @($generatorScriptPath, "--spec", $resolvedSpecPath, "--output", $resolvedWorkbookPath) `
        -WorkingDirectory $workspaceRoot `
        -StdOutPath (Join-Path $artifactDirectory "generate-workbook.stdout.log") `
        -StdErrPath (Join-Path $artifactDirectory "generate-workbook.stderr.log")
    if ($generatorResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resolvedWorkbookPath)) {
        throw "Workbook generation failed. See $(Join-Path $artifactDirectory 'generate-workbook.stderr.log')."
    }
}

Copy-Item -LiteralPath $resolvedDwgPath -Destination $workingDwgPath -Force

$scriptLines = @(
    "SECURELOAD",
    "0",
    "FILEDIA",
    "0",
    "CMDDIA",
    "0",
    "NETLOAD",
    ('"' + $resolvedPluginDllPath + '"'),
    "ATSBUILD_XLS_BATCH",
    "QUIT",
    "N"
)
[System.IO.File]::WriteAllLines($scriptPath, $scriptLines, [System.Text.Encoding]::ASCII)

$accoreArguments = @(
    "/i",
    $workingDwgPath,
    "/s",
    $scriptPath,
    "/l",
    "en-US",
    "/isolate",
    "ATSBUILDHARNESS",
    $isolateDirectory
)

$environmentVariables = @{
    ATSBUILD_BATCH_WORKBOOK = $resolvedWorkbookPath
    ATSBUILD_BATCH_DXF_PATH = $dxfPath
}

$accoreResult = Invoke-ExternalProcess `
    -FilePath $resolvedAccoreConsolePath `
    -Arguments $accoreArguments `
    -WorkingDirectory $runDirectory `
    -EnvironmentVariables $environmentVariables `
    -StdOutPath $consoleStdOutPath `
    -StdErrPath $consoleStdErrPath

if (Test-Path -LiteralPath $pluginLogPath) {
    Copy-Item -LiteralPath $pluginLogPath -Destination $pluginFullLogCopyPath -Force
    Copy-AppendedFileSegment -SourcePath $pluginLogPath -DestinationPath $pluginRunLogPath -StartOffset $pluginLogOffset | Out-Null
}

$resolvedPluginSummaryPath = if (Test-Path -LiteralPath $pluginRunLogPath) {
    $pluginRunLogPath
}
elseif (Test-Path -LiteralPath $pluginFullLogCopyPath) {
    $pluginFullLogCopyPath
}
else {
    $null
}

$batchCompleted = $false
if ($resolvedPluginSummaryPath) {
    $batchCompleted = [bool](Select-String -Path $resolvedPluginSummaryPath -SimpleMatch "ATSBUILD_XLS_BATCH exit stage: completed (ok)" -Quiet)
}

$reviewExitCode = $null
$reviewEnabled = $DefaultBlindMidpoints.IsPresent -or -not [string]::IsNullOrWhiteSpace($ReviewConfigPath)
if ($reviewEnabled -and (Test-Path -LiteralPath $dxfPath)) {
    $reviewScriptPath = Resolve-ExistingPath -Path (Join-Path $PSScriptRoot "atsbuild_review.py") -Label "DXF review script"
    $reviewArguments = @($reviewScriptPath, "--dxf", $dxfPath, "--report", $reviewReportPath)
    if (-not [string]::IsNullOrWhiteSpace($ReviewConfigPath)) {
        $reviewArguments += @("--config", (Resolve-ExistingPath -Path $ReviewConfigPath -Label "Review config"))
    }
    elseif ($DefaultBlindMidpoints.IsPresent) {
        $reviewArguments += "--default-blind-midpoints"
    }

    $reviewOutput = & $PythonExe @reviewArguments 2>&1
    $reviewExitCode = $LASTEXITCODE
    $reviewText = ($reviewOutput | ForEach-Object { "$_" }) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($reviewStdOutPath, $reviewText, [System.Text.Encoding]::UTF8)
}

if (-not $KeepWorkingDrawing.IsPresent -and (Test-Path -LiteralPath $workingDwgPath)) {
    Remove-Item -LiteralPath $workingDwgPath -Force
}

$summary = [pscustomobject]@{
    runDirectory = $runDirectory
    workbook = $resolvedWorkbookPath
    dxf = $(if (Test-Path -LiteralPath $dxfPath) { $dxfPath } else { $null })
    pluginLog = $resolvedPluginSummaryPath
    consoleStdOut = $consoleStdOutPath
    consoleStdErr = $consoleStdErrPath
    reviewReport = $(if (Test-Path -LiteralPath $reviewReportPath) { $reviewReportPath } else { $null })
    accoreExitCode = $accoreResult.ExitCode
    reviewExitCode = $reviewExitCode
    reviewEnabled = $reviewEnabled
    batchCompleted = $batchCompleted
}

$summary | ConvertTo-Json -Depth 4

if (-not $summary.dxf -or -not $batchCompleted) {
    if ($accoreResult.ExitCode -ne 0) {
        exit $accoreResult.ExitCode
    }

    exit 1
}

if ($reviewEnabled -and $null -ne $reviewExitCode -and $reviewExitCode -ne 0) {
    exit $reviewExitCode
}
