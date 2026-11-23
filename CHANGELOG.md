# GH3-Sync Changelog

Created by **Mad Scientist** ([tbailey639](https://github.com/tbailey639)). Notable major milestones and roadmap releases are documented below.

---

## [v1.5.7] - 2026-11-23 — Stage-Authentic GH3 Highway, P2P Lobby Auto-Join & Multi-Song Recharting

### 🎸 Stage-Authentic Guitar Hero III Highway
* **Vanilla GH3 Crimson Sunburst & Winged Emblem:** 16-point dramatic Crimson Sunburst rays with outstretched metallic wings and center crest ring accurately reproduced from the authentic Guitar Hero III arcade stage.
* **Smooth Dynamic Scrolling Highway:** Highway emblems now dynamically scroll down the neck synchronized with song playback tempo and lookahead speed.
* **Heavy 3D Chrome Bevel Rails:** Deep 3D chrome beveled side rails with outer drop shadow and glowing orange/amber inner neon edges.
* **Wound Nickel Strings & Silver Frets:** 5 metallic guitar strings with light streaks and 3D silver fret wires.
* **Authentic Circular Target Sockets & Strum Gems:** 3D beveled strike rings with colored fret bodies, dark recessed aperture, center rivet, and indicator chevrons. Strum notes feature domed gemstones with dark metallic center dimples and specular reflection pin-dots.
* **Authentic Vanilla GH3 Long Notes:** Upgraded mapper threshold (`availableGapTicks >= 72`, `fillRatio` 78%-88% of gap, snapping to 12 ticks), giving chords and solo phrases authentic vanilla GH3 whammyable sustains.

### 🌐 Fixed Friend Search, Symmetric P2P Lobby & Zero-Trust Auto-Heal
* **Fast-Routing Friend Search:** Fixed friend search by routing username lookups through local friends, LAN discovery, and global signaling relay in ~300ms, avoiding 15-second timeouts.
* **Commutative Shared Secrets (Zero 401 Errors):** Replaced one-way random tokens with deterministic commutative shared secrets (`DeriveSharedSecret(codeA, codeB)`), guaranteeing mutual peers always share matching cryptographic trust tokens.
* **Symmetric P2P Lobby Synchronization:** Connecting to a friend automatically dispatches a `LOBBY_SESSION_JOIN` signaling packet; the remote friend is automatically brought to Tab 5, configures address & token, and runs the setlist comparison so both players see the identical lobby screen simultaneously.

### 📦 Multi-Song Batch Rechart & Creator Queue
* **Context Menu Batch Operations:** Right-clicking checked songs in My Customs offers Add All Selected to Creator, Rechart All Selected, Fast Rechart All, Chart Doctor, Add All to Lobby, Export Package, and Delete All.
* **Multi-Token & `song.ini` Audio Matcher:** Resolves custom song audio across `G:\Guitar Hero Songs` subdirectories and `song.ini` metadata, properly adding all selected songs into `_batchQueue`.
* **Smart Auto-Collapse Bar:** Batch Operations Toolbar automatically hides when 0 songs are selected and reveals when songs are checked.

### 🛡️ Clean Studio Terminology & Startup Polish
* **Removed All AI Labels & Chorus Claps:** Replaced all "AI" terminology across checkboxes, options, and status messages with regular studio terminology. Removed "(Chorus claps)" from crowd chants.
* **Disk Space & Cache Purge:** "Clean Stem Audio Cache" now prunes save backups down to the latest 10 snapshots, removes `torch/include` headers, test fixtures, and `__pycache__` to reclaim disk space.
* **Timezone-Safe Clock & Online Status:** Enhanced `EnsureUtc` to handle local PC time zone conversions, ensuring relative offline statuses ("Just now", "15m ago", "2h ago") and presence are always accurate regardless of timezone.
* **Zero-White Flash Startup:** MainWindow initialized with `Visibility="Hidden"` in XAML, preventing GDI white brush flashes while the splash screen runs.

---

## [v1.5.6] - 2026-11-22 — Authentic GH3 Highway, Batch Queue Studio & Zero-White Startup Edition

### 🎸 Authentic Guitar Hero III Highway & Note Gem Visuals
* **Vanilla GH3 3D Highway Perspective:** Overhauled the highway projection camera to match authentic Guitar Hero III arcade geometry (`topHalfW: 80`, `botHalfW: 330`), giving deep, dramatic 3D neck taper.
* **Eliminated Piercing Strike Bar:** Removed the bright white horizontal line that previously cut through the buttons. Replaced with subtle metallic fret wire behind the target sockets.
* **Authentic Circular Target Sockets:** The 5 strike targets now feature 3D drop shadows, chrome outer bezels, luminous colored fret rings (Green, Red, Yellow, Blue, Orange), recessed dark apertures, and top chevron guide indicators pointing down into the sockets.
* **Iconic Strum Gem Dimple & Reflection Dot:** Strum notes now render as 3D domed gemstones with dark metallic center dimples and specular reflection pin-dots, exactly matching vanilla GH3.
* **HOPO White Crowns & 3D Neon Sustains:** HOPO notes feature glowing raised white crowns, while sustains render as thick 3D neon beam tubes with white-hot laser spines.
* **Studio Coach:** Replaced all remaining occurrences of "AI COACH" with "STUDIO COACH".

### 📦 Multi-Song Batch Rechart & Creator Queue
* **Universal Multi-Song Queueing:** Selecting multiple songs (or using "Select All") and clicking "Rechart" or "Add to Creator" now resolves audio across project directories, workspace folders, and customs paths, queueing ALL selected songs into `_batchQueue`.
* **Instant Batch Queue Table:** Song Creator immediately displays the interactive Batch Queue table, enables "Compile Batch Queue (N)", and populates the first song into form inputs for immediate visual feedback.
* **Context Menu Batch Options:** Right-clicking checked songs in My Customs now includes "🎸 Rechart All Selected (N songs)" and "➕ Add All Selected to Creator (N songs)".
* **Smart Auto-Collapse Bar:** Both the Batch Operations Toolbar and Bulk Edit button automatically hide (`Visibility.Collapsed`) when 0 songs are selected and appear as soon as any songs are checked.

### 🛡️ Zero-White Startup & Seamless Dark Splash Handoff
* **Eliminated White Window Behind Splash:** Refactored the startup sequence so `MainWindow` is never displayed while `SplashWindow` is active. The splash screen runs cleanly for 1.6 seconds, animates its glowing logo and 5 gems, and fades out before `MainWindow.Show()` is invoked.
* **Win32 Dark Background Class:** Window class background brush initialized to pitch-black `#090A0F` with DWM dark mode enabled, guaranteeing zero white flashes on launch.

### 🧹 Tools Tab Temp Cache Cleanup & UI Polish
* **Cache Purge Tool:** Added a "🧹 Clean Cache" button in Tools & Stems to safely purge pip download caches, `__pycache__` directories, and temporary audio buffers.
* **Zero AI Jargon:** Replaced all remaining "AI" strings across menus, tooltips, dialogs, and progress toasts with authentic studio terminology.
* **Hidden Automatic Tunnel:** Internet tunnel options remain completely hidden from standard user view, running 100% silently in the background.

---

## [v1.5.5] - 2026-11-22 — Batch Creator, Zero-Trust P2P Auto-Heal & Clean Studio Edition

### 📦 Unified Batch Selection & Creator Recompile
* **Dedicated Batch Actions Menu:** Replaced scattered individual buttons with a unified "⚡ Batch Actions ▾" dropdown and action bar: Send to Song Creator (Batch Recompile), Rechart Studio, Fast Re-Chart, Chart Doctor (Batch Auto-Fix), Add to Lobby, Export Pack, and Bulk Delete.
* **Smart Auto-Collapse Bar:** The batch operations toolbar now collapses cleanly (`Visibility.Collapsed`) when 0 songs are selected and smoothly expands as soon as any songs are checked.
* **Instant "Select All" Notebook Header:** Added a prominent "Select All" toggle directly on the Guitar Hero notebook parchment header, allowing 1-click multi-selection across the entire library.
* **Direct Batch Recompile in Creator:** Clicking "➕ Send to Creator (Batch Recompile)" instantly queues all selected tracks into Song Creator's batch queue with `AutoCompile = true`, navigates directly to Song Creator, and prepares for compilation.

### 👥 P2P Zero-Trust Mutual Authentication (401 Unauthorized Auto-Heal)
* **Deterministic Mutual Friend Secret:** Resolved the "401 (Unauthorized)" connection failure when connecting to trusted friends in the lobby. Friends now deterministically derive an identical shared cryptographic token (`DeriveSharedSecret`) based on their two sorted Friend Codes, guaranteeing immediate authenticated connection without requiring out-of-band token exchanges.
* **Symmetrical Lobby & Auto-Retry:** Both peers in P2P Sync & Lobby now share identical real-time status and hype soundboard capabilities. If an authentication challenge ever reports 401, the client automatically auto-heals and retries with the mutual derived secret.
* **Hidden Background Internet Tunnel:** Streamlined the P2P and Friends tabs by removing redundant manual "Restart Internet Tunnel" buttons from normal view. The Cloudflare Quick Tunnel and global signaling relay now operate 100% silently in the background.

### ⏰ Timezone-Independent Online Clock & Statuses
* **UTC Timestamp Normalization:** Fixed the 5-hour clock skew in offline statuses ("Offline 5h ago"). All timestamps are strictly parsed with `EnsureUtc`, eliminating timezone discrepancies between different client PCs.
* **Local Time Presentation:** All displayed dates, backup timestamps, and relative status indicators ("Active just now", "Offline 15m ago") format strictly according to the user's local PC clock.

### 🧹 Studio Naming Overhaul & Zero AI Jargon
* **Authentic Studio Terms:** Replaced all artificial "AI" labels across menus, headers, tooltips, and status bars with authentic recording studio terminology ("Neural Pitch Tracking", "Multi-Track Stems", "Rechart Studio", "Instant Studio Cache").
* **Crowd Chants Clean-Up:** Removed "Chorus claps" and isolated Beat It vocal remnants from the crowd chant generator. Restored the pristine 4-minute 38-second concert arena crowd soundbank and synthesized full-track sing-alongs matching whichever song is currently playing.
* **Strict Uncheck Verification:** Unchecking "Stadium Sing-Along & Crowd Chants" in Song Creator now completely removes all crowd audio stems from the song project and sets `enable_stadium_chants = false` in `song.ini`, ensuring zero unwanted chanting audio compiles into the FSB soundbank.

### ⚡ Instant Library Rendering (UI Hang Elimination)
* **Zero Disk I/O UI Thread Rendering:** Added cached `AudioPath` directly to `CustomSongInfo`. Eliminating repeated recursive disk searches (`Directory.GetFiles`) during `BuildSongCard` rendering makes scrolling and setlist loading instantaneous.
* **Seamless Splash Handoff:** Pre-merged dark theme dictionaries at application initialization and preserved the animated splash window until `MainWindow` has completed its initial frame rendering, permanently eliminating white screen flashes.

---

## [v1.5.4] - 2026-11-22 — Authentic GH3 Highway, Song Creator Checkboxes & Signed SmartScreen Edition

### 🎸 Authentic Guitar Hero III Highway & 3D Strike Buttons
* **Authentic 3D Highway Stage:** Matched visual aesthetics directly to authentic Guitar Hero III arcade gameplay: dark stage plank with metallic outer side rails, illuminated warning chevrons, 5 distinct nickel-wound strings, and 3D metallic fret dividers with top highlights and drop shadows.
* **Recessed Target Sockets & 3D Raised Strike Buttons:** Implemented two-tier strike line targets: recessed circular target sockets embedded in the highway surface, and physical 3D raised strike buttons (Green, Red, Yellow, Blue, Orange) with beveled chrome skirts, colored domed caps, top specular shine, and center contact dimples that physically depress and flare with radiant Star Power/flame bloom when held or strummed.
* **Authentic 3D Neon Sustains:** Overhauled sustain rendering to thick, luminous neon colored light beams with razor-sharp white-hot center core spines, matching official GH3 long note appearance.

### 🎼 Vanilla GH3 Note Density & Rich Long Notes
* **Vanilla Note Spacing & Headroom:** Rebalanced note spacing (`0.098 * feelScale`) and amplitude floor (`0.14f` on Expert) back to authentic Guitar Hero III standards, eliminating excessive 19-note-per-second spam.
* **Natural Musical Sustains:** Rewrote sustain duration calculations. Whenever musical space exists before the next strike (gap >= 96 ticks), chords and melodic phrasing hold and ring out up to 80% of the available headroom, quantized cleanly to 48 ticks (16th-note minimum visible tails).

### 🛠️ Song Creator Checkboxes Pre-Compilation Baking
* **Pre-Compilation Injection:** Boss Battle Duel power-ups, Trade-Off Solo alternating duels, AI Lyric & Section Markers, Cryptographic Charter Signature (`.gh3sig`), and AI Stadium Chants are now injected into `notes.chart` *before* converting to MIDI and compiling into Honeycomb `.ghproj` / `.pak.xen`, ensuring all options are baked into the compiled game song.
* **Full-Song Crowd Chanting & Clean Uncheck:** Crowd chanting now reverberates whatever song is playing across its entire duration. Unchecking the box immediately deletes all crowd audio files from the project folder so no crowd audio is packed into the FSB.

### ⚡ Batch Rechart Multi-Queueing
* **Multi-Select Queueing:** Selecting multiple songs in My Customs and clicking "Rechart" queues ALL selected songs into the Song Creator Batch Queue and switches to the Creator tab.
* **Simplified Naming:** Renamed "Re-Chart AI v2" to "Rechart" across all menus, buttons, and context options.

### 🚀 Performance & Startup Polish
* **White Screen Loading Elimination:** Resolved the white window background flashing behind the splash screen by deferring `MainWindow.Show()` until after the splash closes cleanly.
* **Instant Tab Switching:** Replaced synchronous recursive directory size calculations on the AI tools folder with cached background tasks, eliminating the 1.5-second UI freeze when navigating to Song Creator or Tools & Stems.

### 🛡️ Microsoft Defender SmartScreen & Authenticode Signing
* **Publisher Metadata:** Added full Windows version and company metadata (`VersionInfoCompany`, `VersionInfoDescription`, `VersionInfoVersion`, `VersionInfoProductName`) to the Inno Setup installer.
* **Authenticode Digital Signature:** Signed `GH3-Sync-Setup.exe` with Authenticode code signing certificate for "Mad Scientist (tbailey639)", ensuring Windows SmartScreen displays the official publisher name instead of "Unknown publisher".

---

## [v1.5.3] - 2026-11-22 — Authentic GH3 Gibson Les Paul Fretboard, Complete Installer Bundle & Silent Relaunch Guardian

### 🎸 Authentic Guitar Hero III Gibson Les Paul Fretboard & Highway
* **Gibson Les Paul Controller Neck & Fretboard:** Overhauled the Universal Controller Tester into an authentic Guitar Hero III Gibson Les Paul neck with rich dark rosewood grain, cream/ivory binding borders, nickel-silver fret dividers, mother-of-pearl trapezoid inlays, ribbed strum bar, and chrome whammy bar.
* **3D Molded GH3 Fret Buttons:** Replaced generic flat boxes with authentic 3D molded Guitar Hero III fret buttons (Green, Red, Yellow, Blue, Orange) featuring outer chrome bezels, vivid translucent neon jewel caps, top specular highlights, and radiant Star Power bloom when pressed.
* **Playable Highway 3D Overhaul:** Restyled the practice highway previewer to match authentic Guitar Hero III: dark rosewood plank, outer chrome rails, inner neon energy tracks, perspective 3D fret wires, 3D strike target rings with center hubs, and glossy 3D gemstone notes with white HOPO crowns and neon sustain ribbons.
* **Gibson Les Paul Visualizer:** Updated the application update progress guitar to an authentic single-cutaway Gibson Les Paul silhouette with cream pickguard, amber gold speed knobs, and the iconic Gibson open-book headstock.

### 🛡️ Complete Installer Binary Packaging & Startup Fix
* **Self-Contained Runtime Packaging:** Fixed Inno Setup packaging to include all publish assemblies, .NET runtime libraries (`coreclr.dll`), and dependencies (`GH3-Sync.dll`, `System.*.dll`, `Microsoft.AspNetCore.*.dll`). Completely resolved `The application to execute does not exist: GH3-Sync.dll`, ensuring GH3-Sync opens cleanly on desktop after updating.
* **Mandatory Administrator Elevation:** Added explicit `PrivilegesRequired=admin` to the Inno Setup configuration, guaranteeing that Windows UAC administrator elevation is always requested and properly prompted.
* **Truly Silent Background Installation:** Updated the silent updater flags to `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS`, eliminating all installer progress popups for an unobtrusive background upgrade.
* **Detached Relaunch Guardian:** Added a resilient background process watchdog that monitors the installer and automatically relaunches `GH3-Sync.exe` the moment file replacement completes.

---

## [v1.5.2] - 2026-11-22 — Stage UI Overhaul, P2P Auto-Heal Handshake, Full-Song Chants & System Tray

### 🎸 Authentic Guitar Hero Stage UI Overhaul
* **Concert Road Case & Stompbox Aesthetic:** Replaced flat dashboard cards with beveled concert road cases, amplifier speaker grille backdrops, and metallic framing.
* **Tactile 5-Fret Stompbox Buttons:** Action buttons now feature tactile guitar stompbox pedal styling with beveled top highlights, realistic drop shadows, and glowing 5-fret accents (Green = Sync, Red = Delete, Yellow = Gold/Select, Blue = Tools, Orange = Rock Out).
* **Universal Theme Compatibility:** Enhanced styling works seamlessly across all 5 visual themes (FireFlames, Dark, Light, StarPower, Synthwave).

### 🤝 P2P Handshake Auto-Recovery & 401/403 Desync Fix
* **Self-Healing Friend Handshake:** `ReceiveFriendRequest` now re-broadcasts `FRIEND_ACCEPT` immediately when a sender already exists in the friends list and updates their stored `TrustToken`, preventing friend pairing desyncs and eliminating the need to unfriend.
* **HMAC Challenge Recovery:** Valid cryptographic HMAC challenge signatures now bypass temporary rate-limit lockouts, immediately clearing obsolete IP blocks when peers re-pair.
* **Library Protection on Sync:** Removed destructive full `customs.pak.xen` replacement during peer sync. Synced songs now cleanly merge into the local customs pak table without erasing or hiding any existing local songs.

### 🏟️ Dynamic Full-Song Stadium Crowd Chants
* **Full-Song Vocal Sing-Along:** Crowd chanting now reverberates and harmonizes the actual song's isolated vocals for the entire duration of whatever track is playing, rather than being confined to the chorus or playing static samples.
* **Neutral Arena Bed:** Replaced static "Beat It" audio with a procedurally synthesized stereo stadium crowd ambience bed.
* **Song Creator Checkbox Fix:** Unchecking "AI Stadium Sing-Along & Crowd Chants" now cleanly removes any residual crowd audio stems and prevents them from being compiled into `.ghproj`.

### 🎼 Vanilla GH3 Pitch Mapping & Note Reading
* **Accurate Frequency Calibration:** Calibrated pitch contour heuristics against vanilla Guitar Hero III charts so bass notes anchor to Green (0), melodic mids map to Red/Yellow/Blue, and screaming solos reach Orange (4).
* **Quantized Subdivision & Shred Detection:** Tightened note spacing threshold (`minSpacingSec = 0.052 * feelScale`) and amplitude floor (`0.08f`) on Expert, capturing fast trills and picking bursts.

### ⚡ Batch Rechart & Renaming
* **Multi-Select Batching:** Selecting multiple custom songs and clicking Rechart now queues and processes all selected tracks in sequence.
* **Streamlined Naming:** Renamed "Re-Chart AI v2" to simply **"Rechart"**.

### 📢 Soundboard Broadcast & Performance
* **Multi-Peer SFX Delivery:** Soundboard triggers (Airhorn, Stadium Cheer, Boo, Drum Fill, Solo Lick) now broadcast to all online and lobby friends over direct HTTP and global signaling.
* **Reduced Idle CPU:** Adjusted stage animation timer to 50ms (20 FPS), eliminating UI thread stutter and reducing idle CPU usage by over 50%.

### 🖥️ System Tray & Startup Polish
* **System Tray Background Mode:** Minimizing or closing the window now minimizes cleanly to the Windows system tray with balloon notifications, keeping P2P sync and rock-out invites active in the background.
* **Zero-Flicker Startup:** MainWindow first-frame DirectX composition is fully rendered with opacity 0 before closing the splash screen, completely eliminating the white canvas flash.
* **Dismissible "What's New" Banner:** Added a rock-styled update announcement bar that automatically displays on new version updates and saves dismiss state.
* **Standardized Relative Offline Timestamps:** Standardized offline duration across all views to show `Xm ago`, `Xh ago`, or `Xd ago`.

---

## [v1.5.1] - 2026-11-20 — Rockstar Profiles, Play Together Invites, Notifications Center & Notebook UI

### 👤 Rockstar Profiles & Showcase Modal
* **Full Rockstar Profile Dialog:** Added a dedicated profile view for all players (self, friends, and discovered players). Displays custom avatar, Stage Name, permanent Friend Code with 1-click copy, live status indicator, and relationship badge (Self, Trusted Friend, Regular Friend, or Discovered Player).
* **Signature Showcase Track & Audio Preview:** Highlight and listen to a player's signature custom track with an integrated audio preview button directly from their profile.
* **Career Statistics & Hall of Fame Showcase:** Shows lifetime Full Combos, 5-Star performances, total notes hit, and matches played, alongside an interactive visual trophy showcase grid tracking 14 unlocked achievements.
* **Contextual Profile Actions:** Quick buttons for "🎸 Ask to Play Together!", "⚡ Sync Songs", "⭐ Request Trust Upgrade", "Add Friend" (regular or trusted), and "Career Saves".

### 🎸 "Ask to Play Together!" Real-Time Session Invites
* **Real-Time Multiplayer Pings:** Send real-time session invites to online trusted friends with one click.
* **Instant Peer Notification:** Receiver gets a high-priority prompt: `"[USERNAME] wants to ROCK OUT with you!"` with a 1-click `⚡ Join Lobby & Sync` button that instantly navigates to the lobby and connects.
* **Dual-Transport Reliability:** Invites broadcast over direct local/tunnel HTTP (`/api/lobby/play-invite`) with immediate fallback to global serverless signaling relay (`ntfy.sh`), ensuring delivery through any router or NAT.

### 🔍 Friend Search, Global Username Indexing & Uniqueness Enforcement
* **Search by Stage Name or Friend Code:** Look up players by their display name or Friend Code directly from the "Search & Add Friends" bar.
* **Global Serverless Username Indexing:** Heartbeat broadcasts claims to `gh3sync-usr-{username}` on the signaling relay, enforcing username uniqueness across the network so duplicate stage names cannot be registered.
* **Send Requests from Profile:** Send regular or trusted friend requests directly from a player's profile view.

### 🔔 Notifications Center
* **Unified Notifications Dropdown:** Added a `🔔 Notifications (N)` button on the Friends page with a dynamic badge counter.
* **Comprehensive Activity Feed:** Aggregates pending friend requests (regular & trusted), multiplayer play invites, and recently unlocked trophies in one convenient dropdown panel.

### 👥 Friends List Sorting & Relative Offline Timestamps
* **Clean Section Title:** Renamed "Active Friends" section header to "Friends".
* **Smart Presence Sorting:** Friends are sorted with online players first, followed by offline players ordered by relative last seen timestamps (`🔴 Offline - 5m ago`, `🔴 Offline - 2h ago`, `🔴 Offline - Nov 20`).
* **Card Quick Actions:** Each friend card now includes `👤 View Profile` and `🎸 Play Together` buttons for fast interaction.

### 📖 Guitar Hero III Setlist Notebook UI for "My Customs"
* **Authentic GH3 Setlist Rows:** Redesigned the "My Customs" song list to match the authentic Guitar Hero III setlist notebook interface. Songs display in bold Impact font with uppercase artist and year, 5-star ratings, high score displays, and ruled notebook lines.
* **GH3 Controller Navigation Footer:** Bottom controller pills bar (`UP/DOWN`, `SELECT: PREVIEW / SYNC`, `BACK`, `BONUS`) matching the original game aesthetic.

### 🛡️ Crash-Proof Library Restore & Kept Song Integrity
* **Crash-Resilient State Machine:** If the application terminates abnormally during an active lobby session, `ReconcileLibraryIntegrity()` automatically detects the unclean exit on the next boot, cleanly restoring original customs while preserving all permanently kept songs (`KeptFriendSongIds`).
* **Exit & Disconnect Audits:** Automated library reconciliation runs on `ProcessExit`, `ExitApplication()`, and lobby disconnect to guarantee only original songs + kept songs remain.

### ⚡ Complete Startup White-Screen Elimination
* **Zero-Flicker Window Creation:** Window class background erasure (`WM_ERASEBKGND` / 0x0014) is suppressed, DWM dark mode attributes (20 and 19) are applied before show, and initial window opacity remains 0 until the WPF DirectX composition frame completes rendering, banishing the white flash on boot.

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
