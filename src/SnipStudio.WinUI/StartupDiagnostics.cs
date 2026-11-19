using System.Text;

namespace SnipStudio.WinUI;

/// <summary>Small, local startup-only diagnostics; no images, arguments or document content.</summary>
internal static class StartupDiagnostics
{
    private const int MaximumFileBytes = 60 * 1024;
    private const int MaximumEntryCharacters = 8 * 1024;
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Encoding = new(false);

    // This preview-specific path is separate from stable Snip Studio preferences.
    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnipStudio.WinUI3.Preview", "diagnostics", "startup.log");

    internal static void Stage(string name) => Write(name, null);
    internal static void Failure(string name, Exception exception) => Write(name, exception);

    private static void Write(string name, Exception? exception)
    {
        // Diagnostic I/O must never hide, replace or handle the original fault.
        try
        {
            var entry = new StringBuilder();
            entry.Append(DateTimeOffset.UtcNow.ToString("O")).Append(' ').Append(name).AppendLine();
            for (int depth = 0; exception is not null && depth < 3; depth++, exception = exception.InnerException)
            {
                entry.Append("type=").Append(exception.GetType().FullName)
                    .Append(" hresult=0x").Append(exception.HResult.ToString("X8")).AppendLine();
                entry.Append("message=").AppendLine(Limit(Redact(exception.Message), 2_000));
                entry.Append("stack=").AppendLine(Limit(Redact(exception.StackTrace ?? "<unavailable>"), 4_000));
            }
            string text = Limit(Redact(entry.ToString()), MaximumEntryCharacters) + Environment.NewLine;
            byte[] bytes = Encoding.GetBytes(text);
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

    private static string Redact(string text)
    {
        string application = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (application.Length > 0)
        {
            text = text.Replace(application, "[application]", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(application.Replace('\\', '/'), "[application]", StringComparison.OrdinalIgnoreCase);
        }
        if (profile.Length > 0)
        {
            text = text.Replace(profile, "[user-profile]", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(profile.Replace('\\', '/'), "[user-profile]", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private static string Limit(string text, int characters) =>
        text.Length <= characters ? text : text[..characters] + " [truncated]";
}
