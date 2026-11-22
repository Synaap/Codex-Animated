$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $out 'LookTests.exe'
& $compiler /nologo /target:exe /optimize+ /platform:x64 "/out:$exe" /r:System.Drawing.dll `
    /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    (Join-Path $PSScriptRoot 'LookTests.cs') `
    (Join-Path $package 'source\AppTable.cs') `
    (Join-Path $package 'source\ThemeSettings.cs') `
    (Join-Path $package 'source\ThemeLibrary.cs') `
    (Join-Path $package 'source\StarterArt.cs') `
    (Join-Path $package 'source\ThemePack.cs') `
    (Join-Path $package 'source\ArtworkLook.cs') `
    (Join-Path $package 'source\ImageCodec.cs') `
    (Join-Path $package 'source\SplashPage.cs') `
    (Join-Path $package 'source\AppLog.cs') `
    (Join-Path $package 'source\QuietTime.cs')
if ($LASTEXITCODE -ne 0) { throw "LookTests build failed with exit code $LASTEXITCODE." }
& $exe @args
exit $LASTEXITCODE
