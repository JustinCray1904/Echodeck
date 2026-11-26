# Contributing to Tempo

Tempo is a Windows media player (WPF, .NET 8, LibVLCSharp). It only builds and runs on Windows.

## Build, test, format

From a fresh clone, with the .NET 8 SDK installed:

```
dotnet restore EchoDeck/EchoDeck.csproj
dotnet build   EchoDeck/EchoDeck.csproj -c Release
dotnet test    EchoDeck.Tests/EchoDeck.Tests.csproj -c Release
dotnet format  EchoDeck/EchoDeck.csproj --verify-no-changes
dotnet format  EchoDeck.Tests/EchoDeck.Tests.csproj --verify-no-changes
```

What runs on GitHub for every push and pull request (`.github/workflows`):

| Workflow | What it does |
| --- | --- |
| `test.yml` | The unit tests, with a TRX report and a coverage artifact. |
| `build.yml` | Runs the tests first, then builds the self-contained app and the Inno Setup installer (and, for a version tag, the GitHub Release). |
| `format-check.yml` | `dotnet format --verify-no-changes`. It is its own red item and never blocks the others. To fix it: Actions > Maintenance > Run workflow > `format`. |
| `audit.yml` | Looks for NuGet packages with known security advisories (also every Monday). |
| `maintenance.yml` | Manual jobs: regenerate the lock files, run `dotnet format`. |

Package versions are pinned by `packages.lock.json` (restores are locked). To change a package on purpose, change its version and
run Maintenance > `lock-files`, then commit the new lock files.

## Small commits, each with its tests

- One change per commit (or per pull request): a fix or a feature, **with the tests that pin it**. Do not mix formatting, refactors and
  features in one commit.
- Put logic that does not need a window into plain classes (see `PlaybackCoordinator`, `QueueNavigator`, `SmartQueue`, `SongResolver`)
  and test it in `EchoDeck.Tests`. The `MainWindow.*.cs` partial classes should only wire the window to those classes.
- Commit messages: a short imperative first line ("Queue every season of a series"), then why, if it is not obvious.
- When you touch a file that is already large (`MainWindow.xaml.cs`, `MainWindow.Library.cs`, `MainWindow.Anonymous.cs`), prefer moving
  what you touch into a small class rather than growing the file.

## Rules that protect the user

- **Anonymous Mode and the vault leave nothing readable behind.** Never write a file name, a path or an exception message that could
  contain one to a log. Use `AppLog.Failure(where, ex)` for anything vault-related: it records the kind of exception only.
- Problems Tempo handles itself go to `AppLog` (`%LOCALAPPDATA%\Tempo\tempo.log`); do not leave a `catch` block silent unless there is
  nothing useful to say and a comment explains why. `crash.log` is only for real crashes.
- Secrets and keys are never committed. The vault key is wrapped with Windows DPAPI at run time.
- Calls into LibVLCSharp that cannot be checked at build time are looked up by name and wrapped in `try`, so a different wrapper version
  cannot break the build (see `StereoModes.TrySet`).

## Where things are

The main window is one `partial class MainWindow` spread over topic files, none longer than about 450 lines. Put new code in the file
of its topic (or a new one), not at the end of the biggest file.

| File | What is in it |
| --- | --- |
| `MainWindow.xaml.cs` | fields, start-up and shutdown, switching between the library and the player |
| `MainWindow.Playback.cs` | starting playback, the start-up volume guard, sound mode (channels, filters) |
| `MainWindow.MusicView.cs` | the music view (cover, waveform), shuffle and repeat |
| `MainWindow.Menus.cs`, `MainWindow.FastMenu.cs` | context menus and the quick-settings popup |
| `MainWindow.AudioMod.cs` (+ `AudioModWindow`, `AudioMod`, `AudioModDsp`, `AudioModRenderer`) | Audio Mod: the window, the settings and presets, the sound processing, and playing the modded copy |
| `MainWindow.Controls.cs` | toolbar buttons and keyboard handling (hotkeys, passcode keys, panic key) |
| `MainWindow.Library.cs`, `MainWindow.LibraryTiles.cs`, `MainWindow.LibraryLayout.cs` | the pages, tiles, selection and tile menus |
| `MainWindow.Queue.cs`, `MainWindow.Playlists.cs` | the queue panel (drag and drop, Next / Previous) and playlists |
| `MainWindow.Anonymous.cs`, `...AnonymousFiles.cs`, `...AnonymousSession.cs`, `...AnonymousPlayback.cs` | Anonymous Mode: pages, import and restore, entering and leaving, playback |
| `MainWindow.Lyrics.cs`, `MainWindow.LyricsSources.cs`, `MainWindow.LyricsBatch.cs` | the lyrics panel and where lyrics come from |
| `MainWindow.Shell.cs`, `MainWindow.VideoSurface.cs` | taskbar, global keys, playback errors, the native video window |
| `MainWindow.Mini.cs`, `.Overlay.cs`, `.Ambience.cs`, `.Crossfade.cs`, `.Resume.cs`, `.Sleep.cs`, ... | the mini player, on-screen indicator, cover colours, crossfade, resume, sleep timer |

Logic that needs no window lives in plain classes with tests: `PlaybackCoordinator`, `QueueNavigator`, `SmartQueue`, `SongText` /
`SongMatcher` / `SongResolver`, `StereoModes`, `SoundFilters`, `TimeText`, `AudioModDsp`, `WaveformCache`, `PinDetector`, `VaultFormat`.

## Releases

1. Add what changed to `CHANGELOG.md` under a heading `## [2.1.0] - 2026-11-24` (the version without the `v`).
2. Tag the commit and push the tag: `git tag v2.1.0` then `git push origin v2.1.0`.
3. `build.yml` runs the tests first; only if they pass does it build, attach the installer and the portable zip to a GitHub Release, and
   use that CHANGELOG section as the release text.

## Optional: run everything in a Windows container

With Docker Desktop in Windows-containers mode: `docker compose run --rm tests` (or `build`). See `docker-compose.yml`. It is not used by
GitHub Actions.

## Optional: protect the main branch

`.github/rulesets/protect-main.json` is a GitHub ruleset that stops deleting and force-pushing `main` and requires the `test` and `build`
checks to pass before anything is merged into it. Repository admins can still push directly. A file in the repository cannot switch it
on by itself: Settings > Rules > Rulesets > New ruleset > Import a ruleset, and choose that file. (Private repositories need a paid
GitHub plan for rulesets.)
