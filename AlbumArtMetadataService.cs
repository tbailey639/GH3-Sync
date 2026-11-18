using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class FetchedSongMetadata
{
    public bool Found { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Year { get; set; }
    public string Genre { get; set; } = "";
    public string ArtworkUrl { get; set; } = "";
    public string PreviewAudioUrl { get; set; } = "";
    public string SavedArtworkPath { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// Zero-API-key album artwork (600x600) and track metadata lookup via the public iTunes Search API.
/// </summary>
public class AlbumArtMetadataService
{
    private readonly HttpClient _http;
    private readonly SyncLogger? _logger;

    public AlbumArtMetadataService(SyncLogger? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"GH3-Sync/{UpdateService.CurrentAppVersion}");
    }

    public async Task<FetchedSongMetadata> FetchMetadataAndCoverAsync(
        string title,
        string artist,
        string? saveToDirectory = null,
        CancellationToken ct = default)
    {
        var result = new FetchedSongMetadata();
        var term = $"{artist} {title}".Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            result.Message = "Enter a song title or artist first.";
            return result;
        }

        try
        {
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=song&limit=5";
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.GetArrayLength() == 0)
            {
                result.Message = $"No artwork match found for '{term}'.";
                return result;
            }

            // Pick best match (prefer title + artist match)
            JsonElement best = arr[0];
            foreach (var item in arr.EnumerateArray())
            {
                var tn = item.TryGetProperty("trackName", out var tProp) ? tProp.GetString() ?? "" : "";
                var an = item.TryGetProperty("artistName", out var aProp) ? aProp.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(title) && tn.Contains(title, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(artist) || an.Contains(artist, StringComparison.OrdinalIgnoreCase)))
                {
                    best = item;
                    break;
                }
            }

            result.Found = true;
            result.Title = best.TryGetProperty("trackName", out var tr) ? tr.GetString() ?? title : title;
            result.Artist = best.TryGetProperty("artistName", out var ar) ? ar.GetString() ?? artist : artist;
            result.Album = best.TryGetProperty("collectionName", out var al) ? al.GetString() ?? "" : "";
            result.Genre = best.TryGetProperty("primaryGenreName", out var ge) ? ge.GetString() ?? "" : "";
            if (best.TryGetProperty("releaseDate", out var rd) && rd.GetString() is string rdStr && rdStr.Length >= 4 && int.TryParse(rdStr.Substring(0, 4), out var yr))
                result.Year = yr;

            var art100 = best.TryGetProperty("artworkUrl100", out var aw) ? aw.GetString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(art100))
                result.ArtworkUrl = art100.Replace("100x100bb", "600x600bb");
            result.PreviewAudioUrl = best.TryGetProperty("previewUrl", out var pv) ? pv.GetString() ?? "" : "";

            if (!string.IsNullOrWhiteSpace(result.ArtworkUrl) && !string.IsNullOrWhiteSpace(saveToDirectory) && Directory.Exists(saveToDirectory))
            {
                var imgBytes = await _http.GetByteArrayAsync(result.ArtworkUrl, ct);
                if (imgBytes.Length > 1024)
                {
                    var outPath = Path.Combine(saveToDirectory, "album.jpg");
                    await File.WriteAllBytesAsync(outPath, imgBytes, ct);
                    result.SavedArtworkPath = outPath;
                }
            }

            result.Message = $"Fetched cover art & tags: '{result.Title}' — {result.Album} ({(result.Year > 0 ? result.Year.ToString() : "N/A")})";
            _logger?.LogInfo($"[METADATA] {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            result.Message = $"Metadata lookup failed: {ex.Message}";
            _logger?.LogWarn($"[METADATA] {result.Message}");
            return result;
        }
    }
}
