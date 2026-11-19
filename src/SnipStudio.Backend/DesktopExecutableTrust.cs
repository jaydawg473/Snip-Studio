#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography.X509Certificates;

namespace SnipStudio.WinUI.Backend;

/// <summary>In-process Authenticode verification; never launches a helper.</summary>
public static class DesktopExecutableTrust
{
    /// <summary>
    /// Requires a trusted Authenticode signature and a verified signing
    /// timestamp. Preserves the original third-party publisher; optionally
    /// requires Automation Forge's exact publisher name. The caller must retain
    /// its own read-only FileStream with FileShare.Read through Process.Start
    /// when binding this proof to execution; a returned string is not a lease.
    /// </summary>
    public static string VerifySignedTimestampedExecutable(string path, bool requireAutomationForge = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            path.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
            throw new ArgumentException("Choose a fully qualified path to an executable (.exe).", nameof(path));
        string fullPath = Path.GetFullPath(path);
        // Deny replacement/writes while the two native trust readers inspect
        // the file. Execution callers retain an outer read lease through launch.
        using FileStream fileLease = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        nint fileInfoPointer = 0;
        bool fileInfoInitialized = false;
        TrustData data = new()
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            StateAction = 1, ProviderFlags = 0x00000080 | 0x00001000 // cached revocation; do not relax trust when offline
        };
        Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
            Marshal.StructureToPtr(new TrustFileInfo { Size = (uint)Marshal.SizeOf<TrustFileInfo>(), FilePath = fullPath }, fileInfoPointer, false);
            fileInfoInitialized = true;
            data.FileInfo = fileInfoPointer;
            int result = WinVerifyTrust(0, ref action, ref data);
            if (result != 0)
                throw new InvalidOperationException("The executable's Authenticode trust could not be verified (0x" + unchecked((uint)result).ToString("X8") + ").");
            nint provider = WTHelperProvDataFromStateData(data.StateData);
            nint signingData = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint timestampData = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, true, 0);
            if (signingData == 0 || timestampData == 0)
                throw new InvalidOperationException("The executable requires a verified timestamped application signature.");
            ProviderSigner signing = Marshal.PtrToStructure<ProviderSigner>(signingData);
            ProviderSigner timestamp = Marshal.PtrToStructure<ProviderSigner>(timestampData);
            if (signing.Error != 0 || signing.CounterSigners == 0 || timestamp.Error != 0 ||
                signing.CertificateCount == 0 || signing.Certificates == 0)
                throw new InvalidOperationException("The executable requires a verified timestamped application signature.");
            // Read the signer certificate actually selected by WinVerifyTrust,
            // rather than an unrelated first/nested signature from the file.
            ProviderCertificate selectedCertificate = Marshal.PtrToStructure<ProviderCertificate>(signing.Certificates);
            if (selectedCertificate.CertificateContext == 0)
                throw new InvalidOperationException("The verified executable publisher certificate is unavailable.");
            using X509Certificate2 signer = new(selectedCertificate.CertificateContext);
            string publisher = signer.GetNameInfo(X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(publisher))
                throw new InvalidOperationException("The verified executable has no identifiable publisher.");
            if (requireAutomationForge && !string.Equals(publisher, "Automation Forge LLC", StringComparison.Ordinal))
                throw new InvalidOperationException("This action requires an Automation Forge signed application.");
            return publisher;
        }
        finally
        {
            if (data.StateData != 0) { data.StateAction = 2; WinVerifyTrust(0, ref action, ref data); }
            if (fileInfoPointer != 0)
            {
                if (fileInfoInitialized) Marshal.DestroyStructure<TrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFileInfo
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string FilePath; public nint FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public nint PolicyCallbackData, SipClientData; public uint UiChoice, RevocationChecks, UnionChoice;
        public nint FileInfo; public uint StateAction; public nint StateData, UrlReference; public uint ProviderFlags, UiContext; public nint SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderSigner
    {
        public uint Size; public FILETIME VerifyAsOf; public uint CertificateCount; public nint Certificates; public uint SignerType;
        public nint Signer; public uint Error, CounterSigners; public nint CounterSignerData, ChainContext;
    }
    // Only the documented prefix of CRYPT_PROVIDER_CERT is needed. The rest
    // remains owned by WinVerifyTrust until WTD_STATEACTION_CLOSE.
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate
    { public uint Size; public nint CertificateContext; }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint owner, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperProvDataFromStateData(nint stateData);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
}
