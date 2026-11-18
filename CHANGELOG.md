# GH3-Sync Changelog

Created by **Mad Scientist** ([tbailey639](https://github.com/tbailey639)). All notable changes are documented below.

---

## [v1.3.2] - 2026-11-18

### New Features
* **Silent In-App Auto-Updater with Electric Guitar Progress Bar:**
  - Clicking **Update Now** now downloads, verifies SHA-256 integrity, and applies the update completely silently (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`) using your existing install folder and configuration — no interactive Setup Wizard dialogs appear, and GH3-Sync relaunches automatically once updated.
  - Added a custom **Electric Guitar Update Overlay** featuring a 6-string fretboard where the strings ignite and fill with electric neon blue (`0% → 100%`) as the update downloads and installs, complete with a glowing percentage readout and a collapsible **Show Details** console drawer for verbose upgrade logs.

### Bug Fixes
* **Fixed Shutdown Mutex Crash (`System.ApplicationException: Object synchronization method was called from an unsynchronized block of code`):**
  - Switched the single-instance kernel `Mutex` to `initiallyOwned: false` and safely disposed the handle without cross-thread `ReleaseMutex()` calls after async shutdown continuations, eliminating the error popup when closing GH3-Sync.
* **Fixed `CompilePakGh3 returned false` & `customs.pak.xen` Header Corruption After Deleting/Merging Songs:**
  - Fixed a critical PAK header re-hashing bug in `CustomsPakService.RecompileAndSaveCustomsPak()` where missing `QBDebug\keys.txt` entries caused `dlc_songlist.qb` (`0x2cbadf14`) to be re-hashed into `0xf4301aa6` when deleting or merging a custom song, breaking subsequent Honeycomb compilations.
  - Added automatic `CustomsPakService.RepairCustomsPakIfCorrupted()` self-healing on startup and before every compilation, restoring corrupted `DATA\customs.pak.xen` headers back to `dlc_songlist.qb` (`0x2cbadf14`) while preserving every installed custom song.
  - Invoked `CompileGh3PakFile()` directly with full exception propagation instead of `CompilePakGh3()`'s silent headless dialog catch block, and enabled trailing-comma JSON parsing across all `.ghproj` readers for Honeycomb-saved project files.

---

## [v1.3.1] - 2026-11-18

### Bug Fixes
* **Fixed Honeycomb Compiler Permissions Error (`Access to the path ...\tools\compiler\Backups\GH3 is denied`):**
  - When installed to `C:\Program Files\GH3-Sync`, standard non-elevated user accounts lack write permissions to create `Backups\GH3` in `Program Files`.
  - Added `EnsureWritableCompilerDirectory()` in `HoneycombCompilerService`: automatically detects protected/read-only compiler roots and mirrors the compiler binaries & templates to `%APPDATA%\GH3-Sync\tools\compiler`, where full user write and backup rights are guaranteed without UAC prompts.
  - Added Inno Setup `[Dirs]` rules with `Permissions: users-modify` for `{app}\tools`, `{app}\tools\compiler`, and `{app}\tools\compiler\Backups\GH3`.
* **Fixed In-App Update File-Locking Sharing Violation:** Disposed download FileStream (`dst`) before hash verification and opened checksum reader with `FileShare.ReadWrite | FileShare.Delete` to prevent updater self-locking collisions.

---

## [v1.0.3] - 2026-11-18

### Bug Fixes
* **Fixed In-App Update File-Locking Sharing Violation:** Resolved `The process cannot access the file ... GH3-Sync-Setup-*.exe because it is being used by another process` during 1-click in-app updates. The updater now:
  - Automatically reuses already downloaded and verified setup installers in `Temp\GH3-Sync-Updates` without redundant re-downloads or file handle conflicts.
  - Generates unique timestamped installer paths if the default target file is held open by Windows Defender, antivirus scanners, or background processes.
  - Opens download streams with shared read/write/delete permissions (`FileShare.ReadWrite | FileShare.Delete`) to prevent collisions during active disk writes.
  - Automatically purges stale installer executables older than 24 hours.

---

## [v1.0.2] - 2026-11-18

### Bug Fixes
* **Fixed ONOGAMI Song Creator Auto-Compile & Manual Compile Error (`Value of '0' is not valid for 'Value'`):** Added all required Honeycomb `.ghproj` fields (`songYear`, `coverYear`, `hmxHopoVal`, `previewStart`, `previewEnd`, `artistText`, genre/GH3 source defaults, and clamped tier ranges `1–10`) in `SongCreatorService`, `CloneHeroImportService`, and `HoneycombCompilerService.FixProjectPaths()` so both newly created songs and existing older `.ghproj` projects compile cleanly without crashing WinForms `NumericUpDown` controls.
* **Fixed Multi-Track Demucs Stem Audio Routing:** `HoneycombCompilerService` now discovers isolated stems inside `stems\htdemucs_6s\**\*.wav` (and root project folders) and mixes `drums + vocals + piano + other` as the backing track alongside isolated `guitar.wav` and `bass.wav` for true mute-on-miss gameplay instead of falling back to single-track `song.mp3`.
* **Eliminated ~490 MB Uncompressed WAV Bloat After Compilation:** Automatically deletes the temporary `stems\` folder of raw Demucs WAVs once the encrypted `.fsb.xen` and `.dat.xen` soundbanks are compiled and deployed to `DATA\MUSIC`, and added leftover `stems\` cleanup to **Settings → Clean Duplicate & Temp Files**.

---

## [v1.0.1] - 2026-11-18

### New Features
* **Dedicated Tabbed Settings Page:** Replaced the settings popup with a full sidebar **Settings & About** page organized into **General & Paths**, **Appearance**, **Updates & Compatibility**, and **About, Credits & Logs**.
* **Custom Light & Dark Guitar Hero Themes:** Added instant live theme switching between **Dark GH3 (Legends of Rock Stage)** and **Light GH3 (Backstage Amp & Steel)** with synchronized window chrome and saved preference.
* **Temporary Lobby Session Mode:** Sync friends' custom songs temporarily for an online session, promote favorites permanently with **Keep Song**, and auto-remove temporary files + restore `customs.pak.xen` when `GH3.exe` exits or on **End Session & Clean Up**.
* **Smart `customs.pak.xen` Merging & Auto-Strip:** Imported (`.gh3song` / `.gh3pack`) and P2P synced songs now merge directly into the user's existing `DATA\customs.pak.xen` without overwriting existing customs or breaking an active custom save; deleting a song automatically strips its entry from `customs.pak.xen`.
* **Component Update Checker, 5-Point Compatibility Self-Test & Safe Rollback:** Verify GH3-Sync, Honeycomb, BetterGH3, FFmpeg, and Demucs/Basic Pitch versions, run a 5-point post-update compatibility check, and roll back stem package updates automatically if a check fails.
* **First-Time Save Detection on Startup:** Automatically detects if the one-time `Honeycomb}{Honeycomb` custom save setup has not been completed yet and offers a 1-click vanilla save backup.
* **Trusted Friend Quick-Select Dropdown:** 1-click dropdown in **P2P Sync & Lobby** auto-fills a trusted friend's saved address and Trust Token.
* **1-Click Storage Cleanup & Single-Instance Restore:** Added **Clean Duplicate & Temp Files** in Settings and single-instance IPC so relaunching restores the window from the system tray cleanly.
* **Uninstall Component Choices & `TORCH_HOME` Consolidation:** The uninstaller now asks whether to keep or remove downloaded Stem Tools (`%APPDATA%\GH3-Sync\tools\ai`) and user settings/backups (upgrades silently preserve everything); Demucs model weights now store cleanly inside `%APPDATA%\GH3-Sync\tools\ai\models`.

### Security Hardening
* **Trusted-Friends-Only Song Downloads:** Locked `/api/download/{songId}` and `/api/download-customs-pak` strictly to verified Trusted Friend Tokens (`403 Forbidden` to untrusted PIN peers; PIN peers retain view-only manifest access).
* **Headers-Only Auth & Constant-Time Comparison:** Moved P2P tokens/PINs to `X-GH3-Trust-Token` / `X-GH3-PIN` HTTP headers with `CryptographicOperations.FixedTimeEquals`.
* **SHA-256 Integrity Verification:** Verifies downloaded `GH3-Sync-Setup.exe` installers against GitHub Release SHA-256 digests and validates synced `.pak.xen` / `.fsb.xen` file hashes on install.
* **DPAPI Token Encryption at Rest:** Encrypts stored friend Trust Tokens in `config.json` using Windows DPAPI (`CurrentUser`).
* **Clone Hero ZIP Security Guard:** Enforces uncompressed size limits, file-count caps, Zip-Slip defense, and a strict allowlist blocking executables/scripts.
* **Rate Limiting, Concurrency Caps & Offline P2P Mode:** Added 40 req/min per-IP rate limiting, max 2 concurrent song transfers, an **Online/Offline P2P** toggle, and automatic UPnP port `7373` cleanup on exit or Offline Mode.

### Bug Fixes
* **Fixed Duplicate Song Cards & `"Song"` Title Bug:** Library scanning now reads real Titles, Artists, and Charters directly from `DATA\customs.pak.xen`, links `compiled_song_id` in `song.ini`, and deduplicates normalized titles (such as `Ain't` vs `Aint`) so `song_xxxxxxxx` customs display cleanly with zero duplicates.
* **Eliminated Unnecessary Temp & Duplicate Files:** Stopped copying duplicate 30+ MB `.fsb.xen` / `.pak.xen` / `.dat.xen` files into `Custom_Songs` project folders and auto-cleaned intermediate conversion MP3s after compilation.
* **Accurate Source Attribution:** Preserved `createdWithCreator` metadata on export/import and tagged Clone Hero imports as **Imported (Clone Hero)** instead of **Created Locally**.
* **Program Files Compiler Compatibility:** Routed `GH-Toolkit-Core` `QBDebug` workspace to a writable `%APPDATA%\GH3-Sync\compiler_work` directory when installed in `C:\Program Files\`.

---

## [v1.0.0] - 2026-11-18 (First Official Release)

### New Features
* **Active Lobby Setlist (10-Song Auto-Sync Cap):** Tag up to 10 songs (customizable 1–25) in **My Customs** to auto-sync with friends on connect without overflowing storage, plus a **Browse Full Library** toggle and **100% Lobby Matched** readiness indicator.
* **1-Click Clone Hero / YARG Converter:** Import any Clone Hero song folder or `.zip` (`song.ini`, `notes.chart`/`.mid`, and multi-track `.ogg`/`.mp3`/`.wav` stems) directly into BetterGH3.
* **Dynamic Variable Tempo Maps:** Measure-by-measure beat tracking in `[SyncTrack]` keeps fretboard beat lines, lighting, and band animations synced even on live recordings.
* **Built-In Audio & Guitar Stem Preview Player:** Audition songs or solo isolated `guitar.wav` stems in-app and click **Set Preview Start Here** to pick the 30-second menu preview.
* **Chart Feel & Instrument Focus:** Choose **Casual / Standard / Shredder** chart density and **Guitar / Bass / Vocals** focus when stems are separated.
* **Batch Song Queue:** Queue multiple songs in Song Creator and compile them together in one pass.
* **Edit Song Info & Volume Mixer:** Edit Title, Artist, Genre, and Guitar/Band dB volumes on existing custom songs and recompile with 1 click.
* **Launch GH3 Button, Drag-and-Drop, Favorites, Sorting & Selective Pack Export:** Full library management quality-of-life upgrades.
* **Paired Career Save Profiles:** Snapshots now pair `s000.d` / `toc.dat` with their matching `DATA\customs.pak.xen` so restoring named profiles works without corrupt-save errors, along with clear GH3 save warnings.

### Bug Fixes
* **Fixed Two-PC P2P Sync Encryption:** Resolved AES-256-GCM key derivation mismatch between host and client (`EncryptPayload` / `DecryptPayload` now share the validated session PIN or Trust Token cleanly across machines) and ensured `customs.pak.xen` is decrypted and deployed on sync.
* **Fixed Program Files Startup Permissions:** Routed runtime paths and debug logs to `%APPDATA%\GH3-Sync\`.

### Known Limitations
* Adding/syncing new custom songs modifies `DATA\customs.pak.xen`, causing Guitar Hero III to prompt for a save reset on next launch when keeping newly added tracks (use Paired Career Save Profiles to switch cleanly, or use BetterGH3's in-game Unlock All).
* Optional Demucs 6-Stem + Basic Pitch stem tools require ~700 MB – 1.2 GB when installed.

---

## [Beta 5] - 2026-11-17

### 🌟 Major Native Windows 11 Redesign & AI Charting Engine
* **True Native Windows 11 Desktop App (No Browser / No HTML / No WebView2):**
  * Completely rebuilt the user interface as a 100% native Windows 11 WPF desktop application in .NET 10 with immersive Windows 11 dark rounded title bars, system tray minimization, and native file/folder pickers.
  * Styled with a custom **Guitar Hero III: Legends of Rock** dark obsidian, crimson, molten flame, and 5-fret gem theme.
* **Custom GH3-Sync Logo, Shortcut Icons & Animated Intro:**
  * Designed a custom fiery metallic 5-fret GH3-Sync emblem embedded as the application icon, taskbar icon, system tray icon, Windows Installer icon, and Desktop/Start Menu shortcut icon.
  * Added an animated Guitar Hero III splash intro screen with sequenced Star Power 5-fret highway gems on startup.
* **1-Click In-App AI Engine Installer (Demucs 6-Stem + Spotify Basic Pitch):**
  * Added the **AI & Tools Manager** tab with a **1-Click Install AI Engine** button and live progress bar.
  * Automatically downloads and configures a self-contained portable Python 3.11 runtime + PyTorch + Meta **Demucs 6-Stem (`htdemucs_6s`)** + **Spotify Basic Pitch** into `%APPDATA%\GH3-Sync\tools\ai\` — zero Python or terminal commands required.
  * Extracts a dedicated **`guitar.wav`** stem (plus bass, drums, vocals, piano, other) for authentic **mute-on-miss** multi-track audio in Guitar Hero III.
* **Real Musical Pitch-Mapped Charting (Replaces Repeating 4–5 Note Pattern):**
  * Built `MidiToGh3ChartMapper` + upgraded `AudioAnalysisService` with **Cooley-Tukey FFT guitar-bandpass (130–3200 Hz)** and **YIN pitch detection**.
  * Repeated guitar notes stay on the **same fret**, rising/falling melodies map naturally across Green–Orange, polyphonic strums generate 2-note power chords, and real note durations produce authentic sustains + automatic Star Power & Guitar Solo markers.
* **Complete Bundled Honeycomb Compiler (`tools/compiler`):**
  * Bundled all 112 Honeycomb & GH-Toolkit-Core runtime files, `keys.txt`, blank audio pads, and FFmpeg straight into the installer — zero external Honeycomb installation required!
* **Individual Custom Song Removal (`🗑️ Delete`):**
  * Added a Delete button on every custom song card with a confirmation dialog and optional `[✓] Also delete source ONOGAMI project folder & stems` checkbox.
  * Automatically backs up career saves, protects vanilla GH3 songs, and cleanly wipes `.pak.xen`, `.fsb.xen`, `.dat.xen`, previews, config entries, and project files.
* **Automatic Startup Update Checker & Working Update Button:**
  * Checks GitHub Releases automatically upon opening GH3-Sync and displays a glowing update banner + header button when a newer version is available.
  * Downloads `GH3-Sync-Setup.exe` with a live progress bar, launches the installer cleanly, and exits so upgrades apply seamlessly.
* **Full Credits & Open-Source Attribution:**
  * Added `CREDITS.md`, updated `README.md`, and added an in-app **Credits & Open-Source Acknowledgments** section crediting AddyMills (Honeycomb-GUI / GH-Toolkit-Core), Meta AI Research (Demucs), Spotify (Basic Pitch), BetterGH3 Team, FFmpeg, FMOD, NAudio, DryWetMidi, and the custom charting community.

### ⚠️ Known Limitations (What Doesn't Work Yet)
* First-time download of the optional Demucs 6-Stem + Basic Pitch AI Engine requires ~700 MB – 1.2 GB of disk space and takes a few minutes depending on internet speed (the built-in FFT/YIN pitch charter works immediately without downloading).
* Mid-song variable tempo drift maps (dynamic tempo changes for unquantized live recordings) are planned for a future update.

---

## [Beta 4] - 2026-11-15

### ⚠️ Known Limitations in Beta 4 (Fixed in Beta 5)
* Browser/web-based UI instead of a true native Windows 11 desktop application.
* Auto-generated charts used a fixed repeating 4–5 note pattern instead of real pitch transcription.
* No in-app 1-click installer for Demucs stem separation.
* No individual song delete button or automatic startup updater.

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
