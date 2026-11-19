using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private sealed record ReplaceFileBytes(bool Exists, int? Length, string? HexBytes, string? SHA256, string? ErrorType, string? ErrorHResult, string? Error);
    private sealed record ReplaceCall(bool Succeeded, double ElapsedMilliseconds, string? ErrorType, string? HResult, string? Error);

    private static int RunReplaceBaseline(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use a new empty native File.Replace baseline directory.");
            return 2;
        }
        Directory.CreateDirectory(output);
        var observations = new List<object>();
        string? failure = null;
        bool complete = false;
        try
        {
            foreach (FileShare sharing in new[] { FileShare.Read | FileShare.Delete, FileShare.ReadWrite | FileShare.Delete })
                foreach (string held in new[] { "Neither", "PrimaryOnly", "BackupOnly", "Both" })
                    observations.Add(CharacterizeReplace(output, sharing, held));
            complete = true;
            return 0;
        }
        catch (Exception error)
        {
            failure = error.ToString();
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            var result = new
            {
                CheckedUtc = DateTimeOffset.UtcNow,
                CharacterizationComplete = complete,
                CaseCount = observations.Count,
                Scope = "Direct System.IO.File.Replace native contract characterization with no application store, production retry, delete/move fallback, capture or UI. Native-call failures are observed outcomes, not automatically accepted product behavior. A completed characterization is not a passing migration assertion group.",
                Environment = new { OperatingSystem = Environment.OSVersion.VersionString, Runtime = Environment.Version.ToString(), Is64BitProcess = Environment.Is64BitProcess },
                Cases = observations,
                Failure = failure
            };
            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "replace-native-baseline.json"), json, new UTF8Encoding(false));
            Console.WriteLine(json);
        }
    }

    private static object CharacterizeReplace(string output, FileShare sharing, string held)
    {
        string shareName = sharing.HasFlag(FileShare.Write) ? "ReadWriteDelete" : "ReadDelete";
        string folder = Path.Combine(output, shareName + "-" + held);
        Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "replacement.tmp");
        string destination = Path.Combine(folder, "primary.json");
        string backup = Path.Combine(folder, "prior-backup.json");
        byte[] newBytes = Encoding.UTF8.GetBytes("NEW-SOURCE-00007-Aa\n");
        byte[] primaryBytes = Encoding.UTF8.GetBytes("ORIGINAL-PRIMARY-00042-Aa\n");
        byte[] backupBytes = Encoding.UTF8.GetBytes("OLDER-BACKUP-00003-Aa\n");
        File.WriteAllBytes(source, newBytes);
        File.WriteAllBytes(destination, primaryBytes);
        File.WriteAllBytes(backup, backupBytes);
        object before = ObserveReplaceFiles(folder, source, destination, backup);
        FileStream? primaryReader = null, backupReader = null;
        object? primaryHeldAfter = null, backupHeldAfter = null;
        ReplaceCall attempt;
        object whileHeld;
        try
        {
            if (held is "PrimaryOnly" or "Both") primaryReader = new FileStream(destination, FileMode.Open, FileAccess.Read, sharing);
            if (held is "BackupOnly" or "Both") backupReader = new FileStream(backup, FileMode.Open, FileAccess.Read, sharing);
            attempt = ObserveReplaceCall(source, destination, backup);
            whileHeld = ObserveReplaceFiles(folder, source, destination, backup);
            if (primaryReader is not null) primaryHeldAfter = ObserveHeldReplaceBytes(primaryReader);
            if (backupReader is not null) backupHeldAfter = ObserveHeldReplaceBytes(backupReader);
        }
        finally
        {
            primaryReader?.Dispose();
            backupReader?.Dispose();
        }
        object afterClosingHandles = ObserveReplaceFiles(folder, source, destination, backup);
        ReplaceCall? retry = null;
        if (!attempt.Succeeded && File.Exists(source) && File.Exists(destination))
            retry = ObserveReplaceCall(source, destination, backup);
        object afterRetry = ObserveReplaceFiles(folder, source, destination, backup);
        return new
        {
            HeldHandles = held,
            HandleAccess = FileAccess.Read.ToString(),
            Sharing = sharing.ToString(),
            Before = before,
            FirstDirectCall = attempt,
            FilesWhileHandlesHeld = whileHeld,
            PrimaryHeldHandleAfterCall = primaryHeldAfter,
            BackupHeldHandleAfterCall = backupHeldAfter,
            FilesAfterHandlesClosed = afterClosingHandles,
            RetryAfterClosingHandles = retry,
            FilesAfterRetry = afterRetry
        };
    }

    private static ReplaceCall ObserveReplaceCall(string source, string destination, string backup)
    {
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            File.Replace(source, destination, backup);
            clock.Stop();
            return new ReplaceCall(true, clock.Elapsed.TotalMilliseconds, null, null, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            clock.Stop();
            return new ReplaceCall(false, clock.Elapsed.TotalMilliseconds, error.GetType().FullName, $"0x{error.HResult:X8}", error.Message);
        }
    }

    private static object ObserveReplaceFiles(string folder, string source, string destination, string backup) => new
    {
        Source = ObserveReplacePathBytes(source),
        Primary = ObserveReplacePathBytes(destination),
        Backup = ObserveReplacePathBytes(backup),
        DirectoryEntries = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray()
    };

    private static ReplaceFileBytes ObserveReplacePathBytes(string path)
    {
        bool exists = File.Exists(path);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] bytes = ReadHeldStream(stream);
            return new ReplaceFileBytes(exists, bytes.Length, Convert.ToHexString(bytes), Convert.ToHexString(SHA256.HashData(bytes)), null, null, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new ReplaceFileBytes(exists, null, null, null, error.GetType().FullName, $"0x{error.HResult:X8}", error.Message);
        }
    }

    private static ReplaceFileBytes ObserveHeldReplaceBytes(FileStream stream)
    {
        try
        {
            byte[] bytes = ReadHeldStream(stream);
            return new ReplaceFileBytes(true, bytes.Length, Convert.ToHexString(bytes), Convert.ToHexString(SHA256.HashData(bytes)), null, null, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new ReplaceFileBytes(true, null, null, null, error.GetType().FullName, $"0x{error.HResult:X8}", error.Message);
        }
    }
}
