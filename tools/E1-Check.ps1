# Experiment E1: can the in-window artwork carry different transparency per
# pixel? A scratch Devin helper (ANIMATED_DATA=<scratch>) draws over the real
# Devin window with a synthetic WHITE wallpaper at opacity 40, so the artwork's
# contribution dwarfs any app repaint between captures. clear=100 clearSide=right
# must leave the left quarter hazed while the right quarter returns to baseline.
# E5 rides along: a black/white wallpaper with glow=100 (same alpha path keyed
# on luminance) must show the white half and hide the black half.
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
. (Join-Path $root 'Toolkit.ps1')
$out = Join-Path $root 'out\e1-check'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$helperExe = Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers\DevinAnimated.exe'
$live = @(Get-HelperProcesses Devin) | Select-Object -First 1
$livePath = $null; if ($live) { $livePath = $live.Path }
$test = $null

function Client-Rgb($win, $name) {
    # PrintWindow(hwnd, dc, 2) renders the whole window incl. layered children
    # offscreen - immune to occlusion by other windows on the user's desktop.
    $png = Join-Path $out "$name.png"
    [TK]::Capture($win.Handle, $png)
    $r = [TK]::Client($win.Handle).Split(',') | ForEach-Object { [int]$_ }
    $w = $r[2] - $r[0]
    $L = [TK]::MeanRgb($png, 0, 0, [int]($w * 0.25), 100000)
    $R = [TK]::MeanRgb($png, [int]($w * 0.75), 0, [int]($w * 0.25), 100000)
    return @{ L = $L; R = $R; Png = $png }
}
function Rgb-Delta($a, $b) {
    $pa = ($a -split ' ')[0].Split(','); $pb = ($b -split ' ')[0].Split(',')
    $d = 0.0; for ($i = 0; $i -lt 3; $i++) { $d += [Math]::Abs([double]$pa[$i] - [double]$pb[$i]) }
    return [Math]::Round($d, 1)
}
function Set-IniKeys($path, [hashtable]$keys) {
    $lines = @(); if (Test-Path $path) { $lines = @(Get-Content $path) }
    foreach ($k in $keys.Keys) {
        $found = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "^$k=") { $lines[$i] = "$k=$($keys[$k])"; $found = $true; break }
        }
        if (-not $found) { $lines += "$k=$($keys[$k])" }
    }
    [IO.File]::WriteAllText($path, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $false))
}
function Wait-Art($win) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 25) {
        $child = Get-ArtworkChild $win 'Devin'
        if ($child) { return $child }
        Start-Sleep -Milliseconds 250
    }
    throw 'artwork child never attached'
}
function New-Jpeg($path, [scriptblock]$paint) {
    $bmp = New-Object Drawing.Bitmap 640, 480
    try {
        $g = [Drawing.Graphics]::FromImage($bmp)
        & $paint $g $bmp
        $g.Dispose()
        $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Jpeg)
    } finally { $bmp.Dispose() }
}

try {
    Add-Type -AssemblyName System.Drawing
    $win = @(Get-AppWindows Devin) | Select-Object -First 1
    if (-not $win) { throw 'no Devin window found' }

    foreach ($p in @(Get-HelperProcesses Devin)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1

    $scratch = Join-Path $out 'devin-data'
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
    $appDir = Join-Path $scratch 'Devin Animated'
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null
    $liveData = Join-Path $env:LOCALAPPDATA 'Devin Animated'
    $s = Join-Path $liveData 'settings.ini'
    if (Test-Path $s) { Copy-Item $s (Join-Path $appDir 'settings.ini') }
    $set = Join-Path $appDir 'settings.ini'
    $paper = Join-Path $appDir 'wallpaper.jpg'
    # Uniform white: the strongest possible artwork signal.
    New-Jpeg $paper { param($g, $b) $g.Clear([Drawing.Color]::White) }
    Set-IniKeys $set @{ opacity = '40'; blur = '0'; dim = '0'; tint = '0'; clear = '0'; clearSide = ''; glow = '0' }

    $base = Client-Rgb $win 'e1-0-noart'
    "baseline no-art:  L=$($base.L)  R=$($base.R)"

    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $helperExe
    $info.Arguments = '--artwork'
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['ANIMATED_DATA'] = $scratch
    $test = [Diagnostics.Process]::Start($info)
    Wait-Art $win | Out-Null
    Start-Sleep -Seconds 3

    $art = Client-Rgb $win 'e1-1-white40'
    $dLn = Rgb-Delta $base.L $art.L; $dRn = Rgb-Delta $base.R $art.R
    "white artwork:     L=$($art.L)  R=$($art.R)   delta-vs-base L=$dLn R=$dRn"
    if ($dLn -lt 15 -or $dRn -lt 15) { throw 'white artwork too weak to measure a clear zone' }

    Set-IniKeys $set @{ clear = '100'; clearSide = 'right' }
    Start-Sleep -Seconds 5
    if ($test.HasExited) { throw 'helper exited applying the clear look' }
    $clear = Client-Rgb $win 'e1-2-clear-right'
    $dRc = Rgb-Delta $art.R $clear.R; $dLc = Rgb-Delta $art.L $clear.L
    $dRb = Rgb-Delta $base.R $clear.R; $dLb = Rgb-Delta $base.L $clear.L
    "clear-right:       L=$($clear.L)  R=$($clear.R)   vs-art L=$dLc R=$dRc   vs-base L=$dLb R=$dRb"

    # Black left half / white right half: glow must hide black, keep white.
    New-Jpeg $paper { param($g, $b)
        $g.Clear([Drawing.Color]::Black)
        $g.FillRectangle([Drawing.Brushes]::White, $b.Width / 2, 0, $b.Width / 2, $b.Height) }
    Set-IniKeys $set @{ clear = '0'; clearSide = ''; glow = '100' }
    Start-Sleep -Seconds 6
    if ($test.HasExited) { throw 'helper exited applying the glow look' }
    $glow = Client-Rgb $win 'e1-3-glow'
    $dLg = Rgb-Delta $art.L $glow.L; $dRg = Rgb-Delta $art.R $glow.R
    $dLgb = Rgb-Delta $base.L $glow.L; $dRgb = Rgb-Delta $base.R $glow.R
    "glow on b/w:       L=$($glow.L)  R=$($glow.R)   vs-art L=$dLg R=$dRg   vs-base L=$dLgb R=$dRgb"

    $pass1 = $dRb -lt ($dRn * 0.4) -and $dLb -gt ($dLn * 0.5)
    $passE5 = $dLgb -lt ($dLn * 0.4) -and $dRgb -gt ($dRn * 0.5)
    "E1 clear zone: $(if ($pass1) { 'PASS' } else { 'FAIL' })  (right went back to base delta $dRb vs $dRn; left kept haze $dLb vs $dLn)"
    "E5 glow:       $(if ($passE5) { 'PASS' } else { 'FAIL' })  (black half lost its haze: $dLgb vs $dLn; white half kept it: $dRgb vs $dRn)"
    if (-not $pass1) { throw 'clear zone did not render - per-pixel alpha not honoured' }
}
finally {
    if ($test -and -not $test.HasExited) { Stop-Process -Id $test.Id -Force -ErrorAction SilentlyContinue }
    if ($livePath) {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $livePath; $info.Arguments = '--artwork'; $info.UseShellExecute = $false
        [Diagnostics.Process]::Start($info) | Out-Null
        Start-Sleep -Seconds 3
        "live helper back: $(@((Get-HelperProcesses Devin) | ForEach-Object { $_.Id }) -join ', ')"
    }
}
