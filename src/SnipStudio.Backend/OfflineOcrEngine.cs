#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NativeLine = SnipStudio.TextLineRegion;
using NativeRecovery = SnipStudio.RecognizedTextRecovery;
using NativeResult = SnipStudio.TextRecognitionResult;

namespace SnipStudio.WinUI.Backend;

internal static partial class OfflineOcrEngine
{
    private const int MaximumOutput = 1_048_576;
    private const int MaximumSparseRows = 24;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 12
    };
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);
    internal sealed record Box(float X, float Y, float Width, float Height)
    {
        internal RectangleF ToRectangle() => new(X, Y, Width, Height);
    }
    internal sealed record Word(string Text, Box Bounds);
    internal sealed record Row(string Text, Box Bounds, Word[] Words, double Confidence, int Agreement);
    internal sealed record Response(string Kind, Box? Panel, Row[] Rows);
    private enum ReadMode { Standard, InterruptedLine, LineAnchor }

    internal static Task<NativeResult> RecognizeAsync(Bitmap image, CancellationToken token)
    {
        Task<NativeResult> windows = global::SnipStudio.TextRecognition.RecognizeAsync(image, token);
        string root = Path.Combine(AppContext.BaseDirectory, "ocr");
        if (image.Width is < 80 or > 1800 || image.Height is < 38 or > 900 ||
            (long)image.Width * image.Height > 1_600_000 || !File.Exists(Path.Combine(root, "bundle.json")))
            return windows;
        Bitmap snapshot = image.Clone(new Rectangle(Point.Empty, image.Size), PixelFormat.Format32bppArgb);
        return Task.Run(async () =>
        {
            using (snapshot)
            {
                NativeResult primary = await windows.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if ((primary.Available && !primary.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ||
                    primary.TextAngle is double angle && Math.Abs(angle) > 2)
                    return primary;
                string host = Path.Combine(AppContext.BaseDirectory, "translation", "runtime", "node.exe");
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    budget.CancelAfter(TimeSpan.FromSeconds(12));
                    await WorkerGate.WaitAsync(budget.Token).ConfigureAwait(false);
                    try
                    {
                        var sparse = NativeRecovery.FindSparseColumnRecovery(primary, snapshot.Size, budget.Token, reviewExisting: true);
                        Box[]? slots = sparse?.MissingRows.Concat(sparse.ReviewCells.Where(cell => cell.Runs.Count > 1)
                                .Select(cell => cell.Bounds))
                            .Take(MaximumSparseRows).OrderBy(slot => slot.Y).Select(slot => ToBox(Rectangle.Truncate(slot))).ToArray();
                        if (slots is not { Length: > 0 }) sparse = null;
                        byte[] gray = Luminance(snapshot, budget.Token);
                        Response? response = await ReadLocalAsync(snapshot.Size, gray, root, host, sparse, slots,
                            budget.Token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (response is not null)
                        {
                            if (sparse is null)
                            {
                                int previousPasses = primary.RecoveryPasses;
                                Merge(primary, response, snapshot.Size);
                                if (TerminalOcrRecovery.Apply(primary, response, gray, snapshot.Size, token))
                                {
                                    if (primary.RecoveryPasses == previousPasses) RecordRecovery(primary, response);
                                    else primary.Text = string.Join(Environment.NewLine, primary.Lines.Select(line => line.Text));
                                }
                                await TerminalOcrRecovery.ReviewSwitchesAsync(primary, response, snapshot, budget.Token).ConfigureAwait(false);
                                await TerminalOcrRecovery.ReviewClippedSideLabelAsync(primary, response, snapshot, budget.Token).ConfigureAwait(false);
                            }
                            else MergeSparse(primary, response, sparse, snapshot.Size, budget.Token);
                        }
                        if (sparse is null && response?.Kind != "dark" &&
                            !await TryRecoverFragmentedRowsAsync(primary, snapshot.Size, gray, root, host,
                                budget.Token).ConfigureAwait(false))
                        {
                            await TryRecoverClippedEdgeTextAsync(primary, snapshot.Size, gray, root, host,
                                budget.Token).ConfigureAwait(false);
                            await TryRecoverInterruptedLineAsync(primary, snapshot.Size, gray, root, host,
                                budget.Token).ConfigureAwait(false);
                        }
                    }
                    finally { WorkerGate.Release(); }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    primary.Message += " The local second read reached its time limit.";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                    InvalidOperationException or Win32Exception or ArgumentException or global::SnipStudio.TranslationException)
                {
                    // Log the failure category without capturing text or paths.
                    Trace.TraceWarning("The local OCR worker did not complete ({0}).", error.GetType().Name);
                    primary.Message += " The local second read was unavailable.";
                }
                // A second read can supply a new word split. Verify its separator
                // against the same source pixels before exposing the final text.
                NativeRecovery.RecoverSpacedFilenameExtensions(primary, snapshot, token);
                return primary;
            }
        });
    }

    private static async Task<Response?> ReadLocalAsync(Size size, byte[] gray, string root, string host,
        NativeRecovery.SparseColumnRecovery? sparse, Box[]? slots, CancellationToken token, ReadMode mode = ReadMode.Standard)
    {
        using IDisposable lease = mode == ReadMode.InterruptedLine
            ? TranslationRuntimeTrust.VerifyAndLockInterruptedLineOcr(root, host, token)
            : TranslationRuntimeTrust.VerifyAndLockOcr(root, host, token);
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(
            new { width = size.Width, height = size.Height, gray,
                sparseColumn = sparse is null ? null : ToBox(sparse.Column), sparseRows = sparse is null ? null : slots,
                gapRow = mode == ReadMode.InterruptedLine, gapAnchor = mode == ReadMode.LineAnchor }, Json);
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        OfflineWorkerEnvironment.Configure(start);
        start.ArgumentList.Add(Path.Combine(root, "worker.cjs"));
        using Process process = Process.Start(start) ?? throw new IOException("The local OCR worker did not start.");
        try
        {
            Task<string> result = ReadBoundedAsync(process.StandardOutput, MaximumOutput, token);
            Task<string> error = ReadBoundedAsync(process.StandardError, 16 * 1024, token);
            await process.StandardInput.BaseStream.WriteAsync(request, token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("The local OCR worker did not return a result.");
            return JsonSerializer.Deserialize<Response>(await result.ConfigureAwait(false), Json);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    private static async Task TryRecoverInterruptedLineAsync(NativeResult primary, Size size, byte[] gray,
        string root, string host, CancellationToken token)
    {
        InterruptedLineQuery? query = FindInterruptedLine(primary, size, gray, token);
        if (query is null || !File.Exists(Path.Combine(root, TranslationRuntimeTrust.InterruptedLineModel))) return;

        Response? recovered = await ReadCropAsync(query.Source, ReadMode.InterruptedLine).ConfigureAwait(false);
        if (recovered is not { Kind: "gap-row", Rows.Length: 1 } ||
            MergeInterruptedLine(primary, query, recovered, size, gray, token)) return;

        Response? left = await ReadCropAsync(query.LeftProofSource, ReadMode.LineAnchor).ConfigureAwait(false);
        Response? right = await ReadCropAsync(query.RightProofSource, ReadMode.LineAnchor).ConfigureAwait(false);
        MergeInterruptedLine(primary, query, recovered, size, gray, token, left, right);

        Task<Response?> ReadCropAsync(Rectangle crop, ReadMode mode)
        {
            var pixels = new byte[crop.Width * crop.Height];
            for (int row = 0; row < crop.Height; row++)
            {
                token.ThrowIfCancellationRequested();
                Buffer.BlockCopy(gray, (crop.Top + row) * size.Width + crop.Left,
                    pixels, row * crop.Width, crop.Width);
            }
            return ReadLocalAsync(crop.Size, pixels, root, host, null, null, token, mode);
        }
    }

    private static Box ToBox(Rectangle area) => new(area.X, area.Y, area.Width, area.Height);

    private static async Task<string> ReadBoundedAsync(StreamReader stream, int limit, CancellationToken token)
    {
        var content = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (content.Length + count > limit) throw new IOException("The local OCR worker exceeded its output limit.");
            content.Append(buffer, 0, count);
        }
        return content.ToString();
    }

    private static byte[] Luminance(Bitmap image, CancellationToken token)
    {
        byte[] gray = new byte[image.Width * image.Height];
        byte[] row = new byte[image.Width * 4];
        BitmapData bits = image.LockBits(new Rectangle(Point.Empty, image.Size),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                for (int x = 0; x < image.Width; x++)
                {
                    int offset = x * 4;
                    int intensity = (row[offset] + 6 * row[offset + 1] + 3 * row[offset + 2]) / 10;
                    gray[y * image.Width + x] = (byte)(255 - (255 - intensity) * row[offset + 3] / 255);
                }
            }
        }
        finally { image.UnlockBits(bits); }
        return gray;
    }

    internal static void Merge(NativeResult primary, Response response, Size size)
    {
        if (response.Kind is not ("light" or "dark") || response.Rows is not { Length: > 0 and <= 64 })
            return;
        RectangleF frame = new(0, 0, size.Width, size.Height);
        RectangleF panel = response.Panel?.ToRectangle() ?? frame;
        if (!Valid(panel, frame)) return;
        int accepted = 0;
        foreach (Row row in AgreeingRows(response.Rows))
        {
            RectangleF bounds = row.Bounds.ToRectangle();
            if (primary.ProtectedGraphics.Any(bounds.IntersectsWith) || CreateLine(row, panel) is not NativeLine line) continue;
            bool protectedOverlap = false;
            var matches = new List<int>();
            for (int index = 0; index < primary.Lines.Count; index++)
            {
                NativeLine prior = primary.Lines[index];
                RectangleF overlap = RectangleF.Intersect(bounds, prior.Bounds);
                if (overlap.Width < 1 || overlap.Height < Math.Min(bounds.Height, prior.Bounds.Height) * .5f)
                    continue;
                var tolerance = bounds;
                tolerance.Inflate(3, 3);
                if (!tolerance.Contains(prior.Bounds) || (!prior.ExcludedGraphicBounds.IsEmpty && bounds.IntersectsWith(prior.ExcludedGraphicBounds)))
                { protectedOverlap = true; break; }
                matches.Add(index);
                if (!prior.ExcludedGraphicBounds.IsEmpty)
                    line.ExcludedGraphicBounds = line.ExcludedGraphicBounds.IsEmpty ? prior.ExcludedGraphicBounds :
                        RectangleF.Union(line.ExcludedGraphicBounds, prior.ExcludedGraphicBounds);
            }
            if (protectedOverlap) continue;
            if (matches.Count == 1 && primary.Lines[matches[0]].Text == line.Text) continue;
            if (matches.Count == 0 && response.Kind != "dark" && primary.Lines.Count > 0)
            {
                // New rows must align with existing text in this light column.
                if (!primary.Lines.Any(p => Math.Abs(p.Bounds.Left - bounds.Left) <= 8))
                    continue;
            }
            int insertion = matches.Count > 0 ? matches[0] :
                primary.Lines.FindIndex(prior => prior.Bounds.Top > bounds.Top && prior.Bounds.Left >= panel.Left - 2);
            if (insertion < 0) insertion = primary.Lines.Count;
            for (int index = matches.Count - 1; index >= 0; index--) primary.Lines.RemoveAt(matches[index]);
            primary.Lines.Insert(insertion, line);
            accepted++;
        }
        if (accepted == 0) return;
        primary.Available = true;
        if (string.IsNullOrWhiteSpace(primary.Language)) primary.Language = "en";
        RecordRecovery(primary, response);
    }

    internal static void MergeSparse(NativeResult primary, Response response, NativeRecovery.SparseColumnRecovery sparse,
        Size size, CancellationToken token)
    {
        if (response.Kind != "sparse-column" || response.Rows is not { Length: > 0 and <= 64 } ||
            response.Panel?.ToRectangle() != (RectangleF)sparse.Column) return;

        var missing = new NativeResult { Available = true };
        var split = new NativeResult { Available = true };
        foreach (Row row in AgreeingRows(response.Rows))
        {
            if (CreateLine(row, sparse.Column) is not NativeLine line) continue;
            // Whole-cell reads of split names require all three image views
            // to agree. The merger also checks the original character runs.
            if (row.Agreement >= 3 && row.Confidence >= .99 && sparse.ReviewCells.Any(cell =>
                cell.Runs.Count > 1 && cell.Bounds.Contains(line.Bounds))) split.Lines.Add(line);
            else missing.Lines.Add(line);
        }
        var source = new Rectangle(Point.Empty, size);
        int inserted = NativeRecovery.MergeSparseColumnRecovery(primary, missing, sparse, source, token);
        int rejoined = NativeRecovery.MergeSparseColumnReview(primary, split, sparse, source, token);
        if (inserted + rejoined > 0) RecordRecovery(primary, response);
    }

    private static IEnumerable<Row> AgreeingRows(IEnumerable<Row> rows)
    {
        foreach (var band in rows.Where(IsCandidate).GroupBy(row => (row.Bounds.Y, row.Bounds.Height)))
        {
            Row[] best = band.OrderByDescending(candidate => candidate.Agreement).Take(2).ToArray();
            if (best.Length == 1 || best[0].Agreement > best[1].Agreement) yield return best[0];
        }
    }

    private static NativeLine? CreateLine(Row row, RectangleF panel)
    {
        RectangleF bounds = row.Bounds.ToRectangle();
        if (!Valid(bounds, panel)) return null;
        var line = new NativeLine { Bounds = bounds, Text = row.Text, RecognitionClipBounds = panel };
        int offset = 0;
        foreach (Word word in row.Words)
        {
            if (word is null || word.Bounds is null || string.IsNullOrWhiteSpace(word.Text) || !Valid(word.Bounds.ToRectangle(), bounds))
                return null;
            int next = row.Text.IndexOf(word.Text, offset, StringComparison.Ordinal);
            if (next < 0 || !OnlySpaces(row.Text.AsSpan(offset, next - offset))) return null;
            offset = next + word.Text.Length;
            line.Words.Add(new global::SnipStudio.TextWordRegion { Text = word.Text, Bounds = word.Bounds.ToRectangle() });
        }
        return OnlySpaces(row.Text.AsSpan(offset)) ? line : null;
    }

    private static void RecordRecovery(NativeResult primary, Response response)
    {
        primary.Text = string.Join(Environment.NewLine, primary.Lines.Select(line => line.Text));
        primary.RecoveryRawText += (primary.RecoveryRawText.Length == 0 ? "" : Environment.NewLine) +
            string.Join(Environment.NewLine, response.Rows.Select(row => row?.Text ?? ""));
        primary.RecoveryPasses++;
        primary.Message = "Text ready. The local second read recovered text. Review identifiers against the image.";
    }

    private static bool IsCandidate(Row row) => row is not null && row.Bounds is not null &&
        row.Words is { Length: > 0 and <= 128 } && row.Agreement >= 2 && double.IsFinite(row.Confidence) &&
        row.Confidence is >= .95 and <= 1 && !string.IsNullOrWhiteSpace(row.Text) &&
        row.Text.Length <= 512 && !row.Text.Any(char.IsControl);

    private static bool OnlySpaces(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
            if (!char.IsWhiteSpace(character)) return false;
        return true;
    }

    private static bool Valid(RectangleF rectangle, RectangleF container) =>
        float.IsFinite(rectangle.X) && float.IsFinite(rectangle.Y) &&
        float.IsFinite(rectangle.Width) && float.IsFinite(rectangle.Height) &&
        rectangle.Width >= 1 && rectangle.Height >= 1 && container.Contains(rectangle);
}
