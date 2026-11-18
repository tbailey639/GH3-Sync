# GH3-Sync Changelog

Created by **Mad Scientist** ([tbailey639](https://github.com/tbailey639)). All notable milestones from Beta 1 through Official Launch `v1.0.0` are documented below.

---

## [v1.0.0] - 2026-11-18 — Official Launch (Legends of Rock Edition)

### Iconic Guitar Hero III Animated Stage UI & Theme Studio
* **Master UI Animations Toggle (`Enable UI Animations & Stage Effects`):** Instantly toggle all stage effects on or off from `Settings → Appearance & Overlay`.
* **Animated GH3 Rock Meter & Streak Multiplier Ring:** Live Red→Yellow→Green GH3 gauge arc with animated needle in the top header (reacting to BetterGH3/library health & subtly bouncing to preview audio) paired with a dynamic `1x → 4x → ⚡ 8x` Streak Multiplier ring in the sidebar.
* **Rising "Lou's Inferno" Concert Stage Embers & 5-Fret Fretboard Gems:** 22 softly drifting glowing embers & fret sparks behind workspace panels, 5-fret glowing gems on sidebar navigation, flowing 5-fret top shimmer, **Staggered Setlist Card Cascade**, **Spinning Vinyl / Album Disc** + **5-Fret Neon Audio Spectrum Visualizer**, and animated **GH3 Stage Toast Notification Banners**.
* **5 Custom Guitar Hero Stage Themes:** **Dark GH3**, **Light GH3**, **⚡ Star Power (Cyber Blue)**, **🔥 Through the Fire & Flames**, and **🌌 Encore Synthwave**.
* **Power-User Keyboard Shortcuts:** `Ctrl+F` (Search Customs), `F5` (Refresh), `Ctrl+1..8` (Jump Pages), and `Space` (Play/Pause Preview).

### Security Hardening & Privacy Suite
* **Strict External-IP API Lockdown:** External/Internet & proxied tunnel IPs can ONLY ever reach `/api/auth/challenge`, `/api/manifest`, `/api/download/*`, and `/api/download-customs-pak`; all desktop/local routes (`/api/status`, `/api/open-folder`, `/api/settings/*`, `/obs-overlay`, etc.) return `403 Forbidden`.
* **Challenge-Response HMAC-SHA256 P2P Authentication:** Trust Tokens, 4-Hour Expiring Invite Tokens, and Session PINs never cross the wire in plaintext — single-use 90-second nonces prevent replay attacks, protected by unified 5-attempt IP lockout.
* **Per-Transfer Salted AES-256-GCM (`GH3E` v2) & Encrypted `.gh3backup` Profile Backups:** Unique random salt per P2P transfer plus passphrase-encrypted (`PBKDF2-SHA256` 150k + `AES-256-GCM`) profile backup & restore.
* **Settings → 🔒 Security & Privacy Dashboard:** Live security status badges, live Blocked IPs & auth audit log with manual IP ban/unban, **1-Click Rotate All Tokens & PIN**, strict SHA-256 verification on updates & `yt-dlp.exe`, `ArgumentList`-only process execution, and real streaming Zip-Bomb & double-extension/Zip-Slip archive defense.

### Zero-Config Multiplayer, Gameplay & Charting Tools
* **Zero-Config CGNAT / Strict-Router P2P:** Auto-detects **Tailscale** (`100.64.0.0/10`) & **ZeroTier** mesh VPN interfaces plus a **1-Click Cloudflare HTTPS Quick Tunnel (`cloudflared`)** and **4-Hour Expiring Session Invites** for zero port-forwarding sync.
* **Playable Highway Previewer + Co-Op Split-Screen + A→B Solo Looper + Visual Note Editor:** Play Lead or Co-Op Bass tracks using an XInput Guitar Controller or Keyboard (`A S J K L` + `Space`/`Enter`), preview **Lead + Co-Op Bass side-by-side in Split-Screen**, loop A→B sections at `50%–100%` speed, or click lanes in Edit Mode to fix notes & HOPOs.
* **Real `s000.d` Career Save Score Auto-Importer:** Safely scans Guitar Hero III's career save (read-only) on refresh and automatically when `GH3.exe` closes to import real in-game scores & stars into your library and Friend Leaderboard.
* **Streamer OBS "Now Playing" Loopback Browser Overlay:** 1-click copyable local-only transparent animated OBS widget (`http://127.0.0.1:7373/obs-overlay`) with spinning cover art and 5-fret spectrum bars.
* **Venue Customizer & Bulk Metadata Editor:** Pick any GH3 Concert Stage Venue (`Backyard Bash` → `Lou's Inferno`) in Edit Song or batch-update Genre, Year, Charter & Venue across multiple selected songs.
* **1-Click Chorus Encore 30-Second Audio Preview (`▶ 30s`):** Listen to a 30-second song preview directly on Chorus Encore search cards before downloading.
* **Live Guitar Controller Diagnostic Tester**, **Custom GH3 Highway Skin Studio**, **BetterGH3 Live `_log.txt` Verifier & Presets**, **Auto-Fetch Cover Art**, **Batch Volume Soundcheck (`-14 LUFS`)**, **Real-Guitar Practice Stem Exporter**, **Optional `yt-dlp` Importer**, **Encore Roulette (`🎰`)**, **Note Intensity Sparklines**, **Concert Setlist Poster PNG**, **Custom Tags**, and **1-Click Library Auto-Heal**.

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
