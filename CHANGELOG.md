# GH3-Sync Changelog

Created by **Mad Scientist** ([tbailey639](https://github.com/tbailey639)). All notable milestones from Beta 1 through Official Launch `v1.0.0` are documented below.

---

## [v1.2.2] - 2026-11-18 — P2P Auto-Healing, Tunnel Resiliency & In-Game Multiplayer Sync Notice

### Peer Address Auto-Healing & Real-Time Network Discovery
* **Bi-Directional Address Auto-Healing:** P2P requests now include `X-GH3-Sender-Address` and `X-GH3-Sender-Code` verification headers. When a remote friend connects to you over Cloudflare Quick Tunnels or LAN, your client automatically heals and updates their stored connection address without manual re-entry.
* **Strict APIPA Link-Local IP Filtering (`169.254.*` Elimination):** Excluded disconnected/virtual adapter link-local IP addresses (`169.254.*`) from network discovery and public address resolution. Friends will never receive or get blocked by invalid `169.254.*` addresses.
* **Cold-Start Tunnel Resiliency & Extended Timeouts:** Increased friend discovery and handshake timeouts from 3.5 seconds to 15–20 seconds with automatic warm-up retries, ensuring Cloudflare edge tunnels establish reliable connections even on first spin-up.
* **Universal Lobby Invite Code Paste:** The `Paste Invite Code` button on `P2P Sync & Lobby` now accepts all formats (`gh3sync://`, `GH3-XXXX-XXXX@https://...`, `https://...`, or IP:port), auto-associates known friends, loads their mutual trust token, and compares libraries in 1 click.
* **In-Game Multiplayer Pre-Flight Warning Banner:** Added a clear warning on the `P2P Sync & Lobby` screen explaining that in Guitar Hero III online multiplayer, both players must have identical custom song files installed before picking them in-game to prevent loading screen hangs and auto-playing bots.

### Windows 11 Shell & Process Management Fixes
* **Eliminated System Tray Freezes:** Converted system tray icon dispatcher calls to asynchronous non-blocking invocations, eliminating UI thread freezes on Windows 11 when interacting with notifications or the tray icon.
* **Clean App Exit & Orphan Process Cleanup:** Clicking `[X]` now cleanly exits the application and immediately kills any orphaned background `cloudflared.exe` processes, ensuring port 7373 is cleanly released between sessions.

---

## [v1.2.1] - 2026-11-18 — Zero-Config Internet Friend Sync & Self-Contained Release

### 1-Click Internet Friend Invites & Seamless WAN Connection
* **1-Click Online Friend Invites (`📋 Copy Online Invite`):** When playing with friends across the internet (different houses/Wi-Fi), clicking "📋 Copy Online Invite" automatically pairs your Friend Code with your active Cloudflare HTTPS Tunnel address (`GH3-XXXX-XXXX@https://xxxx.trycloudflare.com`) or mesh address. Send this link to your friend on Discord, iMessage, or text.
* **1-Click HTTPS Internet Tunnel Directly on Friends Page:** Added dedicated Internet Sync status card and **"🌐 Start Free Internet Tunnel"** directly onto the `Friends & Trust` tab. Zero router port-forwarding or network hassle required.
* **1-Click Clipboard Paste (`📋 Paste Invite`):** Added a 1-click paste button to the Add Friend bar that unpacks friends' online invite links (`GH3-XXXX-XXXX@https://...`), `gh3sync://` base64 invite strings, or raw codes, and triggers instant profile detection.
* **1-Click "⚡ Sync Songs" Directly on Friend Cards:** Every friend card in `Active Friends` now features a prominent **"⚡ Sync Songs"** button. Clicking it instantly switches to `P2P Sync & Lobby`, populates the friend's address and mutual trust token, and triggers library comparison in 1 click!
* **Automatic Friend Request Acceptance Polling:** When you send an outgoing friend request over the internet, GH3-Sync automatically checks when your friend clicks "✓ Accept & Trust", auto-promoting them to an active mutual friend and playing the Star Power chime with zero manual refreshing.
* **Intelligent WAN Connection Guidance:** If a bare Friend Code (`GH3-XXXX-XXXX`) without an address is entered and not found on LAN, GH3-Sync provides clear, actionable instructions on using the Free Internet Tunnel and Online Invite links.

### Zero Prerequisites / Self-Contained Deployment
* **Fully Self-Contained Single-File Package:** Bundles all .NET 10 Desktop and ASP.NET Core runtimes directly inside `GH3-Sync.exe`. Friends never need to install or download any .NET runtimes manually.
* **Lzma2 High-Compression Installer:** Packaged with Inno Setup with ultra-compression for fast downloads.

---

## [v1.2.0] - 2026-11-18 — Verified P2P Friends & Handshake Edition

### Automatic Username Detection & Handshake Delivery Confirmation
* **Zero-Input Auto-Detection of Username from Unique ID:** Friend Codes (`GH3-XXXX-XXXX`) are automatically resolved in real time across **LAN broadcast beacons (`PeerDiscoveryService`)**, **Central Admin Hub (`/api/admin/resolve-friend`)**, and **direct P2P profile queries (`/api/profile`)**. Typing or pasting a Friend Code immediately previews the friend's actual username and live online status with zero manual nickname typing.
* **Bi-Directional P2P Delivery Handshake:** When adding a friend, GH3-Sync dispatches an actual network handshake request (`POST /api/friends/request`) to the friend's client. The remote client acknowledges receipt with HTTP 200 OK.
* **Strict Receipt Confirmation Requirement:** GH3-Sync verifies that the friend request was actually received by the friend's client before adding them to the Outgoing Requested list. If the peer is unreachable or offline, the request is **NOT** added to the requested list, and a clear diagnostic alert explains why.
* **Incoming Friend Request Approval System:** When a friend sends a request, the recipient receives a live animated **GH3 Stage Toast notification** (`"📩 NEW FRIEND REQUEST!"`) and Star Power audio chime. In the upgraded `Friends & Trusted Network` page, incoming requests appear in a dedicated section with 1-click **Accept & Trust** (generating mutual DPAPI-encrypted 32-character Trust Tokens for custom song downloads & setlist auto-sync), **Accept (View Only)**, or **Decline**.
* **NAT Firewall Relay & Admin Hub Queuing Fallback:** If two players are behind restrictive firewalls/NAT without port forwarding or tunnels, requests automatically queue on the Admin Hub and deliver on the friend's next client heartbeat.
* **External P2P Security Route Whitelist:** Updated the zero-trust external IP lockdown to whitelist `/api/profile`, `/api/friends/request`, `/api/friends/accept`, `/api/friends/decline`, and `/api/friends/check-status`, allowing direct P2P friend connections while maintaining localhost DNS-rebinding and CSRF shields.

---

## [v1.1.0] - 2026-11-18 — Encore & Zero-Trust Edition

### Universal Plug-and-Play Controller Support (DualSense Edge • PlayStation • Xbox • Guitar)
* **Zero-Driver Multi-Controller Engine (`UnifiedControllerService`):** Instant plug-and-play support for **Sony DualSense Edge (PS5)**, **DualSense**, **DualShock 4**, **Xbox 360 / One / Series**, and **USB / XInput Guitar Controllers** — combining XInput + native Windows HID (`winmm`) with zero external drivers required.
* **Official Guitar Hero III Mappings Out of the Box:** Automatic Gamepad Mode (`L2 / L1 / R1 / R2 / ✕` on PlayStation, `LT / LB / RB / RT / A` on Xbox) with **Official GH3 Auto-Strum on Fret Press**, plus Official GH3 Guitar Mode (`A / B / Y / X / LB` + Strum Bar) and **1-click custom back-paddle / keyboard keybinding**!
* **Upgraded Live Controller Diagnostic Tester:** Displays detected controller name (`DualSense Edge (PS5)`, etc.), live 5-fret lights, L2/R2 analog trigger bars, Whammy axis, Star Power, and click-to-bind custom paddle remapping.

### Authentic GH3 Gameplay, Studio Pro Tools & Real Audio Reactivity
* **Upgraded Playable Highway Previewer:** Working **Star Power Meter & `8x` Activation** (`Create / Share / Back / Right Shift`), live **Mini Rock Meter**, **4 Note-Gem Palettes** (`Classic GH3`, `Colorblind High-Contrast`, `Synthwave Neon`, `Metallic Chrome`), **Post-Song `"★ YOU ROCK!"` Results Overlay**, and 1-click **Custom Keyboard Fret Remapper**.
* **1-Click Playable Solo Extractor (`✂️ Build Solo Cut`):** Slice any song section or `A→B` loop into its own standalone playable `[SOLO]` custom song (with 2-second count-in + fade-out) compiled straight into BetterGH3!
* **1-Click Concert Medley Builder (`🎸 Medley`):** Crossfade 2–4 checked custom songs and stitch their `.chart` tempo maps & note tracks seamlessly into one continuous Live Concert Medley.
* **Real Audio Waveform Scrubber & Beat-Reactive Stage:** Extracts real FFmpeg PCM amplitude envelopes so the bottom Audio Player draws a **clickable Waveform Scrubber** (+ inline Volume Slider & Mute) while the **GH3 Rock Meter** and **5-Fret Spectrum Visualizer** react to real music beats!
* **Smart Difficulty Scaler & Reverse Export to Clone Hero / YARG (`.zip`):** Generate progressive Easy/Medium/Hard tracks on any song or export your ONOGAMI customs as clean Clone Hero / YARG `.zip` packages.
* **GH3 Audio/Video Lag Calibration Mini-Game (`🎯 Calibrate`), Chart Quality Scorecard & 8-Tier Venue Tour Ranking (`⚖️ Scorecard`), `Ctrl+K` Spotlight Quick-Command Palette, Compact Setlist View + Artist/GH3-Tour Grouping, Streamer `!sr` Song Request Queue, Discord Setlist Exporter, Multiplayer Best-of-3 Setlist Draft, Tour Stats & Trophies Wall (`🏆 Trophies`), Synthesized GH3 Sound Effects, & 100ms Pre-Flight Launch Check.**

### Zero-Trust Security Upgrades
* **1-Click `👁 Streamer Privacy Mode`:** Instantly masks Public IP, Mesh IP, Tunnel URL, Session PIN, Invite Codes & Friend Codes across every screen for safe Twitch/YouTube/Discord streaming.
* **Localhost DNS-Rebinding & Browser CSRF Shield:** Enforces strict local `Host` header allowlist, blocks cross-origin browser `Origin` requests to local APIs, and removes legacy `?pin=` / `?token=` query-string fallbacks.
* **Mutual Host Identity Verification (ECDSA P-256 Signing + SSH-Style TOFU Fingerprint Pinning):** Every `/api/manifest` is cryptographically signed by a persistent DPAPI-protected Host ECDSA keypair; unexpected host fingerprint changes trigger an immediate tamper warning.
* **Authenticode Signature Verification on `cloudflared.exe`:** Verifies official `Cloudflare, Inc.` Windows Authenticode certificate chain before launching HTTPS Quick Tunnels.
* **Invite-Link SSRF Guard + Pre-Connect Confirmation Card**, **Neversoft `.pak.xen` / FMOD `.fsb.xen` Binary Magic-Byte Validation**, **Optional 1-Click Host Approval Popup for Friend Downloads**, and **1-Click `🛡️ Run Live Security Self-Test` (`A+ HARDENED`)** inside `Settings → 🔒 Security & Privacy`.

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
