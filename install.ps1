<#
.SYNOPSIS
  Installs or uninstalls Clippy's Revenge for the current user (no admin needed).
.PARAMETER Source
  Path to a local ClippysRevenge.exe. If omitted, the latest GitHub release is
  downloaded and checked against SHA256SUMS.txt.
.PARAMETER Uninstall
  Stops the app and removes the exe, Start menu shortcut and "Start with
  Windows" entry. Asks before deleting settings, token, index and logs.
.PARAMETER Purge
  With -Uninstall: delete settings, token, index and logs without asking.
.PARAMETER NoStart
  Don't launch the app after installing.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Source,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Repo        = 'dirkpetersen/clippys-revenge'
$AppName     = 'ClippysRevenge'
$InstallDir  = Join-Path $env:LOCALAPPDATA 'Programs\ClippysRevenge'
$ExePath     = Join-Path $InstallDir 'ClippysRevenge.exe'
$ShortcutDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$Shortcut    = Join-Path $ShortcutDir "Clippy's Revenge.lnk"
$RunKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$SettingsDir = Join-Path $env:APPDATA 'ClippysRevenge'
$DataDir     = Join-Path $env:LOCALAPPDATA 'ClippysRevenge'

function Stop-App {
    $procs = Get-Process -Name $AppName -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        if ($PSCmdlet.ShouldProcess("process $($p.Id)", 'Stop')) {
            Write-Host "Stopping $AppName (pid $($p.Id))..."
            $null = $p.CloseMainWindow()
            if (-not $p.WaitForExit(3000)) { Stop-Process -Id $p.Id -Force }
            $null = $p.WaitForExit(5000)
        }
    }
}

function Remove-IfExists([string]$Path, [switch]$Recurse) {
    if (Test-Path -LiteralPath $Path) {
        if ($PSCmdlet.ShouldProcess($Path, 'Remove')) {
            Remove-Item -LiteralPath $Path -Recurse:$Recurse -Force
            Write-Host "Removed $Path"
        }
    }
}

function Get-ReleaseExe {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('clippy-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'ClippysRevenge-installer' }
    Write-Host 'Looking up the latest release...'
    # /releases (not /releases/latest) so that 0.x pre-releases are found too.
    $rel = @(Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases?per_page=1")[0]
    $exeAsset = $rel.assets | Where-Object { $_.name -eq 'ClippysRevenge.exe' } | Select-Object -First 1
    $sumAsset = $rel.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
    if (-not $exeAsset -or -not $sumAsset) {
        throw "Release $($rel.tag_name) has no ClippysRevenge.exe or SHA256SUMS.txt asset."
    }
    $exe  = Join-Path $tmp 'ClippysRevenge.exe'
    $sums = Join-Path $tmp 'SHA256SUMS.txt'
    Write-Host "Downloading $($rel.tag_name)..."
    Invoke-WebRequest -Headers $headers -Uri $exeAsset.browser_download_url -OutFile $exe -UseBasicParsing
    Invoke-WebRequest -Headers $headers -Uri $sumAsset.browser_download_url -OutFile $sums -UseBasicParsing

    $expected = $null
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq 'ClippysRevenge.exe') {
            $expected = $Matches[1].ToLowerInvariant()
        }
    }
    if (-not $expected) { throw 'SHA256SUMS.txt has no entry for ClippysRevenge.exe.' }
    $actual = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $tmp -Recurse -Force
        throw "SHA-256 mismatch for the downloaded exe (expected $expected, got $actual). Nothing was installed."
    }
    Write-Host 'SHA-256 verified.'
    return $exe
}

if ($Uninstall) {
    Stop-App
    if ($PSCmdlet.ShouldProcess('HKCU Run\ClippysRevenge', 'Remove')) {
        Remove-ItemProperty -Path $RunKey -Name $AppName -ErrorAction SilentlyContinue
    }
    Remove-IfExists $Shortcut
    Remove-IfExists $InstallDir -Recurse

    $doPurge = $Purge.IsPresent
    if (-not $doPurge -and ((Test-Path -LiteralPath $SettingsDir) -or (Test-Path -LiteralPath $DataDir))) {
        $answer = Read-Host "Also delete settings, auth token, mail index and logs ($SettingsDir and $DataDir)? [y/N]"
        $doPurge = $answer -match '^(y|yes)$'
    }
    if ($doPurge) {
        Remove-IfExists $SettingsDir -Recurse
        Remove-IfExists $DataDir -Recurse
    } else {
        Write-Host 'Kept your settings, token, index and logs.'
    }
    Write-Host "Clippy's Revenge uninstalled."
    return
}

# Install
if ($Source) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Source file not found: $Source" }
    $src = (Resolve-Path -LiteralPath $Source).Path
} else {
    $src = Get-ReleaseExe
}

Stop-App
if ($PSCmdlet.ShouldProcess($ExePath, 'Install')) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -LiteralPath $src -Destination $ExePath -Force
    Write-Host "Installed to $ExePath"

    New-Item -ItemType Directory -Force -Path $ShortcutDir | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Shortcut)
    $lnk.TargetPath = $ExePath
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = "Clippy's Revenge - MCP server for the desktop mail client"
    $lnk.Save()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    Write-Host "Start menu shortcut: $Shortcut"

    if (-not $NoStart) {
        # Starts the tray app only. The app attaches to a running mail client and never starts one.
        Start-Process -FilePath $ExePath
        Write-Host 'Started. Right-click the paperclip in the tray and choose Copy "claude mcp add" command.'
    }
}
