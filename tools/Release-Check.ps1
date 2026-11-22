# The one-command release check: offline tests, a clean build, the release zip, an
# extract-and-compare, a Windows Defender scan, and the install from the extracted zip.
# Nothing is committed or uploaded; that is a separate, manual step.
#
#   tools\Release-Check.ps1 -Version 2.1.1 [-SkipInstall]
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$SkipInstall
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $PSScriptRoot 'out\release-check'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$script:pass = 0; $script:fail = 0; $script:skipped = 0
function Check([bool]$ok, [string]$name, [string]$detail) {
    if ($ok) { $script:pass++; Write-Output "PASS $name  [$detail]" }
    else { $script:fail++; Write-Output "FAIL $name  [$detail]" }
}
function NotChecked([string]$name, [string]$why) {
    $script:skipped++; Write-Output "NOT CHECKED $name ($why)"
}
function Step([string]$name) { Write-Output "--- $name" }

function Invoke-Tool([string]$file, [string[]]$toolArgs) {
    $out = Join-Path $work ((Split-Path -Leaf $file) + '.out.txt')
    & powershell -NoProfile -ExecutionPolicy Bypass -File $file @toolArgs *> $out
    return @{ Code = $LASTEXITCODE; Text = [IO.File]::ReadAllText($out) }
}

Step 'offline tests'
$r = Invoke-Tool (Join-Path $root 'tools\Test.ps1') @()
Check (($r.Code -eq 0) -and ($r.Text -match 'ALL \d+ CHECKS PASSED')) 'tools\Test.ps1' $(($r.Text -split "`n" | Where-Object { $_ -match 'PASSED|FAIL' }) -join ' | ')

Step 'clean build'
$buildLog = Join-Path $work 'build.out.txt'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'source\Build.ps1') *> $buildLog
$buildText = [IO.File]::ReadAllText($buildLog)
Check (($LASTEXITCODE -eq 0) -and ($buildText.Trim().Length -eq 0)) 'source\Build.ps1 with no warnings' "exit $LASTEXITCODE, output $($buildText.Trim().Length) chars"
$exes = 'AnimatedLauncher.exe', 'ThemeStudio.exe'
$missing = @($exes | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
Check ($missing.Count -eq 0) 'both executables were written' $(if ($missing.Count) { "missing: $($missing -join ', ')" } else { '' })
$exeHashes = @{}
foreach ($e in $exes) { $exeHashes[$e] = (Get-FileHash -LiteralPath (Join-Path $root $e) -Algorithm SHA256).Hash }

Step 'package'
$r = Invoke-Tool (Join-Path $root 'tools\Package.ps1') @('-Version', $Version, '-NoBuild')
$zip = Get-ChildItem -LiteralPath (Join-Path $root 'dist') -Filter "Codex-Animated-v$Version-windows-x64.zip" -ErrorAction SilentlyContinue | Select-Object -First 1
Check (($r.Code -eq 0) -and $zip) 'the release zip was written' $(if ($zip) { $zip.Name } else { "exit $($r.Code): $(($r.Text -split "`n")[-3..-1] -join ' ')" })

Step 'extract and compare'
if ($zip) {
    $extract = Join-Path $work 'extracted'
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $extract -Force
    $names = @()
    foreach ($entry in [IO.Compression.ZipFile]::OpenRead($zip.FullName).Entries) { $names += $entry.FullName }
    Write-Output "entries: $($names -join ', ')"
    foreach ($e in $exes) {
        $inZip = Join-Path $extract $e
        if (-not (Test-Path -LiteralPath $inZip)) { Check $false "zip entry $e" 'missing' ; continue }
        Check (((Get-FileHash -LiteralPath $inZip -Algorithm SHA256).Hash) -eq $exeHashes[$e]) "zip entry $e matches the tested build" ''
    }
    $shaFile = $zip.FullName + '.sha256'
    if (Test-Path -LiteralPath $shaFile) {
        $expected = ([IO.File]::ReadAllText($shaFile).Trim() -split '\s')[0]
        Check ($expected -eq (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash) 'the .sha256 file matches the zip' $expected.Substring(0, 16)
    }
    else { Check $false 'the .sha256 file exists' 'missing' }
}
else { NotChecked 'extract and compare' 'no zip' }

Step 'Windows Defender scan'
$scan = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
$defender = Get-Service -Name WinDefend -ErrorAction SilentlyContinue
if ($zip -and (Test-Path -LiteralPath $scan) -and $defender -and $defender.Status -eq 'Running') {
    & $scan -Scan -ScanType 3 -File $zip.FullName *> (Join-Path $work 'defender.out.txt')
    Check ($LASTEXITCODE -eq 0) 'Defender found nothing in the zip' "exit $LASTEXITCODE"
}
else { NotChecked 'Windows Defender scan' 'Windows Defender is not running on this PC' }

Step 'install from the extracted zip'
if ($zip -and -not $SkipInstall) {
    $installer = Join-Path (Join-Path $work 'extracted') 'Install.ps1'
    $r = Invoke-Tool $installer @('-NoBuild')
    Check ($r.Code -eq 0) 'Install.ps1 -NoBuild from the extracted zip' $(($r.Text -split "`n" | Select-Object -Last 4) -join ' | ')
    $helpers = @(Get-HelperProcesses | Where-Object { $_.Path -like '*AnimatedLaunchers*' })
    Check ($helpers.Count -ge 1) 'the shared helper is running from the install folder' "$($helpers.Count) helper process(es)"
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers'
    $copies = 'DevinAnimated.exe', 'AntigravityAnimated.exe', 'CodexAnimated.exe', 'ClaudeAnimated.exe',
        'VSCodeAnimated.exe', 'CursorAnimated.exe', 'KiroAnimated.exe', 'WindsurfAnimated.exe',
        'VSCodiumAnimated.exe', 'ObsidianAnimated.exe' |
        Where-Object { Test-Path -LiteralPath (Join-Path $installDir $_) }
    Check ($copies.Count -eq 10) 'the ten launcher copies were installed' "$($copies.Count) of 10"
    $runValue = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AnimatedLaunchers' -ErrorAction SilentlyContinue
    Check ($null -ne $runValue) 'one shared start-with-Windows entry' $(if ($runValue) { $runValue.AnimatedLaunchers } else { 'absent' })
    $legacy = 'DevinAnimated', 'AntigravityAnimated', 'CodexAnimated', 'ClaudeAnimated' |
        Where-Object { $null -ne (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $_ -ErrorAction SilentlyContinue) }
    Check ($legacy.Count -eq 0) 'no per-app start-with-Windows entries are left' $(if ($legacy) { $legacy -join ', ' } else { '' })
}
elseif ($SkipInstall) { NotChecked 'install from the extracted zip' '-SkipInstall' }

Write-Output "== $script:pass passed, $script:fail failed, $script:skipped not checked"
if ($script:fail -gt 0) { exit 1 }
exit 0
