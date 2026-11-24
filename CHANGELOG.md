# Changelog

All notable changes to Animated Launchers (released as Codex Animated). Versions follow
[semantic versioning](https://semver.org/). Download releases from the
[releases page](https://github.com/Synaap/Codex-Animated/releases).

## 3.3.0 — 2026-11-23

### Added

- **Slideshow editor in Theme Studio**: a Slideshow section on the theme
  inspector lists a theme's slides as thumbnails with reorder and remove tools,
  and *Add slides…* picks multiple pictures at once (`addSlides`,
  `removeSlide`, `moveSlide`; slides are normalised to numbered JPEGs).
- **Startup-chime editor**: the Startup animation section can pick a `.wav`,
  choose one of three built-in generated chimes (Bell, Blip, Whoosh), play it
  back and remove it (`setChime`, `useChime`, `clearChime`, `playChime`).
- **Animated wallpapers through per-pixel looks**: clear zones, glow, vignette
  and transparent-bar fits no longer force a still — the child renderer folds
  the alpha mask into the persistent frame DIB in place, so GIF, APNG and
  slideshow themes keep moving under every look. Slower than the opaque path
  (one mask pass per frame), so frame pacing still applies.
- The live preview cycles a slideshow theme's slides and shows them as they
  will look in the app.
- **Custom crosshairs**: an optional reticle that follows the cursor inside each
  app's window — Dot, Cross, Circle or Scope, in the theme accent or a fixed
  colour (`crosshair`, `crosshairColor` per app; a 48 px layered child window
  moved by a 30 Hz cursor poll, painted once per style).
  Twenty-four presets — dot, cross, plus, x, circle, ring, scope, chevron,
  diamond, square, brackets, t-shape, triangle, split, star and centre-dot,
  plus a cute set: cat, paw, heart, bow, ghost, bunny, crown and flower — browsable
  in the **Mouse pointers** tab in Theme Studio: pick an app (or all apps)
  and a colour, then click a reticle card to apply it. The cute set is
  cat, paw, heart, bow, ghost, bunny, crown and flower, plus nine kitty-head
  cursor-role pointers (arrow, plus, ring, I-beam, move, down, no-entry,
  help, resize).
- **Cursor packs** — real image pointers. `%LOCALAPPDATA%\Animated Launchers\Cursors\<pack>\<pointer>.png` folders hold PNG packs; the studio's Packs
  section browses them, drills into each pack's pointers, applies one with a
  click (`crosshair=img:<pack>/<file>`), and **Add pointer…**/**New pack…**
  uploads your own images. Ships with a seeded `kitty` pack; the installer
  seeds new packs from a `cursors\` folder next to the package.
- **Slideshow crossfades**: slide changes blend over ~400 ms instead of cutting.
- **Win+Alt+P** pauses or resumes every animated wallpaper at once, and
  **Win+Alt+Up/Down** nudge every artwork's opacity (in addition to the
  existing Win+Alt+H hide/show and Win+Alt+T next theme).
- **Unfocused pause**: `pauseBg` stops the animation pump while none of the
  app's windows is foreground (per-app toggle in Theme Studio).

### Fixed (bug audit)

- A profile saved without the custom-app flag (applying a theme, rotating, renaming) wrote `splash=on` and
  silently turned the startup animation on for custom apps; `splash=` is now only written when it was present or changed.
- Slide and chime edits in Theme Studio now re-apply to every app showing that theme (they never reached the apps before),
  an emptied slides folder is removed, and a `slides` change reloads the wallpaper at once.
- A look change during a slideshow crossfade could dispose the frame being blended and park the session; crossfades now
  survive re-renders, handle slides of different sizes, and `source` never points at a freed frame.
- Animated wallpapers under clear-zone/glow/vignette no longer trust GDI+'s unspecified alpha bytes in the frame DIB.
- APNG chunk-length and frame-rectangle integer overflows are rejected instead of throwing.
- Theme import reads zip entries through a bounded copy (a lying size field can no longer inflate past the cap) and
  caps slide entries at 600; `addSlides` only honours an explicit `paths` list in test mode.
- Pointer packs: applying a pack no longer leaves stale roles from the previous pack; the saved scheme is never one of
  ours, keeps `%SystemRoot%` paths unexpanded, and oversized PNGs are fitted to 128 px; reserved device names are refused;
  removing a pointer also removes its `.cur`/`.ani`/generated files; uninstall with data removal restores the saved scheme.
- Crosshair overlay retries painting instead of staying blank when `UpdateLayeredWindow` fails, the cache is keyed by file
  time and bounded, and picking a shape no longer resets each app's colour (the colour picker now applies on change).
- Studio: slideshow preview filters apply, no button nested in a button, chat mock escapes the app name, two untranslated
  strings and 20 dead French keys fixed.

## 3.2.0 — 2026-11-22

Animation pacing controls, slideshow and APNG wallpapers, a next-theme hotkey,
app presets, an optional boot chime and a battery-saver pause.

- Playback controls per app: `gifSpeed` (25–400% of authored delays),
  `gifCap` (10/15/20/30 fps floor), `gifPause` freeze and `slideSec` — all
  editable in Theme Studio's app controls and applied live by the session.
- A "Pause animation" toggle in each app's tray submenu.
- Win+Alt+T cycles the artwork to the next theme in the app's pool.
- Slideshow themes: a `slides\` folder of stills inside a theme plays through
  the timeline pump as a uniform-delay animation (30 s default, 5 s–60 min).
  Slides copy and clear with the theme, duplicate, back up, pack into
  `.altheme`/`.albackup`, bust the theme stamp and badge "Slides" in the rail.
- APNG wallpapers: `wallpaper.apng` decodes through a chunk-level decoder that
  rebuilds each frame's `fcTL`+`IDAT`/`fdAT` as a standalone PNG and composites
  blend/dispose ops onto the canvas — same pump, same budgets as GIF.
- Battery-saver: the `battSaver` switch pauses the pump while the machine runs
  on battery; a `PowerModeChanged` hook re-arms it when AC returns.
- Boot sound: an optional `sound.wav` in a theme plays a chime when the startup
  animation starts; it travels with themes, packs into `.altheme` and validates
  RIFF/WAVE on import.
- Theme Studio: animated themes (gif/APNG) preview real frames instead of the
  still; a preset chip row under "Add your own app" probes known install paths
  (Chrome, Spotify, Telegram and friends) for one-click custom apps.
- Theme chip toggling: clicking a lit "Use on" chip removes the theme and
  switches the app's artwork off (`watch=off`); applying re-enables it.
- Spotify verified attachable as a custom app.

## 3.1.0 — 2026-11-22

Animated wallpapers, eased drift, grouped app chips, a global startup-animation
switch and a faster present path.

- Animated wallpapers: a theme may ship `wallpaper.gif` — frames play on the
  opaque present path (delays floored at 50 ms, single-frame and over-budget GIFs
  fall back to a still) and a first-frame `wallpaper.jpg` is always kept beside it
  for the splash and previews. Theme Studio accepts dropped/picked GIFs, badges
  animated themes in the rail, and `.altheme` packs and `.albackup` backups carry
  the GIF through export and import.
- Timeline animation pump: the frame index tracks wall-clock against the GIF's
  authored delays and drops frames when behind, so loops keep their real
  duration instead of running slow. Animated frames draw straight into a
  persistent DIB + memory DC (~2x faster per frame than the still path), letting
  real GIF cadences play without stutter.
- Drift: a new `drift` look key (0..100, Theme Studio "Fine tuning" slider) eases
  the artwork focus point across the window — the measured-viable form of the E4
  parallax experiment. Opaque present path only.
- Theme Studio "Use on" chips are grouped behind AI / Work in progress tabs;
  per-app theme state is unchanged and "All apps" still applies everywhere.
- Settings gains a master "Startup animation" On/Off switch (`setSplashAll`)
  that flips every app at once; the per-app toggle under Apps is unchanged and
  custom apps still default to off.
- `tools\E2-Check.ps1`: attaches a magenta test wallpaper to real third-party
  windows (Chromium, Qt, Tauri tried) and reports attach + paint deltas.

## 3.0.0 — 2026-11-22

The first release under the Animated Launchers name: updates 1–18 from the roadmap, shipped together.

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
