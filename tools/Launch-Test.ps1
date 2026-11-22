# Starts one app once through its launcher and checks that the animation plays and the
# wallpaper attaches. Only run it when the owner has said the app may be started.
# Afterwards the app is closed again, unless it was already open or -KeepOpen is passed.
#
#   tools\Launch-Test.ps1 -App Claude [-From <folder with the exes>] [-KeepOpen] [-Out <folder>]
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Devin', 'Antigravity', 'Codex', 'Claude', 'VS Code', 'Cursor', 'Kiro', 'Windsurf', 'VSCodium', 'Obsidian')]
    [string]$App,
    [string]$From,
    [switch]$KeepOpen,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
Stop-IfBusy
if (-not $From) { $From = Get-InstallFolder }
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'out\launch-test' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path

$appInfo = Get-AnimatedApp $App
$appId = ($appInfo.Helper -replace 'Animated$', '').ToLowerInvariant()
# The installed copy named <App>Animated.exe is the launcher; a lone
# AnimatedLauncher.exe launches the same app with --app.
$launcher = Join-Path $From ($appInfo.Helper + '.exe')
$launchArgs = ''
if (-not (Test-Path -LiteralPath $launcher)) {
    $launcher = Join-Path $From 'AnimatedLauncher.exe'
    $launchArgs = '--app ' + $appId
}
if (-not (Test-Path -LiteralPath $launcher)) { throw "No launcher is under '$From' (expected $($appInfo.Helper).exe or AnimatedLauncher.exe)." }
$launcher = (Resolve-Path -LiteralPath $launcher).Path

$script:pass = 0; $script:fail = 0; $script:skipped = 0
function Check([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output "PASS $name  [$detail]" }
    else { $script:fail++; Write-Output "FAIL $name  [$detail]" }
}
function NotChecked([string]$name, [string]$why) {
    $script:skipped++; Write-Output "NOT CHECKED $name ($why)"
}
function Wait-For([scriptblock]$Until, [int]$Seconds = 60, [scriptblock]$OnPoll) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        if (& $Until) { return $true }
        if ($OnPoll) { & $OnPoll }
        Start-Sleep -Milliseconds 200
    }
    return [bool](& $Until)
}

# Processes that belong to the app itself (not the launcher).
function Get-AppPids {
    $owns = $appInfo.Owns
    $set = @{}
    $filter = (@($appInfo.ExeNames) | ForEach-Object { "Name='$_'" }) -join ' OR '
    foreach ($p in Get-CimInstance Win32_Process -Filter $filter) {
        if ($p.ExecutablePath -and @($owns.Invoke($p.ExecutablePath))[0]) { $set[[int]$p.ProcessId] = $true }
    }
    return $set
}

# A visible top-level window titled <Product> owned by a launcher process: the animation.
function Get-SplashWindow {
    $helperPids = @{}
    foreach ($p in @(Get-HelperProcesses $App)) { $helperPids[$p.Id] = $true }
    $helperNames = @($appInfo.Helper + '.exe', 'AnimatedLauncher.exe')
    $filter = ($helperNames | ForEach-Object { "Name='$_'" }) -join ' OR '
    foreach ($proc in Get-CimInstance Win32_Process -Filter $filter) {
        $helperPids[[int]$proc.ProcessId] = $true
    }
    foreach ($w in [TK]::TopLevel()) {
        if ($w.Visible -and $w.Title -eq $appInfo.Product -and $helperPids.ContainsKey([int]$w.Pid)) { return $w }
    }
    return $null
}

# Any app's playing animation window (titles are "<Product>" on a helper process).
function Get-AnySplashWindow {
    $products = @{}
    foreach ($a in $AnimatedApps) { $products[$a.Product] = $true }
    $helperPids = @{}
    foreach ($p in @(Get-HelperProcesses)) { $helperPids[$p.Id] = $true }
    foreach ($w in [TK]::TopLevel()) {
        if ($w.Visible -and $products.ContainsKey($w.Title) -and $helperPids.ContainsKey([int]$w.Pid)) { return $w }
    }
    return $null
}

$before = @(Get-AppWindows $App)
if ($before.Count -gt 0) {
    NotChecked 'launch test' "$App is already open; close it first or use -KeepOpen on another app"
    Write-Output "== $script:pass passed, $script:fail failed, $script:skipped not checked"
    exit 3
}

$appPidsBefore = Get-AppPids
$openedPids = @()
$script:splashShot = $false
# Only one animation plays at a time across all apps: let a previous app's splash finish first.
if (-not (Wait-For { $null -eq (Get-AnySplashWindow) } -Seconds 20)) {
    Write-Output 'note: an animation is still playing for another app; the new one may be skipped by design'
}
try {
    Write-Output "starting $App through $launcher $launchArgs"
    # explorer.exe so the app starts the way a user's shortcut would.
    [Diagnostics.Process]::Start('explorer.exe', ('"' + $launcher + '" ' + $launchArgs).Trim()) | Out-Null

    $splashSeen = Wait-For { $null -ne (Get-SplashWindow) } -Seconds 30 -OnPoll {
        if (-not $script:splashShot) {
            $w = Get-SplashWindow
            if ($w) {
                $r = $w.Rect.Split(',') | ForEach-Object { [int]$_ }
                try {
                    [TK]::Screen($r[0], $r[1], $r[2] - $r[0], $r[3] - $r[1], (Join-Path $Out "$App-splash.png"))
                    $script:splashShot = $true
                }
                catch { }
            }
        }
    }
    $splash = Get-SplashWindow
    Check $splashSeen 'the animation window appeared' $(if ($splash) { "rect $($splash.Rect) topmost=$($splash.Topmost)" } else { 'not seen in 30 s' })
    if ($splash) { Check ([bool]$splash.Topmost) 'the animation is topmost' '' }

    Check (Wait-For { @(Get-AppWindows $App).Count -ge 1 } -Seconds 60) "the $App window opened" "$(@(Get-AppWindows $App).Count) window(s)"

    $window = @(Get-AppWindows $App) | Select-Object -First 1
    $attached = $false
    if ($window) {
        $attached = Wait-For { $null -ne (Get-ArtworkChild $window $App) } -Seconds 30
        if ($attached) { try { [TK]::Capture($window.Handle, (Join-Path $Out "$App-window.png")) } catch { } }
    }
    Check $attached 'the wallpaper attached inside the window' ''
}
finally {
    $now = Get-AppPids
    foreach ($procId in $now.Keys) {
        if (-not $appPidsBefore.ContainsKey($procId)) { $openedPids += $procId }
    }
    if (-not $KeepOpen) {
        # Electron apps keep child processes after the main window goes; a graceful
        # taskkill (WM_CLOSE) reaches every window, then /f clears the survivors.
        foreach ($procId in $openedPids) {
            try { taskkill /pid $procId 2>&1 | Out-Null } catch { }
        }
        if ($openedPids.Count -gt 0) {
            [void](Wait-For { @(($openedPids | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })).Count -eq 0 } -Seconds 20)
            $still = @($openedPids | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
            foreach ($procId in $still) {
                try { taskkill /pid $procId /f 2>&1 | Out-Null } catch { }
            }
            Start-Sleep -Milliseconds 500
            $still = @($openedPids | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
            if ($still.Count -gt 0) {
                Write-Output "note: $($still.Count) $App process(es) did not close; left running"
            }
        }
    }
    else { Write-Output "left $App open (-KeepOpen)" }
}

Write-Output "== $script:pass passed, $script:fail failed, $script:skipped not checked"
if ($script:fail -gt 0) { exit 1 }
exit 0
