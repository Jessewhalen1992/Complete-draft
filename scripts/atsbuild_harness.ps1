[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DwgPath,

    [ValidateSet("AccoreConsole", "FullAutoCAD")]
    [string]$Runner = "AccoreConsole",

    [string]$WorkbookPath,

    [string]$SpecPath,

    [string]$ReviewConfigPath,

    [switch]$DefaultBlindMidpoints,

    [string]$OutputDir,

    [string]$AccoreConsolePath = "C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe",

    [string]$AcadPath = "C:\Program Files\Autodesk\AutoCAD 2025\acad.exe",

    [string]$AutoCadLauncherRoot = "C:\AtsHarness",

    [string]$FullAutoCadProfileName = "ATSBUILD_TEST",

    [int]$FullAutoCadTimeoutSeconds = 360,

    [int]$FullAutoCadGracefulExitSeconds = 60,

    [string]$PluginDllPath = "",

    [string]$PythonExe = "python",

    [switch]$KeepWorkingDrawing,

    [switch]$LeaveAutoCadOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($LeaveAutoCadOpen.IsPresent -and $Runner -ne "FullAutoCAD") {
    throw "-LeaveAutoCadOpen is only supported with -Runner FullAutoCAD."
}

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

function Try-RemovePathQuietly {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [switch]$Recurse,
        [ref]$Warnings
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    try {
        if ($Recurse.IsPresent) {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        }
        else {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        }
    }
    catch {
        $message = "Cleanup skipped for '$Path': $($_.Exception.Message)"
        if ($Warnings) {
            $Warnings.Value += $message
        }

        Write-Warning $message
    }
}

function Join-ProcessArguments {
    param([string[]]$Arguments)

    return (($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + (($_ -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
        }
        else {
            $_
        }
    }) -join ' ')
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
        $argumentString = Join-ProcessArguments -Arguments $Arguments

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

function Test-AppendedFileSegmentForMarker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,
        [Parameter(Mandatory = $true)]
        [string]$Marker,
        [long]$StartOffset = 0
    )

    if (-not (Test-Path -LiteralPath $SourcePath) -or [string]::IsNullOrWhiteSpace($Marker)) {
        return $false
    }

    $sourceInfo = Get-Item -LiteralPath $SourcePath
    $resolvedStart = [Math]::Max(0, [Math]::Min($StartOffset, $sourceInfo.Length))
    $sourceStream = [System.IO.File]::Open($SourcePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)

    try {
        [void]$sourceStream.Seek($resolvedStart, [System.IO.SeekOrigin]::Begin)
        $reader = New-Object System.IO.StreamReader($sourceStream)
        try {
            return $reader.ReadToEnd().Contains($Marker)
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $sourceStream.Dispose()
    }
}

function Ensure-Junction {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Existing path is not a junction/reparse point: $Path"
        }

        return (Resolve-Path -LiteralPath $Path).Path
    }

    New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-FullAutoCadProfileRoot {
    $autoCadRoot = "HKCU:\Software\Autodesk\AutoCAD"
    if (-not (Test-Path -LiteralPath $autoCadRoot)) {
        return $null
    }

    $roots = @()
    foreach ($releaseKey in Get-ChildItem -LiteralPath $autoCadRoot -ErrorAction SilentlyContinue) {
        foreach ($productKey in Get-ChildItem -LiteralPath $releaseKey.PSPath -ErrorAction SilentlyContinue) {
            $profilesPath = Join-Path $productKey.PSPath "Profiles"
            if (-not (Test-Path -LiteralPath $profilesPath)) {
                continue
            }

            $productProperties = Get-ItemProperty -LiteralPath $productKey.PSPath -ErrorAction SilentlyContinue
            $roots += [pscustomobject]@{
                Path = $profilesPath
                Release = $releaseKey.PSChildName
                Product = $productKey.PSChildName
                RoamableRootFolder = $productProperties.RoamableRootFolder
            }
        }
    }

    $preferred2025 = @($roots | Where-Object { $_.RoamableRootFolder -like "*2025*" -or $_.Release -eq "R25.0" })
    if ($preferred2025.Count -gt 0) {
        return ($preferred2025 | Sort-Object Release, Product -Descending | Select-Object -First 1)
    }

    return ($roots | Sort-Object Release, Product -Descending | Select-Object -First 1)
}

function Ensure-FullAutoCadHarnessProfile {
    param([string]$ProfileName)

    if ([string]::IsNullOrWhiteSpace($ProfileName)) {
        return $null
    }

    $profileRoot = Get-FullAutoCadProfileRoot
    if (-not $profileRoot) {
        Write-Warning "Could not locate AutoCAD profile registry root; Full AutoCAD harness will launch without an isolated /p profile."
        return $null
    }

    $destinationPath = Join-Path $profileRoot.Path $ProfileName
    if (-not (Test-Path -LiteralPath $destinationPath)) {
        $profiles = @(Get-ChildItem -LiteralPath $profileRoot.Path -ErrorAction SilentlyContinue)
        $source = $profiles | Where-Object { $_.PSChildName -eq "COMPASS 2025" } | Select-Object -First 1
        if (-not $source) {
            $source = $profiles | Select-Object -First 1
        }

        if ($source) {
            Copy-Item -LiteralPath $source.PSPath -Destination $destinationPath -Recurse -Force
        }
        else {
            New-Item -Path $destinationPath -Force | Out-Null
        }
    }

    $variablesPath = Join-Path $destinationPath "Variables"
    if (-not (Test-Path -LiteralPath $variablesPath)) {
        New-Item -Path $variablesPath -Force | Out-Null
    }

    New-ItemProperty -LiteralPath $variablesPath -Name "SECURELOAD" -Value "1" -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $variablesPath -Name "FILEDIA" -Value "1" -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $variablesPath -Name "CMDDIA" -Value "1" -PropertyType String -Force | Out-Null

    return $ProfileName
}

function Initialize-FullAutoCadLauncherWorkspace {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedDwgPath,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedWorkbookPath,
        [Parameter(Mandatory = $true)]
        [string]$ResolvedPluginDllPath,
        [Parameter(Mandatory = $true)]
        [string]$RunTag,
        [switch]$LeaveAutoCadOpen
    )

    $resolvedRoot = New-Directory -Path $RootPath
    $pluginFolder = Split-Path -Parent $ResolvedPluginDllPath
    $launcherPluginRoot = Ensure-Junction -Path (Join-Path $resolvedRoot "plugin") -Target $pluginFolder

    $launcherRunDirectory = Join-Path $resolvedRoot ("run-" + $RunTag)
    if (Test-Path -LiteralPath $launcherRunDirectory) {
        Remove-Item -LiteralPath $launcherRunDirectory -Recurse -Force
    }

    New-Directory -Path $launcherRunDirectory | Out-Null

    $drawingExtension = [System.IO.Path]::GetExtension($ResolvedDwgPath)
    if ([string]::IsNullOrWhiteSpace($drawingExtension)) {
        $drawingExtension = ".dwg"
    }

    $launcherDwgPath = Join-Path $launcherRunDirectory ("input" + $drawingExtension)
    $workbookExtension = [System.IO.Path]::GetExtension($ResolvedWorkbookPath)
    if ([string]::IsNullOrWhiteSpace($workbookExtension)) {
        $workbookExtension = ".xlsx"
    }

    $launcherWorkbookPath = Join-Path $launcherRunDirectory ("input" + $workbookExtension)
    $launcherScriptPath = Join-Path $launcherRunDirectory "run.scr"
    $launcherDxfPath = Join-Path $launcherRunDirectory "output.dxf"
    $launcherPluginDllPath = Join-Path $launcherPluginRoot "AtsBackgroundBuilder.dll"

    Copy-Item -LiteralPath $ResolvedDwgPath -Destination $launcherDwgPath -Force
    Copy-Item -LiteralPath $ResolvedWorkbookPath -Destination $launcherWorkbookPath -Force

    $scriptLines = @(
        "SECURELOAD",
        "0",
        "FILEDIA",
        "0",
        "CMDDIA",
        "0",
        "NETLOAD",
        $launcherPluginDllPath,
        "ATSBUILD_XLS_BATCH"
    )

    $scriptLines += @(
        "SECURELOAD",
        "1",
        "FILEDIA",
        "1",
        "CMDDIA",
        "1"
    )

    if ($LeaveAutoCadOpen.IsPresent) {
        # Leave the isolated harness profile in an interactive-safe state.
    }
    else {
        $scriptLines += @(
            "QUIT",
            "N"
        )
    }

    [System.IO.File]::WriteAllLines($launcherScriptPath, $scriptLines, [System.Text.Encoding]::ASCII)

    return [pscustomobject]@{
        RunDirectory = $launcherRunDirectory
        DwgPath = $launcherDwgPath
        WorkbookPath = $launcherWorkbookPath
        ScriptPath = $launcherScriptPath
        DxfPath = $launcherDxfPath
        PluginDllPath = $launcherPluginDllPath
    }
}

function Invoke-FullAutoCadBatch {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AcadPath,
        [Parameter(Mandatory = $true)]
        [string]$DwgPath,
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedDxfPath,
        [Parameter(Mandatory = $true)]
        [string]$PluginLogPath,
        [Parameter(Mandatory = $true)]
        [string]$CompletionMarker,
        [int]$TimeoutSeconds = 360,
        [int]$PollSeconds = 5,
        [long]$PluginLogOffset = 0,
        [hashtable]$EnvironmentVariables,
        [string]$StdOutPath,
        [string]$StdErrPath,
        [switch]$LeaveAutoCadOpen,
        [string]$ProfileName,
        [int]$GracefulExitSeconds = 60
    )

    $savedEnvironment = @{}
    if ($EnvironmentVariables) {
        foreach ($key in $EnvironmentVariables.Keys) {
            $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
            [Environment]::SetEnvironmentVariable($key, [string]$EnvironmentVariables[$key], "Process")
        }
    }

    try {
        $argumentParts = @("/nologo")
        if (-not [string]::IsNullOrWhiteSpace($ProfileName)) {
            $argumentParts += @("/p", $ProfileName)
        }

        $argumentParts += @($DwgPath, "/b", $ScriptPath)
        $argumentString = Join-ProcessArguments -Arguments $argumentParts

        if ($StdOutPath) {
            $stdoutText = @(
                "Full AutoCAD GUI runner."
                ("Launched: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
                ("Command: " + $AcadPath + " " + (Join-ProcessArguments -Arguments $argumentParts))
            ) -join [Environment]::NewLine
            [System.IO.File]::WriteAllText($StdOutPath, $stdoutText, [System.Text.Encoding]::UTF8)
        }

        if ($StdErrPath) {
            [System.IO.File]::WriteAllText($StdErrPath, "", [System.Text.Encoding]::UTF8)
        }

        $process = Start-Process -FilePath $AcadPath -ArgumentList $argumentString -WorkingDirectory $WorkingDirectory -PassThru
        $deadline = (Get-Date).AddSeconds([Math]::Max(30, $TimeoutSeconds))
        $dxfExists = $false
        $batchCompleted = $false
        $timedOut = $false
        $completionObservedAt = $null

        while ($true) {
            $now = Get-Date
            $dxfExists = Test-Path -LiteralPath $ExpectedDxfPath
            $batchCompleted = Test-AppendedFileSegmentForMarker -SourcePath $PluginLogPath -Marker $CompletionMarker -StartOffset $PluginLogOffset

            try {
                $process.Refresh()
            }
            catch {
                # Process may already be gone.
            }

            if (($dxfExists -and $batchCompleted) -or $process.HasExited) {
                if (($dxfExists -and $batchCompleted) -and -not $LeaveAutoCadOpen.IsPresent -and -not $process.HasExited) {
                    if ($null -eq $completionObservedAt) {
                        $completionObservedAt = $now
                    }

                    if ($now -lt $completionObservedAt.AddSeconds([Math]::Max(0, $GracefulExitSeconds))) {
                        Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
                        continue
                    }
                }

                break
            }

            if ($now -ge $deadline) {
                $timedOut = $true
                break
            }

            Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
        }

        $leaveOpenAfterCompletion = $LeaveAutoCadOpen.IsPresent -and $batchCompleted -and -not $timedOut
        $forcedStop = $false
        if (-not $process.HasExited -and ($timedOut -or ($batchCompleted -and -not $leaveOpenAfterCompletion))) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $forcedStop = $true
            Start-Sleep -Seconds 2
            try {
                $process.Refresh()
            }
            catch {
                # Process already stopped.
            }
        }

        $exitCode = $null
        if (-not $forcedStop) {
            try {
                if ($process.HasExited) {
                    $exitCode = $process.ExitCode
                }
            }
            catch {
                # GUI process may not expose an exit code after termination.
            }
        }

        $stdout = if ($StdOutPath -and (Test-Path -LiteralPath $StdOutPath)) { Get-Content -LiteralPath $StdOutPath -Raw } else { "" }
        $stderr = if ($StdErrPath -and (Test-Path -LiteralPath $StdErrPath)) { Get-Content -LiteralPath $StdErrPath -Raw } else { "" }

        return [pscustomobject]@{
            ExitCode = $exitCode
            StdOut = $stdout
            StdErr = $stderr
            DxfExists = $dxfExists
            BatchCompleted = $batchCompleted
            TimedOut = $timedOut
            ForcedStop = $forcedStop
            ProcessAlive = (-not $process.HasExited)
            ProcessId = $process.Id
            LeftOpen = ($leaveOpenAfterCompletion -and -not $forcedStop -and -not $process.HasExited)
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
$resolvedPluginDllPath = Resolve-ExistingPath -Path $PluginDllPath -Label "Plugin DLL"
$pluginFolder = Split-Path -Parent $resolvedPluginDllPath
$pluginLogPath = Join-Path $pluginFolder "AtsBackgroundBuilder.log"
$pluginLogOffset = if (Test-Path -LiteralPath $pluginLogPath) { (Get-Item -LiteralPath $pluginLogPath).Length } else { 0L }
$completionMarker = "ATSBUILD_XLS_BATCH exit stage: completed (ok)"

$resolvedAccoreConsolePath = $null
$resolvedAcadPath = $null
if ($Runner -eq "FullAutoCAD") {
    $resolvedAcadPath = Resolve-ExistingPath -Path $AcadPath -Label "AutoCAD"
}
else {
    $resolvedAccoreConsolePath = Resolve-ExistingPath -Path $AccoreConsolePath -Label "AcCoreConsole"
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $workspaceRoot (".artifacts\atsbuild-harness\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$runDirectory = New-Directory -Path $OutputDir
$artifactDirectory = New-Directory -Path (Join-Path $runDirectory "artifacts")
$workingDirectory = New-Directory -Path (Join-Path $runDirectory "working")
$isolateDirectory = New-Directory -Path (Join-Path $runDirectory "accore-isolate")
$scriptPath = Join-Path $workingDirectory "run.scr"
$runnerStdOutFileName = if ($Runner -eq "FullAutoCAD") { "acad.stdout.log" } else { "accoreconsole.stdout.log" }
$runnerStdErrFileName = if ($Runner -eq "FullAutoCAD") { "acad.stderr.log" } else { "accoreconsole.stderr.log" }
$consoleStdOutPath = Join-Path $artifactDirectory $runnerStdOutFileName
$consoleStdErrPath = Join-Path $artifactDirectory $runnerStdErrFileName
$pluginRunLogPath = Join-Path $artifactDirectory "AtsBackgroundBuilder.run.log"
$pluginFullLogCopyPath = Join-Path $artifactDirectory "AtsBackgroundBuilder.full.log"
$reviewReportPath = Join-Path $artifactDirectory "review-report.json"
$reviewStdOutPath = Join-Path $artifactDirectory "review.stdout.log"
$sourceDrawingExtension = [System.IO.Path]::GetExtension($resolvedDwgPath)
if ([string]::IsNullOrWhiteSpace($sourceDrawingExtension)) {
    $sourceDrawingExtension = ".dwg"
}
$workingDwgPath = Join-Path $workingDirectory ([System.IO.Path]::GetFileNameWithoutExtension($resolvedDwgPath) + ".working" + $sourceDrawingExtension)
$dxfPath = Join-Path $artifactDirectory "output.dxf"
$launcherWorkspace = $null
$runnerResult = $null
$launcherDirectorySummary = $null
$resolvedFullAutoCadProfileName = $null

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

if ($Runner -eq "FullAutoCAD") {
    $resolvedFullAutoCadProfileName = Ensure-FullAutoCadHarnessProfile -ProfileName $FullAutoCadProfileName
    $runTag = (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $launcherWorkspace = Initialize-FullAutoCadLauncherWorkspace `
        -RootPath $AutoCadLauncherRoot `
        -ResolvedDwgPath $resolvedDwgPath `
        -ResolvedWorkbookPath $resolvedWorkbookPath `
        -ResolvedPluginDllPath $resolvedPluginDllPath `
        -RunTag $runTag `
        -LeaveAutoCadOpen:$LeaveAutoCadOpen
    $launcherDirectorySummary = $launcherWorkspace.RunDirectory
    Copy-Item -LiteralPath $launcherWorkspace.ScriptPath -Destination (Join-Path $artifactDirectory "acad.run.scr") -Force

    $environmentVariables = @{
        ATSBUILD_BATCH_WORKBOOK = $launcherWorkspace.WorkbookPath
        ATSBUILD_BATCH_DXF_PATH = $launcherWorkspace.DxfPath
    }

    $runnerResult = Invoke-FullAutoCadBatch `
        -AcadPath $resolvedAcadPath `
        -DwgPath $launcherWorkspace.DwgPath `
        -ScriptPath $launcherWorkspace.ScriptPath `
        -WorkingDirectory $launcherWorkspace.RunDirectory `
        -ExpectedDxfPath $launcherWorkspace.DxfPath `
        -PluginLogPath $pluginLogPath `
        -PluginLogOffset $pluginLogOffset `
        -CompletionMarker $completionMarker `
        -TimeoutSeconds $FullAutoCadTimeoutSeconds `
        -EnvironmentVariables $environmentVariables `
        -StdOutPath $consoleStdOutPath `
        -StdErrPath $consoleStdErrPath `
        -LeaveAutoCadOpen:$LeaveAutoCadOpen `
        -ProfileName $resolvedFullAutoCadProfileName `
        -GracefulExitSeconds $FullAutoCadGracefulExitSeconds

    if (Test-Path -LiteralPath $launcherWorkspace.DxfPath) {
        Copy-Item -LiteralPath $launcherWorkspace.DxfPath -Destination $dxfPath -Force
    }
}
else {
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
        "SECURELOAD",
        "1",
        "FILEDIA",
        "1",
        "CMDDIA",
        "1",
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

    $runnerResult = Invoke-ExternalProcess `
        -FilePath $resolvedAccoreConsolePath `
        -Arguments $accoreArguments `
        -WorkingDirectory $runDirectory `
        -EnvironmentVariables $environmentVariables `
        -StdOutPath $consoleStdOutPath `
        -StdErrPath $consoleStdErrPath
}

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
    $batchCompleted = [bool](Select-String -Path $resolvedPluginSummaryPath -SimpleMatch $completionMarker -Quiet)
}
elseif ($runnerResult -and $runnerResult.PSObject.Properties["BatchCompleted"]) {
    $batchCompleted = [bool]$runnerResult.BatchCompleted
}

$reviewExitCode = $null
$cleanupWarnings = @()
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

$preserveWorkingArtifacts = $KeepWorkingDrawing.IsPresent -or $LeaveAutoCadOpen.IsPresent
if (-not $preserveWorkingArtifacts) {
    Try-RemovePathQuietly -Path $workingDwgPath -Warnings ([ref]$cleanupWarnings)

    if ($launcherWorkspace) {
        Try-RemovePathQuietly -Path $launcherWorkspace.RunDirectory -Recurse -Warnings ([ref]$cleanupWarnings)
    }
}

$summary = [pscustomobject]@{
    runner = $Runner
    runDirectory = $runDirectory
    launcherDirectory = $launcherDirectorySummary
    fullAutoCadProfile = $resolvedFullAutoCadProfileName
    workbook = $resolvedWorkbookPath
    dxf = $(if (Test-Path -LiteralPath $dxfPath) { $dxfPath } else { $null })
    pluginLog = $resolvedPluginSummaryPath
    consoleStdOut = $consoleStdOutPath
    consoleStdErr = $consoleStdErrPath
    reviewReport = $(if (Test-Path -LiteralPath $reviewReportPath) { $reviewReportPath } else { $null })
    runnerExitCode = $(if ($runnerResult) { $runnerResult.ExitCode } else { $null })
    runnerTimedOut = $(if ($runnerResult -and $runnerResult.PSObject.Properties["TimedOut"]) { $runnerResult.TimedOut } else { $false })
    runnerForcedStop = $(if ($runnerResult -and $runnerResult.PSObject.Properties["ForcedStop"]) { $runnerResult.ForcedStop } else { $false })
    leaveAutoCadOpenRequested = $LeaveAutoCadOpen.IsPresent
    autoCadLeftOpen = $(if ($runnerResult -and $runnerResult.PSObject.Properties["LeftOpen"]) { $runnerResult.LeftOpen } else { $false })
    autoCadProcessAlive = $(if ($runnerResult -and $runnerResult.PSObject.Properties["ProcessAlive"]) { $runnerResult.ProcessAlive } else { $false })
    autoCadProcessId = $(if ($runnerResult -and $runnerResult.PSObject.Properties["ProcessId"]) { $runnerResult.ProcessId } else { $null })
    openDrawing = $(if ($LeaveAutoCadOpen.IsPresent -and $launcherWorkspace) { $launcherWorkspace.DwgPath } else { $null })
    accoreExitCode = $(if ($Runner -eq "AccoreConsole" -and $runnerResult) { $runnerResult.ExitCode } else { $null })
    reviewExitCode = $reviewExitCode
    reviewEnabled = $reviewEnabled
    batchCompleted = $batchCompleted
    cleanupWarnings = $cleanupWarnings
}

$summary | ConvertTo-Json -Depth 4

if (-not $summary.dxf -or -not $batchCompleted) {
    if ($runnerResult -and $runnerResult.PSObject.Properties["TimedOut"] -and $runnerResult.TimedOut) {
        exit 124
    }

    if ($runnerResult -and $null -ne $runnerResult.ExitCode -and $runnerResult.ExitCode -ne 0) {
        exit $runnerResult.ExitCode
    }

    exit 1
}

if ($reviewEnabled -and $null -ne $reviewExitCode -and $reviewExitCode -ne 0) {
    exit $reviewExitCode
}
