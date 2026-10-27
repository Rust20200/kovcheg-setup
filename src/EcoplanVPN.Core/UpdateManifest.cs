using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EcoplanVPN;

// Тот же конверт, что latest.json у Windows: подпись над
// version\nurl\nsha256\nsize\n
// Mac не ставит .exe. Свой манифест — latest-mac.json.
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("sig")] public string? Sig { get; set; }
    [JsonPropertyName("file")] public string? File { get; set; }
    [JsonPropertyName("mirrors")] public string[]? Mirrors { get; set; }

    public string SignedPayload() =>
        string.Create(CultureInfo.InvariantCulture, $"{Version}\n{Url}\n{Sha256}\n{Size}\n");

    public bool Verify(string? spki)
    {
        if (string.IsNullOrEmpty(Version) || string.IsNullOrEmpty(Url) || string.IsNullOrEmpty(Sha256))
            return false;
        if (!System.Version.TryParse(Version, out _)) return false;
        return Signing.Verify(spki, SignedPayload(), Sig);
    }

    public IEnumerable<string> DownloadUrls()
    {
        if (!string.IsNullOrWhiteSpace(Url)) yield return Url;
        if (Mirrors == null) yield break;
        foreach (var mirror in Mirrors)
        {
            if (!string.IsNullOrWhiteSpace(mirror)) yield return mirror;
        }
    }

    public static UpdateManifest? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json);
        }
        catch
        {
            return null;
        }
    }
}

public static class MacUpdate
{
    public static IEnumerable<string> ManifestUrls()
    {
        yield return "https://raw.githubusercontent.com/" + Product.DistRepo + "/main/latest-mac.json";
        yield return "https://cdn.jsdelivr.net/gh/" + Product.DistRepo + "@main/latest-mac.json";
        yield return Product.RelayMirror + "/dl/latest-mac.json";
        yield return Product.RelayMirror + "/latest-mac.json";
        yield return "https://github.com/" + Product.DistRepo + "/releases/latest/download/latest-mac.json";
    }

    public static bool IsNewer(UpdateManifest info)
    {
        if (!Version.TryParse(info.Version, out var remote)) return false;
        if (!Version.TryParse(Product.Version, out var local)) return false;
        return remote > local;
    }
}
