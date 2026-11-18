using System;
using System.IO;

namespace GH3_Sync;

public class ObsNowPlayingSnapshot
{
    public string Title { get; set; } = "GH3-Sync Ready";
    public string Artist { get; set; } = "Guitar Hero III Legends of Rock";
    public string Charter { get; set; } = "";
    public string Difficulty { get; set; } = "Expert";
    public int Bpm { get; set; } = 0;
    public bool IsPlaying { get; set; } = false;
    public bool HasCoverArt { get; set; } = false;
    public string UpdatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
}

/// <summary>
/// Loopback-only OBS Studio Browser Source overlay state & sleek animated HTML5 renderer.
/// Accessible strictly at http://127.0.0.1:7373/obs-overlay (blocked to external IPs by Program.cs lockdown).
/// </summary>
public static class ObsOverlayService
{
    private static readonly object _lock = new();
    private static ObsNowPlayingSnapshot _current = new();
    private static string? _coverArtPath;

    public static void UpdateNowPlaying(string title, string artist, string? charter = null, string? difficulty = null, int bpm = 0, bool isPlaying = true, string? coverArtPath = null)
    {
        lock (_lock)
        {
            _coverArtPath = !string.IsNullOrWhiteSpace(coverArtPath) && File.Exists(coverArtPath) ? coverArtPath : null;
            _current = new ObsNowPlayingSnapshot
            {
                Title = string.IsNullOrWhiteSpace(title) ? "GH3-Sync Ready" : title,
                Artist = string.IsNullOrWhiteSpace(artist) ? "Guitar Hero III" : artist,
                Charter = charter ?? "",
                Difficulty = string.IsNullOrWhiteSpace(difficulty) ? "Expert" : difficulty,
                Bpm = bpm,
                IsPlaying = isPlaying,
                HasCoverArt = _coverArtPath != null,
                UpdatedUtc = DateTime.UtcNow.ToString("O")
            };
        }
    }

    public static void SetPlaybackState(bool isPlaying)
    {
        lock (_lock)
        {
            _current.IsPlaying = isPlaying;
            _current.UpdatedUtc = DateTime.UtcNow.ToString("O");
        }
    }

    public static ObsNowPlayingSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new ObsNowPlayingSnapshot
            {
                Title = _current.Title,
                Artist = _current.Artist,
                Charter = _current.Charter,
                Difficulty = _current.Difficulty,
                Bpm = _current.Bpm,
                IsPlaying = _current.IsPlaying,
                HasCoverArt = _current.HasCoverArt,
                UpdatedUtc = _current.UpdatedUtc
            };
        }
    }

    public static string? GetCoverArtPath()
    {
        lock (_lock) return _coverArtPath;
    }

    public static string BuildOverlayHtml()
    {
        return """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>GH3-Sync OBS Now Playing</title>
<style>
  html, body { margin: 0; padding: 0; background: transparent; overflow: hidden; font-family: 'Segoe UI', system-ui, sans-serif; }
  .card {
    display: flex; align-items: center; gap: 14px;
    width: 460px; padding: 12px 16px; border-radius: 14px;
    background: linear-gradient(135deg, rgba(16,18,28,0.92), rgba(26,18,36,0.92));
    border: 1.5px solid rgba(249,115,22,0.75);
    box-shadow: 0 10px 30px rgba(0,0,0,0.55), 0 0 18px rgba(249,115,22,0.28);
    color: #f8fafc;
    animation: fadeSlide 0.5s ease-out;
  }
  @keyframes fadeSlide { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: translateY(0); } }
  .disc-wrap { position: relative; width: 62px; height: 62px; flex-shrink: 0; }
  .disc {
    width: 62px; height: 62px; border-radius: 50%; object-fit: cover;
    border: 2px solid #f59e0b; box-shadow: 0 0 12px rgba(245,158,11,0.45);
    background: radial-gradient(circle, #1f2937 22%, #0f172a 70%);
  }
  .spinning { animation: spin 5.5s linear infinite; }
  @keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
  .meta { flex: 1; min-width: 0; }
  .badge { font-size: 10px; font-weight: 800; letter-spacing: 1.4px; color: #fb923c; text-transform: uppercase; margin-bottom: 2px; }
  .title { font-size: 17px; font-weight: 800; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; color: #ffffff; }
  .artist { font-size: 13px; color: #cbd5e1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-top: 1px; }
  .sub { font-size: 11px; color: #94a3b8; margin-top: 4px; display: flex; gap: 10px; align-items: center; }
  .frets { display: flex; gap: 4px; align-items: flex-end; height: 18px; margin-left: auto; }
  .fret { width: 6px; border-radius: 3px; height: 6px; transition: height 0.14s ease; }
  .f0 { background: #22c55e; box-shadow: 0 0 6px #22c55e; }
  .f1 { background: #ef4444; box-shadow: 0 0 6px #ef4444; }
  .f2 { background: #eab308; box-shadow: 0 0 6px #eab308; }
  .f3 { background: #3b82f6; box-shadow: 0 0 6px #3b82f6; }
  .f4 { background: #f97316; box-shadow: 0 0 6px #f97316; }
</style>
</head>
<body>
  <div class="card">
    <div class="disc-wrap">
      <img id="art" class="disc" src="" alt="" style="display:none;" />
      <div id="artFallback" class="disc" style="display:flex;align-items:center;justify-content:center;font-size:24px;">🎸</div>
    </div>
    <div class="meta">
      <div class="badge" id="badge">● GH3-SYNC NOW PLAYING</div>
      <div class="title" id="title">GH3-Sync Ready</div>
      <div class="artist" id="artist">Guitar Hero III Legends of Rock</div>
      <div class="sub">
        <span id="extra">Expert</span>
        <div class="frets">
          <div class="fret f0" id="b0"></div>
          <div class="fret f1" id="b1"></div>
          <div class="fret f2" id="b2"></div>
          <div class="fret f3" id="b3"></div>
          <div class="fret f4" id="b4"></div>
        </div>
      </div>
    </div>
  </div>
<script>
  let playing = false;
  let lastUpdated = '';
  async function poll() {
    try {
      const r = await fetch('/api/obs-now-playing');
      if (!r.ok) return;
      const d = await r.json();
      playing = !!d.isPlaying;
      document.getElementById('title').textContent = d.title || 'GH3-Sync Ready';
      document.getElementById('artist').textContent = d.artist || 'Guitar Hero III';
      document.getElementById('badge').textContent = playing ? '▶ NOW PLAYING • GH3-SYNC' : '🎸 ON STAGE • GH3-SYNC';
      let extra = d.difficulty || 'Expert';
      if (d.bpm > 0) extra += ' • ' + d.bpm + ' BPM';
      if (d.charter) extra += ' • Chart: ' + d.charter;
      document.getElementById('extra').textContent = extra;
      const art = document.getElementById('art');
      const fb = document.getElementById('artFallback');
      if (d.hasCoverArt) {
        if (lastUpdated !== d.updatedUtc) {
          art.src = '/api/obs-now-playing/art?t=' + encodeURIComponent(d.updatedUtc);
        }
        art.style.display = 'block';
        fb.style.display = 'none';
      } else {
        art.style.display = 'none';
        fb.style.display = 'flex';
      }
      art.classList.toggle('spinning', playing);
      fb.classList.toggle('spinning', playing);
      lastUpdated = d.updatedUtc;
    } catch {}
  }
  setInterval(poll, 1200);
  poll();
  setInterval(() => {
    for (let i = 0; i < 5; i++) {
      const h = playing ? (5 + Math.floor(Math.random() * 14)) : 4;
      document.getElementById('b' + i).style.height = h + 'px';
    }
  }, 150);
</script>
</body>
</html>
""";
    }
}
