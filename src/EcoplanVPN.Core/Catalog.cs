using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace EcoplanVPN;

public sealed record ServerEntry(
    string Id,
    string Host,
    string Label,
    string Subtitle,
    bool IsRelay,
    string? OffNote,
    int Pos,
    IReadOnlyDictionary<string, string> Fields);

public sealed record CatalogSnapshot(
    long Serial,
    IReadOnlyList<ServerEntry> Servers,
    IReadOnlyDictionary<string, string> Defaults);

public static class Catalog
{
    public const string Kind = "ecoplan-servers";

    public static IEnumerable<string> ServerListUrls(CatalogSnapshot? known = null)
    {
        yield return "https://raw.githubusercontent.com/" + Product.DistRepo + "/main/servers.json";
        yield return "https://cdn.jsdelivr.net/gh/" + Product.DistRepo + "@main/servers.json";
        yield return Product.RelayMirror + "/servers.json";
        yield return Product.RelayMirror + "/dl/servers.json";
        if (known == null) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in known.Servers)
        {
            if (!seen.Add(server.Host)) continue;
            yield return "http://" + server.Host + "/servers.json";
        }
    }

    public static CatalogSnapshot? Parse(string text, string? spki, out string why)
    {
        why = "";
        try
        {
            var root = JsonNode.Parse(text);
            var payloadB64 = root?["payload"]?.GetValue<string>();
            var sig = root?["sig"]?.GetValue<string>();
            if (string.IsNullOrEmpty(payloadB64) || string.IsNullOrEmpty(sig))
            {
                why = "нет payload/sig";
                return null;
            }
            string payload;
            try
            {
                payload = Encoding.UTF8.GetString(Convert.FromBase64String(payloadB64));
            }
            catch
            {
                why = "payload не base64";
                return null;
            }
            if (!Signing.Verify(spki, payload, sig))
            {
                why = Signing.IsConfigured(spki)
                    ? "подпись не сошлась"
                    : "нет публичного ключа подписи (OTA_PUB)";
                return null;
            }
            var body = JsonNode.Parse(payload);
            if (body?["kind"]?.GetValue<string>() != Kind)
            {
                why = "не тот kind";
                return null;
            }
            long serial = body["serial"]?.GetValue<long>() ?? 0;
            int format = 1;
            if (body["format"] is JsonValue fmt && fmt.TryGetValue<int>(out var parsedFmt))
                format = parsedFmt;
            var defaults = Fields(body["defaults"] as JsonObject);
            var list = new List<(string Id, string Host, Dictionary<string, string> Fields)>();
            if (body["servers"] is JsonArray servers)
            {
                foreach (var item in servers)
                {
                    if (item is not JsonObject obj) continue;
                    var ip = obj["ip"]?.GetValue<string>()?.Trim();
                    if (ip == null || !IPAddress.TryParse(ip, out var address) ||
                        address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    var idRaw = obj["id"]?.GetValue<string>()?.Trim();
                    var id = string.IsNullOrEmpty(idRaw) ? ip : idRaw;
                    if (list.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var fields = Fields(obj);
                    if (format < 2)
                    {
                        fields.Remove("label");
                        fields.Remove("sub");
                    }
                    list.Add((id, ip, fields));
                }
            }
            if (list.Count == 0)
            {
                why = "пустой список";
                return null;
            }
            return new CatalogSnapshot(serial, Decorate(list), defaults);
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return null;
        }
    }

    public static bool IsOurs(CatalogSnapshot? catalog, string? ip)
    {
        if (catalog == null || string.IsNullOrWhiteSpace(ip)) return false;
        ip = ip.Trim();
        foreach (var server in catalog.Servers)
        {
            if (string.Equals(server.Host, ip, StringComparison.OrdinalIgnoreCase)) return true;
            if (!server.Fields.TryGetValue("exits", out var exits) || string.IsNullOrWhiteSpace(exits))
                continue;
            foreach (var exit in exits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(exit, ip, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<ServerEntry> Decorate(
        List<(string Id, string Host, Dictionary<string, string> Fields)> raw)
    {
        var directIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var item in raw)
        {
            if (IsRelay(item.Fields)) continue;
            directIndex[item.Id] = n++;
        }
        var result = new List<ServerEntry>();
        for (var i = 0; i < raw.Count; i++)
        {
            var item = raw[i];
            var relay = IsRelay(item.Fields);
            var label = Field(item.Fields, "label");
            if (string.IsNullOrWhiteSpace(label))
                label = FallbackLabel(item.Id, relay, directIndex.TryGetValue(item.Id, out var di) ? di : 0);
            var sub = Field(item.Fields, "sub");
            if (string.IsNullOrWhiteSpace(sub))
                sub = relay ? "если другие блокируются" : "прямое подключение";
            var off = OffNote(item.Fields);
            var pos = int.TryParse(Field(item.Fields, "pos"), out var p) ? p : i + 1;
            result.Add(new ServerEntry(item.Id, item.Host, label, sub, relay, off, pos, item.Fields));
        }
        return result.OrderBy(s => s.Pos).ThenBy(s => s.Label, StringComparer.Ordinal).ToArray();
    }

    public static string FallbackLabel(string id, bool relay, int directIndex)
    {
        if (relay)
        {
            return id.EndsWith("#nl1", StringComparison.OrdinalIgnoreCase)
                ? "Через Россию → Нидерланды 1"
                : "Через Россию → Нидерланды 2";
        }
        return directIndex switch
        {
            0 => "Нидерланды 1",
            1 => "Нидерланды 2",
            _ => "Нидерланды " + (directIndex + 1),
        };
    }

    private static bool IsRelay(Dictionary<string, string> fields) =>
        string.Equals(Field(fields, "kind"), "relay", StringComparison.OrdinalIgnoreCase);

    private static string? OffNote(Dictionary<string, string> fields)
    {
        var text = Field(fields, "off");
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase)) return null;
        return text;
    }

    private static string? Field(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static Dictionary<string, string> Fields(JsonObject? obj)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (obj == null) return dict;
        foreach (var item in obj)
        {
            if (item.Value is JsonValue value && value.TryGetValue<string>(out var text) && text != null)
                dict[item.Key] = text;
        }
        return dict;
    }
}

public static class CatalogClient
{
    public static async Task<CatalogSnapshot?> RefreshAsync(
        string cachePath,
        long minSerial,
        string? spki,
        Action<string>? log,
        CancellationToken ct)
    {
        CatalogSnapshot? cached = null;
        if (File.Exists(cachePath))
        {
            try
            {
                cached = Catalog.Parse(await File.ReadAllTextAsync(cachePath, ct), spki, out var why);
                if (cached == null)
                    log?.Invoke("servers: кэш отклонён (" + why + ")");
            }
            catch (Exception ex)
            {
                log?.Invoke("servers: кэш не прочитан — " + ex.Message);
            }
        }

        var floor = Math.Max(minSerial, cached?.Serial ?? 0);
        var urls = Catalog.ServerListUrls(cached).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Product.Name + "/" + Product.Version);

        var bag = new System.Collections.Concurrent.ConcurrentBag<(string Url, string Text, CatalogSnapshot Snap)>();
        await Task.WhenAll(urls.Select(async url =>
        {
            try
            {
                var bust = url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") +
                           "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var text = await http.GetStringAsync(bust, ct);
                var snap = Catalog.Parse(text, spki, out var why);
                if (snap == null)
                {
                    log?.Invoke($"servers: {url} — отклонён ({why})");
                    return;
                }
                bag.Add((url, text, snap));
            }
            catch (Exception ex)
            {
                log?.Invoke("servers: " + url + " — " + ex.Message);
            }
        }));
        var fetched = bag.ToList();

        if (fetched.Count == 0) return cached;
        var best = fetched.OrderByDescending(x => x.Snap.Serial).First();
        if (best.Snap.Serial < floor)
        {
            log?.Invoke($"servers: serial {best.Snap.Serial} старее {floor} — не беру");
            return cached;
        }
        if (cached != null && best.Snap.Serial == cached.Serial)
            return cached;
        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, best.Text, ct);
            File.Move(tmp, cachePath, true);
            TryPrivate(cachePath);
        }
        catch (Exception ex)
        {
            log?.Invoke("servers: кэш не записан — " + ex.Message);
        }
        log?.Invoke($"servers: список v{best.Snap.Serial} с {best.Url}");
        return best.Snap;
    }

    private static void TryPrivate(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }
}
