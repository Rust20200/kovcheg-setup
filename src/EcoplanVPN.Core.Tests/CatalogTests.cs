using System.Net.Http;
using Xunit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EcoplanVPN.Tests;

public class CatalogTests
{
    [Fact]
    public void PlaceholderKey_DoesNotVerify()
    {
        Assert.Equal("REPLACE_OTA_PUB", Product.OtaPubPlaceholder);
        Assert.False(Signing.Verify(Product.OtaPubPlaceholder, "payload", "AAAA"));
    }

    [Fact]
    public void SignedCatalog_RelayKeepsItsOwnKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        const string nlUuid = "11111111-1111-4111-8111-111111111111";
        const string relayUuid = "22222222-2222-4222-8222-222222222222";
        var body = new JsonObject
        {
            ["kind"] = "ecoplan-servers",
            ["format"] = 2,
            ["serial"] = 10,
            ["defaults"] = new JsonObject
            {
                ["uuid"] = nlUuid,
                ["pbk"] = "nl-pub",
                ["sid"] = "aabbccdd",
                ["path"] = "/nl-path",
                ["sni"] = "www.intel.com",
            },
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["ip"] = "198.51.100.10",
                    ["label"] = "Нидерланды 1",
                    ["sub"] = "прямое подключение",
                    ["pos"] = "1",
                },
                new JsonObject
                {
                    ["ip"] = "198.51.100.20",
                    ["id"] = "198.51.100.20#nl1",
                    ["label"] = "Через Россию → Нидерланды 1",
                    ["kind"] = "relay",
                    ["sub"] = "если другие блокируются",
                    ["uuid"] = relayUuid,
                    ["pbk"] = "relay-pub",
                    ["sid"] = "00112233",
                    ["path"] = "/relay-path",
                    ["sni"] = "www.ozon.ru",
                    ["tr"] = "xhttp,vision",
                    ["exits"] = "198.51.100.10",
                    ["pos"] = "2",
                },
            },
        };
        var snap = MustParse(Sign(key, body.ToJsonString()), pub);
        var direct = snap.Servers.Single(s => s.Id == "198.51.100.10");
        var relay = snap.Servers.Single(s => s.IsRelay);

        var directProfile = EndpointProfile.Resolve(snap, direct, "vision", out var directErr);
        Assert.Null(directErr);
        Assert.NotNull(directProfile);
        Assert.Equal(nlUuid, directProfile!.Uuid);
        Assert.Equal("www.intel.com", directProfile.Sni);
        Assert.Equal(8443, directProfile.Port);
        Assert.Equal("xtls-rprx-vision", directProfile.Flow);

        var relayXhttp = EndpointProfile.Resolve(snap, relay, "xhttp", out var relayErr);
        Assert.Null(relayErr);
        Assert.NotNull(relayXhttp);
        Assert.Equal(relayUuid, relayXhttp!.Uuid);
        Assert.Equal("relay-pub", relayXhttp.PublicKey);
        Assert.Equal("/relay-path", relayXhttp.Path);
        Assert.Equal("www.ozon.ru", relayXhttp.Sni);
        Assert.Equal(443, relayXhttp.Port);
        Assert.Equal("", relayXhttp.Flow);
        Assert.NotEqual(nlUuid, relayXhttp.Uuid);

        var relayVision = EndpointProfile.Resolve(snap, relay, "vision", out _);
        Assert.Equal(8443, relayVision!.Port);
        Assert.Equal("xtls-rprx-vision", relayVision.Flow);
        Assert.Equal(new[] { "xhttp", "vision" }, EndpointProfile.TransportsFor(relay));
    }

    [Fact]
    public void RelayWithoutOwnKey_DoesNotBorrowDirectKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var body = new JsonObject
        {
            ["kind"] = "ecoplan-servers",
            ["format"] = 2,
            ["serial"] = 3,
            ["defaults"] = new JsonObject
            {
                ["uuid"] = "11111111-1111-4111-8111-111111111111",
                ["pbk"] = "nl-pub",
                ["sid"] = "aabb",
                ["path"] = "/nl",
                ["sni"] = "www.intel.com",
            },
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["ip"] = "198.51.100.20",
                    ["kind"] = "relay",
                    ["pbk"] = "relay-pub",
                    ["sid"] = "ccdd",
                    ["path"] = "/relay",
                    ["sni"] = "www.ozon.ru",
                },
            },
        };
        var snap = MustParse(Sign(key, body.ToJsonString()), pub);
        var relay = snap.Servers.Single();
        var profile = EndpointProfile.Resolve(snap, relay, "xhttp", out var error);
        Assert.Null(profile);
        Assert.Contains("реле", error);
    }

    [Fact]
    public void BadSignature_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = "{\"kind\":\"ecoplan-servers\",\"format\":2,\"serial\":1,\"servers\":[{\"ip\":\"198.51.100.9\"}]}";
        var signed = Sign(key, body);
        var snap = Catalog.Parse(signed, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), out var why);
        Assert.Null(snap);
        Assert.Equal("подпись не сошлась", why);
    }

    [Fact]
    public void ExitIp_MatchesRelayExit()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = new JsonObject
        {
            ["kind"] = "ecoplan-servers",
            ["format"] = 2,
            ["serial"] = 1,
            ["servers"] = new JsonArray
            {
                new JsonObject { ["ip"] = "198.51.100.20", ["kind"] = "relay", ["exits"] = "198.51.100.10" },
            },
        };
        var snap = MustParse(Sign(key, body.ToJsonString()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.True(Catalog.IsOurs(snap, "198.51.100.10"));
        Assert.True(Catalog.IsOurs(snap, "198.51.100.20"));
        Assert.False(Catalog.IsOurs(snap, "203.0.113.50"));
    }

    [Fact]
    public void ServerListUrls_IncludeGithubAndRelay()
    {
        var urls = Catalog.ServerListUrls().ToArray();
        Assert.Contains(urls, u => u.Contains("raw.githubusercontent.com/Rust20200/ecoplan-vpn-dist/main/servers.json", StringComparison.Ordinal));
        Assert.Contains(urls, u => u.StartsWith("http://147.45.48.35/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveServersJson_WhenKeyProvided()
    {
        var key = Environment.GetEnvironmentVariable("ECOPLAN_OTA_PUB");
        if (string.IsNullOrWhiteSpace(key) || key.Contains("REPLACE", StringComparison.Ordinal))
            return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var text = await http.GetStringAsync(
            "https://raw.githubusercontent.com/Rust20200/ecoplan-vpn-dist/main/servers.json");
        var snap = Catalog.Parse(text, key, out var why);
        Assert.True(snap != null, why);
        Assert.Contains(snap!.Servers, s => s.Label.Contains("Нидерланды", StringComparison.Ordinal));
        Assert.Contains(snap.Servers, s => s.IsRelay && s.Label.Contains("Через Россию", StringComparison.Ordinal));
        var relay = snap.Servers.First(s => s.IsRelay);
        var profile = EndpointProfile.Resolve(snap, relay, "xhttp", out var error);
        Assert.Null(error);
        Assert.NotNull(profile);
        Assert.NotEqual(snap.Defaults.GetValueOrDefault("uuid"), profile!.Uuid);
    }

    private static CatalogSnapshot MustParse(string signed, string pub)
    {
        var snap = Catalog.Parse(signed, pub, out var why);
        Assert.True(snap != null, why);
        return snap!;
    }

    private static string Sign(ECDsa key, string payload)
    {
        var sig = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(payload),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
        var env = new JsonObject
        {
            ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)),
            ["sig"] = sig,
        };
        return env.ToJsonString();
    }
}
