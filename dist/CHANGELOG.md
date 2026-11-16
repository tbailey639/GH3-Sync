# 🎸 GH3-Sync Changelog

All notable changes to the GH3-Sync project are documented in this file.

---

## [Beta 4] - 2026-11-15

### 🌟 New Features & Enhancements
* **Automated Honeycomb Compiler Pipeline in ONOGAMI Creator:**
  * Fully integrated the Honeycomb-GUI and GH-Toolkit compilation engines directly into GH3-Sync.
  * Creating a custom song in the ONOGAMI Song Creator can now compile directly into Guitar Hero III (`DATA\SONGS\*_song.pak.xen` and `DATA\MUSIC\*.fsb.xen`) headlessly with 1 click.
  * Added "Auto-Compile into Game" toggle in the creator form.
* **1-Click "⚡ Compile Song" on Any Custom Song:**
  * Added direct compilation button to all custom song cards in the Library, allowing users to compile uncompiled or imported project songs at any time.
* **Automated Chart-to-MIDI Conversion:**
  * Automatic on-the-fly conversion of `.chart` files (both uploaded and ONOGAMI BPM-generated charts) into standard Guitar Hero III MIDI (`notes.mid`) via `GH_Toolkit_Core.MIDI.Chart`, eliminating EOF or manual tool requirements.
* **Dynamic Portable Path Discovery (Universal Multi-PC Architecture):**
  * Eliminated all hardcoded drive letters and directory paths.
  * Dynamically scans all system drives (C:, D:, E:, G:, etc.), User Documents, Desktop, AppData, and game Tools folders to automatically discover Honeycomb-GUI, FFmpeg, and Guitar Hero III.
  * Added configurable Honeycomb Path input and compiler health indicator in the Game Config / Settings dialog.
* **Automated FFmpeg Resolution:**
  * Dynamically checks Honeycomb root, system `PATH`, and package manager paths (WinGet, Chocolatey), automatically ensuring `ffmpeg.exe` is available for audio soundbank encoding.
* **Career Save Safeguards:**
  * Automatically backs up career progression and save files before and after song compilation.
* **Multi-Track Audio Stem Normalization:**
  * Corrected `.ghproj` audio routing to prevent `amix: inputs=0` FFmpeg mixer failures on single-track songs while supporting isolated stems (guitar, rhythm, backing, vocals, drums) when present.

### 🐛 Bug Fixes & Stability
* **Headless Audio Soundbank Compilation**: Fixed WinForms STA thread synchronization deadlock by implementing direct async audio compilation (`CompileAudioStemsAsync`) via `FSB.ConvertToMp3` and `FSB.CombineFSB3File`, reliably generating `.fsb.xen` and `.dat.xen` soundbanks into `DATA/MUSIC/` without hanging.
* **Song Action Buttons with Special Characters**: Fixed `⚡ Open in Honeycomb`, `📁 Open Folder`, and `⚡ Compile Song` action buttons failing to respond on songs with apostrophes or special characters (such as *Bill Withers - Ain't No Sunshine*) by moving from inline JavaScript string templates to array index-based handlers (`openGhprojByIdx`, `openSongFolderByIdx`, `compileSongByIdx`).
* **Direct Honeycomb Executable Launch**: Fixed Windows file association failure when clicking `⚡ Open in Honeycomb` by directly locating and executing `Honeycomb-GUI.exe "{ghprojPath}"`.
* **In-Game Ready Status Badge**: Ensured newly created songs with auto-compile enabled immediately update their checksum ID and display `✓ In-Game Ready` in the library.
* **Target Runtime Modernization**: Updated project target framework to `.NET 10.0-windows`.

---

## [Beta 3] - 2026-11-14

### 🌟 New Features & Enhancements
* **Public IP Reflection & External WAN Connection Support:**
  * Added automated public IP resolution using multi-source IP echo services (`api.ipify.org`, `icanhazip.com`, `ifconfig.me`).
  * Enables players to connect and sync songs across different Wi-Fi networks and across the Internet.
* **Automatic Router Port Mapping (UPnP IGD & NAT-PMP):**
  * Built-in `NatPortMappingService` automatically communicates with home routers via UPnP Internet Gateway Device (IGD) and NAT-PMP (port 5351) protocols to dynamically forward TCP port 7373 without manual router admin login.
* **Custom Public IP / DDNS Hostname Configuration:**
  * Added direct configuration in the dashboard under **🌐 Network / NAT Settings** for users with Dynamic DNS hostnames (e.g., No-IP, DuckDNS) or static public IPs.
* **Interactive Network Status & NAT Monitoring:**
  * Real-time NAT badge display (`✓ Port Mapped (UPnP IGD)`, `Public IP Detected`, or `LAN Only`) on the P2P Sync dashboard with 1-click re-detect/refresh.
* **Heartbeat & Telemetry Upgraded:**
  * Admin Hub signaling now transmits external public IP and port mappings, ensuring remote friends can auto-resolve addresses outside the local network.

### 🐛 Bug Fixes & Stability
* Fixed external peer connections failing due to localhost / internal subnet IP binding (`192.168.x.x` / `10.x.x.x`).
* Fixed Unique Friend Code auto-resolution when peers are located outside the local network.
* **Resolved ONOGAMI Project Discovery & Song List Visibility:**
  * Fixed created ONOGAMI song projects (`.ghproj`, `song.ini`, `notes.chart`, `song.mp3`) not appearing in the dashboard under **All Songs** and **Created by Me (ONOGAMI)**.
  * `GetInstalledCustomSongs()` now automatically scans all created song directories (`GamePath\Custom_Songs`, `Documents\Guitar Hero Songs`, `AppData\GH3-Sync\CreatedSongs`) in addition to compiled `DATA\SONGS` game PAKs.
  * Added direct **📂 Open Folder** button on created songs to immediately open Windows Explorer to the song project folder.
  * Added project folder packaging support to `PackageSong()` and `ValidateAndExtractZip()` so uncompiled project files can be downloaded, exported in `.gh3pack` archives, and synced across friends seamlessly.
  * Replaced hardcoded `G:\` drive paths with dynamic multi-drive and multi-system directory resolution, fixing `"Could not find a part of the path 'G:\Guitar Hero Songs'"` errors on systems without a `G:` drive.

---

## [Beta 2] - 2026-11-14

### 🌟 New Features & Enhancements
* **Direct Song Downloads from Connected Users:**
  * Added direct `⬇️ Download .gh3song` buttons on both **In Sync** and **Missing Locally** songs when connected to a peer.
  * Added `⬇️ Download Selected Files` batch button to download multiple selected song archives simultaneously.
  * New `/api/peer/download-file` endpoint that securely fetches, decrypts (AES-256-GCM), and packages `.gh3song` files on the fly.
* **Automatic Trusted Friend IP Discovery:**
  * Real-time local network UDP beacons (port 7374) and Central Admin Hub signaling automatically resolve live IP addresses for trusted friends with zero manual IP entry or PIN sharing.
  * Selecting a trusted friend from the dropdown automatically populates and verifies their connection.
* **Strict Privacy Isolation for Regular Friends:**
  * Untrusted friends' IP addresses remain strictly protected and isolated on the server.
* **PolyForm Noncommercial License 1.0.0:**
  * Updated licensing to formally protect the project for free community/personal gaming use while prohibiting commercial or monetized re-distribution.
* **Comprehensive 9-Feature Documentation Guide:**
  * Complete interactive user guide integrated directly into the dashboard's "About & Help" tab.

### 🐛 Bug Fixes & Stability
* Resolved a JavaScript variable scope collision in `compareWithFriend()` ensuring seamless page loading and button responsiveness.
* Fixed character encoding on GitHub Releases and project documentation.

---

## [Beta 1] - 2026-11-14

### 🎸 Initial Public Release
* **High-Speed P2P Synchronization:** Connect to opponents or friends and synchronize custom songs and BetterGH3 setlists in seconds.
* **ONOGAMI Song Creator:** Multi-difficulty (Easy, Medium, Hard, Expert) auto-charting rhythm engine + MIDI/chart import with Honeycomb `.ghproj` generation.
* **Career Save Protection & 1-Click Restore:** Automated timestamped backups of `s000.d` and `toc.dat` before every sync/import, preventing career star and score resets.
* **Duplicate Song Prevention:** Cryptographic fingerprinting and metadata matching automatically unchecks already-owned tracks.
* **Universal Game Directory Engine:** Automatic detection of `GH3.exe`, `bettergh3.asi`, and game partitions across any custom installation folder.
* **In-Game Companion HUD:** Real-time on-screen status notifications in Guitar Hero III during gameplay.
* **Integrated Diagnostic Feedback:** Built-in "⚠️ Not Working?" reporting tool for quick troubleshooting.
