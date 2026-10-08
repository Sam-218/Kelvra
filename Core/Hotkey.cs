using System.Windows.Input;

namespace Kelvra;

/// <summary>A global shortcut such as "Ctrl+Alt+F10", stored as text in settings.</summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    // F-keys with Ctrl+Alt: no program we know of uses them, and AltGr (= Ctrl+Alt) types nothing with F-keys
    public const string DefaultOverlays = "Ctrl+Alt+F10";
    public const string DefaultLock = "Ctrl+Alt+F11";
    public const string DefaultGameOverlays = "Ctrl+Alt+F8";
    public const string DefaultBenchmark = "Ctrl+Alt+F9";

    /// <summary>Shortcuts other popular programs use, so picking one gets a warning even when Windows would allow it.</summary>
    private static readonly Dictionary<string, string> KnownUses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl+Shift+O"] = "Chrome/Edge (bookmarks) and VS Code (go to symbol)",
        ["Ctrl+Shift+L"] = "Excel (filters) and VS Code (select all matches)",
        ["Ctrl+Shift+T"] = "browsers (reopen closed tab)",
        ["Ctrl+Shift+N"] = "browsers (private window) and Explorer (new folder)",
        ["Ctrl+Shift+P"] = "VS Code and Firefox",
        ["Ctrl+Shift+S"] = "most editors (save as)",
        ["Ctrl+Shift+C"] = "browsers (inspect element)",
        ["Ctrl+Shift+I"] = "browsers (developer tools)",
        ["Ctrl+Shift+M"] = "Discord (mute) and browsers",
        ["Ctrl+Shift+D"] = "Discord (deafen)",
        ["Ctrl+Shift+Escape"] = "Windows (Task Manager)",
        ["Alt+F10"] = "NVIDIA ShadowPlay (save replay)",
        ["Alt+F9"] = "NVIDIA ShadowPlay (record)",
        ["Alt+Z"] = "NVIDIA overlay",
        ["Alt+R"] = "AMD Adrenalin overlay",
        ["Ctrl+Shift+F10"] = "JetBrains IDEs (run)",
    };

    public static Hotkey? Parse(string? text) => TryParse(text, out var h) ? h : null;

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None) return false; // two main keys
                    if (raw.Length == 1 && char.IsAsciiDigit(raw[0])) key = Key.D0 + (raw[0] - '0'); // "5" is the 5 key, not Key value 5
                    else if (raw.All(char.IsAsciiDigit) || !Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    if (!IsUsableKey(key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        hotkey = new Hotkey(mods, key);
        return true;
    }

    /// <summary>Keys that can't be the main key of a shortcut (modifiers themselves, IME keys …).</summary>
    public static bool IsUsableKey(Key key) =>
        key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.Apps)
        && Enum.IsDefined(key);

    public override string ToString()
    {
        string mods = ModifierText(Modifiers);
        return mods.Length == 0 ? KeyName(Key) : mods + "+" + KeyName(Key);
    }

    /// <summary>"Ctrl+Alt+Shift" in a fixed order ("" for none).</summary>
    public static string ModifierText(ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Escape => "Escape",
        _ => key.ToString(),
    };

    /// <summary>Win32 MOD_* flags for RegisterHotKey.</summary>
    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? 0x1u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Control) ? 0x2u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? 0x4u : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? 0x8u : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>
    /// Why this shortcut is a poor choice, or null. Global shortcuts swallow the keys in every other program, so a combo
    /// without Ctrl/Alt/Win, or one a popular app uses, gets a warning.
    /// </summary>
    public string? Warning()
    {
        if ((Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            return "Add Ctrl or Alt: without them this key stops working in every other program while Kelvra runs.";
        if (Modifiers is ModifierKeys.Control or ModifierKeys.Alt)
            return "Most programs use this shortcut. Add Shift (or another modifier) so Kelvra doesn't take it away from them.";
        if (Modifiers.HasFlag(ModifierKeys.Control) && Modifiers.HasFlag(ModifierKeys.Alt) && Key is >= Key.A and <= Key.Z)
            return "Ctrl+Alt is the AltGr key on many keyboards, so this can block typing special letters (e.g. Polish ł, ó).";
        return KnownUses.TryGetValue(ToString(), out var who) ? $"Also used by {who}." : null;
    }
}
