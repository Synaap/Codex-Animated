# Exercises the theme-file commands of the real Theme Studio through its eval
# test hook: export, import, duplicate, the credit line, dropping image and pack
# bytes (the same code path as a real drop), and a library backup + restore.
# Everything happens in a scratch theme library (ANIMATED_THEMES); the live
# library and app settings are never touched. The window opens on a secondary
# monitor, takes no focus, and studio.ini is restored afterwards.
#
#   tools\Pack-Check.ps1 [-From <folder with ThemeStudio.exe>] [-Out <folder>]
[CmdletBinding()]
param(
    [string]$From,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
Stop-IfBusy
if (-not $From) { $From = Split-Path -Parent $PSScriptRoot }
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'out\pack-check' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path

$studio = Join-Path $From 'ThemeStudio.exe'
if (-not (Test-Path -LiteralPath $studio)) { throw "ThemeStudio.exe is not under '$From'." }
$studio = (Resolve-Path -LiteralPath $studio).Path

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output "PASS $name  [$detail]" }
    else { $script:fail++; Write-Output "FAIL $name  [$detail]" }
}

# --- scratch library with one theme -----------------------------------------
$themes = Join-Path $Out 'Themes'
$seed = Join-Path $themes 'Seed'
if (Test-Path -LiteralPath $themes) { Remove-Item -LiteralPath $themes -Recurse -Force }
New-Item -ItemType Directory -Force -Path $seed | Out-Null
$bmp = New-Object Drawing.Bitmap(320, 180)
try {
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.Clear([Drawing.Color]::FromArgb(30, 90, 140)); $g.Dispose()
    $bmp.Save((Join-Path $seed 'wallpaper.jpg'), [Drawing.Imaging.ImageFormat]::Jpeg)
}
finally { $bmp.Dispose() }
[IO.File]::WriteAllText((Join-Path $seed 'theme.ini'), "opacity=25`r`ntitle=SEED`r`n", (New-Object Text.UTF8Encoding($false)))

# --- eval plumbing -----------------------------------------------------------
$eval = Join-Path $Out 'pack-eval.js'
$evalOut = $eval + '.out'
function Short([string]$s) { if ($null -eq $s) { return 'no answer' } $s = $s -replace '\s+', ' '; return $s.Substring(0, [Math]::Min(160, $s.Length)) }
function Send-Eval([string]$code, [int]$Seconds = 30) {
    if (Test-Path -LiteralPath $evalOut) { Remove-Item -LiteralPath $evalOut -Force }
    [IO.File]::WriteAllText($eval, $code, (New-Object Text.UTF8Encoding($false)))
    $written = (Get-Item -LiteralPath $eval).LastWriteTimeUtc
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        if ((Test-Path -LiteralPath $evalOut) -and (Get-Item -LiteralPath $evalOut).LastWriteTimeUtc -ge $written) {
            Start-Sleep -Milliseconds 200
            return [IO.File]::ReadAllText($evalOut)
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}
# Runs an async page expression to completion and returns the raw result JSON.
# The eval code is self-initializing: whichever copy the host happens to run
# first kicks the promise off, and later copies report its state, so a write the
# host's 150 ms poll misses cannot lose the kick-off.
$script:evalN = 0
function Send-AsyncEval([string]$promise, [int]$Seconds = 45) {
    $script:evalN++
    $token = '__pack' + $script:evalN
    $code = "if (!window.$token) { window.$token = 'pending'; Promise.resolve($promise).then(r => { window.$token = { ok: true, value: r }; }).catch(e => { window.$token = { ok: false, error: String(e && e.message || e) }; }); } return window.$token === 'pending' ? null : window.$token"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $r = Send-Eval $code
        if ($r -and $r.Contains('"value":{"ok":')) { return $r }
    }
    return $null
}

# The monitor that is not the primary one, so nothing opens on the main screen.
Add-Type -AssemblyName System.Windows.Forms
$monitors = [Windows.Forms.Screen]::AllScreens
$target = $monitors | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $target) { $target = $monitors[0]; Write-Output "note: only one monitor; the studio opens on the primary one" }
$b = $target.Bounds
$x = $b.X + [math]::Max(0, [int](($b.Width - 1280) / 2))
$y = $b.Y + [math]::Max(0, [int](($b.Height - 800) / 2))

$iniDir = Join-Path $env:LOCALAPPDATA 'Animated Launchers'
$ini = Join-Path $iniDir 'studio.ini'
$iniBackup = $null
if (Test-Path -LiteralPath $ini) { $iniBackup = [IO.File]::ReadAllBytes($ini) }

$packFile = Join-Path $Out 'seed.altheme'
$backupFile = Join-Path $Out 'library.albackup'
$dataRoot = Join-Path $Out 'data'
$proc = $null
try {
    New-Item -ItemType Directory -Force -Path $iniDir | Out-Null
    [IO.File]::WriteAllText($ini, "window=$x,$y,1280,800`r`n", (New-Object Text.UTF8Encoding($false)))
    # ANIMATED_DATA sends every per-app settings.ini to scratch; the studio still
    # reads its window position from the (scratch) shared root.
    New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot 'Animated Launchers') | Out-Null
    [IO.File]::WriteAllText((Join-Path $dataRoot 'Animated Launchers\studio.ini'), "window=$x,$y,1280,800`r`n", (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($eval, '// waiting', (New-Object Text.UTF8Encoding($false)))

    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $studio
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['ANIMATED_STUDIO_EVAL'] = $eval
    $info.EnvironmentVariables['ANIMATED_THEMES'] = $themes
    $info.EnvironmentVariables['ANIMATED_DATA'] = $dataRoot
    $info.EnvironmentVariables['ANIMATED_ASSUME_INSTALLED'] = 'devin,antigravity,codex,claude,vscode,cursor,kiro'
    $proc = [Diagnostics.Process]::Start($info)

    $ready = $false; $r = $null
    for ($i = 0; $i -lt 15 -and -not $ready; $i++) {
        $r = Send-Eval 'return document.documentElement.hasAttribute("data-ready")'
        $ready = ($r -and $r.Contains('"ok":true') -and $r.Contains('"value":true'))
    }
    Check $ready 'the studio page rendered' $(if ($ready) { '' } elseif ($r) { $r } else { 'no eval answer' })

    $r = Send-Eval "return !!document.querySelector('#g-sharing') && document.querySelectorAll('.rail-foot button').length >= 4 && document.querySelectorAll('.rail-head button').length >= 2"
    Check ($r -and $r.Contains('"value":true')) 'the sharing and rail controls are on screen' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('exportTheme', { name: 'Seed', path: '$($packFile -replace '\\','/')' })"
    Check ($r -and $r.Contains('"ok":true') -and (Test-Path -LiteralPath $packFile)) 'exportTheme writes the theme file' $(if ($r) { ($r -split "`n")[0].Substring(0, [Math]::Min(140, ($r -split "`n")[0].Length)) } else { 'no answer' })

    $r = Send-AsyncEval "window.__studio.call('importTheme', { path: '$($packFile -replace '\\','/')' })"
    Check ($r -and $r.Contains('"imported":"Seed 2"')) 'importTheme imports the file under a free name' (Short $r)
    Check ((Test-Path -LiteralPath (Join-Path $themes 'Seed 2\wallpaper.jpg')) -and
        ((New-Object IO.FileInfo (Join-Path $seed 'wallpaper.jpg')).Length -eq (New-Object IO.FileInfo (Join-Path $themes 'Seed 2\wallpaper.jpg')).Length)) 'imported wallpaper is identical' ''

    $r = Send-AsyncEval "window.__studio.call('duplicateTheme', { name: 'Seed' })"
    Check ($r -and $r.Contains('"created":"Seed copy"')) 'duplicateTheme creates Seed copy' (Short $r)
    Check (Test-Path -LiteralPath (Join-Path $themes 'Seed copy\wallpaper.jpg')) 'the copy has its files' ''

    $r = Send-AsyncEval "window.__studio.call('openThemesFolder', { dryRun: true })"
    Check ($r -and ($r -replace '\\\\', '/').Contains(($themes -replace '\\', '/'))) 'openThemesFolder resolves the library folder' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('setAutostart', { app: 'devin', on: false, dryRun: true })"
    Check ($r -and $r.Contains('"app":"devin"') -and $r.Contains('"watch":false')) 'setAutostart maps to the app watch flag' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('playSplash', { name: 'Seed', app: 'claude', dryRun: true })"
    Check ($r -and $r.Contains('AnimatedLauncher.exe') -and $r.Contains('--app claude')) 'playSplash launches AnimatedLauncher.exe --app' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('state', {})"
    Check ($r -and $r.Contains('"watch":') -and $r.Contains('"helperExists":')) 'the apps list reports watch and the shared helper' (Short $r)
    Check ($r -and $r.Contains('"id":"vscode"') -and $r.Contains('"id":"obsidian"') -and $r.Contains('"preview":"chat"')) 'the apps list covers all ten with preview kinds' (Short $r)

    $r = Send-Eval "var b = [].find.call(document.querySelectorAll('.rail-foot button'), x => x.textContent.indexOf('Apps') >= 0); if (b) b.click(); return b ? 'clicked' : 'no button'"
    Start-Sleep -Milliseconds 600
    $r = Send-Eval "return document.querySelectorAll('.app-block').length"
    Check ($r -and $r.Contains('"value":11')) 'the apps page lists every known app plus the add block' (Short $r)
    $r = Send-Eval "var b=[].find.call(document.querySelectorAll('.app-block'), a => a.dataset.app === 'vscodium'); return b ? (b.querySelector('.app-state')||{}).textContent : '(no block)'"
    Check ($r -and $r.Contains('Not installed')) 'an app that is not installed says so' (Short $r)
    $r = Send-Eval "var row = document.querySelector('.rail .row'); if (row) row.click(); return 'ok'"
    Start-Sleep -Milliseconds 600
    $r = Send-Eval "var p = document.querySelector('.stage-app'); return p ? p.options.length : -1"
    Check ($r -and $r.Contains('"value":10')) 'the stage previews every app' (Short $r)
    $r = Send-Eval "var p = document.querySelector('.stage-app'); p.value = 'claude'; p.dispatchEvent(new Event('change')); return 'ok'"
    Start-Sleep -Milliseconds 400
    $r = Send-Eval "return !!document.querySelector('.mock.chat')"
    Check ($r -and $r.Contains('"value":true')) 'a chat app previews as a chat window' (Short $r)
    $r = Send-Eval "var p = document.querySelector('.stage-app'); if (p) { p.value = 'vscode'; p.dispatchEvent(new Event('change')); } return 'ok'"
    Start-Sleep -Milliseconds 400
    $r = Send-Eval "return !!document.querySelector('.mock:not(.chat)') && !!document.querySelector('.mock .code')"
    Check ($r -and $r.Contains('"value":true')) 'an editor app previews as a code editor' (Short $r)
    $r = Send-Eval "var b = [].find.call(document.querySelectorAll('.rail-foot button'), x => x.textContent.indexOf('Apps') >= 0); if (b) b.click(); return b ? 'clicked' : 'no button'"
    Start-Sleep -Milliseconds 600
    $r = Send-Eval "var b=[].find.call(document.querySelectorAll('.app-block'), a => a.dataset.app === 'devin'); return b ? b.querySelectorAll('.ctl').length : 0"
    Check ($r -and $r.Contains('"value":15')) 'an app block carries all fifteen controls' (Short $r)

    $notepad = (Join-Path $env:WINDIR 'System32\notepad.exe') -replace '\\','/'
    $r = Send-AsyncEval "window.__studio.call('pickAppExe', { path: '$notepad' })"
    Check ($r -and $r.Contains('"name":"notepad"')) 'pickAppExe answers a path without a dialog' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('probeApp', { path: '$notepad' })"
    Check ($r -and $r.Contains('"exists":true') -and $r.Contains('"covered":false')) 'probeApp reports a plain exe' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('addCustomApp', { name: 'Notepad', path: '$notepad', preview: 'editor' })"
    Check ($r -and $r.Contains('"added":"notepad"') -and $r.Contains('"custom":true')) 'addCustomApp adds notepad' (Short $r)
    $customIni = Join-Path $dataRoot 'Animated Launchers\custom-apps.ini'
    Check ((Test-Path -LiteralPath $customIni) -and ([IO.File]::ReadAllText($customIni) -match 'notepad=Notepad\|')) 'custom-apps.ini holds the row' ''
    $r = Send-AsyncEval "window.__studio.call('addCustomApp', { name: 'Again', path: '$notepad', preview: 'editor' })"
    Check ($r -and $r.Contains('"error":"that app is already added"')) 'the same exe is refused twice' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('removeCustomApp', { app: 'notepad' })"
    Check ($r -and $r.Contains('"ok":true') -and -not $r.Contains('"id":"notepad"')) 'removeCustomApp drops the row' (Short $r)
    Check (-not [IO.File]::ReadAllText($customIni).Contains('notepad=')) 'the ini row is gone' ''

    # --- update 8: faster theme making ----------------------------------------
    $importSrc = Join-Path $Out 'import-src'
    New-Item -ItemType Directory -Force -Path $importSrc | Out-Null
    foreach ($n in 1..2) {
        $b2 = New-Object Drawing.Bitmap(64, 48)
        try {
            $g2 = [Drawing.Graphics]::FromImage($b2)
            $g2.Clear([Drawing.Color]::FromArgb(30 * $n, 60, 200 - 40 * $n)); $g2.Dispose()
            $b2.Save((Join-Path $importSrc "pic$n.jpg"), [Drawing.Imaging.ImageFormat]::Jpeg)
        }
        finally { $b2.Dispose() }
    }
    $srcPath = $importSrc -replace '\\','/'
    $r = Send-AsyncEval "window.__studio.call('importFolder', { path: '$srcPath' })"
    Check ($r -and $r.Contains('"count":2') -and $r.Contains('"created":"pic1"')) 'importFolder adds both images' (Short $r)
    Check ((Test-Path -LiteralPath (Join-Path $themes 'pic1\wallpaper.jpg')) -and (Test-Path -LiteralPath (Join-Path $themes 'pic2\theme.ini'))) 'the folder themes hold their files' ''
    $r = Send-AsyncEval "window.__studio.call('useDesktopWallpaper', { path: '$srcPath/pic2.jpg' })"
    Check ($r -and $r.Contains('"created":"pic2 2"')) 'useDesktopWallpaper makes a theme from a path' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('createGradient', { from: '#ff0000', to: '#0000ff', degrees: 0 })"
    Check ($r -and $r.Contains('"created":"Gradient"')) 'createGradient makes a theme' (Short $r)
    $gradIni = Join-Path $themes 'Gradient\theme.ini'
    Check ((Test-Path -LiteralPath $gradIni) -and ([IO.File]::ReadAllText($gradIni) -match 'gradient=#FF0000\|#0000FF\|0')) 'the gradient recipe is stored' ''
    Check (Test-Path -LiteralPath (Join-Path $themes 'Gradient\wallpaper.jpg')) 'the gradient rendered a wallpaper' ''
    $r = Send-AsyncEval "window.__studio.call('editGradient', { name: 'Gradient', from: '#00ff00', to: '#003000', degrees: 45 })"
    Check ($r -and $r.Contains('"ok":true') -and ([IO.File]::ReadAllText($gradIni) -match 'gradient=#00FF00\|#003000\|45')) 'editGradient rewrites the recipe' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('state', {})"
    Check ($r -and $r.Contains('"gradient":"#00FF00|#003000|45"')) 'state reports the gradient recipe' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('cropAvatar', { name: 'Seed', x: 10, y: 10, size: 64 })"
    Check ($r -and $r.Contains('"ok":true') -and (Test-Path -LiteralPath (Join-Path $seed 'avatar.jpg'))) 'cropAvatar writes avatar.jpg' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('pasteImage', {})"
    Check ($r -and $r.Contains('"ok":true')) 'pasteImage answers without hanging' (Short $r)

    # The crop square: a 2000x1000 horizontal red->blue gradient so the avatar's
    # colour proves which source pixels the JS mapping sent to cropAvatar.
    $wideFile = Join-Path $Out 'wide.jpg'
    $bmp2 = New-Object Drawing.Bitmap(2000, 1000)
    try {
        $g3 = [Drawing.Graphics]::FromImage($bmp2)
        $grad = New-Object Drawing.Drawing2D.LinearGradientBrush(
            (New-Object Drawing.Rectangle(0, 0, 2000, 1000)),
            [Drawing.Color]::FromArgb(255, 0, 0), [Drawing.Color]::FromArgb(0, 0, 255), 0)
        $g3.FillRectangle($grad, 0, 0, 2000, 1000)
        $g3.Dispose(); $grad.Dispose()
        $bmp2.Save($wideFile, [Drawing.Imaging.ImageFormat]::Jpeg)
    }
    finally { $bmp2.Dispose() }
    $wide = $wideFile -replace '\\','/'
    $r = Send-AsyncEval "window.__studio.call('createTheme', { path: '$wide' })"
    Check ($r -and $r.Contains('"created":"wide"')) 'createTheme makes the wide theme' (Short $r)
    $cropEval = @'
window.dispatchEvent(new Event('focus'));
await new Promise(r => setTimeout(r, 500));
var row = [...document.querySelectorAll('.row')].find(r => r.textContent.includes('wide'));
if (!row) return 'no row';
row.click();
await new Promise(r => setTimeout(r, 1500));
var btn = [...document.querySelectorAll('.avatar-row button')].find(b => b.textContent.startsWith('Crop'));
if (!btn) return 'no crop button';
btn.click();
await new Promise(r => setTimeout(r, 250));
var box = document.querySelector('.crop-box'), art = document.querySelector('.art'), stage = document.querySelector('.stage');
if (!box || !art || !stage) return 'no crop ui';
var b = box.getBoundingClientRect();
var cx = b.left + b.width / 2, cy = b.top + b.height / 2;
box.dispatchEvent(new PointerEvent('pointerdown', { clientX: cx, clientY: cy, bubbles: true }));
box.dispatchEvent(new PointerEvent('pointermove', { clientX: cx + 120, clientY: cy, bubbles: true }));
box.dispatchEvent(new PointerEvent('pointerup', { clientX: cx + 120, clientY: cy, bubbles: true }));
var out = { left: parseFloat(box.style.left), top: parseFloat(box.style.top), size: parseFloat(box.style.width),
    sw: stage.clientWidth, sh: stage.clientHeight, nw: art.naturalWidth, nh: art.naturalHeight };
box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
var seedRow = [...document.querySelectorAll('.row')].find(r => r.textContent.indexOf('Seed') >= 0);
if (seedRow) seedRow.click();
return out;
'@
    $r = Send-AsyncEval "(async () => { $cropEval })()"
    $crop = $null
    try { $crop = ($r | ConvertFrom-Json).value.value } catch { $crop = $null }
    Check ($null -ne $crop -and $crop.nw -gt 0) 'the crop overlay opened on the wide wallpaper' (Short $r)
    if ($crop -and $crop.nw -gt 0) {
        $avatar = Join-Path $themes 'wide\avatar.jpg'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $avatar) -and $watch.Elapsed.TotalSeconds -lt 15) { Start-Sleep -Milliseconds 250 }
        if (Test-Path -LiteralPath $avatar) {
            # Stage math back to a fraction of the image (cover fit, focus 50/50),
            # then to source pixels of the 2000x1000 wallpaper.
            $scale = [Math]::Max($crop.sw / $crop.nw, $crop.sh / $crop.nh)
            $ox = ($crop.sw - $crop.nw * $scale) * 0.5
            $frac = ($crop.left - $ox) / ($crop.nw * $scale) + $crop.size / ($crop.nw * $scale) / 2
            $center = $frac * 2000
            $t = [Math]::Min(1.0, [Math]::Max(0.0, $center / 1999))
            $expR = [int](255 * (1 - $t)); $expB = [int](255 * $t)
            $img = New-Object Drawing.Bitmap($avatar)
            try { $px = $img.GetPixel($img.Width / 2, $img.Height / 2) } finally { $img.Dispose() }
            Check ([Math]::Abs($px.R - $expR) -lt 45 -and [Math]::Abs($px.B - $expB) -lt 45) `
                'the crop square lands on the right pixels' "expected ~($expR,0,$expB) got ($($px.R),$($px.G),$($px.B)) at src x $([int]$center)"
        }
        else { Check $false 'the crop square lands on the right pixels' 'avatar.jpg never appeared' }
    }
    else { Check $false 'the crop square lands on the right pixels' 'crop did not open' }

    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', speed: 3, once: true, style: 'short' })"
    Check ($r -and $r.Contains('"speed":3') -and $r.Contains('"once":true') -and $r.Contains('"style":"short"')) 'setApp stores speed, once and style' (Short $r)
    $devinIni = Join-Path $dataRoot 'Devin Animated\settings.ini'
    $devinText = [IO.File]::ReadAllText($devinIni)
    Check ($devinText.Contains('speed=3') -and $devinText.Contains('once=on') -and $devinText.Contains('style=short')) 'the app settings.ini holds the new keys' ''
    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', speed: 1, once: false, style: 'full' })"
    Check ($r -and $r.Contains('"speed":1') -and $r.Contains('"once":false') -and $r.Contains('"style":"full"')) 'setApp puts the defaults back' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', rotate: 'start', rotateFavs: true, followWindows: true, themeLight: 'Seed' })"
    Check ($r -and $r.Contains('"rotate":"start"') -and $r.Contains('"rotateFavs":true') -and $r.Contains('"followWindows":true') -and $r.Contains('"themeLight":"Seed"')) 'setApp stores the variety keys' (Short $r)
    $devinText = [IO.File]::ReadAllText($devinIni)
    Check ($devinText.Contains('rotate=start') -and $devinText.Contains('rotateFavs=on') -and $devinText.Contains('followWindows=on') -and $devinText.Contains('themeLight=Seed')) 'settings.ini holds the variety keys' ''
    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', themeLight: 'Not a theme' }).then(() => 'no error').catch(e => 'err')"
    Check ($r -and $r.Contains('"err"')) 'themeLight refuses an unknown theme' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('applyTheme', { name: 'Seed', app: 'devin' })"
    $devinText = [IO.File]::ReadAllText($devinIni)
    Check ($devinText.Contains('applied=Seed')) 'applyTheme stamps applied=' ''
    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', rotate: 'off', rotateFavs: false, followWindows: false, themeLight: '' })"
    Check ($r -and $r.Contains('"rotate":"off"')) 'the variety keys put the defaults back' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', themeNight: 'Seed', nightStart: 22, nightUntil: 5, nightDim: 30, extraDim: 10, hideFullscreen: false })"
    Check ($r -and $r.Contains('"themeNight":"Seed"') -and $r.Contains('"nightStart":22') -and $r.Contains('"nightDim":30') -and $r.Contains('"hideFullscreen":false')) 'setApp stores the day-and-night keys' (Short $r)
    $devinText = [IO.File]::ReadAllText($devinIni)
    Check ($devinText.Contains('themeNight=Seed') -and $devinText.Contains('nightStart=22') -and $devinText.Contains('nightUntil=5') -and $devinText.Contains('nightDim=30') -and $devinText.Contains('extraDim=10') -and $devinText.Contains('hideFullscreen=off')) 'settings.ini holds the day-and-night keys' ''
    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', themeNight: 'Not a theme' }).then(() => 'no error').catch(e => 'err')"
    Check ($r -and $r.Contains('"err"')) 'themeNight refuses an unknown theme' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', nightStart: 40, nightUntil: -3, nightDim: 200 })"
    Check ($r -and $r.Contains('"nightStart":23') -and $r.Contains('"nightUntil":0') -and $r.Contains('"nightDim":80')) 'the day-and-night numbers clamp' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('setApp', { app: 'devin', themeNight: '', nightStart: 20, nightUntil: 7, nightDim: 0, extraDim: 0, hideFullscreen: true })"
    Check ($r -and $r.Contains('"themeNight":""') -and $r.Contains('"hideFullscreen":true')) 'the day-and-night keys put the defaults back' (Short $r)

    $r = Send-Eval "var b = [].find.call(document.querySelectorAll('.rail-foot button'), x => x.textContent.indexOf('Apps') >= 0); if (b) b.click(); return 'ok'"
    Start-Sleep -Milliseconds 700
    $r = Send-Eval "var block = document.querySelector('.app-block[data-app=\'devin\']'); var labels = block ? [...block.querySelectorAll('.ctl .label')].map(x => x.textContent) : []; return labels.join('|')"
    Check ($r -and $r.Contains('Rotate themes') -and $r.Contains('Starred themes only') -and $r.Contains('Follow Windows') -and $r.Contains('Light theme')) 'the apps block shows the variety controls' (Short $r)
    $r = Send-Eval "var block = document.querySelector('.app-block[data-app=\'devin\']'); var labels = block ? [...block.querySelectorAll('.ctl .label')].map(x => x.textContent) : []; return labels.join('|')"
    Check ($r -and $r.Contains('Night theme') -and $r.Contains('Night hours') -and $r.Contains('Night dim') -and $r.Contains('Extra dim') -and $r.Contains('Hide in full screen')) 'the apps block shows the day-and-night controls' (Short $r)
    $r = Send-Eval "var block = document.querySelector('.app-block[data-app=\'devin\']'); var ctl = block ? [...block.querySelectorAll('.ctl')].find(c => c.querySelector('.label').textContent === 'Night hours') : null; var sels = ctl ? [...ctl.querySelectorAll('select')] : []; return sels.length + ':' + sels.map(s => s.value).join('-') + ':' + ctl.textContent.includes('to')"
    Check ($r -and $r.Contains('2:20-7') -and $r.Contains(':true')) 'the night-hours control offers two hour picks' (Short $r)
    $r = Send-Eval "var block = document.querySelector('.app-block[data-app=\'devin\']'); var seg = block ? [...block.querySelectorAll('.ctl')].find(c => c.querySelector('.label').textContent === 'Rotate themes') : null; return seg ? seg.querySelector('.seg').textContent : 'none'"
    Check ($r -and $r.Contains('Off') -and $r.Contains('On start') -and $r.Contains('15 min') -and $r.Contains('Hourly')) 'the rotate control offers Off, On start, 15 min and Hourly' (Short $r)

    $r = Send-Eval "var row = document.querySelector('.rail .row[data-name=\'Seed\']') || document.querySelector('.rail .row'); if (row) row.click(); return 'ok'"
    Start-Sleep -Milliseconds 500
    $r = Send-Eval "var g = document.getElementById('g-startup'); var sel = g ? g.querySelector('select[aria-label=\'Animation style\']') : null; return sel ? [...sel.options].map(o => o.text).join(',') : 'no select'"
    Check ($r -and $r.Contains('Full') -and $r.Contains('Card') -and $r.Contains('Terminal') -and $r.Contains('Manga') -and $r.Contains('Minimal')) 'the startup group offers every animation style' (Short $r)
    $r = Send-Eval "var sel = document.querySelector('#g-startup select[aria-label=\'Animation style\']'); sel.value = 'short'; sel.dispatchEvent(new Event('change')); return 'ok'"
    Start-Sleep -Milliseconds 800
    $seedIni = [IO.File]::ReadAllText((Join-Path $seed 'theme.ini'))
    Check ($seedIni.Contains('style=short')) 'picking Card writes style=short into the theme' ''
    $r = Send-Eval "var sel = document.querySelector('#g-startup select[aria-label=\'Animation style\']'); sel.value = 'terminal'; sel.dispatchEvent(new Event('change')); return 'ok'"
    Start-Sleep -Milliseconds 800
    $seedIni = [IO.File]::ReadAllText((Join-Path $seed 'theme.ini'))
    Check ($seedIni.Contains('style=terminal')) 'picking Terminal writes style=terminal into the theme' ''

    $r = Send-AsyncEval "window.__studio.call('saveTheme', { name: 'Seed', look: {}, text: { credit: 'art by Pack Check' } })"
    Start-Sleep -Milliseconds 400
    $seedIni = [IO.File]::ReadAllText((Join-Path $seed 'theme.ini'))
    Check ($seedIni.Contains('credit=art by Pack Check')) 'the credit line is saved into theme.ini' ''

    $r = Send-AsyncEval "window.__studio.call('saveTheme', { name: 'Seed', look: {}, text: { font: 'mono', frame: 'hex', lines: ['one pick', 'another'], logs: ['fixed a', 'fixed b'] } })"
    Start-Sleep -Milliseconds 400
    $seedIni = [IO.File]::ReadAllText((Join-Path $seed 'theme.ini'))
    Check ($seedIni.Contains('font=mono') -and $seedIni.Contains('frame=hex') -and $seedIni.Contains('lines=one pick|another')) 'font/frame/lines save into theme.ini' (Short $seedIni)
    Check ($r -and $r.Contains('"font":"mono"') -and $r.Contains('"frame":"hex"')) 'saveTheme returns the stored font and frame' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('saveTheme', { name: 'Seed', look: {}, text: { font: 'papyrus', frame: 'oval' } })"
    Start-Sleep -Milliseconds 400
    $seedIni = [IO.File]::ReadAllText((Join-Path $seed 'theme.ini'))
    Check (-not $seedIni.Contains('font=papyrus') -and -not $seedIni.Contains('frame=oval')) 'invalid font/frame normalize to empty' (Short $seedIni)

    $jpeg64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $seed 'wallpaper.jpg')))
    $r = Send-AsyncEval ("Promise.resolve(Uint8Array.from(atob('$jpeg64'), c => c.charCodeAt(0))).then(a => window.__studio.dropped(new File([a], 'dropped-pic.jpg', { type: 'image/jpeg' })))")
    Check ($r -and $r.Contains('"value":true')) 'dropping image bytes makes a theme' (Short $r)
    Check (Test-Path -LiteralPath (Join-Path $themes 'dropped-pic\wallpaper.jpg')) 'the dropped theme folder exists' ''

    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($packFile))
    $r = Send-AsyncEval ("Promise.resolve(Uint8Array.from(atob('$b64'), c => c.charCodeAt(0))).then(a => window.__studio.dropped(new File([a], 'incoming.altheme', { type: 'application/zip' })))")
    $dropOk = $r -and $r.Contains('"value":true')
    if (-not $dropOk) {
        $st = Send-Eval "var s = document.querySelector('.status'); return s ? s.textContent : '(no status)'"
        $dropOk = $false
        $r = (Short $r) + ' | status: ' + (Short $st)
    }
    Check $dropOk 'dropping a theme file imports it' (Short $r)
    Check (Test-Path -LiteralPath (Join-Path $themes 'Seed 3\wallpaper.jpg')) 'the imported theme folder exists' ''

    # An animated wallpaper: drop gif bytes, export the theme, re-import it.
    $gifB64 = 'R0lGODlhKAAeAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAKAAeAAAIOgABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJsqXLlzAxBgQAIfkEARQAAQAsAAAAACgAHgCBAP8AAAAAAAAAAAAACDoAAQgcSLCgwYMIEypcyLChw4cQI0qcSLGixYsYM2rcyLGjx48gQ4ocSbKkyZMoU6pcybKly5cwMQYEACH5BAEFAAEALAAAAAAoAB4AgQAA/wAAAAAAAAAAAAg6AAEIHEiwoMGDCBMqXMiwocOHECNKnEixosWLGDNq3Mixo8ePIEOKHEmypMmTKFOqXMmypcuXMDEGBAA7'
    $r = Send-AsyncEval ("Promise.resolve(Uint8Array.from(atob('$gifB64'), c => c.charCodeAt(0))).then(a => window.__studio.dropped(new File([a], 'dropped-anim.gif', { type: 'image/gif' })))")
    Check ($r -and $r.Contains('"value":true')) 'dropping a gif makes an animated theme' (Short $r)
    Check ((Test-Path -LiteralPath (Join-Path $themes 'dropped-anim\wallpaper.gif')) -and
           (Test-Path -LiteralPath (Join-Path $themes 'dropped-anim\wallpaper.jpg'))) 'the theme holds the gif and its first frame' ''

    $gifPack = Join-Path $Out 'anim.altheme'
    $r = Send-AsyncEval "window.__studio.call('exportTheme', { name: 'dropped-anim', path: '$($gifPack -replace '\\','/')' })"
    Check ($r -and $r.Contains('"ok":true')) 'exportTheme packs the animated theme' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('importTheme', { path: '$($gifPack -replace '\\','/')' })"
    Check ($r -and $r.Contains('"imported"')) 'importTheme re-imports the animated theme' (Short $r)
    Check (@(Get-ChildItem -LiteralPath $themes -Directory -Filter 'dropped-anim*' |
        Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'wallpaper.gif')) }).Count -ge 2) 'the gif survives the export/import round trip' ''

    $r = Send-AsyncEval "window.__studio.call('backupLibrary', { path: '$($backupFile -replace '\\','/')' })"
    $backedUp = @(Get-ChildItem -LiteralPath $themes -Directory).Count
    Check ($r -and $r.Contains('"ok":true') -and (Test-Path -LiteralPath $backupFile)) 'backupLibrary writes the backup file' (Short $r)

    Remove-Item -LiteralPath (Join-Path $themes 'Seed 3') -Recurse -Force
    $r = Send-AsyncEval "window.__studio.call('restoreLibrary', { path: '$($backupFile -replace '\\','/')' })"
    # Every backup entry lands; existing names dedupe.
    Check ($r -and $r.Contains("`"restored`":$backedUp")) "restoreLibrary restores all $backedUp themes" (Short $r)
    $finalCount = @(Get-ChildItem -LiteralPath $themes -Directory).Count
    Check ($finalCount -eq $backedUp * 2 - 1) 'the library has every theme after restore' "$finalCount of $($backedUp * 2 - 1)"

    $r = Send-AsyncEval "window.__studio.call('saveLookPreset', { name: 'Pack Look', look: { clear: '70', clearSide: 'right', glow: '15', sat: '130', fade: '10', fit: 'contain', vignette: '25' } })"
    Check ($r -and $r.Contains('"Pack Look"')) 'saveLookPreset stores a named look' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('applyLookPreset', { name: 'Pack Look' })"
    Check ($r -and $r.Contains('"clear":70') -and $r.Contains('"fit":"contain"')) 'applyLookPreset returns the stored look' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('saveTheme', { name: 'Seed', look: { clear: '70', clearSide: 'right', glow: '15', sat: '130', fade: '10', fit: 'contain', vignette: '25' }, text: {} })"
    $seedIni = [IO.File]::ReadAllText((Join-Path $seed 'theme.ini'))
    Check ($seedIni.Contains('clear=70') -and $seedIni.Contains('fit=contain') -and $seedIni.Contains('vignette=25')) 'the new look keys save into theme.ini' ''
    $r = Send-AsyncEval "window.__studio.call('deleteLookPreset', { name: 'Pack Look' })"
    Check ($r -and -not $r.Contains('"Pack Look"')) 'deleteLookPreset removes it' (Short $r)

    # --- update 9: studio comfort ----------------------------------------------
    $r = Send-AsyncEval "window.__studio.call('setPrefs', { favorites: 'pic1|Seed', sort: 'favorites' })"
    Check ($r -and $r.Contains('"sort":"favorites"') -and $r.Contains('"pic1"')) 'setPrefs stores sort and favourites' (Short $r)
    $studioIni = Join-Path $dataRoot 'Animated Launchers\studio.ini'
    $studioText = [IO.File]::ReadAllText($studioIni)
    Check ($studioText.Contains('sort=favorites') -and $studioText.Contains('favorites=pic1|Seed')) 'studio.ini persists the prefs' ''
    Check ($studioText.Contains('window=')) 'the window position survives the prefs write' ''
    $r = Send-AsyncEval "window.__studio.call('state', {})"
    Check ($r -and $r.Contains('"prefs":{') -and $r.Contains('"pic1"')) 'state carries the prefs' (Short $r)

    # Undo/redo through the real key handler: blur 50 -> Ctrl+Z -> 0 -> Ctrl+Y -> 50.
    $undoEval = @'
window.dispatchEvent(new Event('focus'));
await new Promise(r => setTimeout(r, 500));
var row = [...document.querySelectorAll('.row')].find(r => r.dataset.name === 'pic1');
if (!row) return 'no pic1 row';
row.click();
await new Promise(r => setTimeout(r, 900));
var slider = document.querySelector('input.slider[aria-label="Blur"]');
if (!slider) return 'no blur slider';
slider.value = '50';
slider.dispatchEvent(new Event('input', { bubbles: true }));
await new Promise(r => setTimeout(r, 900));
var blur50 = window.__studio.state.themes.find(t => t.name === 'pic1').look.blur;
window.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }));
await new Promise(r => setTimeout(r, 1200));
var blurAfterUndo = window.__studio.state.themes.find(t => t.name === 'pic1').look.blur;
window.dispatchEvent(new KeyboardEvent('keydown', { key: 'y', ctrlKey: true, bubbles: true }));
await new Promise(r => setTimeout(r, 1200));
var blurAfterRedo = window.__studio.state.themes.find(t => t.name === 'pic1').look.blur;
window.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }));
await new Promise(r => setTimeout(r, 1200));
var blurRestored = window.__studio.state.themes.find(t => t.name === 'pic1').look.blur;
var slider2 = document.querySelector('input.slider[aria-label="Blur"]');
return { blur50: blur50, undo: blurAfterUndo, redo: blurAfterRedo, back: blurRestored, shown: slider2 ? slider2.value : '?' };
'@
    $r = Send-AsyncEval "(async () => { $undoEval })()"
    Check ($r -and $r.Contains('"blur50":50') -and $r.Contains('"undo":0') -and $r.Contains('"redo":50') -and $r.Contains('"back":0')) 'Ctrl+Z and Ctrl+Y undo and redo a slider edit' (Short $r)
    $pic1Ini = [IO.File]::ReadAllText((Join-Path $themes 'pic1\theme.ini'))
    Check (-not $pic1Ini.Contains('blur=50')) 'the undo writes back to theme.ini' $pic1Ini

    # Double-click a slider to reset it; click the number to type a value.
    $sliderEval = @'
var slider = document.querySelector('input.slider[aria-label="Blur"]');
if (!slider) return 'no slider';
slider.value = '44'; slider.dispatchEvent(new Event('input', { bubbles: true }));
await new Promise(r => setTimeout(r, 700));
slider.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
await new Promise(r => setTimeout(r, 700));
var afterReset = slider.value;
var value = slider.closest('.srow').querySelector('.value');
value.click();
await new Promise(r => setTimeout(r, 120));
var typed = value.querySelector('.val-edit');
if (!typed) return 'no val-edit';
typed.value = '33';
typed.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
await new Promise(r => setTimeout(r, 700));
var t = window.__studio.state.themes.find(x => x.name === 'pic1');
return { reset: afterReset, typed: slider.value, blur: t.look.blur };
'@
    $r = Send-AsyncEval "(async () => { $sliderEval })()"
    Check ($r -and $r.Contains('"reset":"0"') -and $r.Contains('"typed":"33"') -and $r.Contains('"blur":33')) 'slider resets on double-click and takes typed numbers' (Short $r)

    # Search filters the rail; the sort select re-sorts; a star favourites.
    $r = Send-AsyncEval "(async () => {
        window.dispatchEvent(new Event('focus'));
        await new Promise(r => setTimeout(r, 600));
        var search = document.querySelector('.rail-search');
        if (!search) return 'no search';
        var names = [...document.querySelectorAll('.rail .row')].map(r => r.dataset.name);
        var term = names.find(n => names.filter(m => m.toLowerCase().indexOf(n.toLowerCase()) >= 0).length === 1);
        if (!term) return 'no unique theme name';
        search.value = term; search.dispatchEvent(new Event('input', { bubbles: true }));
        await new Promise(r => setTimeout(r, 250));
        var onlyOne = document.querySelectorAll('.rail .row').length;
        search.value = ''; search.dispatchEvent(new Event('input', { bubbles: true }));
        await new Promise(r => setTimeout(r, 250));
        var all = document.querySelectorAll('.rail .row').length;
        var sort = document.querySelector('.rail-sort');
        if (!sort) return 'no sort';
        sort.value = 'favorites'; sort.dispatchEvent(new Event('change', { bubbles: true }));
        await new Promise(r => setTimeout(r, 700));
        var first = document.querySelector('.rail .row');
        var fav = first ? first.querySelector('.fav') : null;
        if (!fav) return 'no fav star';
        var starBefore = fav.getAttribute('aria-pressed');
        fav.click();
        await new Promise(r => setTimeout(r, 800));
        var first2 = document.querySelector('.rail .row');
        return { onlyOne: onlyOne, all: all, starBefore: starBefore,
            firstAfter: first2 ? first2.dataset.name : '?',
            starAfter: first2 ? first2.querySelector('.fav').getAttribute('aria-pressed') : '?' };
    })()"
    Check ($r -and $r.Contains('"onlyOne":1') -and $r.Contains('"starBefore":"true"')) 'search filters and favourites sort first' (Short $r)
    $studioText = [IO.File]::ReadAllText($studioIni)
    Check ($studioText.Contains('favorites=')) 'starring updates studio.ini' ''

    # Hold C (or the Compare button) shows the untouched picture.
    $r = Send-AsyncEval "(async () => {
        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'c', bubbles: true }));
        await new Promise(r => setTimeout(r, 1200));
        var art = document.querySelector('.stage .art');
        var badge = document.querySelector('.stage.comparing') ? 'Original' : '';
        var src = art ? art.src : '';
        window.dispatchEvent(new KeyboardEvent('keyup', { key: 'c', bubbles: true }));
        await new Promise(r => setTimeout(r, 1200));
        var back = art ? art.src : '';
        return { src: src, badge: badge, back: back };
    })()"
    Check ($r -and $r.Contains('img/wallpaper') -and $r.Contains('"badge":"Original"') -and $r.Contains('img/look')) 'holding C compares with the untouched picture' (Short $r)

    # The Keys card lists the shortcuts.
    $r = Send-AsyncEval "(async () => {
        window.dispatchEvent(new KeyboardEvent('keydown', { key: '?', bubbles: true }));
        await new Promise(r => setTimeout(r, 200));
        var card = document.querySelector('.keys-card');
        var rows = card ? card.querySelectorAll('.key-row').length : 0;
        if (card) card.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        return { rows: rows };
    })()"
    Check ($r -and $r.Contains('"rows":11')) 'the keys card lists every shortcut' (Short $r)

    # Ctrl+D duplicates the current theme through the key handler.
    $r = Send-AsyncEval "(async () => {
        var sel = window.__studio.state.sel;
        window.dispatchEvent(new KeyboardEvent('keydown', { key: 'd', ctrlKey: true, bubbles: true }));
        await new Promise(r => setTimeout(r, 1500));
        return { sel: window.__studio.state.sel, count: window.__studio.state.themes.length };
    })()"
    Check ($r -and $r.Contains('copy')) 'Ctrl+D duplicates the theme' (Short $r)

    # --- update 10: status, report, settings, updates --------------------------
    $r = Send-AsyncEval "window.__studio.call('checkReadability', { name: 'Seed', blur: 0, dim: 0, tint: 0, sat: 100, fade: 0 })"
    Check ($r -and $r.Contains('"luma"') -and $r.Contains('"bright"')) 'checkReadability returns a luma' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('copyReport', { dryRun: true })"
    Check ($r -and $r.Contains('Devin (devin)') -and $r.Contains('theme:')) 'copyReport lists the apps' (Short $r)

    $r = Send-AsyncEval "window.__studio.call('openLogFolder', { dryRun: true })"
    Check ($r -and $r.Contains('Logs')) 'openLogFolder resolves the log folder' (Short $r)

    $upd = Join-Path $Out 'latest.json'
    [IO.File]::WriteAllText($upd, '{"tag_name":"v99.0.0","html_url":"https://github.com/Synaap/Codex-Animated/releases/tag/v99.0.0","assets":[{"name":"Codex-Animated-v99.0.0-windows-x64.zip","browser_download_url":"https://github.com/Synaap/Codex-Animated/releases/download/v99.0.0/Codex-Animated-v99.0.0-windows-x64.zip"}]}', (New-Object Text.UTF8Encoding($false)))
    $updUri = ([uri]$upd).AbsoluteUri
    $r = Send-AsyncEval "window.__studio.call('checkUpdates', { url: '$updUri' })"
    Check ($r -and $r.Contains('"updateAvailable":true') -and $r.Contains('99.0.0') -and $r.Contains('windows-x64.zip')) 'checkUpdates reads a release feed and its zip' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('getUpdate', { url: 'https://example.com/evil' })"
    Check ($r -and ($r.Contains('error') -or $r.Contains('Only'))) 'getUpdate refuses a foreign URL' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('getUpdate', { url: 'https://github.com/Synaap/Codex-Animated/releases/tag/v99.0.0', zip: 'https://github.com/Synaap/Codex-Animated/releases/download/v99.0.0/x.zip', version: '99.0.0', dryRun: true })"
    Check ($r -and $r.Contains('"dryRun":true') -and $r.Contains('animated-launchers-update-99.0.0')) 'getUpdate resolves the update target under TEMP' (Short $r)

    $r = Send-Eval "var b=[].find.call(document.querySelectorAll('.rail-foot button'), x => x.textContent.indexOf('Settings') >= 0); if (b) b.click(); return b ? 'clicked' : 'no button'"
    Check ($r -and $r.Contains('clicked')) 'the rail footer has a Settings button' (Short $r)
    Start-Sleep -Milliseconds 600
    $r = Send-Eval "return [].find.call(document.querySelectorAll('.settings .app-name'), x => x.textContent === 'Updates') ? 'yes' : 'no'"
    Check ($r -and $r.Contains('yes')) 'the settings view renders the Updates block' (Short $r)

    # restartHelpers: the stamp lives under the scratch data root, so while the
    # real installed helper holds the mutex it cannot see it and the stop wait
    # must time out cleanly. With no helper running the whole cycle works.
    $created = $false
    $probe = New-Object System.Threading.Mutex($false, 'Local\AnimatedLaunchers.Artwork', [ref]$created)
    $probe.Dispose()
    $r = Send-AsyncEval "window.__studio.call('restartHelpers')" -Seconds 20
    if (-not $created) {
        Check ($r -and $r.Contains('did not stop')) 'restartHelpers reports when the helper cannot see the stamp' (Short $r)
    } else {
        Check ($r -and $r.Contains('"id"')) 'restartHelpers returns the app list' (Short $r)
        $deadline = (Get-Date).AddSeconds(10); $statusFiles = @()
        while ((Get-Date) -lt $deadline -and -not $statusFiles) {
            Start-Sleep -Milliseconds 400
            $statusFiles = @(Get-ChildItem -LiteralPath $dataRoot -Recurse -Filter 'status.ini' -ErrorAction SilentlyContinue)
        }
        $first = if ($statusFiles.Count) { Get-Content -LiteralPath $statusFiles[0].FullName -Raw } else { '' }
        Check ($statusFiles.Count -gt 0 -and $first -match 'status=(on|waiting|off|parked)') 'sessions write status.ini' (Short $first)
        $r = Send-AsyncEval "window.__studio.call('restartHelpers')" -Seconds 20
        Check ($r -and $r.Contains('"id"')) 'restartHelpers round-trips through the stamp' (Short $r)
        [IO.File]::WriteAllText((Join-Path $dataRoot 'Animated Launchers\restart.stamp'), 'stop', (New-Object Text.UTF8Encoding($false)))
        Start-Sleep -Seconds 3
        $helpers = @(Get-Process AnimatedLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$From\*" })
        Check ($helpers.Count -eq 0) 'the restart stamp stops the helper' ($helpers.Count)
    }

    # Update 16: the starter pack. Previews come back as base64 thumbs, seeding
    # creates the six themes once, the welcome flag round-trips through prefs.
    $r = Send-AsyncEval "window.__studio.call('starterPreview')" -Seconds 30
    Check ($r -and $r.Contains('"Aurora fields"') -and $r.Contains('"Sunset"') -and $r.Contains('"jpeg"')) 'starterPreview returns six thumbs' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('seedStarters')" -Seconds 40
    Check ($r -and $r.Contains('"Aurora fields"') -and $r.Contains('"Deep sea"')) 'seedStarters creates the pack' (Short $r)
    $r = Send-AsyncEval "window.__studio.call('seedStarters')" -Seconds 40
    Check ($r -and $r.Contains('"seeded":[]')) 'seedStarters is idempotent' (Short $r)
    $gradientTheme = Join-Path $themes 'Deep sea\theme.ini'
    $gradientIni = Get-Content -LiteralPath $gradientTheme -Raw -ErrorAction SilentlyContinue
    Check ($gradientIni -match 'gradient=#062E4F\|#0A7C8C\|160' -and $gradientIni -match 'credit=Generated by Animated Launchers') 'a seeded gradient keeps its recipe and credit' (Short $gradientIni)
    $r = Send-AsyncEval "window.__studio.call('setPrefs', { favorites: '', sort: 'name', welcome: 'off' })"
    Check ($r -and $r.Contains('"welcome":false')) 'dismissing the welcome card writes welcome=off' (Short $r)

    # --- language preference ---
    $r = Send-AsyncEval "window.__studio.call('setPrefs', { lang: 'fr' })"
    Check ($r -and $r.Contains('"lang":"fr"')) 'setPrefs stores the language preference' (Short $r)
    $r = Send-Eval 'return [!!(window.STUDIO_I18N && window.STUDIO_I18N.fr), window.STUDIO_I18N ? window.STUDIO_I18N.fr["Settings"] : ""].join("|")'
    # [char]0xE9 keeps the check independent of how PowerShell decodes this file.
    Check ($r -and $r.Contains('true|R' + [char]0xE9 + 'glages')) 'the French dictionary is loaded' (Short $r)
    $studioIniCheck = Get-Content -LiteralPath (Join-Path $dataRoot 'Animated Launchers\studio.ini') -Raw -ErrorAction SilentlyContinue
    Check ($studioIniCheck -match 'lang=fr') 'the language persists to studio.ini' (Short $studioIniCheck)

    [void](Send-Eval '@close' -Seconds 10)
    $proc.WaitForExit(15000) | Out-Null
}
finally {
    if ($proc -and -not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { } }
    if ($null -ne $iniBackup) { [IO.File]::WriteAllBytes($ini, $iniBackup) }
    elseif (Test-Path -LiteralPath $ini) { Remove-Item -LiteralPath $ini -Force }
}

Write-Output "== $script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
exit 0
