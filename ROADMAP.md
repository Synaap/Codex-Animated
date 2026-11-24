# Roadmap

What is planned for Animated Launchers. There are no dates. This is a plan, not a promise: the order can change, and items marked "experiment" ship only if a trial shows they work well.

## Finished, coming in 3.0.0

Updates 1 to 18 are done on `main` and ship together in the next release. [CHANGELOG.md](CHANGELOG.md) lists them in release-note form.

### 1. Safety net
- A small log file for each helper, and a helper that restarts itself after a crash
- The uninstaller stays on the PC and the project appears in Windows "Installed apps"
- No startup animation while a full-screen game or a presentation is in front
- Test and release scripts in the repository

### 2. Themes to go
- Export a theme as one file and import it again, by button or by dropping it on Theme Studio
- Back up and restore the whole theme library
- Duplicate a theme, or drop any image on Theme Studio to make one
- A credit line per theme for the artist or source

### 3. One helper
- One helper program and one notification-area icon for all apps, instead of one per app
- Apps described in one table, so adding an app no longer needs a new program
- Existing shortcuts, pins, settings and themes keep working
- Only one startup animation at a time

### 4. Editors
- VS Code, Cursor and Kiro
- An apps page in Theme Studio: every known app, whether it was found, and a switch for its wallpaper
- A table of supported apps in the README: tested, reported working, untested

### 5. Sharper looks
- Clear zone: fade the artwork out where the text is, so the picture stays sharp
- Saturation and vignette controls
- Fit modes (fill, fit, center) and crisp scaling for pixel art
- Looks for light-colored apps
- Your own presets

### 6. Animation styles
- A short style: a title card of about three seconds
- A calm version when Windows asks for reduced motion
- A third speed, and click anywhere to skip
- Play only on the first start after signing in (option)

### 7. Chat and notes apps
- Obsidian
- Add your own app: pick a program and Theme Studio checks whether its window can take a wallpaper, including apps with frameless windows

### 8. Faster theme making
- Crop the avatar from the wallpaper by dragging a square
- Use the desktop wallpaper, paste an image from the clipboard, add a whole folder of images
- Gradient themes that need no image
- Double-click a theme file to import it into an already open Theme Studio

### 9. Theme Studio comfort
- Undo, redo and a reset back to how the theme was when you opened it
- Double-click a slider to reset it; click its number to type an exact value
- Hold to compare with the untouched picture
- Keyboard shortcuts and a built-in list of them
- Search, sorting and favorites in the theme list, remembered between sessions

### 10. Status and updates
- Per-app status on the apps page — wallpaper attached, watching, off or parked, plus the last problem
- A hint when a wallpaper stays bright where app text sits
- A Settings page: version, "copy report" for bug reports, open the logs folder, restart helpers
- "Check for updates" that contacts GitHub only when clicked, with one-click download and install

### 11. Variety
- Rotate themes on app start or on a timer, never while the app is in the foreground
- Rotation through starred themes only, or the whole library
- Win+Alt+H hides or shows the artwork in every app at once
- More/less visible from the tray; Next theme from the tray
- Follow Windows light and dark mode with a second theme pick per app

### 12. Day and night
- A night theme per app with its own hours, next to the light and dark picks
- Night dim and a per-app extra dim that darken the artwork without touching the theme
- Artwork hides itself while the app is full screen or Windows reports presentation mode

### 13. Every window
- The wallpaper appears on every window an app opens, not only the focused one — up to eight windows per app
- Windows opened later are picked up automatically, and closing one keeps the rest
- Full-screen hiding is per window now: only the full-screen window loses its artwork

### 14. Animation details
- Six extra title fonts from fonts Windows already has (Segoe UI, console, serif, gothic, typewriter, Impact)
- Four new avatar frame shapes: rounded, circle, diamond, hexagon
- Boot lines can use variables — {app}, {theme}, {user}, {date}, {time}, {starts} — and a random line from your own list shows each play

### 15. Animation skins
- Three more animation looks join Full and the short Card: **Terminal** (typed boot lines, ASCII progress bar), **Manga** (tilted panel slam with speed lines and halftone) and **Minimal** (quiet artwork, hairline progress, one line of type)
- A theme picks its skin in Theme Studio; apps can keep a skin as their own default too

### 16. Starter themes
- Six ready-made themes painted in code — no stock photos, no licenses to track: Aurora fields, Paper dusk, Night garden, Sakura rain, plus the Deep sea and Sunset gradients
- An empty library shows a welcome card with previews and one click adds them all

### 17. Launch prep
- `Setup.exe` joins the zip — a single-file installer you just double-click (the plain scripts still work too)
- Screenshots in the README, a build workflow ready for GitHub Actions, and winget manifest drafts ready for submission
- Still parked: signing the programs (needs a certificate) and turning the pipeline on

### 18. Translations
- Theme Studio speaks French — every label, button, status line and hint, with English as the automatic fallback
- A Language select in Settings switches instantly (Same as Windows / English / Français); more languages slot into the same table

## Shipped since 3.0.0

### 3.1.0 — Animated wallpapers and drift
- Animated GIF wallpapers on the opaque present path with a timeline pump and persistent-DIB fast path
- Eased focus drift (`drift`), AI / Work in progress app chip tabs, and a master startup-animation toggle

### 3.2.0 — Pacing, slideshows, APNG, presets and chime
- Per-app wallpaper speed, FPS cap, freeze/pause (tray and Studio) and battery-saver pause
- **Win+Alt+T** global next-theme hotkey
- Slideshow themes (`slides\` folder of stills) and APNG (`wallpaper.apng`) animated wallpaper support
- Optional startup chime (`sound.wav`) per theme
- Animated GIF/APNG preview in Theme Studio, one-click app presets, and click-to-remove theme chips

### 3.3.0 — Mouse pointers, wallpaper controls and a bug audit
- A **Mouse pointers** tab in Theme Studio: pointer packs (drawn shapes and image packs) with live previews,
  upload-your-own images, one-click apply as the real Windows cursor scheme, and **Windows default** restore
- A drawn reticle that follows the cursor inside each app's window, per app, in the theme accent or a fixed colour
- **Win+Alt+P** pause/resume for every animated wallpaper, **Win+Alt+Up/Down** visibility nudges,
  and pause-while-unfocused per app
- Slideshow crossfades, an in-Studio slideshow editor, and a startup-chime editor with three built-in chimes
- Animated GIF/APNG/slideshow wallpapers under every look (clear zones, glow, vignette, transparent fits)
- Audit fixes: settings saves no longer flip a custom app's animation on, theme edits reach running apps,
  and archive/APNG parsing is hardened; uninstall can restore the previous cursor scheme

## Next

- **Theme gallery** — a curated community index browsable inside Theme Studio
- **Pointer packs to go** — import a downloaded `.zip`/folder of `.cur`/`.ani`/XCursor files as a pack,
  fill a pack's missing roles from a base pack, pack search, rename and share a pack
- **Bundled starter packs** — a small set of properly-licensed pointer packs shipped with the installer
- **Video wallpapers (`.mp4`)** — Media Foundation decoding into the same frame pump
- **Discord** (under review — its terms may not allow it)
- **Reach:** signed programs, winget submission, more languages on the same table

## Experiments

Small trials that decide whether an idea is worth building:

- Apps built on other technology — Chromium browsers, Spotify, Telegram and Tauri apps tested and attachable via custom apps; Steam/OBS UI helper processes stay out of reach
- Moving wallpapers (GIF, APNG and slideshows shipped; short video under consideration)
- A slight shift of the artwork with the mouse (shipped as eased focus drift)
- The animation covering the app window exactly

## Not planned

- Games, game launchers and anything with anti-cheat
- Changing the supported apps' own files, or running code inside them
- Telemetry or accounts
- Shipping or hosting artwork without the right to share it
- macOS and Linux

## Suggest something

Open an issue and describe what you would like and which app you use it with.
