# How it works

Animated Launchers is three small programs and a folder of settings. This page explains what each
part does — and, just as important, what none of them ever do.

## The parts

| Part | File | What it does |
|---|---|---|
| Launchers | `DevinAnimated.exe`, `CodexAnimated.exe`, … | One copy per app. Starts the app at once and plays the startup animation on top while it loads. |
| Helper | `AnimatedLauncher.exe --artwork` | One background process for every app. Draws the wallpaper inside the app windows and plays the animation when an app starts some other way (taskbar, Start menu, a file). Lives in the notification area. |
| Theme Studio | `ThemeStudio.exe` | The editor for themes, looks, animation text and per-app settings. |

Everything installs per user into `%LOCALAPPDATA%\Programs\AnimatedLaunchers`. No administrator
rights are needed and nothing is installed system-wide.

## The wallpaper inside the app

1. The helper watches for windows of the supported apps. It recognises an app by the program file
   that owns the window (and, for built-in apps, the kind of window), never by its title.
2. When a window appears, the helper creates its **own** child window inside it: a transparent,
   click-through layer that never takes focus. Clicks and typing still go straight to the app.
3. The layer is painted with your wallpaper at the visibility you chose, after the look settings
   (blur, dim, tint, clear zone and so on) have been applied once, ahead of time.
4. When the app window moves, resizes or closes, the layer follows or goes away with it. If Windows
   refuses a child layer, the helper falls back to a separate window that sits exactly over the app.

The layer belongs to the helper's process. Quitting the helper (tray icon → Exit) removes every
layer at once and leaves the apps exactly as they were.

## The startup animation

The animation is a local web page built into the program and shown with Microsoft Edge WebView2.
It plays only when an app is starting (its process is a few seconds old), never when the app was
already open, never while a full-screen game or a presentation is in front, and only one at a time.
Click it, press Esc or choose Skip to close it straight away.

## Where your settings live

- `%LOCALAPPDATA%\Animated Launchers\Themes\` — the theme library (one folder per theme).
- `%LOCALAPPDATA%\<App> Animated\settings.ini` — each app's theme, look and animation choices,
  plus that app's own copy of the wallpaper.
- `%LOCALAPPDATA%\Animated Launchers\Logs\` — the helper's log (at most 256 KB, plus one older
  file). Logs never contain window titles or file contents.

All of these are plain files you can read, back up or delete.

## What it never does

- **It never changes the apps.** No files of Devin, Codex, Claude, VS Code or any other app are
  modified, patched or replaced, and their settings are never touched.
- **It never runs code inside the apps.** Nothing is injected, no add-ons or extensions are
  installed, and no debugging ports are opened. The wallpaper is a separate window from a
  separate process.
- **It never collects data.** There is no telemetry, no analytics and no account. Nothing about
  you, your apps or your files leaves the PC.
- **It stays offline.** The only network requests are the ones you ask for: **Check for updates**
  in Theme Studio asks GitHub for the latest release, and **Download and install** fetches that
  release from GitHub — checking it against the published SHA-256 when the release has one.
- **It never reads what is on screen in the apps.** The layer only draws; it does not capture or
  inspect the app's content.

## Removing it

Run `Uninstall.cmd` from the install folder, or remove **Animated Launchers** under Windows
Settings → Apps → Installed apps. It stops the helper, removes the start-with-Windows entry,
restores your shortcuts and deletes the program folder. Your themes are kept unless you choose to
remove them too.
