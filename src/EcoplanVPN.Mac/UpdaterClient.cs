using System.Net.Http;
using System.Security.Cryptography;

namespace EcoplanVPN.Mac;

internal static class UpdaterClient
{
    public static async Task<UpdateManifest?> CheckAsync(CancellationToken ct)
    {
        if (!Signing.IsConfigured())
        {
            Log.Write("update: нет OTA_PUB — проверку обновлений пропускаю");
            return null;
        }
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Product.Name + "/" + Product.Version);
        UpdateManifest? best = null;
        Version bestVer = new(0, 0);
        foreach (var url in MacUpdate.ManifestUrls())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bust = url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
                           "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var json = await http.GetStringAsync(bust, ct);
                var info = UpdateManifest.Parse(json);
                if (info == null || !info.Verify(Signing.PublicKeySpki))
                {
                    Log.Write("update: " + url + " без валидной подписи — игнорирую");
                    continue;
                }
                if (!Version.TryParse(info.Version, out var remote)) continue;
                if (remote > bestVer)
                {
                    bestVer = remote;
                    best = info;
                }
            }
            catch (Exception ex)
            {
                Log.Write("update: " + url + " — " + ex.Message);
            }
        }
        if (best != null && MacUpdate.IsNewer(best))
        {
            Log.Write("update: доступна v" + best.Version);
            return best;
        }
        return null;
    }

    public static async Task<(bool Ok, string PathOrError)> DownloadAsync(UpdateManifest info, CancellationToken ct)
    {
        if (!info.Verify(Signing.PublicKeySpki))
            return (false, "манифест не подписан");
        Directory.CreateDirectory(AppPaths.UpdatesDir);
        var name = string.IsNullOrWhiteSpace(info.File) ? Product.Name + "-" + info.Version + ".dmg" : info.File;
        var dest = Path.Combine(AppPaths.UpdatesDir, name);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        foreach (var url in info.DownloadUrls().Distinct())
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url, ct);
                if (info.Size > 0 && bytes.Length != info.Size)
                {
                    Log.Write($"update: {url} размер {bytes.Length} != {info.Size}");
                    continue;
                }
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write("update: sha256 не сошёлся");
                    continue;
                }
                await File.WriteAllBytesAsync(dest, bytes, ct);
                return (true, dest);
            }
            catch (Exception ex)
            {
                Log.Write("update: " + url + " — " + ex.Message);
            }
        }
        return (false, "не удалось скачать образ");
    }
}
