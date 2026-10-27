using System.Diagnostics;
using Xunit;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EcoplanVPN.Tests;

public class ConfigTests
{
    [Fact]
    public void YoutubeStaysOnTheProxy()
    {
        Assert.Contains("youtube.com", RoutePolicy.ForceProxySuffixes);
        Assert.Contains("googlevideo.com", RoutePolicy.ForceProxySuffixes);
        Assert.DoesNotContain(RoutePolicy.HomeDirectSuffixes, s =>
            s.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("ytimg", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("ggpht", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XrayConfig_UsesRelayMaterialAndSplit()
    {
        var profile = SampleRelay();
        var node = JsonNode.Parse(XrayConfigBuilder.Build(profile))!;
        var vnext = node["outbounds"]![0]!["settings"]!["vnext"]![0]!;
        Assert.Equal("198.51.100.20", vnext["address"]!.GetValue<string>());
        Assert.Equal(443, vnext["port"]!.GetValue<int>());
        Assert.Equal("relay-uuid", vnext["users"]![0]!["id"]!.GetValue<string>());
        var reality = node["outbounds"]![0]!["streamSettings"]!["realitySettings"]!;
        Assert.Equal("www.ozon.ru", reality["serverName"]!.GetValue<string>());
        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", reality["publicKey"]!.GetValue<string>());
        Assert.Equal("firefox", reality["fingerprint"]!.GetValue<string>());
        Assert.Equal("/relay-path", node["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["path"]!.GetValue<string>());

        var rules = node["routing"]!["rules"]!.AsArray();
        Assert.Contains(rules, r => HasDomain(r, "domain:youtube.com") && r!["outboundTag"]!.GetValue<string>() == "proxy");
        Assert.Contains(rules, r => HasDomain(r, "domain:ru") && r!["outboundTag"]!.GetValue<string>() == "direct");
        var directDomains = rules
            .Where(r => r?["outboundTag"]?.GetValue<string>() == "direct")
            .SelectMany(r => r!["domain"]?.AsArray() ?? new JsonArray())
            .Select(d => d!.GetValue<string>());
        Assert.DoesNotContain(directDomains, d => d.Contains("youtube", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VisionConfig_SetsFlowAndDropsXhttp()
    {
        var profile = SampleRelay() with { Transport = "vision", Port = 8443, Flow = "xtls-rprx-vision" };
        var node = JsonNode.Parse(XrayConfigBuilder.Build(profile))!;
        var stream = node["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal("tcp", stream["network"]!.GetValue<string>());
        Assert.Null(stream["xhttpSettings"]);
        Assert.Equal("xtls-rprx-vision", node["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]![0]!["flow"]!.GetValue<string>());
    }

    [Fact]
    public void SingboxConfig_ExcludesServerAndSendsYoutubeViaProxy()
    {
        var json = SingboxConfigBuilder.Build("198.51.100.20", new[] { "198.51.100.10" }, "/tmp/ecoplan-singbox.log");
        var node = JsonNode.Parse(json)!;
        var exclude = node["inbounds"]![0]!["route_exclude_address"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.Contains("198.51.100.20/32", exclude);
        Assert.Contains("198.51.100.10/32", exclude);
        var rules = node["route"]!["rules"]!.AsArray();
        Assert.Contains(rules, r => HasSuffix(r, "youtube.com") && r!["outbound"]!.GetValue<string>() == "proxy");
        Assert.Contains(rules, r => HasSuffix(r, "ru") && r!["outbound"]!.GetValue<string>() == "direct");
        Assert.Equal("remote", node["dns"]!["final"]!.GetValue<string>());
        var dnsRules = node["dns"]!["rules"]!.AsArray();
        Assert.Contains(dnsRules, r => HasSuffix(r, "youtube.com") && r!["server"]!.GetValue<string>() == "remote");
        Assert.Contains(dnsRules, r => HasSuffix(r, "ru") && r!["server"]!.GetValue<string>() == "local");
    }

    [Fact]
    public void GeneratedConfigs_PassUpstreamCheckersWhenPresent()
    {
        var profile = SampleRelay();
        var dir = Path.Combine(Path.GetTempPath(), "ecoplan-cfg-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var xrayPath = Path.Combine(dir, "xray.json");
            var sbPath = Path.Combine(dir, "singbox.json");
            File.WriteAllText(xrayPath, XrayConfigBuilder.Build(profile));
            File.WriteAllText(sbPath, SingboxConfigBuilder.Build(profile.Host, profile.Exits, Path.Combine(dir, "singbox.log")));
            RunIfPresent("ECOPLAN_XRAY", new[] { "run", "-test", "-c", xrayPath });
            RunIfPresent("ECOPLAN_SINGBOX", new[] { "check", "-c", sbPath });
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void UpdateManifest_RoundTrip()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var info = new UpdateManifest
        {
            Version = "1.0.10",
            Url = "http://147.45.48.35/dl/EcoplanVPN-1.0.10.dmg",
            Sha256 = new string('a', 64),
            Size = 42,
            Notes = "mac",
        };
        var sig = Convert.ToBase64String(key.SignData(
            System.Text.Encoding.UTF8.GetBytes(info.SignedPayload()),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
        info.Sig = sig;
        var parsed = UpdateManifest.Parse(System.Text.Json.JsonSerializer.Serialize(info));
        Assert.NotNull(parsed);
        Assert.True(parsed!.Verify(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        parsed.Size = 43;
        Assert.False(parsed.Verify(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        Assert.True(MacUpdate.IsNewer(info));
        Assert.Contains(MacUpdate.ManifestUrls(), u => u.EndsWith("latest-mac.json", StringComparison.Ordinal));
    }

    private static void RunIfPresent(string env, string[] args)
    {
        var bin = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(bin) || !File.Exists(bin)) return;
        var psi = new ProcessStartInfo(bin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi);
        Assert.NotNull(proc);
        var stdout = proc!.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(20000));
        Assert.True(proc.ExitCode == 0, stdout + stderr);
    }

    private static bool HasDomain(JsonNode? rule, string domain) =>
        rule?["domain"]?.AsArray().Any(d => d!.GetValue<string>() == domain) == true;

    private static bool HasSuffix(JsonNode? rule, string suffix) =>
        rule?["domain_suffix"]?.AsArray().Any(d => d!.GetValue<string>() == suffix) == true;

    private static EndpointProfile SampleRelay() => new(
        "198.51.100.20",
        "198.51.100.20",
        443,
        "xhttp",
        "relay-uuid",
        "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
        "00112233",
        "/relay-path",
        "www.ozon.ru",
        "",
        true,
        new[] { "198.51.100.10" });
}
