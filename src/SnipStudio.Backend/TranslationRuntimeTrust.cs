#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SnipStudio.WinUI.Backend;

/// <summary>
/// Preflight for the reviewed local translator. No executable helper is started
/// to validate executable signatures. Each invocation rechecks and locks the
/// original publisher binary plus the complete pinned code/model manifest.
/// </summary>
internal static class TranslationRuntimeTrust
{
    private const string ManifestSha256 = "DEC18E73BED3F1F29BC677D230616D12E0E7907B37F182704C639552BB8A7D18";
    private const string NodeSha256 = "86FC4785327DB5F5C7558DC6156C543B433C313E813A222E088086222AB4AFA6";
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static IDisposable VerifyAndLock(string rootDirectory, string runtimePath, string workerPath, CancellationToken token)
    {
        var lease = new RuntimeLease();
        try
        {
            token.ThrowIfCancellationRequested();
            string root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // A file's read lease alone does not pin every directory name used
            // by CreateProcess. Lock that path ancestry before checking bytes,
            // then retain those directory handles for the child's lifetime.
            lease.LockDirectoryChain(root);
            string manifestPath = ChildPath(root, "bundle.json");
            FileStream manifest = lease.Open(manifestPath);
            if (manifest.Length <= 0 || manifest.Length > MaximumManifestBytes || Hash(manifest, token) != ManifestSha256)
                throw Blocked("The offline translation manifest does not match the reviewed release.");
            manifest.Position = 0;
            using JsonDocument document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement contents = document.RootElement;
            if (contents.GetProperty("version").GetInt32() != 1)
                throw Blocked("The offline translation manifest version is not supported.");
            JsonElement files = contents.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 113)
                throw Blocked("The complete reviewed offline translation bundle is required.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifestPath };
            string expectedRuntime = ChildPath(root, "runtime/node.exe");
            string expectedWorker = ChildPath(root, "worker.js");
            if (!string.Equals(expectedRuntime, Path.GetFullPath(runtimePath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(expectedWorker, Path.GetFullPath(workerPath), StringComparison.OrdinalIgnoreCase))
                throw Blocked("The offline translation worker or runtime path is not the reviewed path.");
            FileStream? runtime = null;
            byte[] buffer = new byte[128 * 1024];
            foreach (JsonElement file in files.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                string relative = file.GetProperty("path").GetString() ?? "";
                string expected = file.GetProperty("sha256").GetString() ?? "";
                string path = ChildPath(root, relative);
                if (!paths.Add(path) || expected.Length != 64 || !IsHex(expected))
                    throw Blocked("The offline translation file list is invalid.");
                FileStream stream = lease.Open(path);
                if (Hash(stream, token, buffer) != expected.ToUpperInvariant())
                    throw Blocked("An offline translation component no longer matches the reviewed release. Restore the signed package before translating.");
                stream.Position = 0;
                int first = stream.ReadByte(), second = stream.ReadByte();
                stream.Position = 0;
                if (first == 'M' && second == 'Z' && !string.Equals(path, expectedRuntime, StringComparison.OrdinalIgnoreCase))
                    throw Blocked("The offline translation bundle contains an unreviewed executable component.");
                if (string.Equals(path, expectedRuntime, StringComparison.OrdinalIgnoreCase))
                {
                    if (expected.ToUpperInvariant() != NodeSha256)
                        throw Blocked("The offline translation runtime pin is invalid.");
                    runtime = stream;
                }
            }
            if (runtime is null || !paths.Contains(expectedWorker))
                throw Blocked("The original offline translation runtime or worker is missing.");
            RejectUnlistedCode(root, paths, token);
            token.ThrowIfCancellationRequested();
            VerifyOriginalNodeSignature(expectedRuntime, runtime);
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static string Hash(FileStream stream, CancellationToken token, byte[]? buffer = null)
    {
        stream.Position = 0;
        buffer ??= new byte[128 * 1024];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsHex(string value)
    {
        foreach (char character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F')) return false;
        return true;
    }

    private static string ChildPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw Blocked("The offline translation component path is invalid.");
        string[] parts = relative.Replace('\\', '/').Split('/');
        string path = root;
        foreach (string part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.'))
                throw Blocked("The offline translation component path is invalid.");
            path = Path.Combine(path, part);
            EnsureOrdinaryOrCloud(path);
        }
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Blocked("The offline translation component is outside its bundle.");
        return full;
    }

    private static void EnsureOrdinaryOrCloud(string path)
    {
        if (global::SnipStudio.OfflineTranslationService.HasUnsupportedReparsePoint(path))
            throw Blocked("Linked offline translation files are not supported. Restore the complete signed package.");
    }

    private static void RejectUnlistedCode(string root, HashSet<string> declared, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        int directories = 0, files = 0;
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            if (++directories > 256) throw Blocked("The offline translation bundle contains too many directories.");
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                EnsureOrdinaryOrCloud(child);
                pending.Push(child);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                EnsureOrdinaryOrCloud(file);
                if (++files > 512) throw Blocked("The offline translation bundle contains too many files.");
                string extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is ".exe" or ".dll" or ".node" or ".js" or ".mjs" or ".cjs" or ".json" or ".wasm")
                    if (!declared.Contains(Path.GetFullPath(file)))
                        throw Blocked("The offline translation bundle contains an unreviewed code module. Restore the signed package before translating.");
            }
        }
    }

    private static void VerifyOriginalNodeSignature(string path, FileStream lockedRuntime)
    {
        IntPtr fileName = Marshal.StringToCoTaskMemUni(path);
        IntPtr fileInfoPointer = IntPtr.Zero;
        var fileInfo = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = fileName,
            FileHandle = lockedRuntime.SafeFileHandle.DangerousGetHandle()
        };
        var data = new WinTrustData
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>(),
            UIChoice = 2,                       // WTD_UI_NONE
            RevocationChecks = 1,               // WTD_REVOKE_WHOLECHAIN
            UnionChoice = 1,                    // WTD_CHOICE_FILE
            StateAction = 1,                    // WTD_STATEACTION_VERIFY
            ProviderFlags = 0x80 | 0x1000        // Chain excluding root, cache-only URLs.
        };
        Guid action = GenericVerifyV2;
        try
        {
            fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            data.FileInfo = fileInfoPointer;
            int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0)
                throw Blocked("Windows could not validate the original offline translator signature and cached certificate status (0x" + unchecked((uint)status).ToString("X8") + "). Translation was not started.");
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero)
                throw Blocked("Windows did not return the offline translator's trusted signer.");
            IntPtr signerPointer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signerPointer == IntPtr.Zero)
                throw Blocked("Windows did not return the offline translator's trusted signer.");
            ProviderSigner signer = Marshal.PtrToStructure<ProviderSigner>(signerPointer);
            if (signer.Error != 0 || signer.CounterSignerCount == 0)
                throw Blocked("The original offline translator requires a valid trusted timestamp. Translation was not started.");
            IntPtr timestampPointer = WTHelperGetProvSignerFromChain(provider, 0, true, 0);
            if (timestampPointer == IntPtr.Zero)
                throw Blocked("Windows did not return the offline translator's trusted timestamp.");
            ProviderSigner timestamp = Marshal.PtrToStructure<ProviderSigner>(timestampPointer);
            if (timestamp.Error != 0 || timestamp.CertificateCount == 0 || timestamp.CertificateChain == IntPtr.Zero)
                throw Blocked("The original offline translator timestamp could not be validated.");
            IntPtr certificatePointer = WTHelperGetProvCertFromChain(signerPointer, 0);
            if (certificatePointer == IntPtr.Zero)
                throw Blocked("The offline translator publisher certificate is unavailable.");
            ProviderCertificateHead certificate = Marshal.PtrToStructure<ProviderCertificateHead>(certificatePointer);
            if (certificate.Certificate == IntPtr.Zero)
                throw Blocked("The offline translator publisher certificate is unavailable.");
            using var publisher = new X509Certificate2(certificate.Certificate);
            if (!string.Equals(publisher.GetNameInfo(X509NameType.SimpleName, false), "OpenJS Foundation", StringComparison.Ordinal))
                throw Blocked("The offline translator does not have its original OpenJS Foundation publisher signature.");
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                data.StateAction = 2;           // WTD_STATEACTION_CLOSE, including failures.
                _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }
            Marshal.FreeCoTaskMem(fileName);
            GC.KeepAlive(lockedRuntime);
        }
    }

    private static global::SnipStudio.TranslationException Blocked(string message) =>
        new(global::SnipStudio.TranslationErrorKind.ServiceUnavailable, message);

    private sealed class RuntimeLease : IDisposable
    {
        private readonly List<FileStream> files = new();
        private readonly List<SafeFileHandle> directories = new();
        private readonly HashSet<string> lockedDirectories = new(StringComparer.OrdinalIgnoreCase);

        internal void LockDirectoryChain(string directory)
        {
            string full = Path.GetFullPath(directory);
            string volume = Path.GetPathRoot(full) ?? throw Blocked("The offline translation folder must have an absolute path.");
            string current = volume;
            LockDirectory(current);
            foreach (string part in full[volume.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                LockDirectory(current);
            }
        }

        private void LockDirectory(string path)
        {
            if (lockedDirectories.Contains(path)) return;
            EnsureOrdinaryOrCloud(path);
            SafeFileHandle handle = OpenDirectory(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw Blocked("The offline translation folder could not be locked against replacement. Translation was not started.");
            }
            directories.Add(handle);
            lockedDirectories.Add(path);
            EnsureOrdinaryOrCloud(path);
        }

        internal FileStream Open(string path)
        {
            LockDirectoryChain(Path.GetDirectoryName(path) ?? throw Blocked("The offline translation component folder is unavailable."));
            EnsureOrdinaryOrCloud(path);
            // Open the leaf itself rather than following a swapped symbolic
            // link between the attribute check and FileStream construction.
            SafeFileHandle handle = OpenDirectory(path, 0x80000000, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
            try
            {
                if (handle.IsInvalid || !GetAttributeTagInformation(handle, 9, out AttributeTagInfo information, 8) ||
                    (information.Attributes & 0x10) != 0 ||
                    ((information.Attributes & 0x400) != 0 && !global::SnipStudio.OfflineTranslationService.IsAllowedCloudReparseTag(information.Tag)))
                    throw Blocked("The offline translation component could not be opened without following an unreviewed link.");
                var stream = new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false);
                files.Add(stream);
                return stream;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        public void Dispose()
        {
            foreach (FileStream stream in files) stream.Dispose();
            files.Clear();
            for (int index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
            directories.Clear();
            lockedDirectories.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        internal uint Attributes, Tag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint Size;
        internal IntPtr FilePath, FileHandle, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint Size;
        internal IntPtr PolicyCallbackData, SipClientData;
        internal uint UIChoice, RevocationChecks, UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData, UrlReference;
        internal uint ProviderFlags, UIContext;
        internal IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        internal uint Size;
        internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
        internal uint CertificateCount;
        internal IntPtr CertificateChain;
        internal uint SignerType;
        internal IntPtr Signer;
        internal uint Error, CounterSignerCount;
        internal IntPtr CounterSigners, ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificateHead
    {
        internal uint Size;
        internal IntPtr Certificate;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeTagInformation(SafeFileHandle handle, int informationClass,
        out AttributeTagInfo information, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificateIndex);
}
