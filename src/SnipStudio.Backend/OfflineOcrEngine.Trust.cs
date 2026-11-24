#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace SnipStudio.WinUI.Backend;

internal static partial class TranslationRuntimeTrust
{
    // The two offline workers use the same original publisher-signed host.
    private const string OcrBundleManifestSha256 = "B4A72B85DCD5DF0C98E9A5B8B4D78A942E20F8BF3364EB1D3F268C18A4CEC10D";
    internal const string InterruptedLineModel = "models/PP-OCRv6_rec_small.onnx";

    internal static IDisposable VerifyAndLockOcr(string root, string runtime, CancellationToken token) =>
        VerifyOcrBundle(root, runtime, loadInterruptedLineModel: false, token);

    internal static IDisposable VerifyAndLockInterruptedLineOcr(string root, string runtime, CancellationToken token) =>
        VerifyOcrBundle(root, runtime, loadInterruptedLineModel: true, token);

    private static IDisposable VerifyOcrBundle(string root, string runtime, bool loadInterruptedLineModel, CancellationToken token)
    {
        var lease = new RuntimeLease();
        try
        {
            string manifestPath = ChildPath(root, "bundle.json");
            using JsonDocument manifest = OpenManifest();
            JsonElement files = manifest.RootElement.GetProperty("files");
            if (manifest.RootElement.GetProperty("version").GetInt32() != 1 || files.GetArrayLength() is < 4 or > 240)
                throw new InvalidDataException("The local OCR manifest is invalid.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifestPath };
            string interruptedLineModel = ChildPath(root, InterruptedLineModel);
            byte[] buffer = new byte[128 * 1024];
            foreach (JsonElement file in files.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                string path = ChildPath(root, file.GetProperty("path").GetString() ?? "");
                string pin = file.GetProperty("sha256").GetString() ?? "";
                if (!paths.Add(path) || pin.Length != 64 || !IsHex(pin) ||
                    Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".node")
                    throw new InvalidDataException("The local OCR asset list is invalid.");
                // This model is loaded only by an explicitly requested line reread.
                if (!loadInterruptedLineModel && path.Equals(interruptedLineModel, StringComparison.OrdinalIgnoreCase))
                    continue;
                FileStream asset = lease.Open(path);
                if (Hash(asset, token, buffer) != pin)
                    throw new InvalidDataException("A local OCR asset differs from the reviewed release.");
            }
            if (!paths.Contains(ChildPath(root, "worker.cjs")))
                throw new InvalidDataException("The local OCR worker is missing.");
            if (loadInterruptedLineModel && !paths.Contains(interruptedLineModel))
                throw new InvalidDataException("The OCR line reread model is missing.");
            RejectUnlistedCode(root, paths, token);
            FileStream node = lease.Open(runtime);
            if (Hash(node, token, buffer) != NodeSha256)
                throw new InvalidDataException("The original local OCR host differs from the reviewed release.");
            VerifyOriginalNodeSignature(runtime, node);
            return lease;

            JsonDocument OpenManifest()
            {
                FileStream stream = lease.Open(manifestPath);
                if (stream.Length is <= 0 or > MaximumManifestBytes || Hash(stream, token) != OcrBundleManifestSha256)
                    throw new InvalidDataException("The local OCR manifest differs from the reviewed release.");
                stream.Position = 0;
                return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 6 });
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
