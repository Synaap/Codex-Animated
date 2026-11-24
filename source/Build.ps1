$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location -LiteralPath $package
try {
    $assets = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll',
        'web/index.html', 'web/style.css', 'web/animation.js', 'web/assets/artwork.jpg', 'web/assets/avatar.jpg',
        'web/assets/contours.js', 'notices/LICENSE.txt', 'notices/NOTICE.txt')
    $resources = foreach ($asset in $assets) {
        '/resource:{0},bundle/{0}' -f $asset
    }
    $sources = '.\source\PortableStartup.cs', '.\source\ArtworkUpgrade.cs', '.\source\Launcher.cs',
        '.\source\AppSession.cs', '.\source\HelperHost.cs', '.\source\PreviewArtworkChild.cs',
        '.\source\WallpaperSettings.cs', '.\source\AppTable.cs', '.\source\ThemeSettings.cs',
        '.\source\ThemeLibrary.cs', '.\source\CursorScheme.cs', '.\source\StarterArt.cs',
        '.\source\ArtworkLook.cs', '.\source\ImageCodec.cs',
        '.\source\SplashPage.cs', '.\source\DarkMenu.cs', '.\source\EmbeddedBundle.cs',
        '.\source\AppLog.cs', '.\source\QuietTime.cs', '.\source\CrashGuard.cs'
    $manifestTemplate = Get-Content 'source\app.manifest' -Raw
    $manifest = Join-Path $env:TEMP 'AnimatedLauncher.manifest'
    Set-Content -LiteralPath $manifest -Value $manifestTemplate
    # One binary serves every app: installed copies named <App>Animated.exe are that
    # app's launcher, AnimatedLauncher.exe --artwork is the shared wallpaper helper.
    & $compiler /nologo /target:winexe /optimize+ /platform:x64 /out:AnimatedLauncher.exe /win32icon:source\launcher.ico /win32manifest:$manifest /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll @resources $sources
    if ($LASTEXITCODE -ne 0) { throw "The AnimatedLauncher.exe build failed with exit code $LASTEXITCODE." }
    Remove-Item -LiteralPath $manifest -ErrorAction SilentlyContinue
    $studioManifest = Join-Path $env:TEMP 'ThemeStudio.manifest'
    Set-Content -LiteralPath $studioManifest -Value ($manifestTemplate -replace 'name="AnimatedLauncher"', 'name="ThemeStudio"')
    $studioFiles = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll',
        'notices/LICENSE.txt', 'notices/NOTICE.txt')
    $studioResources = @(foreach ($file in $studioFiles) { '/resource:{0},bundle/{0}' -f $file })
    $studioResources += @('studio/index.html', 'studio/studio.css', 'studio/studio.js', 'studio/strings.js',
        'web/index.html', 'web/style.css', 'web/animation.js', 'web/assets/contours.js') | ForEach-Object { '/resource:{0},{0}' -f $_ }
    $studioSources = '.\source\ThemeStudio.cs', '.\source\ThemeStudio.Commands.cs', '.\source\AppTable.cs', '.\source\ThemeSettings.cs',
        '.\source\ThemeLibrary.cs', '.\source\CursorScheme.cs', '.\source\StarterArt.cs', '.\source\ThemePack.cs', '.\source\ArtworkLook.cs', '.\source\ImageCodec.cs',
        '.\source\SplashPage.cs', '.\source\EmbeddedBundle.cs'
    & $compiler /nologo /target:winexe /optimize+ /platform:x64 /out:ThemeStudio.exe /win32icon:source\launcher.ico /win32manifest:$studioManifest /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll @studioResources @studioSources
    if ($LASTEXITCODE -ne 0) { throw "The ThemeStudio.exe build failed with exit code $LASTEXITCODE." }
    Remove-Item -LiteralPath $studioManifest -ErrorAction SilentlyContinue
} finally {
    Pop-Location
}
