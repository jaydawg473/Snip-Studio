using System.Diagnostics;
using System.Text;

namespace SnipStudio.WinUI;

/// <summary>Small, local startup-only diagnostics; no images, arguments or document content.</summary>
internal static class StartupDiagnostics
{
    private const int MaximumFileBytes = 60 * 1024;
    private const int MaximumEntryCharacters = 8 * 1024;
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Encoding = new(false);

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnipStudio", "diagnostics", "startup.log");

    internal static void Stage(string name) => Write(name, null);
    internal static void Failure(string name, Exception exception) => Write(name, exception);

    private static void Write(string name, Exception? exception)
    {
        // Diagnostic I/O must never hide, replace or handle the original fault.
        try
        {
            byte[] bytes = Encoding.GetBytes(FormatEntry(DateTimeOffset.UtcNow, name, exception));
            lock (Sync)
            {
                string path = LogPath;
                string diagnostics = Path.GetDirectoryName(path)!;
                string profile = Path.GetDirectoryName(diagnostics)!;
                EnsurePlainDirectory(profile);
                EnsurePlainDirectory(diagnostics);
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                // Bound the entire log below 64 KB. Rotate by truncating prior
                // startup entries rather than reading or exporting user data.
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumFileBytes)
                    File.WriteAllText(path, "Earlier startup diagnostics were truncated to keep this log bounded." + Environment.NewLine, Encoding);
                using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (output.Length + bytes.Length <= MaximumFileBytes) output.Write(bytes);
            }
        }
        catch (Exception)
        {
            // Logging is best-effort; preserve the original startup behavior.
        }
    }

    private static void EnsurePlainDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The diagnostics directory must not be redirected.");
        }
        else Directory.CreateDirectory(path);
    }

    internal static string FormatEntry(DateTimeOffset timestamp, string stage, Exception? exception)
    {
        // Callers provide fixed stage labels. Exception messages and source
        // file names can contain private paths or data from outside the app.
        var entry = new StringBuilder();
        entry.Append(timestamp.ToString("O")).Append(' ').Append(stage).AppendLine();
        for (int depth = 0; exception is not null && depth < 3; depth++, exception = exception.InnerException)
        {
            entry.Append("type=").Append(exception.GetType().FullName)
                .Append(" hresult=0x").Append(exception.HResult.ToString("X8")).AppendLine();
            string trace = new StackTrace(exception, fNeedFileInfo: false).ToString();
            entry.Append("stack=").AppendLine(trace.Length == 0 ? "<unavailable>" : Limit(trace.TrimEnd(), 4_000));
        }
        return Limit(entry.ToString().TrimEnd('\r', '\n'), MaximumEntryCharacters) + Environment.NewLine;
    }

    private static string Limit(string text, int characters) =>
        text.Length <= characters ? text : text[..characters] + " [truncated]";
}
