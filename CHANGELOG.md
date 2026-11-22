# Changelog

All notable changes to Animated Launchers (released as Codex Animated). Versions follow
[semantic versioning](https://semver.org/). Download releases from the
[releases page](https://github.com/Synaap/Codex-Animated/releases).

## 3.0.0 — unreleased

Everything below is on `main` and ships together in 3.0.0.

### Apps
- **Ten built-in apps.** Devin, Antigravity IDE, Codex and Claude are joined by VS Code, Cursor, Kiro,
  Windsurf, VSCodium and Obsidian. Windsurf and VSCodium are detected but untested.
- **Add your own app.** Pick any program's `.exe` in Theme Studio; it gets the same wallpaper,
  animation and settings, frameless windows included. The helper picks it up without a restart.
- **One helper for everything.** A single helper process and one notification-area icon, with a
  submenu per app, replace one helper per app. Shortcuts, pins, settings and themes carry over.
- **Every window.** The wallpaper follows every window an app opens (up to eight), and closing one
  window keeps the others.

### Themes and Theme Studio
- **Share themes.** Export a theme as one `.altheme` file and import it by button, by dropping it on
  Theme Studio or by double-clicking it. Back up and restore the whole library (`.albackup`).
- **Faster theme making.** Use the desktop wallpaper, paste an image, add a folder of images, make a
  gradient theme that needs no image, duplicate a theme, and crop the avatar by dragging a square.
- **Six starter themes.** Aurora fields, Paper dusk, Night garden, Sakura rain, Deep sea and Sunset,
  painted in code so there is nothing to license. An empty library offers them on a welcome card.
- **Sharper looks.** Clear zone (keep text readable on one side or the middle), saturation, vignette,
  glow, fade for light-colored apps, fit modes including crisp pixel art, and your own presets.
- **Comfort.** Undo, redo and reset; double-click a slider to reset it or click its number to type a
  value; hold to compare with the untouched picture; keyboard shortcuts; search, sorting and favorites.
- **Status and updates.** Per-app status (attached, watching, off, parked and the last problem), a
  Settings page with a copyable report, the logs folder and a helper restart, and an opt-in update
  check that can download and install a release in one click.
- **French.** Theme Studio speaks French; Settings has a language picker (Same as Windows, English,
  Français), and untranslated text falls back to English.

### Startup animation
- **Five styles.** The original Full sequence, a short Card, **Terminal**, **Manga** and **Minimal**.
- **Details.** Six extra title fonts, five avatar frame shapes, boot-line variables (`{app}`, `{theme}`,
  `{user}`, `{date}`, `{time}`, `{starts}`) and one random line per play from your own list.
- **Gentler by default.** A calm card when Windows asks for reduced motion, a third speed, click
  anywhere to skip, and an option to play only on the first start after signing in. Only one
  animation plays at a time, never while a full-screen game or a presentation is in front.

### Variety and day/night
- Rotate themes on app start or on a timer (never while you use the app), optionally through your
  starred themes only. **Next theme**, **More/Less visible** and **Win+Alt+H** (hide or show all
  artwork) from the tray.
- Follow Windows light and dark mode with a second theme pick per app, and a night theme with its
  own hours. Night dim and extra dim darken the artwork without changing the theme.
- The artwork hides while an app window is in real full screen or Windows reports presentation mode.

### Reliability
- A log file per helper, an automatic restart after a crash, and a session that keeps failing is
  parked on its own while the other apps keep their artwork.
- The uninstaller stays on the PC and the project is listed under Windows **Installed apps**.
- `Setup.exe`, a single-file installer, joins the release zip; the scripts still work too.

### Fixed before release
- A maximized window on a monitor without a taskbar (or with an auto-hiding one) no longer counts as
  full screen, so its artwork stays.
- Renaming or deleting a theme updates the light-mode and night picks too, and a renamed favorite
  keeps its star.
- Editing a theme reaches only the apps that are showing it, through the slot it holds there, so
  editing the base theme at night no longer replaces the night theme.
- The gradient maker's preview matches the saved wallpaper at every angle, and 180° and 270°
  gradients no longer get a thin line of the start colour along one edge.
- The one-click update checks the download against the release's published SHA-256 when there is one.
- The tray marks the theme that is actually showing; new night and light-mode picks apply at once.
- Uninstalling with "remove data" also removes the settings of apps you added yourself.

## 2.1.0 — 2026-11-21
- Claude joins Devin, Antigravity IDE and Codex. The helper matches only the desktop app's own
  window, never the Claude Code command-line tool.

## 2.0.0 — 2026-11-21
- Themes: a wallpaper with its own look and startup-animation text, in one library shared by Devin,
  Antigravity IDE and Codex.
- The animation plays however the app is opened, never when it was already open.
- Per-theme sliders for visibility, blur, dim, tint and tint color, with presets.
- Theme Studio rebuilt: theme list, live preview and inspector; edits reach running apps at once.
- `Install.cmd` installs to `%LOCALAPPDATA%\Programs\AnimatedLaunchers` and sets up each app found.

## 1.2.1 — 2026-11-20
- The 12-second sunset portrait animation, adapted from panding999's Codex Startup Animation, is
  built into the launcher, with a matching bundled wallpaper.

## 1.2.0 — 2026-11-19
- The kinetic startup is back and the wallpaper stays attached to the Codex window.

## 1.1.0 — 2026-11-19
- Choose your own wallpaper from the notification-area icon; it applies at once and survives restarts.

## 1.0.0 — 2026-11-19
- First Windows x64 release: an aurora startup animation and a matching wallpaper for the Codex
  desktop app.
