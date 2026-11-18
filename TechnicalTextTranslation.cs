using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio
{
    // Translate prose around technical spans, never the identifiers themselves.
    // No placeholder is sent through an engine that could translate/corrupt it.
    internal sealed class TechnicalTextTranslationService : ISegmentedTextTranslationService
    {
        private readonly ITextTranslationService inner;
        internal TechnicalTextTranslationService(ITextTranslationService inner)
        { if (inner == null) throw new ArgumentNullException("inner"); this.inner = inner; }

        public Task<IList<TranslationLanguage>> GetLanguagesAsync(CancellationToken token)
        { return inner.GetLanguagesAsync(token); }

        public async Task<TranslationResult> TranslateAsync(string text, string source, string target, CancellationToken token)
        {
            TechnicalTextTranslation.ValidateSegments(new string[] { text });
            token.ThrowIfCancellationRequested();
            if (!TechnicalTextTranslation.HasProtectedSpans(text))
                return await inner.TranslateAsync(text, source, target, token).ConfigureAwait(false);
            TranslationResult first = null;
            IList<string> values = await TechnicalTextTranslation.TranslateSegmentsAsync(new string[] { text },
                async delegate(IList<string> chunks, CancellationToken cancellation)
                {
                    ISegmentedTextTranslationService segmented = inner as ISegmentedTextTranslationService;
                    if (segmented != null)
                        return await segmented.TranslateSegmentsAsync(chunks, source, target, cancellation).ConfigureAwait(false);
                    List<string> translated = new List<string>();
                    foreach (string chunk in chunks)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        TranslationResult result = await inner.TranslateAsync(chunk, source, target, cancellation).ConfigureAwait(false);
                        if (result == null) throw TechnicalTextTranslation.InvalidResponse();
                        if (first == null) first = result;
                        translated.Add(result.Text);
                    }
                    return translated;
                }, token).ConfigureAwait(false);
            return new TranslationResult(values[0], first == null ? source : first.SourceCode, target,
                first == null ? null : first.DetectedConfidence, first == null ? "Technical identifiers preserved" : first.ProviderName);
        }

        public Task<IList<string>> TranslateSegmentsAsync(IList<string> segments, string source, string target, CancellationToken token)
        {
            return TechnicalTextTranslation.TranslateSegmentsAsync(segments,
                async delegate(IList<string> chunks, CancellationToken cancellation)
                {
                    ISegmentedTextTranslationService segmented = inner as ISegmentedTextTranslationService;
                    if (segmented != null)
                        return await segmented.TranslateSegmentsAsync(chunks, source, target, cancellation).ConfigureAwait(false);
                    List<string> translated = new List<string>();
                    foreach (string chunk in chunks)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        TranslationResult result = await inner.TranslateAsync(chunk, source, target, cancellation).ConfigureAwait(false);
                        if (result == null) throw TechnicalTextTranslation.InvalidResponse();
                        translated.Add(result.Text);
                    }
                    return translated;
                }, token);
        }

        // This temporary adapter borrows the dialog-owned service.
        public void Dispose() { }
    }

    internal static class TechnicalTextTranslation
    {
        private const int MaximumTextLength = 16000, MaximumSegments = 256, MaximumResponseCharacters = 1024 * 1024;
        private static readonly Regex Url = Pattern(@"\b(?:https?://|ftp://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase);
        private static readonly Regex Email = Pattern(@"(?<![A-Za-z0-9_.+-])[A-Za-z0-9_.+%-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,63}(?![A-Za-z0-9_-])", RegexOptions.None);
        private static readonly Regex Domain = Pattern(@"(?<![A-Za-z0-9_@])(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+(?:com|org|net|edu|gov|mil|io|dev|app|co|us|uk|de|fr|ca|au|info|biz|ai)(?::[0-9]{1,5})?(?:[/?#][^\s<>""']*)?(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase);
        private static readonly Regex Token = Pattern(@"[\p{L}\p{N}_][\p{L}\p{N}_.\-/\\]*", RegexOptions.None);
        private static readonly Regex Extension = Pattern(@"(?<![\p{L}\p{N}_])\.(?:pdf|step|stp|iges|igs|sldprt|sldasm|slddrw|dwg|dxf|png|jpe?g|gif|bmp|tiff?|svg|webp|heic|txt|csv|tsv|xlsx?|xlsm|docx?|pptx?|rtf|zip|7z|rar|gz|tar|mp4|mov|avi|mkv|webm|mp3|wav|flac|json|xml|yaml|yml|ini|cfg|exe|dll|msi|ps1|js|ts|py|cs|html?|css)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase);
        private static readonly Regex ContextIdentifier = Pattern(@"\b(?:part(?:\s+(?:number|no\.?))?|p/n|sku|model|id)(?:\s*[:#=]\s*|\s+)(?<value>[A-Za-z0-9][A-Za-z0-9_.\-/]*\d[A-Za-z0-9_.\-/]*)", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "pdf", "step", "stp", "iges", "igs", "sldprt", "sldasm", "slddrw", "dwg", "dxf", "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "svg", "webp", "heic", "txt", "csv", "tsv", "xls", "xlsx", "xlsm", "doc", "docx", "ppt", "pptx", "rtf", "zip", "7z", "rar", "gz", "tar", "mp4", "mov", "avi", "mkv", "webm", "mp3", "wav", "flac", "json", "xml", "yaml", "yml", "ini", "cfg", "exe", "dll", "msi", "ps1", "js", "ts", "py", "cs", "htm", "html", "css" };

        internal static bool HasProtectedSpans(string text) { return FindSpans(text ?? "").Count > 0; }

        // Delegate seam tests the actual planning/batching/reassembly path using
        // deterministic in-memory translators, without a server or model process.
        internal static async Task<IList<string>> TranslateSegmentsAsync(IList<string> segments,
            Func<IList<string>, CancellationToken, Task<IList<string>>> translate, CancellationToken token)
        {
            if (translate == null) throw new ArgumentNullException("translate");
            ValidateSegments(segments);
            token.ThrowIfCancellationRequested();
            List<SegmentPlan> plans = new List<SegmentPlan>();
            List<string> chunks = new List<string>();
            foreach (string segment in segments)
            {
                token.ThrowIfCancellationRequested();
                SegmentPlan plan = new SegmentPlan(); plans.Add(plan);
                List<Span> spans = FindSpans(segment);
                if (spans.Count == 0)
                { AddTranslated(plan, chunks, segment, false); continue; }
                int cursor = 0;
                foreach (Span span in spans)
                {
                    AddTranslated(plan, chunks, segment.Substring(cursor, span.Start - cursor), true);
                    plan.Pieces.Add(new Piece(segment.Substring(span.Start, span.Length), -1));
                    cursor = span.Start + span.Length;
                }
                AddTranslated(plan, chunks, segment.Substring(cursor), true);
            }
            List<string> results = new List<string>();
            int responseCharacters = 0;
            for (int offset = 0; offset < chunks.Count; offset += MaximumSegments)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(MaximumSegments, chunks.Count - offset);
                List<string> batch = chunks.GetRange(offset, count);
                IList<string> translated = await translate(batch.AsReadOnly(), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (translated == null || translated.Count != batch.Count) throw InvalidResponse();
                foreach (string value in translated)
                {
                    if (String.IsNullOrWhiteSpace(value)) throw InvalidResponse();
                    if (value.Length > MaximumResponseCharacters - responseCharacters) throw InvalidResponse();
                    responseCharacters += value.Length;
                    results.Add(value);
                }
            }
            List<string> restored = new List<string>();
            foreach (SegmentPlan plan in plans)
            {
                StringBuilder value = new StringBuilder();
                foreach (Piece piece in plan.Pieces)
                {
                    string next = piece.Index < 0 ? piece.Literal : results[piece.Index];
                    if (next.Length > MaximumResponseCharacters - value.Length) throw InvalidResponse();
                    value.Append(next);
                }
                restored.Add(value.ToString());
            }
            return restored.AsReadOnly();
        }

        internal static void ValidateSegments(IList<string> segments)
        {
            if (segments == null || segments.Count == 0 || segments.Count > MaximumSegments)
                throw new TranslationException(TranslationErrorKind.InvalidRequest, "Select between 1 and 256 lines to translate. Your text has not been changed.");
            int length = 0;
            foreach (string value in segments)
            {
                if (value == null) throw new TranslationException(TranslationErrorKind.InvalidRequest, "The translation source contains an invalid line. Your text has not been changed.");
                for (int index = 0; index < value.Length; index++)
                {
                    if (Char.IsHighSurrogate(value[index]))
                    {
                        if (++index >= value.Length || !Char.IsLowSurrogate(value[index])) throw InvalidUnicode();
                    }
                    else if (Char.IsLowSurrogate(value[index])) throw InvalidUnicode();
                }
                length += value.Length;
                if (length > MaximumTextLength) throw new TranslationException(TranslationErrorKind.InvalidRequest, "Select at most 16,000 characters to translate at once. Your text has not been changed.");
            }
        }

        internal static TranslationException InvalidResponse()
        { return new TranslationException(TranslationErrorKind.InvalidResponse, "The translation engine returned incomplete matching text. Your original text and earlier result are kept."); }
        private static TranslationException InvalidUnicode()
        { return new TranslationException(TranslationErrorKind.InvalidRequest, "The selected text contains an incomplete Unicode character. Select the text again before translating."); }

        private static void AddTranslated(SegmentPlan plan, List<string> chunks, string value, bool trim)
        {
            if (value.Length == 0) return;
            if (trim)
            {
                int first = 0, last = value.Length;
                while (first < last && Char.IsWhiteSpace(value[first])) first++;
                while (last > first && Char.IsWhiteSpace(value[last - 1])) last--;
                if (first > 0) plan.Pieces.Add(new Piece(value.Substring(0, first), -1));
                string core = value.Substring(first, last - first);
                bool letters = false;
                foreach (char c in core) if (Char.IsLetter(c)) { letters = true; break; }
                if (core.Length > 0)
                {
                    if (letters) { plan.Pieces.Add(new Piece(null, chunks.Count)); chunks.Add(core); }
                    else plan.Pieces.Add(new Piece(core, -1));
                }
                if (last < value.Length) plan.Pieces.Add(new Piece(value.Substring(last), -1));
            }
            else if (String.IsNullOrWhiteSpace(value)) plan.Pieces.Add(new Piece(value, -1));
            else { plan.Pieces.Add(new Piece(null, chunks.Count)); chunks.Add(value); }
        }

        private static List<Span> FindSpans(string text)
        {
            List<Span> candidates = new List<Span>();
            foreach (Match match in Url.Matches(text)) AddCandidate(candidates, text, match.Index, match.Length, true);
            foreach (Match match in Email.Matches(text)) AddCandidate(candidates, text, match.Index, match.Length, false);
            foreach (Match match in Domain.Matches(text)) AddCandidate(candidates, text, match.Index, match.Length, true);
            foreach (Match match in Extension.Matches(text)) AddCandidate(candidates, text, match.Index, match.Length, false);
            foreach (Match match in ContextIdentifier.Matches(text))
            {
                Group value = match.Groups["value"];
                if (HasLetterAndDigit(value.Value)) AddCandidate(candidates, text, value.Index, value.Length, false);
            }
            foreach (Match match in Token.Matches(text))
            {
                string value = match.Value.TrimEnd('.', '/', '\\');
                int dot = value.LastIndexOf('.');
                bool file = dot >= 0 && dot < value.Length - 1 && Extensions.Contains(value.Substring(dot + 1));
                bool identifier = HasLetterAndDigit(value) && (OnlyUppercaseLetters(value) ||
                    (HasUppercaseIdentifierRun(value) && value.IndexOfAny(new char[] { '-', '_', '/', '\\', '.' }) >= 0));
                if (file || identifier) AddCandidate(candidates, text, match.Index, value.Length, false);
            }
            candidates.Sort(delegate(Span left, Span right) { int at = left.Start.CompareTo(right.Start); return at != 0 ? at : right.Length.CompareTo(left.Length); });
            List<Span> spans = new List<Span>();
            int end = 0;
            foreach (Span candidate in candidates)
            {
                if (candidate.Start < end) continue;
                spans.Add(candidate); end = candidate.Start + candidate.Length;
            }
            return spans;
        }

        private static void AddCandidate(List<Span> spans, string text, int start, int length, bool trimPunctuation)
        {
            if (trimPunctuation)
                while (length > 0 && ".,;:!?".IndexOf(text[start + length - 1]) >= 0) length--;
            if (length > 0) spans.Add(new Span(start, length));
        }
        private static bool HasLetterAndDigit(string value)
        { bool letter = false, digit = false; foreach (char c in value) { letter |= Char.IsLetter(c); digit |= Char.IsDigit(c); } return letter && digit; }
        private static bool OnlyUppercaseLetters(string value)
        { foreach (char c in value) if (Char.IsLetter(c) && !Char.IsUpper(c)) return false; return true; }
        private static bool HasUppercaseIdentifierRun(string value)
        {
            // A separator alone also appears in ordinary prose (32-bit,
            // 10-year-old). Lowercase identifiers need an explicit part/model
            // label; mixed-case unlabelled codes need a clear uppercase run.
            int run = 0;
            foreach (char c in value) { run = Char.IsUpper(c) ? run + 1 : 0; if (run >= 2) return true; }
            return false;
        }
        private static Regex Pattern(string pattern, RegexOptions options)
        { return new Regex(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        private sealed class SegmentPlan { internal readonly List<Piece> Pieces = new List<Piece>(); }
        private sealed class Piece { internal readonly string Literal; internal readonly int Index; internal Piece(string literal, int index) { Literal = literal; Index = index; } }
        private sealed class Span { internal readonly int Start, Length; internal Span(int start, int length) { Start = start; Length = length; } }
    }
}
