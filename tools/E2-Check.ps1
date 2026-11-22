# E2: does the wallpaper attach to apps on other windowing tech? Launches each
# app, moves its window to the secondary monitor, stages it as a custom app with
# a solid magenta wallpaper, then measures the window's mean RGB before/after.
# Read-only on the target (no focus, no input). Restores custom-apps.ini and
# removes staged folders in finally. Run: powershell -File tools\E2-Check.ps1
[CmdletBinding()] param([string[]]$Only = @())
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class E2Win {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
}
"@

$la = $env:LOCALAPPDATA
$ini = Join-Path $la 'Animated Launchers\custom-apps.ini'
$backup = if (Test-Path $ini) { [IO.File]::ReadAllBytes($ini) } else { $null }
$staged = @()
$results = @()
$mon = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $mon) { $mon = [System.Windows.Forms.Screen]::AllScreens[0] }
$mx = $mon.WorkingArea.X + 80; $my = $mon.WorkingArea.Y + 60
Write-Host "Monitor: $($mon.DeviceName) at $mx,$my"

$targets = @(
  @{ Id='e2chrome';   Name='E2Chrome';   Exe='C:\Program Files\Google\Chrome\Application\chrome.exe'; Proc='chrome' },
  @{ Id='e2spotify';  Name='E2Spotify';  Exe="$env:APPDATA\Spotify\Spotify.exe";                      Proc='Spotify' },
  @{ Id='e2telegram'; Name='E2Telegram'; Exe="$env:APPDATA\Telegram Desktop\Telegram.exe";            Proc='Telegram' },
  @{ Id='e2obs';      Name='E2OBS';      Exe='C:\Program Files\obs-studio\bin\64bit\obs64.exe';       Proc='obs64' },
  @{ Id='e2steam';    Name='E2Steam';    Exe='C:\Program Files (x86)\Steam\steam.exe';                Proc='steam' },
  @{ Id='e2modrinth'; Name='E2Modrinth'; Exe='';                                                     Proc='Modrinth App' }
)
$lnk = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Modrinth App.lnk"
if (Test-Path $lnk) { $targets[5].Exe = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath }
$targets = $targets | Where-Object { $_.Exe -and (Test-Path $_.Exe) }
if ($Only.Count) { $targets = $targets | Where-Object { $Only -contains $_.Name -or $Only -contains $_.Proc } }
$all = @($targets | Where-Object { $_.Exe }); $skipped = $targets | Where-Object { -not $_.Exe -or -not (Test-Path $_.Exe) }
Write-Host "Targets: $($targets.Count)$(if($skipped){'; skipped: ' + (($skipped | ForEach-Object { $_.Name + '=' + $_.Exe }) -join ', ')})"

function Stage-App($t) {
  $dir = Join-Path $la "$($t.Name) Animated"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $staged += $dir
  $img = New-Object Drawing.Bitmap 320,200
  $g = [Drawing.Graphics]::FromImage($img); $g.Clear([Drawing.Color]::FromArgb(255,0,255)); $g.Dispose()
  $img.Save((Join-Path $dir 'wallpaper.jpg'), [Drawing.Imaging.ImageFormat]::Jpeg); $img.Dispose()
  [IO.File]::WriteAllText((Join-Path $dir 'settings.ini'),
    "watch=on`r`nopacity=40`r`nblur=0`r`ndim=0`r`ntint=100`r`ntintColor=#FF00FF`r`nfocusX=50`r`nfocusY=50`r`nclear=0`r`nclearSide=`r`nglow=0`r`nvignette=0`r`nsat=100`r`nfade=0`r`nfit=cover`r`ndrift=0`r`n", [Text.Encoding]::UTF8)
}

function Snap($hwnd, $path) {
  $w = [TK]::TopLevel() | Where-Object { $_.Handle -eq $hwnd } | Select-Object -First 1
  $r = $w.Rect -split ','
  $wpx = [int]$r[2] - [int]$r[0]; $hpx = [int]$r[3] - [int]$r[1]
  if ($wpx -lt 200 -or $hpx -lt 200) { throw "window too small to sample (${wpx}x${hpx})" }
  [TK]::Screen([int]$r[0] + 50, [int]$r[1] + 50, [Math]::Min(700, $wpx - 100), [Math]::Min(500, $hpx - 100), $path)
}
function Mean($path) {
  $b = New-Object Drawing.Bitmap $path
  $s = [TK]::MeanRgb($path, 20, 20, [Math]::Max(20, $b.Width - 40), [Math]::Max(20, $b.Height - 40))
  $b.Dispose()
  if ($s -match '([\d.]+),([\d.]+),([\d.]+)') {
    return @{ R=[double]$Matches[1]; G=[double]$Matches[2]; B=[double]$Matches[3] } }
  return @{ R=0; G=0; B=0 }
}

try {
  foreach ($t in $targets) {
    $verdict = 'NO-WINDOW'; $note = ''; $cls = ''
    $base = $null; $after = $null; $childFound = $false
    try {
      $before = @{}; [TK]::TopLevel() | Where-Object { $_.Pid -gt 0 } | ForEach-Object { $before[$_.Handle] = $true }
      $wasRunning = @(Get-Process -Name $t.Proc -ErrorAction SilentlyContinue).Count -gt 0
      if (-not $wasRunning) { explorer.exe "`"$($t.Exe)`"" | Out-Null }
      $win = $null
      for ($i=0; $i -lt 60 -and -not $win; $i++) {
        Start-Sleep -Seconds 1
        $pids = @{}; Get-Process -Name $t.Proc -ErrorAction SilentlyContinue | ForEach-Object { $pids[[uint32]$_.Id]=$true }
        $cand = [TK]::TopLevel() | Where-Object { $_.Visible -and -not $_.Minimized -and $_.Owner -eq 0 -and $_.Title.Length -gt 0 -and $pids.ContainsKey($_.Pid) -and $_.Class -notmatch 'Pbuffer|Intermediate' }
        $win = $cand | Sort-Object { [long]($_.Rect -split ',')[2] - [long]($_.Rect -split ',')[0] } -Descending | Select-Object -First 1
      }
      if (-not $win) {
        $cand = [TK]::TopLevel() | Where-Object { $_.Owner -eq 0 -and $_.Title.Length -gt 0 -and $pids.ContainsKey($_.Pid) }
        $win = $cand | Sort-Object { $_.Title.Length } -Descending | Select-Object -First 1
      }
      if (-not $win) { throw "no window after 60 s" }
      $cls = $win.Class
      [E2Win]::ShowWindow([IntPtr]$win.Handle, 9) | Out-Null
      [E2Win]::SetWindowPos([IntPtr]$win.Handle, [IntPtr]::Zero, $mx, $my, 1100, 750, 0x0040) | Out-Null
      Start-Sleep -Seconds 3
      $p1 = Join-Path $env:TEMP "e2-$($t.Id)-before.png"; Snap $win.Handle $p1; $base = Mean $p1
      # stage as custom app
      $rows = @(); if (Test-Path $ini) { $rows = [IO.File]::ReadAllLines($ini) }
      [IO.File]::WriteAllLines($ini, @($rows + "$($t.Id)=$($t.Name)|$($t.Exe)|editor"), [Text.Encoding]::UTF8)
      Stage-App $t
      for ($i=0; $i -lt 30 -and -not $childFound; $i++) {
        Start-Sleep -Seconds 1
        $c = [TK]::Children($win.Handle) | Where-Object { $_.Title -eq "$($t.Name) Animated artwork preview" }
        if ($c) { $childFound = $true; break }
        $ov = [TK]::TopLevel() | Where-Object { $_.Title -eq "$($t.Name) Animated artwork preview" -and $_.Visible }
        if ($ov) { $childFound = $true; $note = 'overlay'; break }
      }
      Start-Sleep -Seconds 2
      $p2 = Join-Path $env:TEMP "e2-$($t.Id)-after.png"; Snap $win.Handle $p2; $after = Mean $p2
      $dR = $after.R - $base.R; $dG = $after.G - $base.G; $dB = $after.B - $base.B
      if ($dR -gt 8 -and $dB -gt 8 -and $dG -lt -3) { $verdict = 'ATTACHED' }
      elseif ($childFound) { $verdict = 'WINDOW-NO-PAINT' }
      else { $verdict = 'NO-ATTACH' }
      $note = ($note + " class=$cls d=($dR,$dG,$dB) " + $(if($childFound){'artwork-window=yes'}else{'artwork-window=no'})).Trim()
      [E2Win]::ShowWindow([IntPtr]$win.Handle, 6) | Out-Null # minimize
      if (-not $wasRunning) { try { (Get-Process -Id $win.Pid).CloseMainWindow() | Out-Null } catch {} }
    } catch { $verdict = 'ERROR'; $note = $_.Exception.Message }
    $results += [pscustomobject]@{ App=$t.Name; Verdict=$verdict; Note=$note }
    Write-Host ("{0,-12} {1,-15} {2}" -f $t.Name, $verdict, $note)
  }
} finally {
  if ($null -ne $backup) { [IO.File]::WriteAllBytes($ini, $backup) } elseif (Test-Path $ini) { Remove-Item $ini -Force }
  foreach ($d in $staged) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
}
$results | Format-Table -AutoSize
