namespace EcoplanVPN;

public sealed record EndpointProfile(
    string ServerId,
    string Host,
    int Port,
    string Transport,
    string Uuid,
    string PublicKey,
    string ShortId,
    string Path,
    string Sni,
    string Flow,
    bool IsRelay,
    string[] Exits)
{
    public static readonly string[] DefaultTransports = { "xhttp", "vision" };

    public static string[] TransportsFor(ServerEntry server)
    {
        if (server.Fields.TryGetValue("tr", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (list.Length > 0) return list;
        }
        return DefaultTransports;
    }

    // У реле свои uuid/pbk/sid/sni. Подставлять ключ NL-сервера нельзя:
    // так ломалось «Через Россию» в Windows 1.0.8.
    public static EndpointProfile? Resolve(CatalogSnapshot catalog, ServerEntry server, string transport, out string? error)
    {
        error = null;
        transport = transport.Trim().ToLowerInvariant();
        if (transport is not ("xhttp" or "vision"))
        {
            error = "неизвестный транспорт " + transport;
            return null;
        }

        string? problem = null;
        string? Take(string field, bool required)
        {
            if (server.Fields.TryGetValue(field, out var own) && !string.IsNullOrWhiteSpace(own))
                return own.Trim();
            if (server.IsRelay)
            {
                if (required)
                    problem = "реле без своего " + field + ", ключ прямого сервера не подставляю";
                return null;
            }
            if (catalog.Defaults.TryGetValue(field, out var fallback) && !string.IsNullOrWhiteSpace(fallback))
                return fallback.Trim();
            if (required)
                problem = "нет поля " + field;
            return null;
        }

        var uuid = Take("uuid", true);
        var pub = Take("pbk", true);
        var sid = Take("sid", true);
        var sni = Take("sni", true);
        if (uuid == null || pub == null || sid == null || sni == null)
        {
            error = problem ?? "нет ключей сервера";
            return null;
        }
        var path = Take("path", transport == "xhttp") ?? "/";
        if (problem != null)
        {
            error = problem;
            return null;
        }

        var exits = Array.Empty<string>();
        if (server.Fields.TryGetValue("exits", out var exitRaw) && !string.IsNullOrWhiteSpace(exitRaw))
        {
            exits = exitRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var port = transport == "vision" ? Product.VisionPort : Product.XhttpPort;
        var flow = transport == "vision" ? "xtls-rprx-vision" : "";
        return new EndpointProfile(
            server.Id, server.Host, port, transport, uuid, pub, sid, path, sni, flow, server.IsRelay, exits);
    }

    public string PhoneVisionLink()
    {
        var name = Uri.EscapeDataString("Ecoplan Vision — " + ServerId);
        return "vless://" + Uuid + "@" + Host + ":" + Product.VisionPort +
               "?encryption=none&security=reality&sni=" + Uri.EscapeDataString(Sni) +
               "&fp=" + Product.Fingerprint +
               "&pbk=" + Uri.EscapeDataString(PublicKey) +
               "&sid=" + ShortId +
               "&type=tcp&flow=xtls-rprx-vision#" + name;
    }
}
