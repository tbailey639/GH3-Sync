# 🎸 GH3-Sync | Complete P2P Synchronizer, Friends Network & ONOGAMI Song Creator

**GH3-Sync** is an all-in-one companion utility and in-game HUD overlay designed specifically for **BetterGH3 (Guitar Hero III PC)**. It enables players worldwide to synchronize custom songs and setlists in seconds, create new custom songs using the **ONOGAMI Method**, safeguard and restore career save files, manage trusted friends to skip PIN/IP entry, and prevent duplicate downloads.

---

## 🌟 Key Features

### 1. ⚙️ Universal Game Path & BetterGH3 Compatibility Verification
* **Works on Any PC:** Users can set their custom Guitar Hero III installation directory in **Game Config**.
* **Compatibility Detection:** Checks automatically for `GH3.exe`, `PLUGINS\FastGH3.dll`, and `dinput8.dll`.
  * `[✓ BetterGH3 Mod Detected - 100% Compatible]`
  * `[⚠️ Vanilla GH3 Detected - BetterGH3 Recommended]`
* **Direct Install vs. Staging:** Choose whether downloaded customs go straight into `DATA/SONGS` and `DATA/MUSIC` or into a staging folder.

### 2. 🛡️ Career Save Protection & 1-Click Restore
* **The Problem:** In vanilla GH3 and early BetterGH3, adding custom songs or partitions could wipe career stars, high scores, and unlocks.
* **Automated Safety Backup:** GH3-Sync automatically creates a timestamped backup of `%USERPROFILE%\Documents\Aspyr\Guitar Hero III\Save` (`s000.d` and `toc.dat`) before *every* P2P sync and file import.
* **1-Click Restore:** Visit the **Career Saves** tab anytime and click **"Restore Latest Career Save"** to recover all career progress with customs intact!

### 3. 🔍 Duplicate Song Detection & ONOGAMI Fingerprinting
* **No More Duplicate Downloads:** When syncing with a friend, GH3-Sync compares your local songs against your friend's manifest.
* **Duplicate Detection:** Matches songs by internal ID, Title, Artist, and ONOGAMI fingerprint.
* **Auto-Unchecked:** Detected duplicates are **automatically unchecked** and marked with `⚠️ Duplicate Detected (Already Owned)`, preventing accidental re-downloads while still allowing manual override if desired.
* **Pre-verified Songs:** *Beat It*, *While My Guitar Gently Weeps*, and *ONOGAMI* are recognized as ONOGAMI-created customs.

### 4. 👥 Friends, "Trusted Friend" System & Public Internet Connectivity
* **Automatic IP Sharing (Trusted Friends):** When you mark a friend as **Trusted**, their address is automatically discovered and resolved in real time via LAN UDP beacons (port 7374), UPnP / NAT-PMP external mappings, and the Central Admin Hub. No manual IP exchange or PIN entry needed!
* **Public IP & UPnP / NAT-PMP Port Mapping (Beta 3):** Automatically detects your router's public WAN IP and forwards port 7373 via UPnP IGD or NAT-PMP so peers can connect from anywhere on the internet without manual router setup.
* **Custom DDNS Support:** Easily bind dynamic DNS hostnames (e.g., No-IP, DuckDNS) under **Network / NAT Settings**.
* **IP Privacy (Regular Friends):** Untrusted friends' IPs remain strictly private for security; manual IP exchange and session PINs are required until trust is granted.
* **Permanent Unique Friend Code:** Every player receives a permanent unique code (e.g. `GH3-6475-71AF`) with a Show/Hide toggle to prevent spam.
* **Instant Revocation:** Click **"Revoke Trust"** or **"Unfriend"** at any time to immediately destroy the access token.

### 5. 🎸 ONOGAMI Song Creator
* Built-in custom song generator using the proven ONOGAMI method:
  * **Auto-Chart Generation:** Auto-generates full 5-fret note charts across all 4 difficulties (**Easy, Medium, Hard, Expert**) from your chosen BPM + audio file!
  * **Custom MIDI / Chart Support:** Upload your own `.chart` or `.mid` files if charted in Moonscraper/Reaper.
  * **Audio Support:** Supports `.mp3`, `.wav`, and `.ogg`.
  * **Honeycomb Ready:** Generates Neversoft `song.ini` and the complete Honeycomb `.ghproj` project file.
  * **Created by Me Tagging:** Created songs automatically appear in your **"Created by Me"** library filter with the `🎸 ONOGAMI Song Creator` badge.

### 6. 📁 1-Click File Import (`.gh3pack`, `.gh3song`, `.sgh`, `.zip`)
* Drag and drop any custom song archive into the **Import Songs** tab. GH3-Sync validates headers, protects career saves, and installs them directly into your game.

### 7. 🛡️ Military-Grade P2P Security
* **AES-256-GCM Authenticated Encryption:** All payloads in transit are encrypted on the fly.
* **Zip-Slip & Path Traversal Immunity:** Strict sanitization ensures no archive can write outside designated game directories.
* **PAK Magic Header Verification:** Validates Neversoft headers (`A7 F5 05 C4`) to prevent malicious executable injection.
* **Brute-Force Rate Limiting:** 5 failed PIN attempts results in a 5-minute IP lockout.

### 8. 🎮 In-Game HUD Overlay
* Transparent, click-through overlay attached to `GH3.exe`.
* Displays `[ACTIVE]`, live transfer progress (`Syncing (1/3): Beat It (45%) [||||....]`), and `[✓ SUCCESS]` notifications during matches.

---

## 🚀 How to Run & Distribute

### Option A: 1-Click Windows Installer (Recommended)
* Download and run **`GH3-Sync-Setup.exe`** from the [Latest Release](https://github.com/tbailey639/GH3-Sync/releases/latest).
* Automatically installs desktop & start menu shortcuts, BetterGH3 components, and an uninstaller.

### Option B: Standalone Executable (.exe)
* Run `GH3-Sync.exe` in `dist/`.
* Automatically launches the server and opens your web browser to `http://localhost:7373`.

---

## 🛠️ Logs & Troubleshooting
* **App Log:** `%APPDATA%\GH3-Sync\config.json` & `dist\debug.log`
* **In-Game Log:** `gh3_sync_debug.log` in your GH3 folder.
* **Save Backups:** `%APPDATA%\GH3-Sync\SaveBackups\`

---

## ⚖️ License & Terms of Use
This software is licensed under the **PolyForm Noncommercial License 1.0.0**.
* **Allowed:** Free for personal, community, and noncommercial gaming use.
* **Prohibited:** Any commercial use, monetized distribution, or selling of this software in whole or in part without explicit written permission is strictly prohibited.
