# Forces a helper error through its test switch and checks the log and the restart.
# Never starts an app and never writes live data. While it runs, the live shared helper
# is replaced by a test helper, so the artwork of an open window may blink. It is put
# back at the end, whatever happens.
#
#   tools\Fault-Test.ps1 [-App Claude] [-From <folder with the exes>]
#                        [-Only tick,always,thread,start,reload,session] [-Out <folder>]
[CmdletBinding()]
param(
    [ValidateSet('Devin', 'Antigravity', 'Codex', 'Claude', 'VS Code', 'Cursor', 'Kiro', 'Windsurf', 'VSCodium', 'Obsidian')]
    [string]$App = 'Claude',
    [string]$From,
    [string[]]$Only,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
if (-not $From) { $From = Split-Path -Parent $PSScriptRoot }
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'out\fault-test' }
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
Stop-IfBusy

$appInfo = Get-AnimatedApp $App
$appId = $appInfo.Helper -replace 'Animated$', '' | ForEach-Object { $_.ToLowerInvariant() }
$helperPath = Join-Path $From 'AnimatedLauncher.exe'
if (-not (Test-Path -LiteralPath $helperPath)) { $helperPath = Join-Path $From ($appInfo.Helper + '.exe') }
if (-not (Test-Path -LiteralPath $helperPath)) {
    throw "AnimatedLauncher.exe is not under '$From'. Build first: source\Build.ps1 puts the exe in the repository root."
}
$helperPath = (Resolve-Path -LiteralPath $helperPath).Path
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path

$script:pass = 0; $script:fail = 0; $script:skipped = 0
function Check([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output "PASS $name  [$detail]" }
    else { $script:fail++; Write-Output "FAIL $name  [$detail]" }
}
function NotChecked([string]$name, [string]$why) {
    $script:skipped++; Write-Output "NOT CHECKED $name ($why)"
}
function Wait-For([scriptblock]$Until, [int]$Seconds = 25, [scriptblock]$OnPoll) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        if (& $Until) { return $true }
        if ($OnPoll) { & $OnPoll }
        Start-Sleep -Milliseconds 200
    }
    return [bool](& $Until)
}

# The scratch data root, made fresh for every step and seeded from the live settings:
# <root>\<Product>\* per app plus <root>\Animated Launchers\Logs\helper.log for the
# shared helper.
function New-Scratch {
    $scratch = Join-Path $Out "$App-data"
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    $live = Join-Path $env:LOCALAPPDATA $appInfo.Product
    $appFolder = Join-Path $scratch $appInfo.Product
    New-Item -ItemType Directory -Force -Path $appFolder | Out-Null
    foreach ($f in 'settings.ini', 'wallpaper.jpg', 'avatar.jpg') {
        $src = Join-Path $live $f
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $appFolder $f) }
    }
    return $scratch
}

function Start-TestHelper([string]$scratch, [string]$fault) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $helperPath
    $info.Arguments = '--artwork'
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['ANIMATED_DATA'] = $scratch
    $info.EnvironmentVariables['ANIMATED_ASSUME_INSTALLED'] = $appId
    if ($fault) { $info.EnvironmentVariables['ANIMATED_FAULT'] = $fault }
    elseif ($info.EnvironmentVariables.ContainsKey('ANIMATED_FAULT')) { $info.EnvironmentVariables.Remove('ANIMATED_FAULT') }
    return [Diagnostics.Process]::Start($info)
}

# Every helper of this app running from the folder under test (the test helper and its restarts).
function Get-TestHelpers {
    @(Get-HelperProcesses $App | Where-Object { $_.Path -eq $helperPath })
}
function Stop-TestHelpers {
    foreach ($p in @(Get-TestHelpers)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
    }
}

function Get-LogLines([string]$scratch) {
    $log = Join-Path $scratch 'Animated Launchers\Logs\helper.log'
    if (-not (Test-Path -LiteralPath $log)) { return @() }
    $stream = New-Object IO.FileStream($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.StreamReader($stream)
        return @($reader.ReadToEnd() -split "`r`n")
    }
    finally { $stream.Dispose() }
}
# How many log lines contain the exact text.
function Find-Log([string]$scratch, [string]$text) {
    return @(Get-LogLines $scratch | Where-Object { $_.Contains($text) }).Count
}
# The process ids whose tag wrote a 'helper: ready' line.
function Get-ReadyPids([string]$scratch) {
    return @(Get-LogLines $scratch | ForEach-Object { if ($_ -match '\[(\d+) helper\] helper: ready') { [int]$Matches[1] } })
}
function Save-StepLog([string]$scratch, [string]$step) {
    $log = Join-Path $scratch 'Animated Launchers\Logs\helper.log'
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $Out "$App-$step.log") -Force }
}

function Write-Jpeg([string]$path, [int]$w, [int]$h) {
    $bmp = New-Object Drawing.Bitmap($w, $h)
    try {
        $g = [Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([Drawing.Color]::FromArgb(40, 90, 160))
            $pen = New-Object Drawing.Pen([Drawing.Color]::White, 4)
            $g.DrawEllipse($pen, 10, 10, $w - 20, $h - 20)
            $pen.Dispose()
        }
        finally { $g.Dispose() }
        $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Jpeg)
    }
    finally { $bmp.Dispose() }
}

# The forced-fault steps tick, thread and start share one shape. $where is the expected
# 'unexpected error in the <where>' text; $expectReady says the first helper reaches ready.
function Test-CrashStep([string]$step, [string]$fault, [string]$where, [bool]$expectReady) {
    $scratch = New-Scratch
    $p1 = $null
    try {
        $p1 = Start-TestHelper $scratch $fault
        $window = @(Get-AppWindows $App) | Select-Object -First 1

        if ($expectReady) {
            $ready = Wait-For { (Get-ReadyPids $scratch) -contains $p1.Id }
            Check $ready 'first helper became ready' "pid $($p1.Id)"
            if ($window -and $ready) {
                $child = Wait-For -Seconds 10 { $c = Get-ArtworkChild $window $App; $c -and $c.OwnerPid -eq $p1.Id }
                if ($child) { Check $true 'artwork child belongs to the first helper' "owner $($p1.Id)" }
                elseif ($p1.HasExited) { NotChecked 'artwork child of the first helper' 'the helper stopped before the child appeared' }
                else { Check $false 'artwork child belongs to the first helper' 'no child after 10 s' }
            }
        }
        else {
            $ready = Wait-For -Seconds 15 { (Get-ReadyPids $scratch).Count -ge 1 -or $p1.HasExited }
            Check ((Get-ReadyPids $scratch) -notcontains $p1.Id) 'first helper never became ready' 'the constructor fault happens before ready'
        }

        $exited = $p1.WaitForExit(25000)
        Check $exited 'first helper exited' "pid $($p1.Id)"
        if ($exited) { Check ($p1.ExitCode -eq 70) 'first helper exit code is 70' "code $($p1.ExitCode)" }

        Check (Wait-For { (Find-Log $scratch "unexpected error in the $where") -ge 1 }) "log: unexpected error in the $where" ''
        Check (Wait-For { (Find-Log $scratch 'starting the helper again') -ge 1 }) 'log: starting the helper again' ''
        Check (Wait-For { (Find-Log $scratch 'started again after the helper stopped unexpectedly') -ge 1 }) 'log: started again after the helper stopped unexpectedly' ''

        $againCount = if ($expectReady) { 2 } else { 1 }
        Check (Wait-For { (Get-ReadyPids $scratch).Count -ge $againCount }) 'second helper became ready' "ready pids: $((Get-ReadyPids $scratch) -join ', ')"

        Start-Sleep -Seconds 6
        $alive = @(Get-TestHelpers)
        Check (($alive.Count -eq 1) -and ($alive[0].Id -ne $p1.Id)) 'exactly one new helper is still running' "pids: $(($alive | ForEach-Object { $_.Id }) -join ', ')"
        if ($window) {
            if ($alive.Count -eq 1) {
                Check (Wait-For { $c = Get-ArtworkChild $window $App; $c -and $c.OwnerPid -eq $alive[0].Id }) 'artwork child belongs to the new helper' "owner $($alive[0].Id)"
            }
            else { Check $false 'artwork child belongs to the new helper' 'no single new helper to check against' }
        }
        else { NotChecked 'artwork child belongs to the new helper' "$App has no open window" }
    }
    finally {
        Save-StepLog $scratch $step
        Stop-TestHelpers
        if ($p1 -and -not $p1.HasExited) { try { Stop-Process -Id $p1.Id -Force -ErrorAction Stop } catch { } }
    }
}

function Test-AlwaysStep {
    $scratch = New-Scratch
    $p1 = $null
    try {
        $script:dialogSeen = $null
        $watchDialogs = {
            $pids = @{}
            foreach ($p in @(Get-HelperProcesses $App)) { $pids[[int]$p.Id] = $true }
            if ($pids.Count -eq 0) { return }
            foreach ($w in [TK]::TopLevel()) {
                if ($w.Class -eq '#32770' -and $w.Visible -and $pids.ContainsKey([int]$w.Pid)) {
                    $script:dialogSeen = "hwnd=$($w.Handle) pid=$($w.Pid)"
                }
            }
        }
        $p1 = Start-TestHelper $scratch 'tick,always'
        $done = Wait-For { (Find-Log $scratch 'unexpected error') -ge 2 -and @(Get-HelperProcesses $App).Count -eq 0 } -OnPoll $watchDialogs
        Check $done 'both helpers stopped after two stops in ten minutes' ''
        Check ((Find-Log $scratch 'unexpected error') -eq 2) 'log: unexpected error twice' "count $((Find-Log $scratch 'unexpected error'))"
        Check ((Find-Log $scratch 'starting the helper again') -eq 1) 'log: starting the helper again once' ''
        Check ((Find-Log $scratch 'not starting again: this is the second stop within ten minutes') -eq 1) 'log: not starting again once' ''
        Check ((Find-Log $scratch 'no message shown (forced fault)') -eq 1) 'log: no message shown (forced fault)' ''
        Check ($null -eq $script:dialogSeen) 'no message window was shown' $(if ($script:dialogSeen) { $script:dialogSeen } else { 'none seen' })
        Check (@(Get-HelperProcesses $App).Count -eq 0) 'no helper process is left' ''
    }
    finally {
        Save-StepLog $scratch 'always'
        Stop-TestHelpers
        if ($p1 -and -not $p1.HasExited) { try { Stop-Process -Id $p1.Id -Force -ErrorAction Stop } catch { } }
    }
}

function Test-ReloadStep {
    if (@(Get-AppWindows $App).Count -gt 0) {
        NotChecked 'reload of damaged and locked wallpaper' "$App has an open window"
        return
    }
    $scratch = New-Scratch
    $appFolder = Join-Path $scratch $appInfo.Product
    $p1 = $null
    try {
        Write-Jpeg (Join-Path $appFolder 'wallpaper.jpg') 640 360
        $p1 = Start-TestHelper $scratch ''
        Check (Wait-For { (Find-Log $scratch '] ready (theme') -ge 1 }) 'helper became ready' ''

        # 1. A damaged file: all four attempts fail, then the helper gives up and stays running.
        $bytes = New-Object byte[] 64
        (New-Object Random).NextBytes($bytes)
        [IO.File]::WriteAllBytes((Join-Path $appFolder 'wallpaper.jpg'), $bytes)
        Check (Wait-For { (Find-Log $scratch 'reload gave up') -ge 1 }) 'damaged file: reload gave up after all attempts' ''
        Check (((Find-Log $scratch 'reload failed (attempt 1 of 4)') -ge 1) -and ((Find-Log $scratch 'reload failed (attempt 4 of 4)') -ge 1)) 'damaged file: attempts 1 to 4 logged' "failures $((Find-Log $scratch 'reload failed'))"
        Check (-not $p1.HasExited) 'helper still running after the damaged file' ''

        # 2. A good file again loads on the next change.
        Write-Jpeg (Join-Path $appFolder 'wallpaper.jpg') 800 450
        Check (Wait-For { (Find-Log $scratch 'wallpaper loaded (800x450)') -ge 1 }) 'good file again: wallpaper loaded (800x450)' ''

        # 3. A locked file: failures while it is held, the picture once it is free.
        $wp = Join-Path $appFolder 'wallpaper.jpg'
        $failBefore = Find-Log $scratch 'reload failed'
        $gaveBefore = Find-Log $scratch 'reload gave up'
        $tmp = Join-Path $Out 'lock-write.jpg'
        Write-Jpeg $tmp 1024 576
        $fs = New-Object IO.FileStream($wp, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $content = [IO.File]::ReadAllBytes($tmp)
            $fs.SetLength(0)
            $fs.Write($content, 0, $content.Length)
            $fs.Flush()
            Start-Sleep -Seconds 4
        }
        finally { $fs.Dispose() }
        Check (Wait-For { (Find-Log $scratch 'reload failed') -ge ($failBefore + 2) }) 'locked file: at least two failed reload attempts' "failures $((Find-Log $scratch 'reload failed'))"
        Check (Wait-For { (Find-Log $scratch 'wallpaper loaded (1024x576)') -ge 1 }) 'after the lock: wallpaper loaded (1024x576)' ''
        Check ((Find-Log $scratch 'reload gave up') -eq $gaveBefore) 'locked file: no reload gave up' "gave up $((Find-Log $scratch 'reload gave up'))"
    }
    finally {
        Save-StepLog $scratch 'reload'
        Stop-TestHelpers
        if ($p1 -and -not $p1.HasExited) { try { Stop-Process -Id $p1.Id -Force -ErrorAction Stop } catch { } }
    }
}

# A fault inside one app's session parks only that session: the process stays alive,
# no restart is logged, and toggling watch off and on brings the session back.
function Test-SessionStep {
    $scratch = New-Scratch
    $appFolder = Join-Path $scratch $appInfo.Product
    $ini = Join-Path $appFolder 'settings.ini'
    $p1 = $null
    try {
        $p1 = Start-TestHelper $scratch "${appId}:tick"
        $window = @(Get-AppWindows $App) | Select-Object -First 1
        Check (Wait-For { (Get-ReadyPids $scratch) -contains $p1.Id }) 'helper became ready' "pid $($p1.Id)"
        if ($window) {
            Check (Wait-For -Seconds 10 { $c = Get-ArtworkChild $window $App; $c -and $c.OwnerPid -eq $p1.Id }) 'artwork child belongs to the helper' "owner $($p1.Id)"
        }
        # Three session ticks inside the window park the session; the process goes on.
        Check (Wait-For { (Find-Log $scratch 'parked: too many errors') -ge 1 } -Seconds 30) "log: session parked after three faults" ''
        Check ((Find-Log $scratch "[$appId]") -ge 3) 'log: session faults carry the app tag' "tagged $((Find-Log $scratch "[$appId]"))"
        Start-Sleep -Seconds 2
        Check (-not $p1.HasExited) 'helper process still alive' ''
        Check ((Find-Log $scratch 'starting the helper again') -eq 0) 'no process restart for a session fault' ''
        if ($window) {
            Check (Wait-For -Seconds 8 { $null -eq (Get-ArtworkChild $window $App) }) 'artwork child released when the session parked' ''
        }
        # Revive: watch off then on through settings.ini (what the tray toggle writes).
        $lines = @()
        if (Test-Path -LiteralPath $ini) { $lines = @(Get-Content -LiteralPath $ini | Where-Object { $_ -notmatch '^\s*watch=' }) }
        [IO.File]::WriteAllLines($ini, [string[]]($lines + 'watch=off'), (New-Object Text.UTF8Encoding($false)))
        Check (Wait-For { (Find-Log $scratch 'watch is off') -ge 1 }) 'watch=off logged' ''
        [IO.File]::WriteAllLines($ini, [string[]]($lines + 'watch=on'), (New-Object Text.UTF8Encoding($false)))
        Check (Wait-For { (Find-Log $scratch "[$appId] ready") -ge 1 }) 'session ready again after watch=on' ''
        if ($window) {
            Check (Wait-For -Seconds 10 { $c = Get-ArtworkChild $window $App; $c -and $c.OwnerPid -eq $p1.Id }) 'artwork child back after revive' ''
        }
        Check (-not $p1.HasExited) 'helper process still alive after revive' ''
    }
    finally {
        Save-StepLog $scratch 'session'
        Stop-TestHelpers
        if ($p1 -and -not $p1.HasExited) { try { Stop-Process -Id $p1.Id -Force -ErrorAction Stop } catch { } }
    }
}

# --- run -----------------------------------------------------------------

$steps = if ($Only) { $Only } else { 'session', 'tick', 'always', 'thread', 'start', 'reload' }
$live = @(Get-HelperProcesses $App) | Select-Object -First 1
$livePath = $null; $liveId = 0
if ($live) {
    $livePath = $live.Path; $liveId = $live.Id
    Write-Output "helper of $App running now: pid $liveId at $livePath"
}
else { Write-Output "helper of $App is not running" }

try {
    # The live helper is replaced by the test helper for the length of the test.
    foreach ($p in @(Get-HelperProcesses $App)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { Write-Warning "could not stop helper $($p.Id): $($_.Exception.Message)" }
    }
    [void](Wait-For { @(Get-HelperProcesses $App).Count -eq 0 } -Seconds 15)

    foreach ($step in $steps) {
        Write-Output "--- step $step"
        switch ($step) {
            'session' { Test-SessionStep }
            'tick'   { Test-CrashStep 'tick' 'tick' 'message loop' $true }
            'always' { Test-AlwaysStep }
            'thread' { Test-CrashStep 'thread' 'thread' 'background thread' $true }
            'start'  { Test-CrashStep 'start' 'start' 'startup' $false }
            'reload' { Test-ReloadStep }
            default  { Write-Output "SKIP unknown step '$step'"; $script:skipped++ }
        }
    }
}
finally {
    Stop-TestHelpers
    if ($livePath) {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $livePath
        $info.Arguments = '--artwork'
        $info.UseShellExecute = $false
        [Diagnostics.Process]::Start($info) | Out-Null
        if (Wait-For { @(Get-HelperProcesses $App | Where-Object { $_.Path -eq $livePath }).Count -ge 1 }) {
            Write-Output "helper of $App running again: $(@((Get-HelperProcesses $App) | ForEach-Object { $_.Id }) -join ', ')"
        }
        else { Write-Output "WARNING: the helper that ran before (pid $liveId) did not come back; run `"$livePath`" --artwork" }
    }
}

Write-Output "== $script:pass passed, $script:fail failed, $script:skipped not checked"
if ($script:fail -gt 0) { exit 1 }
exit 0
