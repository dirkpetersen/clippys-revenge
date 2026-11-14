<#
.SYNOPSIS
  Runs the opt-in live Outlook tests (SPEC 15) against your running classic Outlook.

.DESCRIPTION
  Pre-checks Outlook (running, responding), sets CLIPPY_LIVE=1, builds, runs
  `dotnet test --filter live=outlook` with a hard timeout, and prints a summary.
  The tests create drafts and two unsent meeting drafts (create_meeting_draft), delete them again, and check that
  nothing was sent. Results and leftovers are printed at the end.

  Exit codes: 0 ok; 1 test or build failures; 2 preconditions; 3 Outlook not responding;
  4 timeout; 5 only leftovers.

.PARAMETER TimeoutMinutes  Hard limit for the whole test run (default 20).
.PARAMETER Shared          SMTP address of a shared mailbox open in Outlook (enables the shared-mailbox tests).
.PARAMETER AllowClosed     Run only the attach-never-launches test when Outlook is closed.
.PARAMETER Claude          Also run the live Claude tests (sets CLIPPY_LIVE_CLAUDE=1). They use a little of your Claude usage (two small Haiku calls).
.PARAMETER NoBuild         Skip `dotnet build` (use the last build).
.PARAMETER DryRun          Print what would run, then exit 0 without running anything.
#>
[CmdletBinding()]
param(
    [int]$TimeoutMinutes = 20,
    [string]$Shared,
    [switch]$AllowClosed,
    [switch]$Claude,
    [switch]$NoBuild,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$isWin = $true
if (Test-Path variable:IsWindows) { $isWin = [bool]$IsWindows }   # PowerShell 5.1 has no $IsWindows and is always Windows
if (-not $isWin) {
    Write-Host 'Live tests need Windows with classic Outlook.'
    exit 2
}


# First line of <Output>/<ErrorInfo>/<Message> or <Output>/<StdOut> of a trx result.
# SelectSingleNode keeps this safe under strict mode (the trx has a default namespace).
function Get-FirstLine($result, [string]$kind) {
    $xpath = "*[local-name()='Output']/*[local-name()='StdOut']"
    if ($kind -eq 'Message') { $xpath = "*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']" }
    $node = $result.SelectSingleNode($xpath)
    if ($null -eq $node) { return '' }
    return (($node.InnerText -split "`r?`n")[0])
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = Join-Path $env:TEMP 'clippy-live'
$results = Join-Path $root "results-$stamp.txt"
$resultsDir = Join-Path $root "TestResults-$stamp"
$filter = 'live=outlook'
$useClaude = [bool]$Claude

# 1. Outlook must be running.
$outlook = @(Get-Process OUTLOOK -ErrorAction SilentlyContinue)
if ($outlook.Count -eq 0) {
    if (-not $AllowClosed) {
        Write-Host 'Outlook is not running. Start classic Outlook (not the new Outlook) and try again.'
        exit 2
    }
    $filter = 'FullyQualifiedName~Attach_never_launches_outlook'
    $useClaude = $false
}

# 2. Outlook must respond (no COM from this script).
if ($outlook.Count -gt 0) {
    $responding = $false
    for ($try = 1; $try -le 3; $try++) {
        $procs = @(Get-Process OUTLOOK -ErrorAction SilentlyContinue)
        if ($procs.Count -gt 0 -and -not (@($procs | Where-Object { -not $_.Responding }).Count -gt 0)) {
            $responding = $true
            break
        }
        if ($try -lt 3) { Start-Sleep -Seconds 5 }
    }
    if (-not $responding) {
        Write-Host 'Outlook is not responding. Check it for an open dialog, or wait until it finishes starting, then try again.'
        exit 3
    }
}

# 3. The tray app may run. It only makes the run slower.
if (@(Get-Process ClippysRevenge -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-Warning "Clippy's Revenge is running. That is OK, but pausing sync from the tray makes the run faster."
}

if ($useClaude) { $filter = 'live=outlook|live=claude' }

$testArgs = @(
    'test', 'tests/ClippysRevenge.Core.Tests', '-c', 'Release', '--no-build',
    '--filter', $filter,
    '--logger', 'trx;LogFileName=live.trx',
    '--logger', 'console;verbosity=normal',
    '--results-directory', $resultsDir,
    '--blame-hang', '--blame-hang-dump-type', 'none', '--blame-hang-timeout', '11m'
)

# Start-Process joins the array without quoting; quote what needs it.
$quotedArgs = @($testArgs | ForEach-Object { if ($_ -match '[\s;]') { '"' + $_ + '"' } else { $_ } })

if ($DryRun) {
    Write-Host 'Dry run. Nothing is executed.'
    if (-not $NoBuild) { Write-Host '  dotnet build -c Release' }
    Write-Host ('  dotnet ' + ($testArgs -join ' '))
    Write-Host '  CLIPPY_LIVE=1'
    if ($useClaude) { Write-Host '  CLIPPY_LIVE_CLAUDE=1' }
    Write-Host "  CLIPPY_LIVE_RESULTS=$results"
    if ($Shared) { Write-Host "  CLIPPY_LIVE_SHARED=$Shared" }
    Write-Host "  Timeout: $TimeoutMinutes minutes"
    exit 0
}

# 4. Environment, restored in finally.
$saved = @{}
foreach ($name in 'CLIPPY_LIVE', 'CLIPPY_LIVE_RESULTS', 'CLIPPY_LIVE_SHARED', 'CLIPPY_LIVE_CLAUDE') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$exit = 0
try {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $env:CLIPPY_LIVE = '1'
    # A plain run never spends Claude usage, even when the variable is set in the user's shell.
    if ($useClaude) { $env:CLIPPY_LIVE_CLAUDE = '1' } else { Remove-Item Env:CLIPPY_LIVE_CLAUDE -ErrorAction SilentlyContinue }
    $env:CLIPPY_LIVE_RESULTS = $results
    if ($Shared) { $env:CLIPPY_LIVE_SHARED = $Shared } else { Remove-Item Env:CLIPPY_LIVE_SHARED -ErrorAction SilentlyContinue }

    Push-Location $repo
    try {
        if (-not $NoBuild) {
            & dotnet build -c Release
            if ($LASTEXITCODE -ne 0) {
                Write-Host 'The build failed.'
                exit 1
            }
        }

        $timedOut = $false
        $proc = Start-Process -FilePath 'dotnet' -ArgumentList $quotedArgs -PassThru -NoNewWindow -WorkingDirectory $repo
        if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
            $timedOut = $true
            & taskkill /T /F /PID $proc.Id | Out-Null
            Write-Host ('Timed out after {0} minutes; the test run was stopped. Check Outlook, then look in Drafts, Calendar and Deleted Items for "[clippy-live-" items.' -f $TimeoutMinutes)
        }
    }
    finally {
        Pop-Location
    }

    # 5. Summary.
    $passed = 0; $failed = 0; $skipped = 0
    $failedList = @(); $skippedList = @()
    $trx = Get-ChildItem -Path $resultsDir -Filter 'live.trx' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($trx) {
        [xml]$doc = Get-Content -LiteralPath $trx.FullName -Raw
        foreach ($r in @($doc.GetElementsByTagName('UnitTestResult'))) {
            switch ($r.outcome) {
                'Passed' { $passed++ }
                'Failed' {
                    $failed++
                    $failedList += ('  {0}: {1}' -f $r.testName, (Get-FirstLine $r 'Message'))
                }
                'NotExecuted' {
                    $skipped++
                    $why = Get-FirstLine $r 'Message'
                    if (-not $why) { $why = Get-FirstLine $r 'StdOut' }
                    $skippedList += ('  {0}: {1}' -f $r.testName, $why)
                }
                default { }
            }
        }
    }
    else {
        Write-Host 'No test result file was written.'
        if (-not $timedOut) { $failed = 1 }
    }

    Write-Host ''
    Write-Host ('Passed: {0}  Failed: {1}  Skipped: {2}' -f $passed, $failed, $skipped)
    if ($failedList.Count -gt 0) { Write-Host 'Failed:'; $failedList | ForEach-Object { Write-Host $_ } }
    if ($skippedList.Count -gt 0) { Write-Host 'Skipped:'; $skippedList | ForEach-Object { Write-Host $_ } }

    $lines = @()
    if (Test-Path -LiteralPath $results) { $lines = @(Get-Content -LiteralPath $results -Encoding UTF8) }
    $resultLines = @($lines | Where-Object { $_ -like 'LIVE-RESULT:*' })
    $leftovers = @($lines | Where-Object { $_ -like 'LIVE-LEFTOVER:*' })
    if ($resultLines.Count -gt 0) { Write-Host ''; $resultLines | ForEach-Object { Write-Host $_ } }
    if ($leftovers.Count -gt 0) {
        Write-Host ''
        $leftovers | ForEach-Object { Write-Host $_ }
        Write-Host 'Delete the items above in Outlook (they were never sent).'
    }

    if ($timedOut) { $exit = 4 }
    elseif ($failed -gt 0) { $exit = 1 }
    elseif ($leftovers.Count -gt 0) { $exit = 5 }
    else { $exit = 0 }
}
finally {
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
}
exit $exit
