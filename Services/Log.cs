using System.Text;

namespace Kelvra;

/// <summary>
/// A small text log in %APPDATA%\Kelvra\logs (crashes, failed saves, sensor errors). Never throws: logging must not be
/// the thing that breaks the app. Kept to about 1 MB: the previous file is rolled over to kelvra.old.log.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    /// <summary>Where the log lives (tests point it at a temp folder).</summary>
    internal static string Folder { get; set; } = Path.Combine(AppSettings.Dir, "logs");

    public static string FilePath => Path.Combine(Folder, "kelvra.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(file.FullName, Path.Combine(Folder, "kelvra.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // disk full, folder locked … nothing sensible left to do
        }
    }

    /// <summary>The last <paramref name="count"/> lines, for the diagnostics text.</summary>
    public static IReadOnlyList<string> Tail(int count)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath)) return Array.Empty<string>();
                var lines = File.ReadAllLines(FilePath);
                return lines.Skip(Math.Max(0, lines.Length - count)).ToList();
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
