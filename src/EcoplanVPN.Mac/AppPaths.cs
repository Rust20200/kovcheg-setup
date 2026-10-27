namespace EcoplanVPN.Mac;

internal static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static string LogPath => Path.Combine(DataDir, "ecoplan.log");
    public static string XrayConfig => Path.Combine(DataDir, "xray.json");
    public static string SingboxConfig => Path.Combine(DataDir, "singbox-tun.json");
    public static string SingboxLog => Path.Combine(DataDir, "singbox-tun.log");
    public static string ServersCache => Path.Combine(DataDir, "servers.json");
    public static string SettingsPath => Path.Combine(DataDir, "settings.ini");
    public static string PidFile => Path.Combine(DataDir, "sing-box.pid");
    public static string ProxyState => Path.Combine(DataDir, "proxy-state.txt");
    public static string UpdatesDir => Path.Combine(DataDir, "updates");

    public static string? Tool(string name)
    {
        var dir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(dir, name),
            Path.Combine(dir, "tools", name),
            Path.GetFullPath(Path.Combine(dir, "..", "Resources", name)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void Ensure() => Directory.CreateDirectory(DataDir);

    public static void Private(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }

    private static string ResolveDataDir()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", Product.Name);
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdg = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(xdg, Product.Name);
    }
}

internal static class Settings
{
    public static string? Get(string key)
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath)) return null;
            foreach (var line in File.ReadAllLines(AppPaths.SettingsPath))
            {
                var t = line.Trim();
                if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    return t[(key.Length + 1)..].Trim();
            }
        }
        catch { }
        return null;
    }

    public static void Set(string key, string value)
    {
        AppPaths.Ensure();
        var lines = File.Exists(AppPaths.SettingsPath)
            ? File.ReadAllLines(AppPaths.SettingsPath).ToList()
            : new List<string>();
        var prefix = key + "=";
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = prefix + value;
                replaced = true;
            }
        }
        if (!replaced) lines.Add(prefix + value);
        File.WriteAllLines(AppPaths.SettingsPath, lines);
        AppPaths.Private(AppPaths.SettingsPath);
    }

    public static long Serial() =>
        long.TryParse(Get("ServersSerial"), out var n) ? n : 0;
}

internal static class Log
{
    public static event Action<string>? Line;
    private static readonly object Gate = new();

    public static void Write(string msg)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
        lock (Gate)
        {
            try
            {
                AppPaths.Ensure();
                File.AppendAllText(AppPaths.LogPath, line + "\n");
            }
            catch { }
        }
        try { Line?.Invoke(line); } catch { }
    }
}
