[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DwgPath,

    [Parameter(Mandatory = $true)]
    [string]$RequestPath,

    [string]$OutputDir,

    [string]$AccoreConsolePath = "C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe",

    [string]$PluginDllPath = "",

    [switch]$CreateGeometry
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

        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $FilePath
        $startInfo.Arguments = $argumentString
        $startInfo.WorkingDirectory = $WorkingDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true

        $process = [System.Diagnostics.Process]::Start($startInfo)
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()

        if ($StdOutPath) {
            [System.IO.File]::WriteAllText($StdOutPath, $stdout, [System.Text.Encoding]::Unicode)
        }

        if ($StdErrPath) {
            [System.IO.File]::WriteAllText($StdErrPath, $stderr, [System.Text.Encoding]::Unicode)
        }

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

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$resolvedDwgPath = Resolve-ExistingPath -Path $DwgPath -Label "Drawing"
$resolvedRequestPath = Resolve-ExistingPath -Path $RequestPath -Label "Build a Drill request"
$resolvedAccoreConsolePath = Resolve-ExistingPath -Path $AccoreConsolePath -Label "AcCoreConsole"

if ([string]::IsNullOrWhiteSpace($PluginDllPath)) {
    $resolvedPluginDllPath = Resolve-ExistingPath -Path (Join-Path $workspaceRoot "build\compass\single-dll\Compass.dll") -Label "Compass plugin DLL"
}
else {
    $resolvedPluginDllPath = Resolve-ExistingPath -Path $PluginDllPath -Label "Compass plugin DLL"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $runTag = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path $workspaceRoot (Join-Path ".artifacts" ("compass-builddrill-headless-" + $runTag))
}

$runDirectory = New-Directory -Path $OutputDir
$artifactDirectory = New-Directory -Path (Join-Path $runDirectory "artifacts")
$workingDirectory = New-Directory -Path (Join-Path $runDirectory "working")
$isolateDirectory = New-Directory -Path (Join-Path $runDirectory "accore-isolate")
$scriptPath = Join-Path $workingDirectory "run.scr"
$consoleStdOutPath = Join-Path $artifactDirectory "accoreconsole.stdout.log"
$consoleStdErrPath = Join-Path $artifactDirectory "accoreconsole.stderr.log"
$textReportPath = Join-Path $artifactDirectory "builddrill-report.txt"
$jsonReportPath = Join-Path $artifactDirectory "builddrill-report.json"
$savedDrawingPath = Join-Path $artifactDirectory "builddrill-output.dwg"

$sourceDrawingExtension = [System.IO.Path]::GetExtension($resolvedDwgPath)
if ([string]::IsNullOrWhiteSpace($sourceDrawingExtension)) {
    $sourceDrawingExtension = ".dwg"
}

$workingDwgPath = Join-Path $workingDirectory ([System.IO.Path]::GetFileNameWithoutExtension($resolvedDwgPath) + ".working" + $sourceDrawingExtension)
$workingRequestPath = Join-Path $workingDirectory "builddrill-request.json"

Copy-Item -LiteralPath $resolvedDwgPath -Destination $workingDwgPath -Force
Copy-Item -LiteralPath $resolvedRequestPath -Destination $workingRequestPath -Force

$scriptLines = @(
    "SECURELOAD",
    "0",
    "FILEDIA",
    "0",
    "CMDDIA",
    "0",
    "NETLOAD",
    ('"' + $resolvedPluginDllPath + '"'),
    "COMPASSBUILDDRILL_HEADLESS",
    "QUIT",
    "Y"
)
[System.IO.File]::WriteAllLines($scriptPath, $scriptLines, [System.Text.Encoding]::ASCII)

$environmentVariables = @{
    COMPASS_HEADLESS = "1"
    COMPASS_BUILDDRILL_REQUEST_PATH = $workingRequestPath
    COMPASS_BUILDDRILL_OUTPUT_PATH = $textReportPath
    COMPASS_BUILDDRILL_OUTPUT_JSON_PATH = $jsonReportPath
    COMPASS_BUILDDRILL_SAVED_DRAWING_PATH = $savedDrawingPath
    COMPASS_BUILDDRILL_CREATE_GEOMETRY = $(if ($CreateGeometry.IsPresent) { "1" } else { "0" })
}

$accoreArguments = @(
    "/i",
    $workingDwgPath,
    "/s",
    $scriptPath,
    "/l",
    "en-US",
    "/isolate",
    "COMPASSBUILDDRILL",
    $isolateDirectory
)

$runnerResult = Invoke-ExternalProcess `
    -FilePath $resolvedAccoreConsolePath `
    -Arguments $accoreArguments `
    -WorkingDirectory $runDirectory `
    -EnvironmentVariables $environmentVariables `
    -StdOutPath $consoleStdOutPath `
    -StdErrPath $consoleStdErrPath

$reportJson = $null
if (Test-Path -LiteralPath $jsonReportPath) {
    $reportJson = Get-Content -LiteralPath $jsonReportPath -Raw | ConvertFrom-Json
}

$summary = [pscustomobject]@{
    runDirectory = $runDirectory
    workingDwgPath = $workingDwgPath
    savedDrawingPath = $savedDrawingPath
    requestPath = $workingRequestPath
    textReportPath = $textReportPath
    jsonReportPath = $jsonReportPath
    accoreconsoleExitCode = $runnerResult.ExitCode
    success = $(if ($reportJson) { [bool]$reportJson.Success } else { $false })
}

$summaryPath = Join-Path $artifactDirectory "summary.json"
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Output ($summary | ConvertTo-Json -Depth 5)
if (Test-Path -LiteralPath $textReportPath) {
    Write-Output ""
    Write-Output "==== BuildDrill Report ===="
    Get-Content -LiteralPath $textReportPath
}
