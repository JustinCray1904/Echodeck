# Third-party notices

Tempo's own code is released under the MIT License (see `LICENSE`). It is built on, and ships with, the open-source components below.
Each keeps its own license. This list was written by hand: check each project's current license text before redistributing.

| Component | Used for | License (as published by the project) |
|---|---|---|
| LibVLC (VideoLAN), via the `VideoLAN.LibVLC.Windows` package | Playing audio and video | GNU LGPL 2.1 or later. Source: https://www.videolan.org |
| LibVLCSharp and LibVLCSharp.WPF | .NET wrapper for LibVLC | GNU LGPL 2.1 |
| TagLib# (`TagLibSharp`) | Reading song tags and embedded covers | GNU LGPL 2.1 |
| NAudio | Reading audio for the waveform | MIT |
| System.Security.Cryptography.ProtectedData | Protecting the Anonymous Mode master key with the Windows account | MIT (Microsoft) |
| .NET 8 runtime and WPF | The application platform | MIT (Microsoft) |
| Inno Setup | Building the installer (build tool, not shipped) | Inno Setup License |

Optional downloads, fetched by the user from inside Tempo and not part of this repository:

| Component | Used for | License (as published by the project) |
|---|---|---|
| whisper.cpp | Generating lyrics on the user's computer | MIT |
| Whisper speech models | Generating lyrics | MIT (OpenAI) |

Online services the app can contact when the user switches the matching settings on: LRCLIB (lyrics), Deezer, iTunes Search, MusicBrainz and the
Cover Art Archive, and YouTube thumbnails (covers). Their data and images belong to their owners and are shown to the user only.

**LGPL components.** LibVLC, LibVLCSharp and TagLib# are used as separate, unmodified libraries (DLL files next to the program), so they can be
replaced by the user with another build of the same library. Their source code is available from their projects.

**Design.** The interface follows the community "VLC Redesign" Figma file; the design belongs to its author.
