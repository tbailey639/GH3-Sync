# GH3-Sync Changelog

Created by **Mad Scientist** ([tbailey639](https://github.com/tbailey639)). Notable major milestones and roadmap releases are documented below.

---

## [v1.5.0] - 2026-11-20 — P2P Setlist Notebook, Permanent Song Keeping & Soundboard Hype Edition

### 🎸 Guitar Hero III Authentic Setlist Notebook UI
* **Authentic Setlist Notebook Design:** Rebuilt the P2P Lobby setlist browser to match the iconic Guitar Hero III setlist notebook interface. Features rich wooden table styling, cream parchment paper with red vertical margin rules and blue ruled lines, metallic paperclip badges, red stenciled rock tier headers, 5-star ratings, high score displays, and bottom arcade controller navigation pills (`UP/DOWN`, `SELECT: PREVIEW / SYNC`, `BACK: END SYNC`, `BONUS: KEEP SONG`).
* **Active Track Highlight:** Highlighted tracks glow in burnt amber/gold marker ink (`#B45309`) with venue badges held by metallic paperclips.

### ⭐ Dedicated Permanent Song Keeping & Safe Pre-Sync Restore
* **Per-Song Permanent Keep Action:** Added a dedicated `⭐ Keep Forever` button directly on each synced song row. Once clicked, kept songs are permanently added to your library and will never be removed on session exit or disconnect.
* **Regular Friends vs. Trusted Friends Distinction:** Regular friends can sync and play songs temporarily together in lobby sessions. Permanent keeping is exclusive to Trusted Friends, with locked regular-friend tooltips prompting trust upgrades.
* **Always-Temporary Lobby Sessions:** Removed manual temporary mode checkbox; all multiplayer lobby sync sessions are safely temporary by default.
* **Pre-Sync Snapshot & Post-Disconnect Restoration:** Automatically takes an immutable backup of `customs.pak.xen` before any lobby sync. Disconnecting or clicking `⏹ End Lobby Sync` cleanly restores original customs while seamlessly preserving all permanently kept songs.
* **Automated Integrity Reconciliation:** Runs an integrity audit on application boot, lobby disconnect, and GH3 game launch to verify only original + permanently kept songs exist in your library.

### 📢 Dual-Transport Lobby Hype Soundboard
* **Multiplayer Soundboard SFX:** Fixed soundboard playback and peer broadcasting. Users can fire Airhorn, Stadium Cheer, Boo, Drum Fill, Lightning, and Solo Lick cues in real-time.
* **Direct P2P + Global Signaling Relay:** Audio cues broadcast directly over HTTP (`/api/lobby/hype-sfx`) with instant fallback through the ntfy.sh signaling relay, guaranteeing sound effects deliver across any NAT or firewall.
* **Synthesized Retro Rock Audio:** Custom in-memory PCM audio synthesizers generate punchy stadium sound cues with zero external dependencies.

### ⚡ Startup White-Screen Flicker Elimination & Network Watchdog
* **Smooth Splash Transition:** MainWindow now initializes with dark `#090A0F` background and smoothly renders its composition frame before splash fadeout, completely eliminating the white screen startup hang.
* **Accurate Online/Offline Status:** Fixed offline friends falsely showing "⚪ Online". Inactive peers display "🔴 Offline" and active peers display "🟢 Online" with real-time ping latency.
* **Cloudflare Tunnel Watchdog:** Automatically monitors Cloudflare Quick Tunnel processes, restarting dropped tunnels and re-broadcasting live presence to peer networks.

---

## [v1.4.4] - 2026-11-19 — Thread Deadlock Immunity & High-Reliability Signaling Edition

### 🛡️ Thread Deadlock Immunity (Open Calls Pattern)
* **Eliminated Background/UI Dispatcher Lock Contention:** Decoupled internal synchronization locking (`_lock`) from public event dispatching in `FriendService`. Events `OnFriendRequestReceived` and `OnFriendRequestAccepted` now fire strictly outside `_lock`, completely preventing circular lock contention between background network worker threads and the WPF UI Dispatcher.
* **Non-Blocking Asynchronous UI Dispatching (`Dispatcher.BeginInvoke`):** Switched all background signaling and P2P notification handlers in `MainWindow.xaml.cs` to asynchronous queueing (`Dispatcher.BeginInvoke`). Background worker loops no longer halt waiting for the UI thread, preventing app freezes when friend requests are received during active gameplay or startup.
* **Smart Event Deduping:** Hardened `ReceiveFriendRequest` and `ReceiveTrustUpgradeRequest` so incoming request notifications and chime sound effects only fire when a request is genuinely new or re-opened, eliminating redundant UI resets on repeated signaling poll cycles.

---

## [v1.4.3] - 2026-11-19 — Instant IPv4 Fast-Path P2P Relay & Live Discovery Engine

### ⚡ Instant IPv4 Fast-Path Socket Handler
* **Eliminated Residential IPv6 Dead-Route Blackhole:** Replaced default .NET `HttpClient` with a custom `SocketsHttpHandler` configured with IPv4 DNS priority. Bypasses residential ISP IPv6 routing dead-ends that previously caused 21-second TCP SYN hangs and canceled background signaling requests.
* **Sub-50ms Global Rendezvous:** Heartbeat publishing, incoming friend request polling, and peer address lookups now execute in under 30ms (previously timed out at 4,000ms).

### 🌐 Tunnel-Independent Online Presence
* **Instant Online Status on App Launch:** GH3-Sync now publishes presence heartbeats immediately upon application startup—even while the Cloudflare Internet Tunnel is negotiating in the background.
* **Graceful Peer Detection:** Players are detected online instantly upon typing their Friend Code in the UI without waiting for tunnel URLs or displaying premature offline warnings.

---

## [v1.4.2] - 2026-11-19 — Xbox-Style Asynchronous Friends & Authentic Arena Concert Crowd Choir

### 🎮 True Xbox-Style Asynchronous Friend Requests
* **Guaranteed Outgoing Request Persistence:** Entering any valid Friend Code (e.g. `5126-A893`) immediately registers an Outgoing Pending Request in your profile without hard-failing if the recipient is currently offline or away.
* **Offline-Tolerant P2P Relay Queue:** Friend requests are securely queued on the recipient's persistent signaling topic (`gh3sync-req-{targetCode}`) and re-broadcast automatically by the background signaling loop until delivered.
* **Signaling Relay Rate-Limit Immunity:** Relaxed signaling heartbeat and poll cadence from 6s to 16s with staggered peer updates, eliminating HTTP 429 rate limits on ntfy.sh.
* **Streamlined Friends UI:** Outgoing requests are cleanly surfaced under `Sent Requests (Awaiting Friend Acceptance)` with instant status updates and a 1-click cancel option.

### 🏟️ Authentic Arena Concert Crowd Choir (Option 1 - Studio-Grade Multi-Tracks)
* **Zero Comb-Filter Distortion:** Replaced full-mix audio filtering with official Guitar Hero World Tour isolated concert multi-track crowd soundbanks (`stadium_crowd_bed.mp3`), delivering pristine 48kHz stereo arena applause, singing, and hype cheering.
* **Star Power Dropout Immunity:** Hardened in-game QB bytecode monitor (`script_override.q`) so the stadium crowd choir **never drops out during active Star Power**, even if notes are missed.
* **Streak-Responsive Crowd Dynamics:** Crowd choir automatically drops out when Star Power is inactive and note streak drops below 4x (`multiplier < 4`), exploding back into full arena sing-along volume at 4x and massive roar during Star Power.

---

## [v1.4.1] - 2026-11-19 — Instant P2P Signaling & Concert Crowd Master

### 🚀 Instant P2P Friend Rendezvous & Signaling Relay Fix
* **ntfy.sh Time-Window Stream Filter (`since=10m`):** Eliminates 40+ second chunked stream buffering delays by querying only active heartbeats within the past 10 minutes, cutting friend resolution latency down to ~300ms.
* **Prioritized Signaling Pipeline:** Reordered peer resolution to query the Global Signaling Relay immediately after self-detection, preventing 2-second localhost AdminHub timeouts.
* **Auto-Fallback on Tunnel Rotation & HTTP 502/530:** Automatically falls back to the Global P2P Signaling Relay if direct HTTP encounters stale Cloudflare tunnels or network errors, ensuring friend requests and acknowledgments deliver reliably across different networks.

### 🎤 Dynamic Concert Stadium Crowd Choir (Option A)
* **Channel 1 Rhythm Buss Injection:** Seamlessly bakes authentic stadium crowd singing and hype chants directly into the song's audio package during compilation.
* **In-Game QB Script Modulation:** Real-time volume crossfade triggers the stadium crowd choir once the player reaches a 4x multiplier (10+ note streak) and explodes in volume during Star Power activation.

### 🎨 UI & Layout Refinements
* **Condensed Song Actions:** Replaced crowded row buttons with a clean 3-dot context menu for secondary actions.
* **Batch Selection Bar:** Select All and multi-item deletion/re-charting support in My Customs.
* **Multi-Cover Studio Clear Button:** Dedicated reset button to wipe artwork and reload variations.
* **Zero-Trust Security Status Clean-up:** Removed redundant debug badges for a sleek, authentic Guitar Hero aesthetic.

---

## [v1.4.0] - 2026-11-19 — The Concert Master Edition

### 🎯 Bulletproof Cover Art & Metadata Engine
* **Smart Filename & YouTube Title Sanitizer:** Automatically strips clutter (`[Official Video]`, `(Remastered 2021)`, `[4K]`, `[FLAC]`, `(Audio)`, `[Lyrics]`, track numbers `01 - `) to isolate clean `{Artist} - {Title}`.
* **Local Embedded Tag Inspection:** Reads existing ID3v2, Vorbis, and FLAC headers directly from dropped audio files before querying the web for instant 100% accuracy.
* **Multi-Provider Web Cascade:** Tiered search using Deezer Public API ($1000\times 1000$ HD art, lenient search, zero API key), iTunes Search API ($600\times 600$ art), and MusicBrainz / Cover Art Archive.
* **Interactive 3-Cover Visual Carousel:** Displays 3 selectable artwork thumbnails under the song creator card when multiple covers exist.

### 📊 Universal "Stage HUD" Progress System (All 8 Screens)
* **Zero Silent Freezes:** Every operation lasting over 150ms displays active, non-blocking visual feedback.
* **2-Line Explanatory Readout:** Plain-English explanation of the active step + animated percentage.
* **Animated 5-Fret Highway Wave:** Visual 🟢 🔴 🟡 🔵 🟠 fret gems pulse along the progress bar to show continuous engine activity.
* **Full Coverage:** Active across My Customs, ONOGAMI Creator, Tools & Stems, Friends & Trust, P2P Sync & Lobby, Import & Convert, Career Saves, and Settings & Updates.

### 🎸 Custom Guitar Hero Rock Dialogs & Victory Showcase
* **Eliminate Standard Windows Alert Boxes:** Replaced all Win32 silver message boxes with the custom `GuitarHeroModalDialog` suite (Dark Obsidian, theme glow border, rock emblem header, `BackEase` scale bounce animation).
* **Authentic Audio Cues:** Star Power chime/power chord on success, note-miss clunk on warnings, fret snap on confirmations.
* **Encore Song Complete Victory Showcase:** Post-creation screen featuring spinning vinyl album art, metadata, BPM, note count, 4 difficulty badges, Soundcheck preview button, and 1-click launch or setlist addition.

### ⚡ Dynamic Stage Visuals, Animations & Rock Hardware
* **Moving Ambient Stage Lights:** Background canvas with slow-sweeping radial spotlights creating stadium stage ambiance.
* **Floating Theme Particles:** Floating ember/dust particle effects (orange embers on Fire & Flames, cyber blue sparks on Star Power, violet dust on Synthwave).
* **Reactive LED VU Meters:** Real-time green-yellow-red LED volume bounce bars that react dynamically to audio playback during Soundcheck previews.
* **Skeuomorphic Rock Hardware:** Vintage Marshall/Fender chrome amp toggle switches for checkboxes, rotary dials (numbered 1 to 11!) for volume/sliders, and live animated gradient borders.

### 🎵 Studio-Grade Audio & Stem Mastery
* **Automatic Loudness Normalization (EBU R128 / ReplayGain):** Automatically normalizes all compiled audio to standard broadcast loudness (-14 LUFS), matching vanilla GH3 and Spotify/YouTube standards.
* **True Multi-Track Muting on Miss:** Channels 1 & 2 (Guitar stem) mute when notes are missed, Channels 3 & 4 (Backing/Bass/Drums/Vocals) continue rocking uninterrupted, Channels 5 & 6 provide crowd ambience.
* **Co-Op Bass / Rhythm Auto-Charting:** Automatically analyzes the isolated bass stem to generate a playable Co-Op Bass / Rhythm track for 2-player P2P multiplayer.
* **Stems Export / Karaoke Mode:** Export isolated stems (`guitar.wav`, `drums.wav`, `vocals.wav`, `bass.wav`) for practice or live backing tracks.

### 🎼 Neversoft-Accurate Charting & AI Polish
* **Dynamic HOPO Flow Optimizer:** Automatically analyzes pitch intervals (< 170ms / 16th notes) and marks them as natural hammer-ons/pull-offs for fluid solo playability.
* **Intelligent Power-Chord & Dual-Fret Detection:** Detects root and 5th harmonics on heavy distortion downbeats to generate authentic 2-fret power chords (🟢+🔴, 🔴+🟡, etc.).
* **Musical Star Power Placement:** Places Star Power phrases on musical hooks, drum fills, and pre-choruses directly preceding difficult guitar solos.
* **Stem Separation Cache:** Caches separated Demucs stems by song hash in `tools/cache` so re-charting or adjusting difficulty takes under 2 seconds.

### 🤝 Social, Tournaments & Competitive Multiplayer
* **Asymmetric Head-to-Head Rival Scoreboard:** Compares your career score against your friend's score directly on song cards with percentage accuracy, max streak, and point deficits.
* **"Rock Tour" Tournament Bracket Generator:** Built-in 2-to-8 player tournament organizer featuring double-elimination brackets, shared setlist seeds, and automatic advancement based on `s000.d` save file results.
* **Live Lobby Hype Soundboard:** Clickable rock sound bites in the P2P Lobby (feedback squeal, drum roll, crowd roar, ready chime) that play on both players' machines in real time.

### 🎮 Hardware Calibration & Windows 11 Optimization
* **Sub-Millisecond Controller Polling Tester:** Detects USB polling rates (125Hz, 500Hz, 1000Hz) with configuration tips for Raphnet, Ardwiino, and Santroller adapters.
* **Interactive Strum Sync Calibrator:** Interactive highway metronome allowing you to strum your real controller to calculate display/audio lag and auto-patch the offset into BetterGH3.
* **Windows 11 Gaming Mode (0.5ms Timer Resolution Boost):** Automatically requests Windows 0.5ms high-precision multimedia timer resolution and sets High process priority when launching `GH3.exe`.
* **Hardware Anti-Overstrum Debounce Filter:** Configurable software debounce delay (15ms, 25ms, 35ms, or Off) to eliminate switch-vibration double strums on older Gibson Les Paul / Kramer guitars.
* **Whammy & Tilt Sensor Deadzone Calibration:** Fine-tune the return deadzone for whammy bars and accelerometer sensitivity for Star Power tilt.
* **1-Click Gamepad "Instant Jam" Mode:** Default controller layout for casual friends playing on Xbox, PlayStation, or DualSense pads without guitar controllers.

### 🛡️ Advanced Security & Zero-Trust Shield
* **Sonic Quarantine (Zero-Trust Archive Scanner):** Pre-scans all imported ZIP, SNG, and P2P packages with strict extension allowlists, magic-byte binary header validation, and anti-zip-slip protection to destroy any hidden executables or scripts.
* **Engine Vaccine (Anti-Tamper Hash Guard):** Keeps a cryptographic SHA-256 fingerprint baseline of clean BetterGH3 core files (`core.dll`, `binkw32.dll`, base paks) with 1-click automatic restoration if modified or corrupted.
* **Emergency Air-Gap Panic Switch:** One-click header button or hotkey (`Ctrl+Shift+X`) that instantly kills the Cloudflare tunnel, drops all P2P connections, rotates trust tokens, and locks the app into offline mode.
* **Port Stealth & Silent Blackhole:** Drops any network request lacking a valid HMAC handshake signature with zero bytes returned, making port 7373 appear closed and invisible to scanners.
* **Charter Cryptographic Signatures (`.gh3sig`):** Cryptographically signs original charts with the creator's key so songs display verified original badges and detect unauthorized modifications.

### 🌟 Exclusive Features GH3 Never Had
* **AI Stadium Sing-Along & Chant Engine:** Synthesizes stadium crowd voices singing along to your guitar lead melody in audio channels 5 & 6 when rocking high streaks.
* **Boss Battle Duel Creator:** Converts ANY custom track into a 1v1 Battle Mode duel with battle attacks (Broken String, Whammy Overload, Amp Overload, Lefty Flip) against AI or P2P friends.
* **"Trade-Off Solo" Asymmetric Co-Op:** Slices solos back and forth between Player 1 and Player 2 for realistic band duals.
* **Dynamic Venue & Highway Themes:** Auto-applies matching visual highways (Inferno Fire for metal, Neon Grid for synthwave, Star Power Nebula for ballads).
* **Custom Career Campaign Builder:** Build full custom world tours with named venues, custom song tiers, encores, and boss battles.
* **AI Guitar Coach & Miss Heatmap:** Reads career saves to map where you missed notes and generates a 10-second practice loop in the Highway Previewer at 50%–100% speed.
* **"Endless Rock Concert" Seamless Medleys:** Crossfades 3 to 6 songs with live drum fills and aligned beat grids for uninterrupted 15-minute rock party marathons.
* **AI Lyric Transcription to Section Markers:** Automatically transcribes lyrics via vocal stem speech-to-text and injects them as in-game teleprompter markers.
* **Tour Poster Merch Studio:** Customizes setlist posters with selectable artistic themes (70s Psychedelic, 80s Hair Metal, 90s Grunge Flyer, Modern Festival).
* **Offline Local LAN UDP Auto-Discovery:** Broadcasts beacons on the local subnet (`255.255.255.255:7373`) for zero-internet dorm room or LAN party play.
* **In-App Rock Hall of Fame Achievements:** Unlockable trophies and milestone achievements for charting, streaks, and multiplayer tours.

### 🌐 Internationalization (8 Supported Languages)
* **8-Language Localization Suite:** English (Default), Español, Português (Brasil), Français, Deutsch, Italiano, 日本語, and 한국어.
* **Runtime Live Switching & GH3 Language Auto-Match:** Instant language changes without restarting, with optional auto-detection of your BetterGH3 game language file.

### 🧹 Housekeeping & Maintenance
* **1-Click Smart Cache Cleaner:** Frees up gigabytes of disk space by clearing temporary stems and transit files while keeping custom songs safe.
* **1-Click Portable Library Vault (`.gh3vault`):** One-click backup and restore of your complete custom library, high scores, presets, and settings.
* **Dynamic Version Display Everywhere:** Startup splash screen, header badge, and credits synchronize dynamically with `UpdateService.CurrentAppVersion`.

---

## [v1.3.0] - 2026-11-19 — The Global Multiplayer & Synchronized Lobby Edition
* **Synchronized Match Ready-Up & 3-2-1 Rock Launch Countdown:** Both players ready up in the P2P Lobby and launch into `GH3.exe` simultaneously with an authentic visual/audio countdown.
* **Synchronized Party Roulette (Co-Op Song Draft):** 1-Click co-op song draft picks identical random tracks on both players' machines using a shared random seed.
* **Live Friend Activity Status & Rich Presence:** Real-time activity broadcasts (🎸 Playing GH3, 🟢 In Lobby, ⚪ Online) with live status badges.
* **Player Showcase Song / Signature Track:** Select your custom signature track in your profile; friends can listen to a 25-second audio preview directly in the UI.
* **Connection Quality & Ping Indicator:** Asynchronous ping measurement displays real-time color-coded latency indicators (🟢 <60ms, 🟡 60-140ms, 🟠 >140ms).
* **Instant 'Compare Missing' Quick Filter:** Checkbox to hide installed tracks and show only missing songs to sync in 1 click.
* **Dual-Intent Friends & In-App Mutual Trust Upgrade:** Choose between Add Friend (view setlist) and Add Trusted Friend (1-click song sync), with 1-click in-app trust upgrades.
* **Always-On Silent Background Internet Sync:** Automatically runs background Cloudflare Quick Tunnel and Global Signaling Relay (`ntfy.sh`) for zero-port-forwarding connectivity across different houses and Wi-Fi networks.
* **Lobby Setlist Pre-Flight Auto-Heal & Theme Fallback Immunity:** Pre-launch validation ensures all 10 lobby songs have valid audio and pak assets. Universal `BgCardAlt` and `BorderDefault` brush definitions eliminate UI resource exceptions.

---

## [v1.2.0] - 2026-11-18 — Zero-Trust P2P Security & Network Edition
* **Verified P2P Friends Handshake & Bi-Directional Delivery:** Authenticated friend requests with mutual delivery receipts.
* **Zero-Input Auto-Detection of Username from Friend Code:** Friend codes immediately preview remote username and online status across LAN beacons, central signaling, and direct P2P.
* **Keep Synced Songs Permanent Promotion:** 1-Click promotion of temporarily synced multiplayer songs into your permanent customs library.
* **Deterministic Setlist Slot Ordering:** Custom songs in `DATA\customs.pak.xen` sort deterministically in alphabetical order, guaranteeing identical tier slots for both players and eliminating loading screen desyncs.
* **Asymmetric Library Mirroring:** Detects when your library has songs your friend is missing and offers 1-click Temporary Setlist Mirroring to prevent BetterGH3 crashes.
* **Zero-Crash Fullscreen Engine:** Eliminated modal Win32 message boxes during gameplay to prevent DirectX 9 `D3DERR_DEVICELOST` fullscreen focus loss crashes.

---

## [v1.1.0] - 2026-11-18 — Encore & Universal Hardware Edition
* **Universal Multi-Controller Engine:** Plug-and-play support for Sony DualSense Edge (PS5), DualSense, DualShock 4, Xbox 360/One/Series, and USB/XInput guitars with zero external drivers.
* **Official GH3 Auto-Strum on Fret Press:** Gamepad mode with back-paddle remapping and live controller diagnostics.
* **Playable Solo Extractor:** Slice any solo section or loop into a standalone playable custom song with count-in and fade-out.
* **Live Concert Medley Builder:** Crossfade 2 to 4 custom songs and stitch their charts into one continuous live medley.
* **Real Waveform Scrubber & Beat-Reactive Stage:** Clickable FFmpeg PCM audio waveform scrubber with beat-reactive 5-fret spectrum visualizer.
* **Audio/Video Lag Calibration Mini-Game & Chart Doctor:** Strum-to-the-beat calibrator and automated chart inspection/repair tool.

---

## [v1.0.0] - 2026-11-18 — Official Launch (Legends of Rock Edition)
* **Iconic Animated Stage UI & 5 Stage Themes:** Dark GH3, Light GH3, Star Power Cyber Blue, Through the Fire & Flames, and Encore Synthwave.
* **Interactive 3D Playable Highway Previewer:** Watch charts flow in 60 FPS synced to audio before launching the game.
* **ONOGAMI AI Song Creator:** Automated Demucs stem separation, Basic Pitch transcription, and Honeycomb pak compilation.
* **Full Clone Hero & YARG Conversion:** Native `.sng` package extraction, MIDI conversion, and variable tempo mapping.
* **Paired Career Save Profiles & Inno Setup Single-File Installer.**

---

## 📦 Archived Beta Milestones (v0.1.0 – v0.9.0-rc)
* **Beta 1 through Beta 8 (2026-11-14 to 2026-11-18):** Initial WPF UI architecture, Python Demucs + Basic Pitch integration, FSB3 multichannel audio compilation, Honeycomb compiler integration, Web UI bridge, and GitHub auto-updater foundations.
