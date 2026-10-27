using System.Security.Cryptography;
using System.Text;

namespace EcoplanVPN;

public static class Signing
{
    // Локальная проверка без пересборки: ECOPLAN_OTA_PUB=SPKI. В релиз ключ штампуется.
    public static string PublicKeySpki
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ECOPLAN_OTA_PUB");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return Product.OtaPubPlaceholder;
        }
    }

    public static bool IsConfigured(string? spki = null)
    {
        spki ??= PublicKeySpki;
        if (string.IsNullOrWhiteSpace(spki)) return false;
        if (spki.Contains("REPLACE", StringComparison.Ordinal)) return false;
        try
        {
            _ = Convert.FromBase64String(spki);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Тот же конверт, что у Windows: ECDSA P-256, SHA-256, подпись DER.
    public static bool Verify(string? spki, string payload, string? sig)
    {
        try
        {
            if (!IsConfigured(spki) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(payload))
                return false;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki!), out _);
            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(payload),
                Convert.FromBase64String(sig),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }
}
