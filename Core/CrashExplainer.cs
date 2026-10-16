using System.Globalization;
using System.Text.Json;

namespace Kelvra;

/// <summary>The crash kinds Kelvra records (see <see cref="CrashWatcher"/>).</summary>
public static class CrashKinds
{
    public const string AppCrash = "app-crash", AppHang = "app-hang", DriverReset = "driver-reset", BlueScreen = "bluescreen", Shutdown = "shutdown";
}

/// <summary>A crash in plain words: a title, the short culprit ("NVIDIA driver"), what probably happened and what to try.</summary>
public sealed record CrashExplanation(string Title, string Culprit, string Reason, string Advice);

/// <summary>
/// Turns the cryptic parts of a crash record (module names, exception and bugcheck codes) into plain words, from the
/// table in Assets\crash-explain.json (embedded): adding a driver or a code is a data change.
/// </summary>
public static class CrashExplainer
{
    private sealed record ModuleInfo(string Prefix, string Culprit, string Reason, string Advice);
    private sealed record ExceptionInfo(string Name, string Meaning);
    private sealed record BugcheckInfo(string Name, string Reason, string Advice);
    private sealed record Table(List<ModuleInfo> Modules, Dictionary<string, ExceptionInfo> Exceptions, Dictionary<string, BugcheckInfo> Bugchecks);

    private static readonly Lazy<Table> Data = new(Load);

    private static Table Load()
    {
        using var stream = typeof(CrashExplainer).Assembly.GetManifestResourceStream("crash-explain.json")!;
        return JsonSerializer.Deserialize<Table>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static CrashExplanation Explain(CrashRecord c)
    {
        string app = AppName(c.App);
        var module = ModuleOf(c.Module);
        switch (c.Kind)
        {
            case CrashKinds.AppCrash:
            {
                if (module != null)
                    return new($"{app} crashed", module.Culprit, Fill(module.Reason, app), module.Advice);
                string? code = NormalizeCode(c.Code);
                if (code != null && Data.Value.Exceptions.TryGetValue(code, out var ex))
                    return new($"{app} crashed", ex.Name, $"{app} crashed because {ex.Meaning}{InModule(c.Module)}.",
                               code is "c000001d" or "c0000096" ? "Undo CPU overclocks and undervolts, then update the app." : "Update the app; reinstall it if it keeps happening.");
                return new($"{app} crashed", c.Module is { Length: > 0 } m ? m : "Unknown",
                           $"{app} closed unexpectedly{InModule(c.Module)}.", "Update the app; reinstall it if it keeps happening.");
            }
            case CrashKinds.AppHang:
                return new($"{app} stopped responding", "Not responding",
                           $"{app} froze and was closed.", "Update the app. If several apps freeze, check that the drives are healthy (Kelvra → System).");
            case CrashKinds.DriverReset:
                return new("Graphics driver reset", module?.Culprit ?? "Graphics driver",
                           "The screen froze for a moment and Windows restarted the graphics driver.",
                           module?.Advice ?? "Update or clean-install the graphics driver; undo graphics card overclocks.");
            case CrashKinds.BlueScreen:
            {
                uint? bug = ParseBugcheck(c.Code);
                if (bug is uint b && Data.Value.Bugchecks.TryGetValue(b.ToString("x", CultureInfo.InvariantCulture), out var info))
                    return new($"Blue screen: {info.Name}", info.Name, info.Reason, info.Advice);
                return new("Blue screen", c.Code ?? "Unknown stop code",
                           $"Windows stopped with an error{(c.Code != null ? $" ({c.Code})" : "")}.",
                           "Update drivers and the BIOS; undo overclocks if any; test the RAM if it repeats.");
            }
            default:
                return new("Unexpected shutdown", "Power lost",
                           "The PC went off without shutting down: a power cut, the power button held down, or a freeze too hard for Windows to write a blue screen.",
                           "If it happens under load: check temperatures and the power supply. Otherwise check the power cable and the socket.");
        }
    }

    private static ModuleInfo? ModuleOf(string? module)
    {
        if (string.IsNullOrEmpty(module)) return null;
        string m = Path.GetFileNameWithoutExtension(module).ToLowerInvariant();
        return Data.Value.Modules.FirstOrDefault(x => m.StartsWith(x.Prefix, StringComparison.Ordinal));
    }

    private static string Fill(string text, string app) => text.Replace("{app}", app);
    private static string InModule(string? module) => string.IsNullOrEmpty(module) ? "" : $" (in {module})";

    /// <summary>"Cyberpunk2077.exe" → "Cyberpunk2077".</summary>
    public static string AppName(string exe) => exe.Length == 0 ? "An app" : Path.GetFileNameWithoutExtension(exe);

    /// <summary>"0xc0000005", "C0000005" or 3221225477 → "c0000005".</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string c = code.Trim().ToLowerInvariant();
        if (c.StartsWith("0x")) c = c[2..];
        if (uint.TryParse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) && c.Length == 8) return hex.ToString("x8", CultureInfo.InvariantCulture);
        if (uint.TryParse(code.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec)) return dec.ToString("x8", CultureInfo.InvariantCulture);
        return c;
    }

    /// <summary>"0x00000124", "0x124" or "292" → 0x124; the first word of a bugcheck text ("0x00000124 (0x0, …)") too.</summary>
    public static uint? ParseBugcheck(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string first = code.Trim().Split(' ', '(')[0];
        if (first.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(first[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex)) return hex;
        return uint.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dec) ? dec : null;
    }
}
