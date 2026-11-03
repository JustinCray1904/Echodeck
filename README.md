# Tempo

A modern Windows media player for video and music, built on LibVLC (WPF, .NET 8), with a private encrypted space called **Anonymous Mode**. The interface follows the community *VLC Redesign* Figma file, with a light-blue accent.

## Get a build
Push to GitHub: the **Build Tempo** workflow publishes a self-contained x64 app and compiles the Inno Setup installer. Download `Tempo-Setup-<version>.exe` from the run's **Artifacts**. Tag a commit `v2.0.1` to also attach a portable zip to a GitHub Release.

## Features
- Music page with Songs / Albums / Artists (read from file tags), Videos page, several library folders (Settings > Library), tick-box multi-select, equaliser with presets
- Recent page (first page by default), Home / Videos / Music browsing with four-per-row thumbnail tiles, hover play and ··· menus, playlists (create, rename, cover, shuffle, repeat, sort, search) and a queue panel (Q / P)
- Video mini-player while browsing, floating auto-hiding control bar in fullscreen, volume popup + on-screen indicators, rounded video corners
- Library-first layout from the Figma file: labelled nav, thumbnail grid / list, mini player, docked quick-settings panel (Video / Audio / Subtitles)
- Waveform seek bar for music, VLC-style wedge volume bar, shuffle + repeat, right-click menu
- Control bar with blue action bundles, blue seek and volume bars, audio/subtitle/settings buttons, play pill
- Dark, Light and System themes, 9 accent colours (default: Sky light blue)
- File browser + queue side panel, music view, subtitles, picture adjustments, speed, repeat
- First-run onboarding: media folder, theme, control tour, Anonymous Mode passcode, shortcuts
- Full Settings window; separate Anonymous Mode settings (import files or a whole folder, passcode, security)
- **Fast library scans**: song tags are read straight from the files (TagLib, in parallel) and cached in `%LOCALAPPDATA%\Tempo\library-cache.json`. The Music and Videos pages open instantly from the cache, a loading bar shows progress, and only new or changed files are read on later launches. An interrupted scan resumes where it stopped.
- **Anonymous Mode**: hold `Ctrl` and type your passcode (4-8 digits), or use the menu. A "going dark" animation plays, the UI turns violet-dark, and the encrypted library opens. `Esc` leaves instantly.

## Anonymous Mode
The lock icon in the title bar shows that the mode is on and holds **Import files / Import folder / Restore selected / Anonymous settings / Exit**. Recent, Home, Videos, Music, Playlists and the queue work as usual but show only vault files (normal files are never shown, and vault files never appear in normal mode).
- Song / video details (title, artist, album, genre, length) are read at import and stored in the encrypted file header; Home shows virtual folders built from each file's original folder.
- Playlists are kept in `vault.db` (AES-GCM, key derived from the master key). Thumbnails are small encrypted sidecar files (`.svt`).
- "Recent" in Anonymous Mode lives in memory only and is wiped on exit. Files imported by earlier builds have no stored details (shown as Unknown artist / album); import them again to add them.

## First launch
A fresh install (the installer drops `fresh-install.flag`) shows the logo splash and then the onboarding tour. To see it again by hand, delete `%LOCALAPPDATA%\Tempo\settings.json` (never `vault.key`).

## Anonymous Mode security model
- Files are encrypted with AES-256-GCM under per-file keys, stored under random names in `%USERPROFILE%\.EchoDeckVault`; original names are encrypted too.
- The master key is wrapped with Windows DPAPI for your account and lives in `%LOCALAPPDATA%\Tempo\vault.key`, outside the vault folder.
- Playback is decrypted in memory (custom LibVLC media input); nothing is written to disk. No history, resume positions, or snapshots in Anonymous Mode.
- Deleting overwrites the file with random data before removal.
- The passcode only gates the UI (salted PBKDF2 hash, DPAPI-wrapped). It is not the encryption key.
- Data from the older "EchoDeck" and "Deefxult Media Player" builds (`%LOCALAPPDATA%\EchoDeck`, `.SecureVideoVault`, `DeefxultMediaPlayer\vault.key`) is copied over automatically on first launch (never moved or deleted). The vault folder keeps its name, `.EchoDeckVault`.

## Size
The publish step produces a ReadyToRun (fast-starting) self-contained app plus a trimmed `libvlc\win-x64` folder (Qt GUI, Lua, stream-out, locales and encoders removed, see `TrimLibVlc` in `EchoDeck/EchoDeck.csproj`). Opt out with `-p:TrimLibVlc=false`.

## LibVLC native architecture
The project is pinned to `win-x64`. The workflow verifies `libvlc\win-x64\libvlc.dll` exists and is an x64 binary, and at runtime `App.xaml.cs` locates the natives explicitly and shows a clear message if they are missing.

## Global key hook (optional)
Ctrl+passcode and Esc use WPF input by default. If they stop working after clicking the video, build with `-p:EnableGlobalKeyHook=true`. The hook callback only queues one dispatcher item and returns, so Windows never times it out. Signed builds avoid antivirus false positives.

## Build locally
```
dotnet publish EchoDeck/EchoDeck.csproj -c Release -r win-x64 -o publish
iscc /DSourceDir=%CD%\publish installer\EchoDeck.iss
```
