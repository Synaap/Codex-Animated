# Shared helpers for the test tools. Dot-source this file. Nothing here takes focus or sends input.
if (-not ('TK' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Toolkit.cs')))
}

# How to tell whether a process belongs to each app.
$AnimatedApps = @(
    @{ Name = 'Devin'; Product = 'Devin Animated'; Helper = 'DevinAnimated'; ExeNames = 'Devin.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'Devin.exe' -and (Split-Path -Leaf (Split-Path -Parent $path)) -ieq 'Devin' } },
    @{ Name = 'Antigravity'; Product = 'Antigravity Animated'; Helper = 'AntigravityAnimated'; ExeNames = 'Antigravity IDE.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'Antigravity IDE.exe' } },
    @{ Name = 'Codex'; Product = 'Codex Animated'; Helper = 'CodexAnimated'; ExeNames = 'ChatGPT.exe', 'Codex.exe'
       Owns = { param($path) $path -and ((Split-Path -Leaf $path) -in 'ChatGPT.exe', 'Codex.exe') -and $path -like '*OpenAI.Codex_*' } },
    @{ Name = 'Claude'; Product = 'Claude Animated'; Helper = 'ClaudeAnimated'; ExeNames = 'claude.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'claude.exe' -and $path.StartsWith((Join-Path $env:LOCALAPPDATA 'AnthropicClaude') + '\', [StringComparison]::OrdinalIgnoreCase) } },
    @{ Name = 'VS Code'; Product = 'VS Code Animated'; Helper = 'VSCodeAnimated'; ExeNames = 'Code.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'Code.exe' -and (Split-Path -Leaf (Split-Path -Parent $path)) -ieq 'Microsoft VS Code' } },
    @{ Name = 'Cursor'; Product = 'Cursor Animated'; Helper = 'CursorAnimated'; ExeNames = 'Cursor.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'Cursor.exe' -and (Split-Path -Leaf (Split-Path -Parent $path)) -ieq 'cursor' } },
    @{ Name = 'Kiro'; Product = 'Kiro Animated'; Helper = 'KiroAnimated'; ExeNames = 'Kiro.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'Kiro.exe' -and (Split-Path -Leaf (Split-Path -Parent $path)) -ieq 'Kiro' } },
    @{ Name = 'Windsurf'; Product = 'Windsurf Animated'; Helper = 'WindsurfAnimated'; ExeNames = 'Windsurf.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'Windsurf.exe' -and (Split-Path -Leaf (Split-Path -Parent $path)) -ieq 'Windsurf' } },
    @{ Name = 'VSCodium'; Product = 'VSCodium Animated'; Helper = 'VSCodiumAnimated'; ExeNames = 'VSCodium.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'VSCodium.exe' -and (Split-Path -Leaf (Split-Path -Parent $path)) -ieq 'VSCodium' } },
    @{ Name = 'Obsidian'; Product = 'Obsidian Animated'; Helper = 'ObsidianAnimated'; ExeNames = 'Obsidian.exe'
       Owns = { param($path) $path -and (Split-Path -Leaf $path) -ieq 'Obsidian.exe' -and (Split-Path -Leaf (Split-Path -Parent $path)) -ieq 'Obsidian' } }
)

function Get-AnimatedApp([string]$Name) {
    $app = $AnimatedApps | Where-Object { $_.Name -eq $Name }
    if (-not $app) { throw "Unknown app '$Name'. Use one of: $(($AnimatedApps | ForEach-Object { $_.Name }) -join ', ')." }
    return $app
}

# $null, or why a test must not show anything now (same rule as QuietTime, without the app's-own-window exception).
function Get-BusyReason {
    $state = [TK]::UserState()
    if ($state -eq 3) { return 'Windows reports a full-screen game in front' }
    if ($state -eq 4) { return 'Windows reports presentation mode' }
    if ($state -eq 2 -and [TK]::FrontIsFullScreen()) { return 'a full-screen window is in front' }
    return $null
}

function Stop-IfBusy {
    $reason = Get-BusyReason
    if ($reason) { Write-Output "SKIPPED: $reason"; exit 3 }
}

function Get-InstallFolder { Join-Path $env:LOCALAPPDATA 'Programs\AnimatedLaunchers' }

# Id, exe path and command line of the running helpers (of one app, or all). The shared
# helper is AnimatedLauncher.exe; every installed <App>Animated.exe copy is the same program.
# A bare "SELECT *" from Win32_Process intermittently drops rows, so always use a WQL filter.
function Get-HelperProcesses([string]$App) {
    $names = if ($App) { @((Get-AnimatedApp $App).Helper + '.exe') + 'AnimatedLauncher.exe' }
        else { @($AnimatedApps | ForEach-Object { $_.Helper + '.exe' }) + 'AnimatedLauncher.exe' }
    $filter = ($names | ForEach-Object { "Name='$_'" }) -join ' OR '
    Get-CimInstance Win32_Process -Filter $filter |
        ForEach-Object { [pscustomobject]@{ Id = [int]$_.ProcessId; Path = $_.ExecutablePath; CommandLine = $_.CommandLine } }
}

# Visible, not minimized, unowned Chrome_WidgetWin_1 windows of that app's processes.
function Get-AppWindows([string]$App) {
    $info = Get-AnimatedApp $App
    $owns = $info.Owns
    $filter = (@($info.ExeNames) | ForEach-Object { "Name='$_'" }) -join ' OR '
    $pids = @{}
    foreach ($p in Get-CimInstance Win32_Process -Filter $filter) {
        if ($p.ExecutablePath -and @($owns.Invoke($p.ExecutablePath))[0]) { $pids[[uint32]$p.ProcessId] = $true }
    }
    [TK]::TopLevel() | Where-Object { $_.Visible -and -not $_.Minimized -and $_.Owner -eq 0 -and $_.Class -eq 'Chrome_WidgetWin_1' -and $pids.ContainsKey($_.Pid) -and $_.Title.Length -gt 0 }
}

# The artwork child of an app window and the id of the process that owns it, or $null.
function Get-ArtworkChild($Window, [string]$App) {
    $product = (Get-AnimatedApp $App).Product
    $child = [TK]::Children($Window.Handle) | Where-Object { $_.Title -eq "$product artwork preview" } | Select-Object -First 1
    if ($child) { return [pscustomobject]@{ Handle = $child.Handle; OwnerPid = [int]$child.Pid } }
    return $null
}
