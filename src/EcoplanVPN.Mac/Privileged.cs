using System.Diagnostics;
using System.Text;

namespace EcoplanVPN.Mac;

internal static class Privileged
{
    public static string? ScriptPath => AppPaths.Tool("ecoplan-priv.sh");

    public static async Task<(bool Ok, string Output)> RunAsync(params string[] args)
    {
        if (!OperatingSystem.IsMacOS())
            return (false, "пароль администратора запрашивается только на macOS");
        var script = ScriptPath;
        if (script == null)
            return (false, "нет ecoplan-priv.sh рядом с программой");
        try { File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute); }
        catch { }

        var shell = ShellQuote(script) + " " + string.Join(" ", args.Select(ShellQuote));
        var apple = "do shell script " + AppleQuote(shell) + " with administrator privileges";
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(apple);
        using var proc = Process.Start(psi);
        if (proc == null) return (false, "osascript не запустился");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        var exited = await Task.Run(() => proc.WaitForExit(180000));
        if (!exited)
        {
            try { proc.Kill(true); } catch { }
            return (false, "диалог пароля завис");
        }
        var text = (stdout + "\n" + stderr).Trim();
        if (proc.ExitCode != 0)
        {
            if (text.Contains("User canceled", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("-128", StringComparison.Ordinal))
                return (false, "пароль не введён");
            return (false, string.IsNullOrWhiteSpace(text) ? "osascript завершился с ошибкой" : text);
        }
        return (true, text);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string AppleQuote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            if (ch == '\\' || ch == '"') sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }
}
