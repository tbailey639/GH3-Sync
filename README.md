# 🎸 GH3-Sync | Complete P2P Synchronizer, Friends Network & ONOGAMI Song Creator

**GH3-Sync** is an all-in-one companion desktop application and in-game HUD overlay designed specifically for **BetterGH3 (Guitar Hero III PC)**. It enables players worldwide to synchronize custom songs and setlists in seconds, create new custom songs using the **ONOGAMI Method**, safeguard and restore career save files, manage trusted friends to skip PIN/IP entry, and prevent duplicate downloads.

---

## 🌟 Key Features

### 1. 🖥️ True Native Windows 11 Desktop App (Guitar Hero III: Legends of Rock Theme)
* **100% Native Windows 11 WPF Application:** Zero browser tabs, zero HTML, zero WebView2 — built natively in .NET 10 WPF with Windows 11 rounded dark title bars, system tray minimization, and native file pickers.
* **Custom GH3 Emblem & Animated Intro:** Includes a custom fiery metallic 5-fret emblem for the app, taskbar, installer, and desktop shortcut, plus an animated Star Power 5-fret splash intro!
* **Automatic Startup Update Checker & 1-Click Updater:** Checks GitHub Releases automatically upon opening and includes a live **"Check Updates / Update Now"** button that downloads and installs updates seamlessly.

### 2. ⚡ Bundled Honeycomb Compiler Toolkit (Zero Separate Install Required)
* **Built-in Compilation Engine:** Bundles `GH-Toolkit-Core`, `Honeycomb-GUI.dll`, Neversoft QB byte compilers, `keys.txt`, FFmpeg, and FSB soundbank tools directly inside the installer.
* **Individual Song Deletion:** Every song card in **My Customs** includes a **🗑️ Delete** button that safely backs up career saves, wipes `.pak.xen`, `.fsb.xen`, `.dat.xen`, previews, and optionally deletes the source ONOGAMI project folder & stems!

### 3. 🤖 Demucs 6-Stem (`htdemucs_6s`) + Spotify `Basic Pitch` AI Charting
* **1-Click In-App AI Engine Installer:** Open **AI & Tools Manager** and click **1-Click Install AI Engine** to automatically install portable Python 3.11 + PyTorch + Meta Demucs 6-Stem + Spotify Basic Pitch into `%APPDATA%\GH3-Sync\tools\ai\` — zero Python or terminal commands required!
* **True 6-Stem Guitar Isolation:** `htdemucs_6s` extracts a dedicated **`guitar.wav`** stem (plus `bass`, `drums`, `vocals`, `piano`, `other`) for authentic **mute-on-miss** Guitar Hero III gameplay.
* **Real Musical Pitch-Mapped Charts (No More Repeating Patterns!):** Spotify `Basic Pitch` (plus a built-in pure-C# **FFT Guitar-Bandpass + YIN Pitch** engine for zero-download use) maps real guitar pitches, repeated riffs on the same fret, 2-note power chords, Star Power phrases, and true sustain lengths!

### 5. 👥 Friends, "Trusted Friend" System & Public Internet Connectivity
* **Automatic IP Sharing (Trusted Friends):** When you mark a friend as **Trusted**, their address is automatically discovered and resolved in real time via LAN UDP beacons (port 7374), UPnP / NAT-PMP external mappings, and the Central Admin Hub. No manual IP exchange or PIN entry needed!
* **Public IP & UPnP / NAT-PMP Port Mapping:** Automatically detects your router's public WAN IP and forwards port 7373 via UPnP IGD or NAT-PMP so peers can connect from anywhere on the internet without manual router setup.
* **IP Privacy (Regular Friends):** Untrusted friends' IPs remain strictly private for security; manual IP exchange and session PINs are required until trust is granted.
* **Permanent Unique Friend Code:** Every player receives a permanent unique code (e.g. `GH3-6475-71AF`) with a Show/Hide toggle to prevent spam.

### 6. 🛡️ Career Save Protection & 1-Click Restore
* **Automated Safety Backup:** GH3-Sync automatically creates a timestamped backup of `%USERPROFILE%\Documents\Aspyr\Guitar Hero III\Save` (`s000.d` and `toc.dat`) before *every* P2P sync, import, or song compilation.
* **1-Click Restore:** Visit the **Career Saves** tab anytime and click **"Restore Latest Career Save"** to recover all career progress with customs intact!

### 7. 🔍 Duplicate Song Detection & ONOGAMI Fingerprinting
* **No More Duplicate Downloads:** Automatically compares song IDs, Titles, Artists, and audio fingerprints against peers, auto-unchecking tracks already installed on your PC.

### 8. 📁 1-Click File Import (`.gh3pack`, `.gh3song`, `.sgh`, `.zip`)
* Drag and drop any custom song archive into the **Import Songs** tab to install it straight into your game with header verification and career save protection.

### 9. 🎮 In-Game HUD Overlay
* Transparent, click-through overlay attached to `GH3.exe`.
* Displays `[ACTIVE]`, live transfer progress (`Syncing (1/3): Beat It (45%) [||||....]`), and `[✓ SUCCESS]` notifications during matches.

---

## ⚠️ Feature Evolution & Known Limitations by Version

| Version | Status / Highlights | ⚠️ Known Limitations (What Doesn't Work) |
| :--- | :--- | :--- |
| **Beta 1** | Initial P2P sync, save protection, basic ONOGAMI project creator. | • Local LAN only (no external WAN / router traversal).<br>• No automated compilation (required manual Honeycomb GUI steps).<br>• No peer song file downloads. |
| **Beta 2** | Direct peer song downloads (`.gh3song` / standalone packages). | • Still required external manual Honeycomb compilation.<br>• External connections required manual port forwarding.<br>• Created songs were not automatically verified in-game. |
| **Beta 3** | Automated UPnP / NAT-PMP port mapping, public IP resolution, DDNS support. | • ONOGAMI custom songs still generated uncompiled project folders.<br>• Required clicking into Honeycomb to build `.pak.xen` and `.fsb.xen`. |
| **Beta 4** | Automated headless Honeycomb compilation directly into GH3 (`.pak.xen` + `.fsb.xen`), 1-click in-game ready status, action button fix. | • **No Automatic BPM Detection**: Users had to enter the BPM manually.<br>• **Procedural Repeating Chart**: Auto-generated charts used a fixed rhythmic pattern rather than listening to actual guitar transients.<br>• **Single Mix Audio**: Required multi-track stems uploaded manually to mute guitar on miss.<br>• **Constant BPM Only**: Variable tempo maps / time signature changes not yet detected. |
| **Beta 5** *(Current)* | **True Native Windows 11 WPF App** (zero browser/HTML), **GH3 Legends of Rock Theme**, **Custom Logo & Animated Intro**, **Bundled Compiler**, **1-Click Demucs 6-Stem + Spotify Basic Pitch AI Installer**, **Real Pitch-Mapped Charts**, **Individual Song Deletion**, and **Auto-Updater (checks on open)**. | • First-time AI Engine download requires ~700 MB – 1.2 GB disk space (optional; built-in FFT/YIN pitch charting works out of the box).<br>• Variable mid-song tempo drift map is planned for future updates. |

---

## 📜 Credits & Acknowledgments

GH3-Sync is built with immense gratitude to the open-source community, reverse engineers, and developers whose work powers custom rhythm gaming:

* **[Honeycomb-GUI & GH-Toolkit-Core](https://github.com/AddyMills/Honeycomb-GUI):** Created by **AddyMills** & Neversoft reverse engineers. Powers the headless `.pak.xen` chart compiler, QB bytecode serialization, and CRC checksum tables (`keys.txt`).
* **[Demucs (`htdemucs_6s`)](https://github.com/facebookresearch/demucs):** Created by **Alexandre Défossez et al. (Meta AI Research)**. Powers state-of-the-art 6-stem deep learning stem separation (`guitar.wav`, `bass`, `drums`, `vocals`, `piano`, `other`).
* **[Basic Pitch](https://github.com/spotify/basic-pitch):** Created by **Spotify Audio Intelligence Lab**. Lightweight neural pitch-to-MIDI guitar transcription engine.
* **[BetterGH3](https://github.com/marchellodev/BetterGH3):** Developed by the **BetterGH3 Modding Team**. The premier quality-of-life mod for Guitar Hero III PC.
* **[FFmpeg](https://ffmpeg.org/):** Industry-standard audio downmixing, stem extraction, and raw PCM transient analysis.
* **[FMOD Sound System](https://www.fmod.com/):** FMOD sound engine and FSB3 soundbank assembly utilities.
* **[python-build-standalone](https://github.com/astral-sh/python-build-standalone):** Maintained by **Astral**, powering the 1-click zero-setup portable Python 3.11 AI runtime.
* **[NAudio](https://github.com/naudio/NAudio) & [DryWetMidi](https://github.com/melanchall/drywetmidi):** .NET audio transcoding and MIDI note track serialization.
* **Moonscraper & Reaper Community:** For standardizing `.chart` and `.mid` custom rhythm game formats.

For full license notices, visit **[CREDITS.md](CREDITS.md)**.

---

## 🚀 How to Run & Distribute

### Option A: 1-Click Windows Installer (Recommended)
* Download and run **`GH3-Sync-Setup.exe`** from the [Latest Release](https://github.com/tbailey639/GH3-Sync/releases/latest).
* Automatically installs desktop & start menu shortcuts, BetterGH3 components, and an uninstaller.

### Option B: Standalone Portable (.exe)
* Run `GH3-Sync.exe` in `dist/`.
* Automatically launches the native Windows 11 desktop application window.

---

## ⚖️ License & Terms of Use
This software is licensed under the **PolyForm Noncommercial License 1.0.0**.
* **Allowed:** Free for personal, community, and noncommercial gaming use.
* **Prohibited:** Any commercial use, monetized distribution, or selling of this software in whole or in part without explicit written permission is strictly prohibited.
