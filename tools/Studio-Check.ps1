# Opens the real Theme Studio on a secondary monitor through its eval test hook and
# captures it. Never takes focus and never sends input. Backs up studio.ini byte for
# byte and restores it at the end, whatever happens.
#
#   tools\Studio-Check.ps1 [-From <folder with ThemeStudio.exe>] [-Open "<query>"] [-Out <folder>]
[CmdletBinding()]
param(
    [string]$From,
    [string]$Open,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
Stop-IfBusy
if (-not $From) { $From = Get-InstallFolder }
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'out\studio-check' }
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

$eval = Join-Path $Out 'studio-eval.js'
$evalOut = $eval + '.out'
$png = Join-Path $Out 'studio.png'

function Send-Eval([string]$code, [int]$Seconds = 30) {
    if (Test-Path -LiteralPath $evalOut) { Remove-Item -LiteralPath $evalOut -Force }
    [IO.File]::WriteAllText($eval, $code, (New-Object Text.UTF8Encoding($false)))
    # Only an answer written after this eval file counts.
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

$proc = $null
try {
    New-Item -ItemType Directory -Force -Path $iniDir | Out-Null
    [IO.File]::WriteAllText($ini, "window=$x,$y,1280,800`r`n", (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $evalOut) { Remove-Item -LiteralPath $evalOut -Force }
    [IO.File]::WriteAllText($eval, '// waiting', (New-Object Text.UTF8Encoding($false)))

    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $studio
    if ($Open) { $info.Arguments = '--open "' + $Open + '"' }
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['ANIMATED_STUDIO_EVAL'] = $eval
    $proc = [Diagnostics.Process]::Start($info)

    # data-ready is set after the first full render; poll until then.
    $ready = $false; $r = $null
    for ($i = 0; $i -lt 15 -and -not $ready; $i++) {
        $r = Send-Eval 'return document.documentElement.hasAttribute("data-ready")'
        $ready = ($r -and $r.Contains('"ok":true') -and $r.Contains('"value":true'))
    }
    Check $ready 'the studio page rendered' $(if ($ready) { '' } elseif ($r) { $r } else { 'no eval answer' })

    $r = Send-Eval 'return document.body.innerText.substring(0, 20000)'
    if ($r -and $r.Contains('"ok":true')) {
        $text = $r
        foreach ($app in $AnimatedApps) {
            Check ($text.Contains($app.Name)) "studio lists $($app.Name)" ''
        }
    }
    else { Check $false 'the studio page text could be read' $(if ($r) { $r } else { 'no eval answer' }) }

    $r = Send-Eval ('@capture ' + $png)
    Check (($r -and $r.Contains('"ok":true')) -and (Test-Path -LiteralPath $png)) 'captured the studio window' $png

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
