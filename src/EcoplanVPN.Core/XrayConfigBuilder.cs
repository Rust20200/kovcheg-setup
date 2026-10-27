using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace EcoplanVPN;

public static class XrayConfigBuilder
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static string Build(EndpointProfile profile)
    {
        var user = new JsonObject
        {
            ["id"] = profile.Uuid,
            ["encryption"] = "none",
        };
        if (!string.IsNullOrEmpty(profile.Flow))
            user["flow"] = profile.Flow;

        var stream = new JsonObject
        {
            ["network"] = profile.Transport == "vision" ? "tcp" : "xhttp",
            ["security"] = "reality",
            ["realitySettings"] = new JsonObject
            {
                ["serverName"] = profile.Sni,
                ["fingerprint"] = Product.Fingerprint,
                ["publicKey"] = profile.PublicKey,
                ["shortId"] = profile.ShortId,
            },
            ["sockopt"] = new JsonObject
            {
                ["tcpKeepAliveIdle"] = 20,
                ["tcpKeepAliveInterval"] = 20,
                ["tcpNoDelay"] = true,
            },
        };
        if (profile.Transport != "vision")
        {
            stream["xhttpSettings"] = new JsonObject
            {
                ["path"] = profile.Path,
                ["mode"] = "stream-one",
                ["xPaddingBytes"] = "100-1000",
                ["xmux"] = new JsonObject { ["maxConcurrency"] = 1 },
            };
        }

        var proxy = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["address"] = profile.Host,
                        ["port"] = profile.Port,
                        ["users"] = new JsonArray { user },
                    },
                },
            },
            ["streamSettings"] = stream,
        };

        var node = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks",
                    ["listen"] = "127.0.0.1",
                    ["port"] = Product.SocksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true },
                },
                new JsonObject
                {
                    ["tag"] = "http",
                    ["listen"] = "127.0.0.1",
                    ["port"] = Product.HttpPort,
                    ["protocol"] = "http",
                },
            },
            ["outbounds"] = new JsonArray
            {
                proxy,
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = BuildRules(),
            },
        };
        return node.ToJsonString(Pretty);
    }

    private static JsonArray BuildRules()
    {
        JsonArray Domains(IEnumerable<string> suffixes)
        {
            var arr = new JsonArray();
            foreach (var s in suffixes) arr.Add("domain:" + s);
            return arr;
        }

        JsonArray Cidrs(IEnumerable<string> cidrs)
        {
            var arr = new JsonArray();
            foreach (var c in cidrs) arr.Add(c);
            return arr;
        }

        var rules = new JsonArray
        {
            Rule(new JsonObject
            {
                ["inboundTag"] = Tags(),
                ["domain"] = Domains(RoutePolicy.ForceProxySuffixes),
                ["outboundTag"] = "proxy",
            }),
            Rule(new JsonObject
            {
                ["network"] = "udp",
                ["port"] = 443,
                ["outboundTag"] = "block",
            }),
            Rule(new JsonObject
            {
                ["ip"] = Cidrs(RoutePolicy.PoisonCidrs),
                ["outboundTag"] = "block",
            }),
            Rule(new JsonObject
            {
                ["inboundTag"] = Tags(),
                ["domain"] = Domains(RoutePolicy.TelegramSuffixes),
                ["outboundTag"] = "proxy",
            }),
            Rule(new JsonObject
            {
                ["ip"] = Cidrs(RoutePolicy.TelegramCidrs),
                ["outboundTag"] = "proxy",
            }),
            Rule(new JsonObject
            {
                ["inboundTag"] = Tags(),
                ["domain"] = Domains(RoutePolicy.HomeDirectSuffixes),
                ["outboundTag"] = "direct",
            }),
            Rule(new JsonObject
            {
                ["ip"] = Cidrs(RoutePolicy.PrivateCidrs),
                ["outboundTag"] = "direct",
            }),
            Rule(new JsonObject
            {
                ["network"] = "tcp",
                ["port"] = "1-79,81-442,444-852,854-65535",
                ["outboundTag"] = "direct",
            }),
        };
        return rules;
    }

    private static JsonArray Tags()
    {
        var tags = new JsonArray();
        tags.Add("http");
        tags.Add("socks");
        return tags;
    }

    private static JsonObject Rule(JsonObject body)
    {
        body["type"] = "field";
        return body;
    }
}
