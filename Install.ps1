[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoStart
)
# Installs the executables lying next to this script (building them first when the source is here),
# creates the per-app launcher copies, then restarts the shared wallpaper helper.
$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
$destination = Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers'
$oldFolder = Join-Path $env:LOCALAPPDATA 'Programs\DevinAnimated'
# The one binary, then the launcher name each app keeps for its shortcuts and pins.
$launcher = 'AnimatedLauncher.exe'
$apps = @(
    @{ App = 'Devin';       Exe = 'DevinAnimated.exe';       Product = 'Devin Animated';       IconFor = 'Devin.exe';          Folder = 'Devin';           Scheme = 'devin' },
    @{ App = 'Antigravity'; Exe = 'AntigravityAnimated.exe'; Product = 'Antigravity Animated'; IconFor = 'Antigravity IDE.exe'; Folder = 'Antigravity IDE';  Scheme = 'antigravity' },
    @{ App = 'Codex';       Exe = 'CodexAnimated.exe';       Product = 'Codex Animated';       IconFor = '';                  Folder = '';               Scheme = '' },
    @{ App = 'Claude';      Exe = 'ClaudeAnimated.exe';      Product = 'Claude Animated';      IconFor = 'claude.exe';         Folder = '';               Scheme = 'claude' },
    @{ App = 'VS Code';     Exe = 'VSCodeAnimated.exe';      Product = 'VS Code Animated';     IconFor = 'Code.exe';           Folder = 'Microsoft VS Code'; Scheme = 'vscode' },
    @{ App = 'Cursor';      Exe = 'CursorAnimated.exe';      Product = 'Cursor Animated';      IconFor = 'Cursor.exe';         Folder = 'cursor';         Scheme = 'cursor' },
    @{ App = 'Kiro';        Exe = 'KiroAnimated.exe';        Product = 'Kiro Animated';        IconFor = 'Kiro.exe';           Folder = 'Kiro';           Scheme = 'kiro' },
    @{ App = 'Windsurf';    Exe = 'WindsurfAnimated.exe';    Product = 'Windsurf Animated';    IconFor = 'Windsurf.exe';       Folder = 'Windsurf';       Scheme = 'windsurf' },
    @{ App = 'VSCodium';    Exe = 'VSCodiumAnimated.exe';    Product = 'VSCodium Animated';    IconFor = 'VSCodium.exe';       Folder = 'VSCodium';       Scheme = 'vscodium' },
    @{ App = 'Obsidian';    Exe = 'ObsidianAnimated.exe';    Product = 'Obsidian Animated';    IconFor = 'Obsidian.exe';       Folder = 'Obsidian';       Scheme = 'obsidian' }
)

# Whether the desktop app is on this machine. ANIMATED_INSTALL_ASSUME (a comma-separated list) overrides this for tests.
function Test-AppInstalled([string]$app) {
    if ($env:ANIMATED_INSTALL_ASSUME) {
        return @($env:ANIMATED_INSTALL_ASSUME -split ',' | ForEach-Object { $_.Trim() }) -contains $app
    }
    if ($app -eq 'Codex') {
        if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages\OpenAI.Codex_2p2nqsd0c76g0')) { return $true }
        try { return $null -ne (Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop | Select-Object -First 1) } catch { return $false }
    }
    if ($app -eq 'Claude') {
        $claudeRoot = Join-Path $env:LOCALAPPDATA 'AnthropicClaude'
        if (Test-Path -LiteralPath (Join-Path $claudeRoot 'claude.exe')) { return $true }
        if (@(Get-ChildItem -Path (Join-Path $claudeRoot 'app-*\claude.exe') -ErrorAction SilentlyContinue).Count -gt 0) { return $true }
        try {
            $command = (Get-ItemProperty -Path 'HKCU:\Software\Classes\claude\shell\open\command' -ErrorAction Stop).'(default)'
            if ($command -match '^\s*"([^"]+)"' -and (Test-Path -LiteralPath $Matches[1]) -and
                (Split-Path -Leaf $Matches[1]) -ieq 'claude.exe') { return $true }
        } catch { }
        return $false
    }
    $row = $apps | Where-Object { $_.App -eq $app } | Select-Object -First 1
    $roots = @((Join-Path $env:LOCALAPPDATA 'Programs'), $env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    foreach ($root in $roots) { if (Test-Path -LiteralPath (Join-Path $root "$($row.Folder)\$($row.IconFor)")) { return $true } }
    if ($row.Scheme) {
        # A stale registration can point at another app's exe: the file name has to match too.
        try {
            $command = (Get-ItemProperty -Path ('HKCU:\Software\Classes\' + $row.Scheme + '\shell\open\command') -ErrorAction Stop).'(default)'
            if ($command -match '^\s*"([^"]+)"' -and (Test-Path -LiteralPath $Matches[1]) -and
                (Split-Path -Leaf $Matches[1]) -ieq $row.IconFor) { return $true }
        } catch { }
    }
    return $false
}

# The real application's executable, for taking its icon. $null when it cannot be found.
function Find-AppExe([hashtable]$app) {
    if ($app.App -eq 'Codex') {
        # The Store package keeps the icon inside its installed folder, under app\.
        try {
            $pkg = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop | Select-Object -First 1
            if ($pkg) {
                foreach ($name in 'app\ChatGPT.exe', 'app\Codex.exe', 'ChatGPT.exe', 'Codex.exe') {
                    $exe = Join-Path $pkg.InstallLocation $name
                    if (Test-Path -LiteralPath $exe) { return $exe }
                }
            }
        } catch { }
        return $null
    }
    if ($app.App -eq 'Claude') {
        $claudeRoot = Join-Path $env:LOCALAPPDATA 'AnthropicClaude'
        $stub = Join-Path $claudeRoot 'claude.exe'
        if (Test-Path -LiteralPath $stub) { return $stub }
        $latest = @(Get-ChildItem -Path (Join-Path $claudeRoot 'app-*\claude.exe') -ErrorAction SilentlyContinue |
            Sort-Object -Property Name -Descending | Select-Object -First 1)
        if ($latest.Count -gt 0) { return $latest[0].FullName }
        return $null
    }
    $roots = @((Join-Path $env:LOCALAPPDATA 'Programs'), $env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
    foreach ($root in $roots) {
        $exe = Join-Path $root "$($app.Folder)\$($app.IconFor)"
        if (Test-Path -LiteralPath $exe) { return $exe }
    }
    return $null
}

$exes = @($launcher) + 'ThemeStudio.exe'
$names = @($apps | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Exe) }) + 'AnimatedLauncher', 'ThemeStudio'

# 1. Build, when this is a source checkout
$build = Join-Path $package 'source\Build.ps1'
if (-not $NoBuild -and (Test-Path -LiteralPath $build)) { & $build }
foreach ($exe in $exes) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $exe))) {
        throw "$exe is not next to Install.ps1. Extract the whole release zip first, or run this from a source checkout without -NoBuild."
    }
}
$uninstallers = 'Uninstall.ps1', 'Uninstall.cmd'
foreach ($file in $uninstallers) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $file))) {
        throw "$file is not next to Install.ps1. Extract the whole release zip first."
    }
}

# 2. Stop the helpers and Theme Studio
foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
    if ($process.ProcessName -eq 'ThemeStudio') {
        if ($process.CloseMainWindow()) { [void]$process.WaitForExit(3000) }
    }
}
foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
    try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch { }
}
$deadline = (Get-Date).AddSeconds(15)
while (@(Get-Process -Name $names -ErrorAction SilentlyContinue).Count -gt 0) {
    if ((Get-Date) -gt $deadline) { throw 'The helpers did not exit.' }
    Start-Sleep -Milliseconds 200
}

# 3. Copy and verify
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$summary = @()
$launcherFrom = Join-Path $package $launcher
$launcherTo = Join-Path $destination $launcher
Copy-Item -LiteralPath $launcherFrom -Destination $launcherTo -Force
$hash = (Get-FileHash -LiteralPath $launcherFrom -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $launcherTo -Algorithm SHA256).Hash -ne $hash) { throw "Copy of $launcher does not match the original." }
Unblock-File -LiteralPath $launcherTo
$summary += [pscustomobject]@{ File = $launcher; Sha256 = $hash.Substring(0, 12); Installed = ''; Run = ''; Pid = '' }
foreach ($app in $apps) {
    $to = Join-Path $destination $app.Exe
    Copy-Item -LiteralPath $launcherTo -Destination $to -Force
    if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $hash) { throw "Copy of $($app.Exe) does not match the launcher." }
    Unblock-File -LiteralPath $to
    $summary += [pscustomobject]@{ File = $app.Exe; Sha256 = '(copy)'; Installed = ''; Run = ''; Pid = '' }
}
$studioFrom = Join-Path $package 'ThemeStudio.exe'
$studioTo = Join-Path $destination 'ThemeStudio.exe'
Copy-Item -LiteralPath $studioFrom -Destination $studioTo -Force
$studioHash = (Get-FileHash -LiteralPath $studioFrom -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $studioTo -Algorithm SHA256).Hash -ne $studioHash) { throw 'Copy of ThemeStudio.exe does not match the original.' }
Unblock-File -LiteralPath $studioTo
$summary += [pscustomobject]@{ File = 'ThemeStudio.exe'; Sha256 = $studioHash.Substring(0, 12); Installed = ''; Run = ''; Pid = '' }
# The uninstaller stays on the PC, so Windows Settings can run it.
foreach ($file in $uninstallers) {
    $to = Join-Path $destination $file
    Copy-Item -LiteralPath (Join-Path $package $file) -Destination $to -Force
    Unblock-File -LiteralPath $to
}

# 3b. Icons: each launcher copy shows its own app's icon, taken from the installed app.
$iconFolder = Join-Path $destination 'icons'
foreach ($app in $apps) { $app.Icon = $null }
try {
    Add-Type -AssemblyName System.Drawing
    foreach ($app in $apps) {
        $appExe = Find-AppExe $app
        if (-not $appExe) { continue }
        $icon = [Drawing.Icon]::ExtractAssociatedIcon($appExe)
        if ($null -eq $icon) { continue }
        New-Item -ItemType Directory -Force -Path $iconFolder | Out-Null
        $iconPath = Join-Path $iconFolder ([IO.Path]::GetFileNameWithoutExtension($app.Exe) + '.ico')
        $stream = [IO.File]::Create($iconPath)
        try { $icon.Save($stream) } finally { $stream.Close() }
        $icon.Dispose()
        $app.Icon = $iconPath
    }
} catch { Write-Output "icons: could not save app icons ($($_.Exception.Message)); shortcuts keep the default icon" }

# 4. Move shortcuts and remove the old install folder (earlier versions installed to Programs\DevinAnimated)
$shell = New-Object -ComObject WScript.Shell
$shortcutRoots = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'))
function Test-Inside([string]$path, [string]$folder) {
    return $path -and $path.StartsWith($folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
if (Test-Path -LiteralPath $oldFolder) {
    foreach ($root in $shortcutRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
            $link = $shell.CreateShortcut($file.FullName)
            if (-not (Test-Inside $link.TargetPath $oldFolder)) { continue }
            $before = $link.TargetPath
            $link.TargetPath = Join-Path $destination (Split-Path -Leaf $link.TargetPath)
            if ((Test-Inside $link.WorkingDirectory $oldFolder) -or ($link.WorkingDirectory.TrimEnd('\') -ieq $oldFolder)) { $link.WorkingDirectory = $destination }
            $icon = $link.IconLocation
            if ($icon) {
                $iconPath = ($icon -split ',')[0]
                if (Test-Inside $iconPath $oldFolder) { $link.IconLocation = (Join-Path $destination (Split-Path -Leaf $iconPath)) + ',' + (($icon -split ',')[1]) }
            }
            $link.Save()
            Write-Output ("shortcut {0}: {1} -> {2}" -f $file.FullName, $before, $link.TargetPath)
        }
    }
    foreach ($exe in $exes + ($apps | ForEach-Object { $_.Exe })) { Remove-Item -LiteralPath (Join-Path $oldFolder $exe) -Force -ErrorAction SilentlyContinue }
    if (@(Get-ChildItem -LiteralPath $oldFolder -Force).Count -eq 0) {
        Remove-Item -LiteralPath $oldFolder
        Write-Output "removed the old install folder $oldFolder"
    }
    else { Write-Output "left $oldFolder in place: it still contains other files" }
}

# 4b. Shortcuts pointing at a launcher copy show the app's own icon.
foreach ($root in $shortcutRoots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
        $link = $shell.CreateShortcut($file.FullName)
        foreach ($app in $apps) {
            $target = Join-Path $destination $app.Exe
            if ($link.TargetPath -ine $target -or -not $app.Icon) { continue }
            if ($link.IconLocation -ne ($app.Icon + ',0')) {
                $link.IconLocation = $app.Icon + ',0'
                $link.Save()
                Write-Output ("shortcut icon {0}: {1}" -f $file.FullName, $app.Icon)
            }
        }
    }
}

# 5. Regenerable bundle caches
foreach ($app in $apps) {
    $runtime = Join-Path $env:LOCALAPPDATA "$($app.Product)\Runtime"
    if (Test-Path -LiteralPath $runtime) { Get-ChildItem -LiteralPath $runtime -Force | Remove-Item -Recurse -Force }
}
$sharedRuntime = Join-Path $env:LOCALAPPDATA 'Animated Launchers\Runtime'
if (Test-Path -LiteralPath $sharedRuntime) { Get-ChildItem -LiteralPath $sharedRuntime -Force | Remove-Item -Recurse -Force }

# 6. Logon entry: the four per-app values become the shared AnimatedLaunchers one.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$previousEntry = $false
$migrated = @{}
foreach ($app in $apps) {
    $name = [IO.Path]::GetFileNameWithoutExtension($app.Exe)
    $had = $null -ne (Get-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue)
    if ($had) { $previousEntry = $true }
    $migrated[$app.App] = $had
    Remove-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue
}
$hadShared = $null -ne (Get-ItemProperty -Path $runKey -Name 'AnimatedLaunchers' -ErrorAction SilentlyContinue)
if ($hadShared) { $previousEntry = $true }
$anyInstalled = $false
foreach ($app in $apps) {
    $installed = Test-AppInstalled $app.App
    ($summary | Where-Object { $_.File -eq $app.Exe }).Installed = if ($installed) { 'yes' } else { 'no' }
    if ($installed) { $anyInstalled = $true }
}
# A per-app entry that was missing while others existed had been turned off:
# carry that choice into watch=off. With no legacy entries at all this is a fresh
# install and every app keeps the default (watched; uninstalled apps stay idle).
$hadLegacy = @($migrated.Values) -contains $true
if ($hadLegacy) {
    foreach ($app in $apps) {
        if ($migrated[$app.App]) { continue }
        $folder = Join-Path $env:LOCALAPPDATA $app.Product
        $ini = Join-Path $folder 'settings.ini'
        try {
            New-Item -ItemType Directory -Force -Path $folder | Out-Null
            $lines = @()
            if (Test-Path -LiteralPath $ini) { $lines = @(Get-Content -LiteralPath $ini | Where-Object { $_ -notmatch '^\s*watch=' }) }
            $lines += 'watch=off'
            [IO.File]::WriteAllLines($ini, $lines, (New-Object System.Text.UTF8Encoding($false)))
        } catch { Write-Output "could not write watch=off for $($app.App): $($_.Exception.Message)" }
    }
}
if ($anyInstalled -or $previousEntry) {
    $value = '"{0}" --artwork' -f $launcherTo
    Set-ItemProperty -Path $runKey -Name 'AnimatedLaunchers' -Value $value
    ($summary | Where-Object { $_.File -eq $launcher }).Run = $value
    Write-Output 'start with Windows: AnimatedLaunchers (one shared entry)'
}
else {
    Remove-ItemProperty -Path $runKey -Name 'AnimatedLaunchers' -ErrorAction SilentlyContinue
    Write-Output 'skipped start with Windows: no app is installed. Turn it on later in Theme Studio, Apps and startup.'
}
foreach ($app in $apps) {
    $row = $summary | Where-Object { $_.File -eq $app.Exe }
    if ($row.Installed -eq 'no') { Write-Output "skipped $($app.App): not installed. Turn its helper on later in Theme Studio, Apps and startup." }
}

# 7. Start Menu shortcut for Theme Studio
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$studio = Join-Path $destination 'ThemeStudio.exe'
$hasShortcut = $false
foreach ($link in Get-ChildItem -LiteralPath $programs -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
    if ($shell.CreateShortcut($link.FullName).TargetPath -eq $studio) { $hasShortcut = $true; break }
}
if (-not $hasShortcut) {
    $shortcut = $shell.CreateShortcut((Join-Path $programs 'Theme Studio.lnk'))
    $shortcut.TargetPath = $studio
    $shortcut.WorkingDirectory = $destination
    $shortcut.Save()
}

# 8. Restore library themes that are missing (only when a themes folder is next to this script)
$library = Join-Path $env:LOCALAPPDATA 'Animated Launchers\Themes'
$themeSource = Join-Path $package 'themes'
if (Test-Path -LiteralPath $themeSource) {
    foreach ($theme in Get-ChildItem -LiteralPath $themeSource -Directory) {
        if (-not ((Test-Path -LiteralPath (Join-Path $theme.FullName 'wallpaper.jpg')) -and
                  (Test-Path -LiteralPath (Join-Path $theme.FullName 'theme.ini')))) { continue }
        $target = Join-Path $library $theme.Name
        if (Test-Path -LiteralPath $target) { continue }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($file in 'wallpaper.jpg', 'avatar.jpg', 'theme.ini') {
            $from = Join-Path $theme.FullName $file
            if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $target $file) }
        }
        Write-Output "restored theme $($theme.Name)"
    }
}

# 8b. Seed cursor packs the same way (a cursors folder or the shipped cursors.zip)
$cursorsLibrary = Join-Path $env:LOCALAPPDATA 'Animated Launchers\Cursors'
$cursorSource = Join-Path $package 'cursors'
$cursorZip = Join-Path $package 'cursors.zip'
$cursorExpand = $null
if (-not (Test-Path -LiteralPath $cursorSource) -and (Test-Path -LiteralPath $cursorZip)) {
    $cursorExpand = Join-Path $env:TEMP ('animated-cursors-' + [guid]::NewGuid().ToString('n'))
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($cursorZip, $cursorExpand)
        $cursorSource = $cursorExpand
    } catch { $cursorExpand = $null }
}
if (Test-Path -LiteralPath $cursorSource) {
    foreach ($pack in Get-ChildItem -LiteralPath $cursorSource -Directory) {
        $target = Join-Path $cursorsLibrary $pack.Name
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($src in Get-ChildItem -LiteralPath $pack.FullName -File) {
            $dest = Join-Path $target $src.Name
            if (Test-Path -LiteralPath $dest) { continue }
            Copy-Item -LiteralPath $src.FullName -Destination $dest
        }
        Write-Output "seeded cursor pack $($pack.Name)"
    }
}
if ($cursorExpand -and (Test-Path -LiteralPath $cursorExpand)) {
    Remove-Item -LiteralPath $cursorExpand -Recurse -Force -ErrorAction SilentlyContinue
}

# 9. Installed apps entry (Windows Settings > Apps > Installed apps)
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$entryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AnimatedLaunchers'
$studioVersion = (Get-Item -LiteralPath (Join-Path $destination 'ThemeStudio.exe')).VersionInfo.FileVersion
$displayVersion = (($studioVersion -split '\.') | Select-Object -First 3) -join '.'
$sizeKb = [int][math]::Ceiling((Get-ChildItem -LiteralPath $destination -File -Recurse | Measure-Object -Property Length -Sum).Sum / 1KB)
New-Item -Path $entryKey -Force | Out-Null
$strings = @{
    DisplayName          = 'Animated Launchers'
    DisplayVersion       = $displayVersion
    Publisher            = 'Codex-Animated contributors'
    URLInfoAbout         = 'https://github.com/Synaap/Codex-Animated'
    DisplayIcon          = Join-Path $destination 'ThemeStudio.exe'
    InstallLocation      = $destination
    InstallDate          = (Get-Date).ToString('yyyyMMdd')
    UninstallString      = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" -FromSettings' -f $powershell, (Join-Path $destination 'Uninstall.ps1')
    QuietUninstallString = '"{0}" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{1}" -Quiet' -f $powershell, (Join-Path $destination 'Uninstall.ps1')
}
foreach ($name in $strings.Keys) { Set-ItemProperty -Path $entryKey -Name $name -Value $strings[$name] -Type String }
Set-ItemProperty -Path $entryKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty -Path $entryKey -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty -Path $entryKey -Name EstimatedSize -Value $sizeKb -Type DWord

# 9b. .altheme file association: double-clicking a theme file opens Theme Studio
$progid = 'HKCU:\Software\Classes\AnimatedLaunchers.ThemeFile'
$ext = 'HKCU:\Software\Classes\.altheme'
New-Item -Path $progid -Force | Out-Null
Set-ItemProperty -Path $progid -Name '(default)' -Value 'Animated Launcher theme'
New-Item -Path "$progid\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$progid\DefaultIcon" -Name '(default)' -Value ('"{0}",0' -f $studio)
New-Item -Path "$progid\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$progid\shell\open\command" -Name '(default)' -Value ('"{0}" --import "%1"' -f $studio)
New-Item -Path $ext -Force | Out-Null
Set-ItemProperty -Path $ext -Name '(default)' -Value 'AnimatedLaunchers.ThemeFile'

# 10. Start the shared helper
if (-not $NoStart -and ($anyInstalled -or $previousEntry)) {
    $process = Start-Process -FilePath $launcherTo -ArgumentList '--artwork' -PassThru
    ($summary | Where-Object { $_.File -eq $launcher }).Pid = $process.Id
}

# 11. Summary
$summary | Format-List
Write-Output ("Uninstaller kept at {0}" -f (Join-Path $destination 'Uninstall.cmd'))
Write-Output ("Installed apps entry: Animated Launchers {0}" -f $displayVersion)
