# Captures the open windows of one app (or all of them) to PNG files and lists the artwork
# child of each. Read-only: nothing is shown, focused or moved.
#
#   tools\Capture.ps1 [-App Claude] [-Out <folder>]
[CmdletBinding()]
param(
    [ValidateSet('Devin', 'Antigravity', 'Codex', 'Claude', 'VS Code', 'Cursor', 'Kiro', 'Windsurf', 'VSCodium')]
    [string]$App,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'out\captures' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path

$apps = if ($App) { @($App) } else { $AnimatedApps | ForEach-Object { $_.Name } }
$found = 0
foreach ($name in $apps) {
    $windows = @(Get-AppWindows $name)
    if ($windows.Count -eq 0) { Write-Output "${name}: no open window" ; continue }
    foreach ($w in $windows) {
        $found++
        $file = Join-Path $Out ("{0}-{1}.png" -f $name, $w.Handle)
        try {
            [TK]::Capture($w.Handle, $file)
            $child = Get-ArtworkChild $w $name
            $note = if ($child) { "artwork child owned by pid $($child.OwnerPid)" } else { 'no artwork child' }
            Write-Output ("{0}: {1} ({2}, {3}) -> {4}" -f $name, $w.Rect, $w.Class, $note, $file)
        }
        catch { Write-Output "${name}: capture of window $($w.Handle) failed: $($_.Exception.Message)" }
    }
}
Write-Output "== $found window(s) captured to $Out"
exit 0
