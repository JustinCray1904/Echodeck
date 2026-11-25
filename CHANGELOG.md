# Changelog

All notable changes to Tempo. The newest are first. Versions are the git tags (`v2.x.y`).

## [2.1.0] - 2026-11-24

### Added
- **Sound mode** (Settings > Audio, and the Channels list in the bottom-bar popup): Normal, Mono, Reverse stereo, Left side only,
  Right side only, Dolby surround. Applies at once and to every new song. Sound space (wide stereo, room / 3D) and Even out volume are
  LibVLC audio filters and need a restart.
- **Mini player**: "Keep the video playing while I move it" (Settings > Interface); off, the picture is blank until the player is docked.
- **Saved waveforms**: each song is analysed once; the waveform and visualiser data load instantly the next time.
- **Tempo prompt** when a cover look-up has no internet connection.
- **PIN box**: Backspace erases the last digit, Delete clears, Esc closes; digits work without holding Ctrl once it is open.
- New vector logo and watermark (`Assets/logo.svg`, `logo-mark.svg`), new icon and logo images.
- `audit.yml` (dependency advisories), `CONTRIBUTING.md`, this file, `docker-compose.yml` (build and test in a Windows container) and a
  ready-to-import branch ruleset (`.github/rulesets`); tests for the cache, the queue rules, the PIN, sound filters, log formatting and the
  vault's failure logging.
- **Structured log lines** in `tempo.log` (`AppLog.Event`: level, event name, key=value fields) for settings, playlists, recent and
  waveform-cache save failures and for a vault original that could not be removed.

### Changed
- **Smart queue** queues the whole series, season after season, including seasons in folders next to each other (`Season 1`, `Season 2`),
  and also when an episode is double-clicked in the library.
- **Vault files** are named `.deex` (thumbnails `.deet`). Files from earlier builds (`.svv`, `.svt`) still open and are not renamed.
- **Faster vault**: larger sequential batches, one random block for the secure overwrite, vault headers read in parallel.
- **Anonymous Mode starts at once** after the passcode: the vault is read while the "going dark" animation plays.
- The light theme's music view is dark glass over the whole window, like the dark theme.
- Vault playlist failures are written to `tempo.log` (the kind of exception only, never a name or path).
- The large source files are split by topic: `MainWindow` is now 29 partial-class files, and `VaultService`, `WhisperService`,
  `SettingsWindow`, `ShellIntegration` and `SongResolver` are split too, so no file is over 500 lines (nothing else changed).
- Queue rules, stereo modes and time text moved out of the window code into `QueueNavigator`, `StereoModes` and `TimeText`.
- `build.yml` runs the tests before anything is published, and a version tag's GitHub Release uses its CHANGELOG section as the text.

### Fixed
- A new song no longer starts at full volume for a few seconds.
- Changing the accent colours the moving background at once (it waited for the next song).
- "This cover is wrong - find another" returns the background to its default colours, and never returns a compilation's cover for a
  song whose file name names the artist.
- The mini player's video no longer leaves a trailing edge while it glides into its dock.

### Removed
- The AMOLED theme (an old `settings.json` that asks for it opens as Dark).
