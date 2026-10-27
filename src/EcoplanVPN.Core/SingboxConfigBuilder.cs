using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace EcoplanVPN;

public static class SingboxConfigBuilder
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    // sing-box 1.14: TUN от root, без Network Extension.
    // DNS зарубежных имён, включая YouTube, уходит в DoH через прокси.
    // .ru / VK / Яндекс резолвятся локально.
    public static string Build(string serverHost, IEnumerable<string> extraExclude, string logPath)
    {
        var exclude = new JsonArray
        {
            serverHost + "/32",
            "127.0.0.0/8",
            "169.254.0.0/16",
            "10.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16",
            "224.0.0.0/4",
            "240.0.0.0/4",
        };
        foreach (var ip in extraExclude)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == serverHost) continue;
            exclude.Add(ip.Trim() + "/32");
        }

        JsonArray Suffixes(IEnumerable<string> values)
        {
            var arr = new JsonArray();
            foreach (var v in values) arr.Add(v);
            return arr;
        }

        JsonArray Cidrs(IEnumerable<string> values)
        {
            var arr = new JsonArray();
            foreach (var v in values) arr.Add(v);
            return arr;
        }

        var homeDns = Suffixes(RoutePolicy.HomeDirectSuffixes);
        var remoteDns = Suffixes(RoutePolicy.ForceProxySuffixes.Concat(RoutePolicy.TelegramSuffixes));

        var node = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = true,
                ["output"] = logPath,
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "local", ["tag"] = "local" },
                    new JsonObject
                    {
                        ["type"] = "https",
                        ["tag"] = "remote",
                        ["server"] = "1.1.1.1",
                        ["detour"] = "proxy",
                    },
                },
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["domain_suffix"] = homeDns, ["server"] = "local" },
                    new JsonObject { ["domain_suffix"] = remoteDns, ["server"] = "remote" },
                },
                ["final"] = "remote",
                ["strategy"] = "ipv4_only",
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["address"] = Address(),
                    ["mtu"] = 1200,
                    ["auto_route"] = true,
                    ["strict_route"] = false,
                    ["stack"] = "gvisor",
                    ["route_exclude_address"] = exclude,
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "proxy",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = Product.SocksPort,
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" },
            },
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
                ["final"] = "proxy",
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                    new JsonObject
                    {
                        ["process_name"] = ProcessNames(),
                        ["action"] = "route",
                        ["outbound"] = "direct",
                    },
                    new JsonObject
                    {
                        ["domain_suffix"] = Suffixes(RoutePolicy.ForceProxySuffixes),
                        ["action"] = "route",
                        ["outbound"] = "proxy",
                    },
                    new JsonObject
                    {
                        ["domain_suffix"] = Suffixes(RoutePolicy.TelegramSuffixes),
                        ["action"] = "route",
                        ["outbound"] = "proxy",
                    },
                    new JsonObject
                    {
                        ["ip_cidr"] = Cidrs(RoutePolicy.TelegramCidrs),
                        ["action"] = "route",
                        ["outbound"] = "proxy",
                    },
                    new JsonObject
                    {
                        ["domain_suffix"] = Suffixes(RoutePolicy.HomeDirectSuffixes),
                        ["action"] = "route",
                        ["outbound"] = "direct",
                    },
                    new JsonObject
                    {
                        ["ip_cidr"] = Cidrs(RoutePolicy.PoisonCidrs),
                        ["action"] = "reject",
                    },
                    new JsonObject
                    {
                        ["network"] = "udp",
                        ["port"] = 443,
                        ["action"] = "reject",
                    },
                    new JsonObject
                    {
                        ["network"] = "udp",
                        ["action"] = "route",
                        ["outbound"] = "direct",
                    },
                    new JsonObject
                    {
                        ["ip_cidr"] = Cidrs(RoutePolicy.PrivateCidrs.Append(serverHost + "/32")),
                        ["action"] = "route",
                        ["outbound"] = "direct",
                    },
                },
            },
        };
        return node.ToJsonString(Pretty);
    }

    private static JsonArray Address()
    {
        var arr = new JsonArray();
        arr.Add(Product.TunAddress);
        return arr;
    }

    private static JsonArray ProcessNames()
    {
        var arr = new JsonArray();
        arr.Add("xray");
        arr.Add("sing-box");
        arr.Add("EcoplanVPN");
        return arr;
    }
}
