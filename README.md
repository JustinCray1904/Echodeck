# Deefxult Media Player

Windows WPF (.NET 8) video player (LibVLCSharp) with a PIN-triggered, AES-encrypted vault overlay.
See [`DeefxultMediaPlayer/README.md`](DeefxultMediaPlayer/README.md) for usage and the security model.

## Get a working installer from GitHub (no local build needed)

1. **Extract the zip and upload its contents to the repo root**, so `.github/`, `installer/` and `DeefxultMediaPlayer/`
   sit at the top level of the repository. With git:

   ```powershell
   git clone https://github.com/<you>/<repo>.git
   cd <repo>
   # copy the extracted files in here (keep the hidden-looking .github folder), then:
   git add -A
   git commit -m "Add Deefxult Media Player"
   git push
   ```

   (Uploading through the GitHub web page works too: drag the extracted folders in, including `.github`.)

2. Open the repo's **Actions** tab. The *Build installer* workflow starts automatically on every push to `main`/`master`.
   When it finishes (about 5 minutes), open the run and download the artifact from **Artifacts**: it contains
   `DeefxultMediaPlayer-Setup-<version>.exe` (the installer) and a portable `.zip`.

3. **To publish a downloadable release**, push a version tag; the installer is attached to a GitHub Release automatically:

   ```powershell
   git tag v1.1.0
   git push origin v1.1.0
   ```

If the *Actions* tab says workflows are disabled, click **I understand my workflows, go ahead and enable them**
(forks) or check *Settings → Actions → General → Allow all actions*.

### What the workflow does

`dotnet restore` → `dotnet publish` (Release, win-x64, **self-contained**, so users don't need .NET installed) →
verifies `libvlc\win-x64\libvlc.dll`, `libvlccore.dll` and `plugins\` are in the output (copying them from the
`VideoLAN.LibVLC.Windows` NuGet package if the publish step missed them, and failing the build if they still aren't there) →
installs Inno Setup → builds the installer from `installer/DeefxultMediaPlayer.iss` → uploads artifacts (and attaches them
to a Release on `v*` tags). The app icon is `DeefxultMediaPlayer/Assets/app.ico` (a real multi-size .ico is included; the
workflow fails early if it's missing).

### File associations

The installer has a *Register Deefxult Media Player for video files* option (on by default) that writes the registry
entries for `.mp4 .mkv .avi .mov .wmv .webm .m4v`: a ProgID with `shell\open\command`, "Open with" entries, and a
Default-apps capability. Double-clicking a video then launches the player with the file path as its argument. Windows 10/11
deliberately stops installers from silently changing the default player (a hash-protected user setting), so the app
shows up under **Open with** immediately, and the installer's last page offers to open *Settings → Default apps* where you
confirm it once. Uninstalling removes the registrations.

### Notes

- **SmartScreen / antivirus:** an unsigned installer shows "Windows protected your PC" on first run (More info → Run anyway).
  To remove that, sign the installer and exe with a code-signing certificate stored as GitHub Actions secrets.
- Uninstalling the app never deletes the vault (`%USERPROFILE%\.SecureVideoVault`) or its key/PIN files in
  `%LOCALAPPDATA%\DeefxultMediaPlayer`; remove those by hand if you want everything gone.
- The workflow pins `actions/checkout@v4`, `setup-dotnet@v4`, `upload-artifact@v4` and `action-gh-release@v2`; bump them
  if GitHub deprecates those majors.
