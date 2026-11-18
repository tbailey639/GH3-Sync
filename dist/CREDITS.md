# 📜 GH3-Sync — Credits & Acknowledgments

**GH3-Sync** is built with deep gratitude to the open-source projects, modding pioneers, and rhythm gaming community members whose incredible tools, libraries, and reverse-engineering breakthroughs make this platform possible.

---

## 🎸 Core Modding & Compilation Engines

### 1. [Honeycomb-GUI & GH-Toolkit-Core](https://github.com/AddyMills/Honeycomb-GUI)
* **Author / Maintainer:** AddyMills & the Neversoft modding community
* **License:** Open Source (GPL / Permissive Community Modding)
* **Usage in GH3-Sync:** Powers the headless `.pak.xen` chart compilation, QB bytecode serialization, Neversoft extension CRC checksums (`keys.txt`), and Guitar Hero III song list injections.

### 2. [BetterGH3](https://github.com/marchellodev/BetterGH3)
* **Author / Maintainer:** Marchello & the BetterGH3 Modding Team
* **Usage in GH3-Sync:** The premier quality-of-life mod for Guitar Hero III PC. GH3-Sync dynamically interfaces with BetterGH3 game directories, verifies mod compatibility, and installs compiled custom assets directly into active game partitions.

### 3. [Neversoft Entertainment](https://en.wikipedia.org/wiki/Neversoft) & [Activision](https://www.activision.com/)
* **Original Creators:** Guitar Hero III: Legends of Rock (PC port by Aspyr Media)
* **Usage in GH3-Sync:** All Guitar Hero game assets, formats (`.pak.xen`, `.fsb.xen`), and trademarks are the property of their respective copyright holders. GH3-Sync is an unofficial noncommercial companion utility distributed under the **PolyForm Noncommercial License 1.0.0**.

---

## 🤖 Audio & AI Engines

### 4. [Demucs](https://github.com/facebookresearch/demucs) (Deep Extractor for Music Sources — `htdemucs_6s`)
* **Authors:** Alexandre Défossez, Nicolas Usunier, Gabriel Synnaeve, Yossi Adi (*Meta AI Research*)
* **License:** MIT License
* **Usage in GH3-Sync:** State-of-the-art 6-stem neural music source separation. Splits single-track audio into 6 isolated multi-track stems: Lead Guitar (`guitar.wav`), Bass (`bass.wav`), Drums (`drums.wav`), Vocals (`vocals.wav`), Piano (`piano.wav`), and Other (`other.wav`), enabling interactive mute-on-miss Guitar Hero gameplay.

### 5. [Basic Pitch](https://github.com/spotify/basic-pitch)
* **Authors:** Rachel Bittner, Juan José Bosch, David Rubinstein, Gabriel Meseguer-Brocal, Sebastian Ewert (*Spotify Audio Intelligence Lab*)
* **License:** Apache 2.0 / MIT
* **Usage in GH3-Sync:** Lightweight neural audio-to-MIDI transcription model used to extract real musical guitar pitches, chords, exact millisecond timestamps, and sustain lengths from the isolated `guitar.wav` stem.

### 6. [FFmpeg](https://ffmpeg.org/)
* **Authors:** Fabrice Bellard & the FFmpeg Project Contributors
* **License:** LGPL / GPL
* **Usage in GH3-Sync:** High-speed audio downmixing, stem extraction, format conversion (`.mp3`, `.ogg`, `.wav`), and raw PCM stream analysis for real-time FFT/YIN BPM & pitch detection.

### 7. [FMOD Sound System](https://www.fmod.com/)
* **Creator:** Firelight Technologies Pty Ltd.
* **Usage in GH3-Sync:** FSB3 audio container assembly and FMOD multi-channel bank encoding.

---

## 💻 .NET Libraries & Runtime Components

### 8. [.NET 10 & Windows Presentation Foundation (WPF)](https://dotnet.microsoft.com/)
* **Developer:** Microsoft Corporation & .NET Foundation
* **Usage in GH3-Sync:** Powers the 100% native Windows 11 desktop application window, Guitar Hero III Legends of Rock UI, animated splash intro, Windows 11 dark title bar, and native file/folder pickers.

### 9. [python-build-standalone](https://github.com/astral-sh/python-build-standalone)
* **Maintainer:** Astral (Gregory Szorc & contributors)
* **Usage in GH3-Sync:** Powers the 1-click zero-setup portable Python 3.11 runtime installed inside `%APPDATA%\GH3-Sync\tools\ai\` for Demucs & Basic Pitch.

### 8. [NAudio](https://github.com/naudio/NAudio)
* **Author:** Mark Heath
* **License:** MIT License
* **Usage in GH3-Sync:** Audio playback, resampling, and format transcoding.

### 9. [DryWetMidi](https://github.com/melanchall/drywetmidi)
* **Author:** Maxim Dobroselsky
* **License:** .NET Foundation / MIT
* **Usage in GH3-Sync:** High-precision MIDI parsing and Guitar Hero note track serialization.

### 10. [FFMpegCore](https://github.com/rosenbjerg/FFMpegCore)
* **Author:** Malte Rosenbjerg
* **License:** MIT License
* **Usage in GH3-Sync:** High-level async FFmpeg process runner in C#.

---

## 🎼 Rhythm Game Community

* **Moonscraper Community:** For advancing the modern `.chart` specification and open-source charting practices.
* **Reaper MIDI Community:** For standardizing multi-track stem charting and Neversoft MIDI event markers.
* **ScoreHero & Guitar Hero Modding Forums:** For decades of pioneering research into Neversoft QB scripts, PAK file structures, and song list checksums.

---

## 📄 License Notice
GH3-Sync is published under the **PolyForm Noncommercial License 1.0.0**. Free for personal and community gaming use. Noncommercial only. Third-party components and libraries remain subject to their respective original licenses.
