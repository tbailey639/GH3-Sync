# GH3-Sync

**Created by Mad Scientist ([tbailey639](https://github.com/tbailey639))**

**GH3-Sync** is an all-in-one companion application, custom song creator, Clone Hero converter, and P2P setlist synchronizer for **Guitar Hero III PC / BetterGH3**.

---

## Features

### 1. P2P Song Sync & Lobby Setlist (10-Song Auto-Sync Cap)
* **Active Lobby Setlist:** Tag up to **10** custom songs (configurable 1–25) in **My Customs** for automatic sync with friends. Prevents large song libraries from overflowing your friend's storage while still letting them browse and download individual extra tracks on demand.
* **Lobby Readiness Check:** Displays a live **100% Lobby Setlist Matched** indicator before launching an online BetterGH3 match.
* **Friends & Trusted Network:** Permanent unique Friend Codes (`GH3-XXXX-XXXX`), Trusted Friend automatic IP/token sharing, and PIN-protected connections for regular friends.
* **UPnP / NAT-PMP & Public IP Support:** Automatically forwards port `7373` and detects public WAN addresses for playing outside your local LAN.
* **End-to-End Encryption:** All song packages and `customs.pak.xen` transfers use AES-256-GCM authenticated encryption with duplicate detection and path-traversal protection.

### 2. ONOGAMI Song Creator & Bundled Compiler
* **Built-In Compilation:** Bundles Honeycomb/GH-Toolkit-Core and FFmpeg directly into GH3-Sync so `.pak.xen` charts and `.fsb.xen` / `.dat.xen` soundbanks compile directly into BetterGH3 with no separate setup.
* **Dynamic Variable Tempo Maps:** Uses dynamic beat tracking to write measure-by-measure `[SyncTrack]` BPM markers so fretboard beat lines, lighting, and band animations stay locked even on live recordings.
* **Pitch-Mapped Auto-Charting:** Generates 4-difficulty charts (`Easy`, `Medium`, `Hard`, `Expert`) from real detected guitar pitches, power chords, sustains, Star Power phrases, and solo markers. Supports **Casual / Standard / Shredder** chart feel and **Guitar / Bass / Vocals** instrument focus.
* **Optional 6-Stem Separation (Demucs `htdemucs_6s` + Basic Pitch):** 1-click installer under **Tools & Stems** isolates `guitar.wav`, `bass.wav`, `drums.wav`, `vocals.wav`, and `piano.wav` for authentic mute-on-miss gameplay and MIDI transcription.
* **Batch Song Queue:** Queue multiple songs and compile them in a single run.

### 3. 1-Click Clone Hero / YARG Converter
* Import any **Clone Hero / YARG** song folder or `.zip` (`song.ini`, `notes.chart` / `notes.mid`, and isolated `.ogg` / `.mp3` / `.wav` stems) — GH3-Sync maps all stems, converts `.chart` to `.mid`, and compiles straight into Guitar Hero III.
* Supports **drag-and-drop** anywhere on the window for audio files, charts, `.gh3song`/`.gh3pack` archives, and Clone Hero songs.

### 4. Library Management & QoL
* **Launch GH3 Button:** Start `GH3.exe` directly from the header bar (with optional auto-minimize to tray).
* **Built-In Audio & Guitar Stem Preview Player:** Audition tracks or solo isolated `guitar.wav` stems and set the 30-second menu preview start time directly from the scrubber.
* **Edit Song Info & Volume Mixer:** Update Title, Artist, Genre, and Guitar/Band dB volumes on existing projects and recompile in 1 click.
* **Individual Song Deletion:** Safely removes `.pak.xen`, `.fsb.xen`, `.dat.xen`, previews, config entries, and optionally the source project folder & stems while protecting vanilla GH3 tracks.
* **Favorites, Sorting & Selective Pack Export:** Star favorites, sort by Title/Artist/Date/Size, and export either selected songs or your entire library as a `.gh3pack`.
* **Automatic Updates:** Checks GitHub Releases on startup and installs new versions via the **Check Updates / Update Now** button.

### 5. Paired Career Save Profiles
* Saves named snapshots pairing your GH3 career save (`s000.d`, `toc.dat`) **together with its matching `DATA\customs.pak.xen`** so restoring a profile works cleanly without corrupt-save errors.

---

## Important: Guitar Hero III Career Save Warning
Guitar Hero III ties your career save (`s000.d`) to your exact `DATA\customs.pak.xen` song list. Whenever you add, sync, or delete custom songs, `customs.pak.xen` changes and GH3 will prompt you to delete/recreate your save file on next launch.
* **Restoring a Paired Snapshot** in **Career Saves** restores *both* your old career progress and its matching `customs.pak.xen` cleanly (rolling your setlist back to that snapshot).
* **Tip:** Save a named `"Main Career"` snapshot first, compile/sync new songs in a single batch, and use BetterGH3's in-game **Unlock All** option after recreating a fresh profile.

---

## Known Limitations
* Modifying custom songs triggers GH3's built-in save reset prompt when keeping newly added tracks (see Career Save Warning above).
* Optional Demucs 6-Stem + Basic Pitch stem separation tools require ~700 MB – 1.2 GB of disk space when installed.

---

## Installation
Download and run **`GH3-Sync-Setup.exe`** from the [Latest Release](https://github.com/tbailey639/GH3-Sync/releases/latest).

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
