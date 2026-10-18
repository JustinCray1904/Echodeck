# Deefxult Media Player

WPF (.NET 8) video player built on LibVLCSharp, with a PIN-triggered, **encrypted** vault overlay.

## Build & run

Requirements: Windows 10/11 (x64), [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd DeefxultMediaPlayer
dotnet restore
dotnet run -c Release                      # or: dotnet run -c Release -- "C:\Videos\clip.mp4"
```

Or open `DeefxultMediaPlayer.csproj` in Visual Studio 2022 (".NET desktop development" workload) and press F5.

Distributable build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
# add --self-contained true to bundle the .NET runtime (larger, no runtime install needed)
```

**Ship the whole `publish` folder.** The `libvlc\win-x64` subfolder must sit next to the exe. The build prints a
warning if it's missing, and the app shows a clear message (instead of crashing) if it can't find `libvlc.dll`.
The app is x64-only.

## Usage

| Action | How |
|---|---|
| Open video | Ctrl+O, the Open button, drag & drop, or pass a path as a command-line argument |
| Play/pause, fullscreen | Space, F11 (Esc leaves fullscreen) |
| Open the vault | Hold **Ctrl** and type the PIN (default `1234`) |
| Browse the vault | Hover the lock pill (top-left of the video); it expands into the deck |
| Import | Encrypts the chosen videos into `%USERPROFILE%\.SecureVideoVault`; your originals are not touched |
| Play | Select an item and click Play (or double-click): decrypted on the fly into the player, vault stays open |
| Delete | Select an item, click **Delete**, then click **Sure?** within 3 seconds: permanent delete (see below) |
| Panic exit | **Esc** closes the vault, wipes its state, zeroes the key in memory, and resumes the normal video at the saved millisecond |

**First run:** on the very first launch a "Choose your PIN" window asks for a 4-digit PIN (entered twice). "Not now"
keeps the default `1234` and the window isn't shown again (the vault still reminds you to change it).

**Changing the PIN:** open the vault, click the gear in its header, type the new 4 digits, then type them again to
confirm (Backspace restarts the entry). Digits show as dots. It is saved immediately and survives restarts. Only a
salted PBKDF2 hash is stored, wrapped with Windows DPAPI, in `%LOCALAPPDATA%\DeefxultMediaPlayer\pin.dat`. Until you
set one, the default `1234` applies (or `DEEFXULT_PIN`, 4 digits, if that environment variable is set); the vault reminds
you to change the default each time it opens. A saved PIN always overrides the environment variable.

## Security model

- **Encryption at rest:** every vault file is AES-256-GCM (authenticated) in 256 KiB chunks, with a fresh per-file
  key derived (HKDF-SHA256) from a random 256-bit master key. Files are stored under random GUID names with a
  `.svv` extension; the original name and length are inside the encrypted header, so a folder listing reveals nothing.
- **Key storage:** the master key is wrapped with Windows DPAPI (current user) and stored *outside* the vault folder in
  `%LOCALAPPDATA%\DeefxultMediaPlayer\vault.key`. Copying the vault folder or the disk to another machine/account
  gives only ciphertext. The key is loaded into memory when the vault opens and zeroed when it closes.
- **Important trade-offs:**
  - The PIN is only a UI gate. A 4-digit PIN can't protect data by itself, so it is deliberately *not* the encryption key.
  - Anyone logged in as you can run the app and open the vault. Lock your Windows session, and use BitLocker for the
    whole drive as defence in depth.
  - If you lose the Windows profile or `vault.key` (reinstall, account deleted), the vault can't be decrypted. Back up
    `vault.key` together with the vault folder if you want recoverability (the key file is itself DPAPI-bound, so a
    backup only helps on the same Windows account).
- **Playback:** decrypted bytes are streamed to LibVLC in memory; no plaintext temp files are written.
- **Existing files:** plaintext videos left in the folder by the earlier version are encrypted automatically the
  first time the vault opens (originals are verified, then securely deleted).
- **Permanent delete:** overwrites the file with random data, flushes, then deletes it (no Recycle Bin). Playback is
  stopped and the stream released first, so Windows doesn't report "file in use". Caveat: on SSDs and wear-levelled
  storage no software can guarantee physical erasure of every old block; BitLocker plus TRIM is what covers that.

## Antivirus / Defender false positives

By default the app uses WPF's own input pipeline for Ctrl+PIN and Esc: **no Windows keyboard hook and no
`GetAsyncKeyState` polling**, so there is nothing for heuristics to mistake for a keylogger. Other things that
keep reputation clean:

- Sign release builds: `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a DeefxultMediaPlayer.exe`
  (a real code-signing certificate; a self-signed one only helps on machines that trust it).
- Don't pack with UPX or similar, and avoid single-file self-extracting builds (LibVLC needs its `libvlc` folder anyway).
- If a scanner still flags a build, submit it at the Microsoft Security Intelligence portal as a false positive.

**If Ctrl+PIN is ignored after you click on the video** (focus ended up inside LibVLC's native window on your setup),
build the optional global-hook variant:

```powershell
dotnet build -c Release -p:EnableGlobalKeyHook=true
```
That compiles `GlobalKeyboardHook.cs` (a `WH_KEYBOARD_LL` hook; digits only considered while Ctrl is held and the app is
active; nothing logged or stored). Expect more antivirus attention for that build, so sign it.

## Focus and overlay behaviour

- The overlay window is non-activating (`WS_EX_NOACTIVATE`): clicking it never takes focus from the player, and it's
  hidden from Alt-Tab.
- It is hidden whenever the app is in the background, so it never floats over other programs.
- The pill collapses after a short 250 ms delay, so quick mouse movement doesn't make it flicker.
- Transport buttons and sliders are non-focusable, so the video controls can't get stuck behind a focused control.

## Notes

- NuGet versions in the `.csproj` (LibVLCSharp 3.8.5, VideoLAN.LibVLC.Windows 3.0.20) can be bumped to newer 3.x releases.
- Replace `Assets\app.ico` with your own icon if you like (a placeholder is included).
- Source map: `VaultFormat.cs` (file format), `EncryptedVaultStream.cs` (seekable decrypting stream), `KeyStore.cs`
  (DPAPI key), `VaultService.cs` (list/import/delete/migrate), `MainWindow.xaml.cs` (player + trigger + panic),
  `VaultOverlayWindow.*` (pill/deck UI).
