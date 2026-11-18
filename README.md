# GH3-Sync

**Created by Mad Scientist ([tbailey639](https://github.com/tbailey639))**

**GH3-Sync** is a companion application, custom song creator, Clone Hero converter, and P2P setlist synchronizer for **Guitar Hero III PC / BetterGH3**.

---

## Features

### 1. P2P Song Sync, Trusted Network & Temporary Lobby Sessions
* **Trusted-Friends-Only Downloads:** Only verified **Trusted Friends** can download custom song packages from your library. Regular friends using a Session PIN can view your setlist only.
* **Temporary Lobby Session Mode:** Sync friends' songs temporarily for a multiplayer session — GH3-Sync snapshots your pre-session `customs.pak.xen` and saves, lets you click **Keep Song** on any track you want to keep, and automatically removes temporary files and restores `customs.pak.xen` when `GH3.exe` closes (or on **End Session & Clean Up**).
* **Active Lobby Setlist (10-Song Auto-Sync Cap):** Tag up to **10** custom songs (configurable 1–25) in **My Customs** to auto-sync on connect so large libraries never flood a friend's drive. Includes a **Trusted Friend Quick-Select** dropdown and a **100% Lobby Matched** readiness indicator.
* **Smart `customs.pak.xen` Songlist Merging:** Synced and imported songs merge cleanly into your existing `DATA\customs.pak.xen` without overwriting your installed customs or resetting an active custom save.
* **Security & Privacy:** End-to-end AES-256-GCM encryption, headers-only auth (`X-GH3-Trust-Token` / `X-GH3-PIN`), Windows DPAPI encryption for stored Trust Tokens, SHA-256 verification on synced songs and app updates, per-IP rate limiting, concurrent download limits, and an **Online/Offline P2P** toggle that immediately closes UPnP port `7373`.

### 2. ONOGAMI Song Creator & Bundled Compiler
* **Built-In Compilation:** Bundles Honeycomb/GH-Toolkit-Core and FFmpeg directly into GH3-Sync so `.pak.xen` charts and `.fsb.xen` / `.dat.xen` soundbanks compile directly into BetterGH3 with zero leftover temporary files or duplicated binaries.
* **Dynamic Variable Tempo Maps:** Measure-by-measure beat tracking in `[SyncTrack]` keeps fretboard beat lines, lighting, and band animations synced even on live recordings.
* **Pitch-Mapped Auto-Charting:** Generates 4-difficulty charts (`Easy`, `Medium`, `Hard`, `Expert`) with **Casual / Standard / Shredder** chart feel and **Guitar / Bass / Vocals** instrument focus.
* **6-Stem Separation (Demucs `htdemucs_6s` + Basic Pitch):** 1-click installer under **Tools & Stems** isolates `guitar.wav`, `bass.wav`, `drums.wav`, `vocals.wav`, and `piano.wav` into `%APPDATA%\GH3-Sync\tools\ai` (including cached model weights via `TORCH_HOME`), preserved automatically across updates and selectable to keep or remove during uninstall.
* **Batch Song Queue:** Queue multiple songs and compile them in a single run.

### 3. 1-Click Clone Hero / YARG Converter & Archive Import
* Import any **Clone Hero / YARG** song folder or `.zip` (`song.ini`, `notes.chart` / `notes.mid`, and `.ogg` / `.mp3` / `.wav` stems) with zip-bomb and file-type security scanning.
* Import `.gh3song` / `.gh3pack` archives cleanly into your existing `DATA\customs.pak.xen` while preserving creator attribution (`Made with GH3-Sync` vs `Imported (Clone Hero)`).
* Supports **drag-and-drop** anywhere on the window.

### 4. Settings, Custom Themes & Component Compatibility
* **Dedicated Tabbed Settings Page:**
  * **General & Paths:** Game/songs paths, Online/Offline P2P toggle, minimize-on-launch, Lobby Setlist cap, and a **1-Click Clean Duplicate & Temp Files** maintenance tool.
  * **Appearance:** Instant live switching between **Dark GH3 Theme** *(Legends of Rock Stage)* and **Light GH3 Theme** *(Backstage Amp & Steel)*.
  * **Updates & Compatibility:** Checks GH3-Sync, Honeycomb, BetterGH3, FFmpeg, and Demucs/Basic Pitch versions, runs a **5-Point Compatibility Self-Test**, and supports automatic rollback if a stem package update fails verification.
  * **About, Credits & Logs:** Version info, credits, known limitations, and diagnostic log viewer.
* **Library Management & QoL:** Launch GH3 button, single-instance window restore from tray, built-in Audio & Guitar Stem Preview Player, Edit Song Info & Volume Mixer, Favorites, Sorting, Selective Pack Export, and individual song deletion that automatically strips entries from `DATA\customs.pak.xen`.

### 5. Paired Career Save Profiles & First-Time Detection
* Automatically detects on first launch whether your Guitar Hero III custom save (`Honeycomb}{Honeycomb`) has been set up yet and offers a 1-click vanilla career save backup.
* Saves named snapshots pairing your career save (`s000.d`, `toc.dat`) **together with its matching `DATA\customs.pak.xen`** so restoring a profile works cleanly without corrupt-save errors.

---

## Known Limitations
* The first time custom songs are added to a vanilla Guitar Hero III save, the game prompts for a one-time save reset. After that initial custom profile is created, additional songs merge cleanly into `DATA\customs.pak.xen`. Back up your vanilla profile in **Career Saves** first if you want to preserve it.
* Restoring a Career Save snapshot rolls back both your career save file and your custom song list together so they remain matched.

---

## Installation
Download and run **`GH3-Sync-Setup.exe`** from the [Latest Release](https://github.com/tbailey639/GH3-Sync/releases/latest). Existing users on `v1.0.0` can click **Check Updates → Update Now** inside the app.

---

## Credits & Acknowledgments
Developed by **Mad Scientist** ([github.com/tbailey639](https://github.com/tbailey639)).

Built with gratitude to the open-source projects and Guitar Hero modding community:
* **[Honeycomb-GUI & GH-Toolkit-Core](https://github.com/AddyMills/Honeycomb-GUI)** — AddyMills & Neversoft modding community
* **[Demucs (`htdemucs_6s`)](https://github.com/facebookresearch/demucs)** — Alexandre Défossez et al. (Meta AI Research)
* **[Basic Pitch](https://github.com/spotify/basic-pitch)** — Spotify Audio Intelligence Lab
* **[BetterGH3](https://github.com/marchellodev/BetterGH3)** — Marchello & the BetterGH3 Team
* **[FFmpeg](https://ffmpeg.org/)**, **[FMOD](https://www.fmod.com/)**, **[NAudio](https://github.com/naudio/NAudio)**, **[DryWetMidi](https://github.com/melanchall/drywetmidi)**, **[python-build-standalone](https://github.com/astral-sh/python-build-standalone)**

See [CREDITS.md](CREDITS.md) for full notices.

---

## License
Licensed under the **PolyForm Noncommercial License 1.0.0**. Free for personal and community gaming use. Commercial use is prohibited.
