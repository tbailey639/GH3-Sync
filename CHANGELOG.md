# GH3-Sync Changelog

Created by **Mad Scientist** ([tbailey639](https://github.com/tbailey639)). All notable milestones from Beta 1 through Official Launch `v1.0.0` are documented below.

---

## [v1.0.0] - 2026-11-18 — Official Launch (Legends of Rock Edition)

### Animated Stage UI Overhaul & Theme Studio
* **Master UI Animations Toggle (`Enable UI Animations & Stage Effects`):** Instantly enable or disable animated concert stage spotlights, flowing 5-fret neon energy shimmer, smooth page & tab slide-in transitions, live status-dot heartbeat pulse, hover lift/glow cards, and the real-time **5-Fret Neon Audio Spectrum Visualizer** in the bottom preview bar.
* **5 Custom Guitar Hero Stage Themes:** **Dark GH3**, **Light GH3**, **⚡ Star Power (Cyber Blue)**, **🔥 Through the Fire & Flames**, and **🌌 Encore Synthwave**.
* **Power-User Keyboard Shortcuts:** `Ctrl+F` (Search Customs), `F5` (Refresh), `Ctrl+1..8` (Jump Pages), and `Space` (Play/Pause Preview).

### Gameplay, Charting & Controller Tools
* **Playable Highway Previewer + A→B Solo Looper:** Play any custom chart directly inside GH3-Sync using an **XInput Guitar Controller** or **Keyboard (`A S J K L` + `Space`/`Enter`)** with hit detection, streak/multiplier, accuracy %, FC tracking, Practice Speed (`50% / 75% / 90% / 100%`), and A→B section looping.
* **Visual Quick-Fix Note Editor:** Switch the Highway Previewer into **Edit Mode** to left-click lanes to add/remove 16th-snapped notes, right-click to toggle HOPO, undo edits, and save/recompile straight into Guitar Hero III.
* **Live Guitar Controller Diagnostic Tester:** Real-time 60 FPS XInput & keyboard diagnostic panel lighting up all 5 frets, Strum Up/Down, Start/Back, Whammy Bar analog meter, and Star Power Tilt axis.
* **Custom GH3 Highway Skin Studio:** Drop any PNG/JPG artwork to auto-format a crisp `512×1024` highway texture with lane guides, safe vault backups, and 1-click default restoration.
* **BetterGH3 Live `_log.txt` Verifier & 1-Click Presets:** Displays live `✓ Loaded (0x...)` verification badges from `PLUGINS\_log.txt`, plus 1-click **Competitive Online** and **Classic 60 FPS Vanilla** plugin presets.

### Library, Audio & Multiplayer Upgrades
* **Auto-Fetch High-Res Album Art & Metadata (iTunes Search API):** 1-click `600×600` cover art, Album, Year, and Genre lookup in My Customs & ONOGAMI Song Creator (zero API keys needed).
* **Batch Library Volume Soundcheck (`-14 LUFS`):** Scans existing customs for quiet/clipping audio and batch-normalizes them to official GH3 loudness.
* **Real-Guitar Practice Stem Exporter:** Exports **75% Pitch-Preserved Slowdown MP3**, **Guitarless Backing Track**, and **Isolated Solo Guitar Stem**.
* **Friend High-Score Leaderboard & FC Tracker:** Displays personal best Stars/Score/FC badges on My Customs cards and compares high scores live with Trusted Friends during P2P library checks.
* **Optional Audio URL Importer (`yt-dlp`):** Managed add-on stored in AppData to grab audio, cover art, and Artist/Title metadata directly from supported URLs.
* **Encore Roulette (`🎰`), Note Intensity Sparklines & Difficulty Tiers, Concert Tour Setlist Poster (`.png`), Custom Colored Song Tags, & 1-Click Library Auto-Heal Scanner.**

---

## [v0.9.0-rc] - 2026-11-18 — Release Candidate
* Added Interactive Mini Highway Chart Previewer with live `±250ms` Sync Offset tuner, 5-second **Instant Re-Chart Cache**, FFmpeg `-14 LUFS` Loudness Normalization & Audio Trim/Fade, **Chart Doctor** auto-fixer, **1-Click Lobby Invite Codes** (`gh3sync://...`) & Saved Setlist Presets, **Discord Rich Presence**, Electric Guitar HUD Size/Opacity & peer-sync strum pulse, **Clone Hero / YARG Full Library Migration Wizard**, **BetterGH3 Quick-Tweaks**, and **Tap Tempo**.

## [v0.8.0-beta] - 2026-11-18 — Beta 8
* Replaced text HUD with transparent per-pixel alpha **Electric Guitar In-Game HUD Overlay** (blue strings when online, silver when offline), fixed Moonscraper `.chart` time-signature `2 ^ den` conversion bug (`Hit The Floor` & 6/8–7/8 charts), added Chorus Encore smart filters & Classic 5-Fret open-note option, interactive editable **Batch Queue**, multi-file Drag-and-Drop, Album Art thumbnails, Duplicate Song Detector, and 6-tab Settings.

## [v0.7.0-beta] - 2026-11-18 — Beta 7
* Introduced **ONOGAMI AI Charting Engine v2** (Smart Multi-Lead Basic Pitch across Guitar/Piano/Other/Bass/Vocals, Librosa precision beat-grid & onset alignment, 2-bar Riff Memory, forced HOPOs, 3-note chords, Co-Op Bass), **Game Audio Mix Modes** (Studio Master / Isolated Lead Stem / Hybrid Blend), and built-in **Chorus Encore Community Chart Search** with pure-C# `.sng` extractor.

## [v0.6.0-beta] - 2026-11-18 — Beta 6
* Added **Silent In-App Auto-Updater** with the Electric Guitar blue-strings progress overlay, fixed shutdown `Mutex` crash, fixed `customs.pak.xen` PAK header re-hashing corruption on song delete/merge, and added automatic header self-repair.

## [v0.5.0-beta] - 2026-11-17 — Beta 5
* Rebuilt GH3-Sync as a native Windows 11 WPF desktop app with Demucs 6-Stem isolation, Spotify Basic Pitch pitch-to-MIDI transcription, dark/light themes, paired Career Save snapshots, and GitHub Releases auto-updater.

## [v0.4.0-beta] - 2026-11-16 — Beta 4
* Integrated bundled Honeycomb compiler & FFmpeg for automated 1-click `.pak.xen` / `.fsb.xen` compilation and direct GH3 installation.

## [v0.3.0-beta] - 2026-11-15 — Beta 3
* Added Public IP resolution, automatic router UPnP / NAT-PMP port mapping, and external Trusted Friend P2P sync.

## [v0.2.0-beta] - 2026-11-14 — Beta 2
* Improved BetterGH3 library detection, song packaging integrity, and P2P sync stability.

## [v0.1.0-beta] - 2026-11-14 — Beta 1
* Initial release of GH3-Sync: Guitar Hero III / BetterGH3 custom song detection and encrypted P2P song synchronization.
