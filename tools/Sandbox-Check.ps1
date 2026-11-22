# Installs the release zip inside Windows Sandbox and collects a report.
# Windows Sandbox is a clean Windows that forgets everything when it closes, so the real
# install and uninstall can be tried without touching this PC. It must be turned on once:
#   Windows Features > "Windows Sandbox" (needs a restart).
#
#   tools\Sandbox-Check.ps1 [-Zip <path>] [-Out <folder>]
[CmdletBinding()]
param(
    [string]$Zip,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Toolkit.ps1')
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'out\sandbox' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path

# WindowsSandbox.exe only exists once the feature payload is on disk, so the file
# check works without admin. The feature query adds the pending-restart state but
# needs elevation, so it runs best-effort.
$sandboxExe = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
$featureState = $null
try {
    $featureState = (Get-WindowsOptionalFeature -Online -FeatureName 'Containers-DisposableClientVM' -ErrorAction Stop).State
}
catch { }
if (-not (Test-Path -LiteralPath $sandboxExe)) {
    Write-Output 'Windows Sandbox is not on this PC. Turn it on under:'
    Write-Output '  Control Panel > Programs > Turn Windows features on or off > Windows Sandbox'
    Write-Output '(a restart is needed). Then run this script again.'
    exit 4
}
if ($featureState -and $featureState -notlike 'Enabled*') {
    Write-Output "Windows Sandbox is present but its feature state is '$featureState' (a restart may be needed)."
    exit 4
}

if (-not $Zip) {
    $Zip = Get-ChildItem -LiteralPath (Join-Path $root 'dist') -Filter '*.zip' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Zip -or -not (Test-Path -LiteralPath $Zip)) { throw "No release zip found. Package first: tools\Package.ps1 -Version <x.y.z>" }
$Zip = (Resolve-Path -LiteralPath $Zip).Path

# What the clean Windows runs at sign-in.
$guest = Join-Path $Out 'Sandbox-Install.ps1'
@'
$ErrorActionPreference = 'Continue'
$report = 'C:\SandboxOut\report.txt'
"=== Animated Launchers sandbox report ===" | Out-File $report -Encoding utf8
"date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Out-File $report -Append
"windows: $([Environment]::OSVersion.Version)" | Out-File $report -Append
try {
    $zip = Get-ChildItem 'C:\SandboxZip\*.zip' | Select-Object -First 1
    "zip: $($zip.Name)" | Out-File $report -Append
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath 'C:\Temp\pkg' -Force
    'extracted' | Out-File $report -Append
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\Temp\pkg\Install.ps1' -NoBuild *> 'C:\SandboxOut\install.out.txt'
    "install exit: $LASTEXITCODE" | Out-File $report -Append
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers'
    "install folder exists: $(Test-Path $dest)" | Out-File $report -Append
    Get-ChildItem $dest -ErrorAction SilentlyContinue | ForEach-Object { "  $($_.Name)" } | Out-File $report -Append
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Get-ItemProperty $run | Get-Member -MemberType NoteProperty | Where-Object { $_.Name -like '*Animated*' } |
        ForEach-Object { "run value: $($_.Name)" } | Out-File $report -Append
    "installed apps entry: $(Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AnimatedLaunchers')" | Out-File $report -Append
    Get-Process | Where-Object { $_.Name -like '*Animated*' } | ForEach-Object { "helper running: $($_.Name) $($_.Id)" } | Out-File $report -Append
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\Temp\pkg\Uninstall.ps1' -WhatIf *> 'C:\SandboxOut\uninstall-whatif.out.txt'
    'uninstall -WhatIf ran' | Out-File $report -Append
}
catch { "ERROR: $_" | Out-File $report -Append }
'done' | Out-File $report -Append
'@ | Set-Content -LiteralPath $guest -Encoding ASCII

# The sandbox configuration: the zip read-only, the output folder writable.
$zipDir = Split-Path -Parent $Zip
$wsb = Join-Path $Out 'AnimatedLaunchers.wsb'
@"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$zipDir</HostFolder>
      <SandboxFolder>C:\SandboxZip</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$Out</HostFolder>
      <SandboxFolder>C:\SandboxOut</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\SandboxOut\Sandbox-Install.ps1</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $wsb -Encoding ASCII

Write-Output "sandbox config: $wsb"
Write-Output "guest script:   $guest"
Write-Output "starting Windows Sandbox (the report lands in $Out\report.txt)..."
[Diagnostics.Process]::Start($sandboxExe, '"' + $wsb + '"') | Out-Null
