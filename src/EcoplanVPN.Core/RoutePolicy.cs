namespace EcoplanVPN;

// Списки как у Windows-клиента. YouTube не в домашнем обходе:
// у обычного провайдера он режется и должен идти через туннель.
public static class RoutePolicy
{
    public static readonly string[] ForceProxySuffixes =
    {
        "instagram.com", "cdninstagram.com", "ig.me",
        "facebook.com", "facebook.net", "fbcdn.net", "fbsbx.com", "meta.com",
        "cursor.sh", "cursor.com", "cursorapi.com", "cursorvm.com", "cursor-cdn.com",
        "anysphere.co", "anysphere.com",
        "grok.com", "x.ai", "livekit.cloud", "livekit.io",
        "openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com",
        "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com",
        "youtube-nocookie.com", "youtubei.googleapis.com", "youtube.googleapis.com",
        "googleusercontent.com",
        "icanhazip.com", "ipify.org", "checkip.amazonaws.com",
    };

    public static readonly string[] HomeDirectSuffixes =
    {
        "ru", "xn--p1ai", "su",
        "vk.com", "vk.me", "vk.cc", "mvk.com",
        "userapi.com", "vk-cdn.net", "vk-portal.net",
        "vkuseraudio.net", "vkuservideo.net", "vkuser.net",
        "yandex.net", "yandex.com", "yastatic.net",
        "mycdn.me", "ozonstatic.net", "wstatic.net", "2gis.com",
    };

    public static readonly string[] TelegramSuffixes =
    {
        "telegram.org", "t.me", "telegram.me", "cdn-telegram.org",
    };

    public static readonly string[] TelegramCidrs =
    {
        "149.154.160.0/20",
        "91.108.4.0/22",
        "91.108.8.0/22",
        "91.108.12.0/22",
        "91.108.16.0/22",
        "91.108.20.0/22",
        "91.108.36.0/23",
        "91.108.56.0/22",
    };

    public static readonly string[] PoisonCidrs = { "8.47.0.0/16", "8.6.0.0/16" };

    public static readonly string[] PrivateCidrs =
    {
        "127.0.0.0/8",
        "169.254.0.0/16",
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "224.0.0.0/4",
        "240.0.0.0/4",
    };
}
