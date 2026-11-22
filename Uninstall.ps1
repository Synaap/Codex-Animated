[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$RemoveData,
    [switch]$FromSettings,
    [switch]$Quiet
)
# Removes the install: stops the helpers, drops the logon entries, retargets or deletes
# shortcuts that point into an install folder, removes the folders and the Installed apps entry.
# -FromSettings: started from Windows Settings (Windows already asked). -Quiet: no questions, no waiting.
$ErrorActionPreference = 'Stop'
# A console opened in the install folder must not keep that folder in use.
[Environment]::CurrentDirectory = [IO.Path]::GetTempPath()
if ($FromSettings -or $Quiet) { $ConfirmPreference = 'None' }
$installs = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers'),
    (Join-Path $env:LOCALAPPDATA 'Programs\DevinAnimated'))
$names = 'AnimatedLauncher', 'DevinAnimated', 'AntigravityAnimated', 'CodexAnimated', 'ClaudeAnimated',
    'VSCodeAnimated', 'CursorAnimated', 'KiroAnimated', 'WindsurfAnimated', 'VSCodiumAnimated', 'ObsidianAnimated', 'ThemeStudio'
$native = @{
    'DevinAnimated.exe'       = Join-Path $env:LOCALAPPDATA 'Programs\Devin\Devin.exe'
    'AntigravityAnimated.exe' = Join-Path $env:LOCALAPPDATA 'Programs\Antigravity IDE\Antigravity IDE.exe'
    'ClaudeAnimated.exe'      = Join-Path $env:LOCALAPPDATA 'AnthropicClaude\claude.exe'
    'VSCodeAnimated.exe'      = Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\Code.exe'
    'CursorAnimated.exe'      = Join-Path $env:LOCALAPPDATA 'Programs\cursor\Cursor.exe'
    'KiroAnimated.exe'        = Join-Path $env:LOCALAPPDATA 'Programs\Kiro\Kiro.exe'
    'WindsurfAnimated.exe'    = Join-Path $env:LOCALAPPDATA 'Programs\Windsurf\Windsurf.exe'
    'VSCodiumAnimated.exe'    = Join-Path $env:LOCALAPPDATA 'Programs\VSCodium\VSCodium.exe'
    'ObsidianAnimated.exe'    = Join-Path $env:LOCALAPPDATA 'Programs\Obsidian\Obsidian.exe'
}
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AnimatedLaunchers'
function Test-Inside([string]$path) {
    foreach ($folder in $installs) {
        if ($path -and $path.StartsWith($folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

$wait = $FromSettings -and -not $Quiet -and -not $WhatIfPreference
try {
    if ($FromSettings -and -not $WhatIfPreference) {
        $answer = Read-Host 'Also delete your themes and settings? Type y and press Enter to delete them, or just press Enter to keep them'
        if ($answer -match '^(y|yes)$') { $RemoveData = $true }
    }

    foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
        if ($PSCmdlet.ShouldProcess("$($process.ProcessName) ($($process.Id))", 'stop process')) {
            try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { Write-Warning $_.Exception.Message }
        }
    }
    if (-not $WhatIfPreference) {
        $deadline = (Get-Date).AddSeconds(15)
        while (@(Get-Process -Name $names -ErrorAction SilentlyContinue).Count -gt 0) {
            if ((Get-Date) -gt $deadline) { throw 'The helpers did not exit. Close them and try again.' }
            Start-Sleep -Milliseconds 200
        }
    }

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    foreach ($name in 'AnimatedLaunchers', 'DevinAnimated', 'AntigravityAnimated', 'CodexAnimated', 'ClaudeAnimated',
        'VSCodeAnimated', 'CursorAnimated', 'KiroAnimated', 'WindsurfAnimated', 'VSCodiumAnimated', 'ObsidianAnimated') {
        if ($null -ne (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue)) {
            if ($PSCmdlet.ShouldProcess("$runKey\$name", 'remove Run value')) {
                Remove-ItemProperty -Path $runKey -Name $name
            }
        }
    }

    $shell = New-Object -ComObject WScript.Shell
    $roots = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        [Environment]::GetFolderPath('Desktop'),
        (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'))
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
            $link = $shell.CreateShortcut($file.FullName)
            if (-not (Test-Inside $link.TargetPath)) { continue }
            $replacement = $native[[IO.Path]::GetFileName($link.TargetPath)]
            if ($replacement -and (Test-Path -LiteralPath $replacement)) {
                if ($PSCmdlet.ShouldProcess($file.FullName, "retarget to $replacement")) {
                    $link.TargetPath = $replacement
                    $link.Arguments = ''
                    $link.WorkingDirectory = Split-Path -Parent $replacement
                    $link.Save()
                }
            }
            elseif ($PSCmdlet.ShouldProcess($file.FullName, 'delete shortcut')) {
                Remove-Item -LiteralPath $file.FullName
            }
        }
    }

    foreach ($install in $installs) {
        if ((Test-Path -LiteralPath $install) -and $PSCmdlet.ShouldProcess($install, 'remove install folder')) {
            Remove-Item -LiteralPath $install -Recurse -Force
        }
    }

    $dataFolders = 'Devin Animated', 'Antigravity Animated', 'Codex Animated', 'Claude Animated',
        'VS Code Animated', 'Cursor Animated', 'Kiro Animated', 'Windsurf Animated', 'VSCodium Animated',
        'Obsidian Animated', 'Animated Launchers' |
        ForEach-Object { Join-Path $env:LOCALAPPDATA $_ }
    if ($RemoveData) {
        # Apps added in Theme Studio keep their settings in '<Name> Animated'; the
        # names are in custom-apps.ini, which goes away with the shared folder.
        $customList = Join-Path $env:LOCALAPPDATA 'Animated Launchers\custom-apps.ini'
        if (Test-Path -LiteralPath $customList) {
            foreach ($line in [IO.File]::ReadAllLines($customList)) {
                $eq = $line.IndexOf('=')
                if ($eq -lt 1) { continue }
                $customName = $line.Substring($eq + 1).Split('|')[0].Trim()
                if ($customName -and $customName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -lt 0) {
                    $dataFolders += Join-Path $env:LOCALAPPDATA "$customName Animated"
                }
            }
        }
        foreach ($folder in $dataFolders) {
            if ((Test-Path -LiteralPath $folder) -and $PSCmdlet.ShouldProcess($folder, 'remove data folder')) {
                Remove-Item -LiteralPath $folder -Recurse -Force
            }
        }
    }

    # The .altheme association and its ProgID; the extension is only removed when
    # it still points at our ProgID (another handler may have taken it over).
    $progid = 'HKCU:\Software\Classes\AnimatedLaunchers.ThemeFile'
    $ext = 'HKCU:\Software\Classes\.altheme'
    if ((Get-ItemProperty -Path $ext -Name '(default)' -ErrorAction SilentlyContinue).'(default)' -eq 'AnimatedLaunchers.ThemeFile') {
        if ($PSCmdlet.ShouldProcess($ext, 'remove the .altheme association')) { Remove-Item -LiteralPath $ext -Recurse -Force }
    }
    if ((Test-Path -LiteralPath $progid) -and $PSCmdlet.ShouldProcess($progid, 'remove the theme file ProgID')) {
        Remove-Item -LiteralPath $progid -Recurse -Force
    }

    # Last, so a failed uninstall keeps its entry in Installed apps.
    if ((Test-Path -LiteralPath $uninstallKey) -and $PSCmdlet.ShouldProcess($uninstallKey, 'remove the Installed apps entry')) {
        Remove-Item -LiteralPath $uninstallKey -Recurse -Force
    }

    if (-not $WhatIfPreference) {
        Write-Host 'Animated Launchers was uninstalled.'
        Write-Host 'The helpers were stopped, their start-with-Windows entries were removed, shortcuts were pointed back to the apps (or removed), and the install folder and the Installed apps entry were deleted.'
        if ($RemoveData) { Write-Host 'Your themes and settings were deleted.' }
        else {
            Write-Host 'Your themes and settings were kept. They are in these folders (delete them yourself if you want them gone):'
            foreach ($folder in $dataFolders) { if (Test-Path -LiteralPath $folder) { Write-Host "  $folder" } }
        }
    }
    if ($wait) { [void](Read-Host 'Press Enter to close') }
}
catch {
    if (-not $FromSettings) { throw }
    Write-Host ''
    Write-Host "The uninstall stopped: $($_.Exception.Message)"
    Write-Host 'Nothing after this point was changed.'
    if ($wait) { [void](Read-Host 'Press Enter to close') }
    exit 1
}
