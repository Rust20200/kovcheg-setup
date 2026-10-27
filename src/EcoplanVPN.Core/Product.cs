namespace EcoplanVPN;

// Имена и порты совпадают с Windows-клиентом EcoplanVPN 1.0.9.
// Секреты сюда не пишутся: OTA_PUB штампует scripts/stamp.py из client.env,
// а uuid/pbk/sid приходят из подписанного servers.json.
public static class Product
{
    public const string Name = "EcoplanVPN";
    public const string Version = "1.0.9";
    public const string DistRepo = "Rust20200/ecoplan-vpn-dist";

    // Публичные зеркала списка. Сами ключи в списке, не в исходниках.
    public const string RelayMirror = "http://147.45.48.35";

    // Заглушка. scripts/stamp.py подставляет OTA_PUB (SPKI) из client.env.
    public const string OtaPubPlaceholder = "REPLACE_OTA_PUB";

    public const int SocksPort = 10818;
    public const int HttpPort = 10819;
    public const int VisionPort = 8443;
    public const int XhttpPort = 443;
    public const string Fingerprint = "firefox";
    public const string TunAddress = "10.0.85.1/24";

    public const string XrayVersion = "26.10.21";
    public const string SingBoxVersion = "1.14.4";
}
