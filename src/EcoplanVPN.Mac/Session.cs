using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace EcoplanVPN.Mac;

internal enum ConnectMode { Tun, Proxy }

internal sealed class Session
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _xray;

    public bool IsConnected { get; private set; }
    public bool IsBusy { get; private set; }
    public string? Transport { get; private set; }
    public string? ExitIp { get; private set; }
    public ConnectMode Mode { get; private set; }

    public async Task<(bool Ok, string Message)> ConnectAsync(
        CatalogSnapshot catalog,
        ServerEntry server,
        ConnectMode mode,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IsBusy = true;
            await StopUnlockedAsync();
            if (!OperatingSystem.IsMacOS())
                return (false, "Подключение работает на macOS. Эта сборка собрана для Mac.");

            var xray = AppPaths.Tool("xray");
            var sing = AppPaths.Tool("sing-box");
            if (xray == null)
                return (false, "В комплекте нет xray. Пересоберите scripts/build-mac.sh.");
            if (mode == ConnectMode.Tun && sing == null)
                return (false, "В комплекте нет sing-box. Для режима «Только прокси» ядро tun не нужно.");

            AppPaths.Ensure();
            var transports = EndpointProfile.TransportsFor(server);
            string? last = null;
            EndpointProfile? live = null;
            foreach (var transport in transports)
            {
                ct.ThrowIfCancellationRequested();
                var profile = EndpointProfile.Resolve(catalog, server, transport, out var error);
                if (profile == null)
                {
                    last = error ?? "не собрался профиль";
                    Log.Write("core: " + transport + " — " + last);
                    continue;
                }
                Log.Write($"core: пробую {transport} {profile.Host}:{profile.Port} sni={profile.Sni} relay={profile.IsRelay}");
                var json = XrayConfigBuilder.Build(profile);
                await File.WriteAllTextAsync(AppPaths.XrayConfig, json, ct);
                AppPaths.Private(AppPaths.XrayConfig);
                if (!await StartXrayAsync(xray, ct))
                {
                    last = "xray не запустился";
                    StopXray();
                    continue;
                }
                var ip = await ProbeExitAsync(ct);
                if (ip != null && Catalog.IsOurs(catalog, ip))
                {
                    live = profile;
                    ExitIp = ip;
                    Transport = transport;
                    Log.Write("core: выход " + ip + " через " + transport);
                    break;
                }
                last = ip == null
                    ? transport + " не дал выход"
                    : transport + " вышел в " + ip + ", это не наш сервер";
                Log.Write("core: " + last);
                StopXray();
            }
            if (live == null)
                return (false, "Ядро не поднялось — не достучался до сервера. " + (last ?? ""));

            if (mode == ConnectMode.Tun)
            {
                var excludes = catalog.Servers.Select(s => s.Host).Concat(live.Exits);
                var sb = SingboxConfigBuilder.Build(live.Host, excludes, AppPaths.SingboxLog);
                await File.WriteAllTextAsync(AppPaths.SingboxConfig, sb, ct);
                AppPaths.Private(AppPaths.SingboxConfig);
                var check = await RunAsync(sing!, "check", "-c", AppPaths.SingboxConfig);
                if (check.Code != 0)
                {
                    StopXray();
                    return (false, "Конфиг tun не прошёл проверку: " + Tail(check.Text));
                }
                var uid = Environment.GetEnvironmentVariable("UID") ?? "";
                if (string.IsNullOrEmpty(uid))
                {
                    var id = await RunAsync("/usr/bin/id", "-u");
                    uid = id.Text.Trim();
                }
                Log.Write("tun: запрашиваю пароль администратора");
                var started = await Privileged.RunAsync(
                    "tun-start", sing!, AppPaths.SingboxConfig, AppPaths.PidFile, AppPaths.SingboxLog, uid);
                if (!started.Ok)
                {
                    StopXray();
                    return (false, "TUN не включился. " + started.Output);
                }
                await Task.Delay(1200, ct);
                if (!PidAlive(AppPaths.PidFile))
                {
                    StopXray();
                    return (false, "sing-box сразу завершился. " + TailFile(AppPaths.SingboxLog));
                }
            }
            else
            {
                Log.Write("proxy: запрашиваю пароль администратора");
                var proxy = await Privileged.RunAsync(
                    "proxy-on", "127.0.0.1", Product.HttpPort.ToString(), AppPaths.ProxyState);
                if (!proxy.Ok)
                {
                    StopXray();
                    return (false, "Системный прокси не включился. " + proxy.Output);
                }
            }

            IsConnected = true;
            Mode = mode;
            var where = mode == ConnectMode.Tun ? "весь Mac" : "системный прокси";
            return (true, "Подключено (" + Transport + ", " + where + "). РФ напрямую, остальное через " + server.Label + ".");
        }
        catch (OperationCanceledException)
        {
            await StopUnlockedAsync();
            return (false, "Отменено.");
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            IsBusy = true;
            await StopUnlockedAsync();
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    private async Task StopUnlockedAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (File.Exists(AppPaths.PidFile))
            {
                var stop = await Privileged.RunAsync("tun-stop", AppPaths.PidFile);
                Log.Write("tun: stop " + (stop.Ok ? "ok" : stop.Output));
            }
            if (File.Exists(AppPaths.ProxyState))
            {
                var off = await Privileged.RunAsync("proxy-off", AppPaths.ProxyState);
                Log.Write("proxy: off " + (off.Ok ? "ok" : off.Output));
            }
        }
        StopXray();
        IsConnected = false;
        Transport = null;
        ExitIp = null;
    }

    private async Task<bool> StartXrayAsync(string bin, CancellationToken ct)
    {
        StopXray();
        var psi = new ProcessStartInfo(bin)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(bin)!,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(AppPaths.XrayConfig);
        _xray = Process.Start(psi);
        if (_xray == null) return false;
        _ = Pump(_xray);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (_xray.HasExited)
            {
                Log.Write("xray: процесс завершился сразу");
                return false;
            }
            if (await PortOpenAsync(Product.HttpPort) && await PortOpenAsync(Product.SocksPort))
                return true;
            await Task.Delay(200, ct);
        }
        return false;
    }

    private void StopXray()
    {
        try
        {
            if (_xray is { HasExited: false })
            {
                _xray.Kill(true);
                _xray.WaitForExit(3000);
            }
        }
        catch { }
        _xray?.Dispose();
        _xray = null;
    }

    private static async Task Pump(Process proc)
    {
        try
        {
            while (true)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line == null) break;
                if (line.Length > 0) Log.Write("xray: " + line);
            }
        }
        catch { }
    }

    private static async Task<string?> ProbeExitAsync(CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy("http://127.0.0.1:" + Product.HttpPort),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        string[] urls = { "https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com" };
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = (await http.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(text, out _)) return text;
            }
            catch (Exception ex)
            {
                Log.Write("probe: " + url + " — " + ex.Message);
            }
        }
        return null;
    }

    private static async Task<bool> PortOpenAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(connect, Task.Delay(400));
            return done == connect && tcp.Connected;
        }
        catch { return false; }
    }

    private static bool PidAlive(string pidfile)
    {
        try
        {
            var text = File.ReadAllText(pidfile).Trim();
            if (!int.TryParse(text, out var pid) || pid <= 0) return false;
            // sing-box запущен от root, поэтому Process.GetProcessById может не сработать.
            var psi = new ProcessStartInfo("/bin/ps", "-p " + pid)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<(int Code, string Text)> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi);
        if (proc == null) return (1, "не запустился");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, (stdout + stderr).Trim());
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.TakeLast(3));
    }

    private static string TailFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return "лога нет";
            return Tail(string.Join('\n', File.ReadLines(path).TakeLast(8)));
        }
        catch { return "лог не прочитан"; }
    }
}
