using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SnipStudio
{
    public sealed class TranslationLanguage
    {
        public string Code { get; private set; }
        public string Name { get; private set; }
        public bool IsRightToLeft { get; private set; }
        public IList<string> Targets { get; private set; }

        public TranslationLanguage(string code, string name, bool isRightToLeft, IEnumerable<string> targets)
        {
            Code = code ?? "";
            Name = name ?? Code;
            IsRightToLeft = isRightToLeft;
            Targets = new List<string>(targets ?? new string[0]).AsReadOnly();
        }

        public override string ToString() { return Name; }
    }

    public sealed class TranslationResult
    {
        public string Text { get; private set; }
        public string SourceCode { get; private set; }
        public string TargetCode { get; private set; }
        public string ProviderName { get; private set; }
        public double? DetectedConfidence { get; private set; }

        public TranslationResult(string text, string sourceCode, string targetCode, double? detectedConfidence)
            : this(text, sourceCode, targetCode, detectedConfidence, "LibreTranslate") { }

        public TranslationResult(string text, string sourceCode, string targetCode, double? detectedConfidence, string providerName)
        {
            Text = text ?? "";
            SourceCode = sourceCode ?? "";
            TargetCode = targetCode ?? "";
            ProviderName = providerName ?? "";
            DetectedConfidence = detectedConfidence;
        }
    }

    public interface ITextTranslationService : IDisposable
    {
        Task<IList<TranslationLanguage>> GetLanguagesAsync(CancellationToken cancellationToken);
        Task<TranslationResult> TranslateAsync(string text, string sourceCodeOrEmpty, string targetCode, CancellationToken cancellationToken);
    }

    public interface ISegmentedTextTranslationService : ITextTranslationService
    {
        Task<IList<string>> TranslateSegmentsAsync(IList<string> segments, string sourceCodeOrEmpty, string targetCode, CancellationToken cancellationToken);
    }

    public enum TranslationErrorKind
    {
        InvalidRequest, ConnectionFailed, TimedOut, KeyRequired, RateLimited,
        ServiceUnavailable, InvalidResponse, ResponseTooLarge, InvalidEndpoint
    }

    // Messages are deliberately generated locally. Server bodies can contain the
    // submitted text, HTML, and other private data, so they never become errors.
    public sealed class TranslationException : Exception
    {
        public TranslationErrorKind Kind { get; private set; }
        public TranslationException(TranslationErrorKind kind, string message) : base(message) { Kind = kind; }
    }

    public sealed class LibreTranslateService : ITextTranslationService
    {
        public const int MaximumTextLength = 16000;
        public const int MaximumResponseBytes = 1024 * 1024;
        private static readonly Regex LanguageCode = new Regex(@"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8}){0,3}\z", RegexOptions.CultureInvariant);
        private readonly HttpClient client;
        private readonly TimeSpan requestTimeout;
        private bool disposed;
        public Uri BaseUri { get; private set; }

        public LibreTranslateService(string baseUrl) : this(baseUrl, null, TimeSpan.FromSeconds(30)) { }

        // The handler/timeout seam permits bounded tests without any external
        // service, account, shared API key, or model download.
        internal LibreTranslateService(string baseUrl, HttpMessageHandler handler, TimeSpan timeout)
        {
            BaseUri = ValidateBaseUri(baseUrl);
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException("timeout");
            requestTimeout = timeout;
            if (handler == null)
            {
                handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseDefaultCredentials = false,
                    UseProxy = !BaseUri.IsLoopback
                };
            }
            client = new HttpClient(handler, true);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = MaximumResponseBytes;
        }

        public static Uri ValidateBaseUri(string baseUrl)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) ||
                String.IsNullOrEmpty(uri.Host) || (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback))
                throw new TranslationException(TranslationErrorKind.InvalidEndpoint,
                    "Use an HTTPS LibreTranslate server address, or an HTTP address on this computer such as http://localhost:5000.");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        }

        public async Task<IList<TranslationLanguage>> GetLanguagesAsync(CancellationToken cancellationToken)
        {
            object response = await RequestAsync("languages", null, cancellationToken).ConfigureAwait(false);
            object[] entries = response as object[];
            if (entries == null || entries.Length == 0 || entries.Length > 512) throw InvalidResponse();
            List<TranslationLanguage> languages = new List<TranslationLanguage>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object entry in entries)
            {
                Dictionary<string, object> item = entry as Dictionary<string, object>;
                string code = GetString(item, "code"), name = GetString(item, "name");
                if (!IsLanguageCode(code) || String.Equals(code, "auto", StringComparison.OrdinalIgnoreCase) ||
                    String.IsNullOrWhiteSpace(name) || name.Length > 160 || HasControlCharacters(name) || !seen.Add(code)) throw InvalidResponse();
                List<string> targets = new List<string>();
                object targetValue;
                if (item.TryGetValue("targets", out targetValue))
                {
                    object[] codes = targetValue as object[];
                    if (codes == null || codes.Length > 512) throw InvalidResponse();
                    HashSet<string> targetSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (object value in codes)
                    {
                        string target = value as string;
                        if (!IsLanguageCode(target) || String.Equals(target, "auto", StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
                        if (targetSeen.Add(target)) targets.Add(target);
                    }
                }
                // Older compatible servers omit targets; an empty collection
                // means the server has not supplied a pair restriction.
                languages.Add(new TranslationLanguage(code, name, IsRightToLeft(code), targets));
            }
            languages.Sort(delegate(TranslationLanguage left, TranslationLanguage right)
            { return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name); });
            return languages.AsReadOnly();
        }

        public async Task<TranslationResult> TranslateAsync(string text, string sourceCodeOrEmpty, string targetCode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (String.IsNullOrWhiteSpace(text)) throw new TranslationException(TranslationErrorKind.InvalidRequest, "There is no recognized text to translate.");
            if (text.Length > MaximumTextLength) throw new TranslationException(TranslationErrorKind.InvalidRequest, "Select at most 16,000 characters to translate at once. Your text has not been sent.");
            string source = String.IsNullOrWhiteSpace(sourceCodeOrEmpty) ? "auto" : sourceCodeOrEmpty.Trim();
            string target = targetCode == null ? "" : targetCode.Trim();
            if (!IsLanguageCode(source) || !IsLanguageCode(target) || String.Equals(target, "auto", StringComparison.OrdinalIgnoreCase))
                throw new TranslationException(TranslationErrorKind.InvalidRequest, "Choose valid source and target languages.");
            if (String.Equals(source, "auto", StringComparison.OrdinalIgnoreCase)) source = "auto";
            Dictionary<string, object> request = new Dictionary<string, object>();
            request.Add("q", text);
            request.Add("source", source);
            request.Add("target", target);
            request.Add("format", "text");
            object response = await RequestAsync("translate", request, cancellationToken).ConfigureAwait(false);
            Dictionary<string, object> result = response as Dictionary<string, object>;
            string translated = GetString(result, "translatedText");
            if (translated == null) throw InvalidResponse();
            string detectedSource = source == "auto" ? "" : source;
            double? confidence = null;
            object detectedValue;
            if (result.TryGetValue("detectedLanguage", out detectedValue))
            {
                Dictionary<string, object> detected = detectedValue as Dictionary<string, object>;
                string code = GetString(detected, "language");
                if (!IsLanguageCode(code)) throw InvalidResponse();
                detectedSource = code;
                object score;
                if (detected.TryGetValue("confidence", out score) && (score is int || score is long || score is decimal || score is double))
                {
                    double number = Convert.ToDouble(score, CultureInfo.InvariantCulture);
                    if (Double.IsNaN(number) || Double.IsInfinity(number) || number < 0 || number > 100) throw InvalidResponse();
                    confidence = number;
                }
            }
            return new TranslationResult(translated, detectedSource, target, confidence);
        }

        private async Task<object> RequestAsync(string relativePath, Dictionary<string, object> payload, CancellationToken cancellationToken)
        {
            if (disposed) throw new ObjectDisposedException("LibreTranslateService");
            cancellationToken.ThrowIfCancellationRequested();
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (HttpRequestMessage request = new HttpRequestMessage(payload == null ? HttpMethod.Get : HttpMethod.Post, new Uri(BaseUri, relativePath)))
            {
                timeout.CancelAfter(requestTimeout);
                request.Headers.Accept.ParseAdd("application/json");
                if (payload != null) request.Content = new StringContent(CreateSerializer().Serialize(payload), Encoding.UTF8, "application/json");
                try
                {
                    using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        ValidateStatus(response.StatusCode);
                        if (response.Content == null) throw InvalidResponse();
                        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw ResponseTooLarge();
                        byte[] bytes;
                        // Disposing the response on cancellation also unblocks
                        // HTTP streams that do not honor ReadAsync's token.
                        using (timeout.Token.Register(delegate { response.Dispose(); }))
                        using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            byte[] chunk = new byte[8192];
                            while (true)
                            {
                                int count = await stream.ReadAsync(chunk, 0, chunk.Length, timeout.Token).ConfigureAwait(false);
                                if (count == 0) break;
                                if (buffer.Length + count > MaximumResponseBytes) throw ResponseTooLarge();
                                buffer.Write(chunk, 0, count);
                            }
                            timeout.Token.ThrowIfCancellationRequested();
                            bytes = buffer.ToArray();
                        }
                        string json;
                        try { json = new UTF8Encoding(false, true).GetString(bytes); }
                        catch (DecoderFallbackException) { throw InvalidResponse(); }
                        try { return CreateSerializer().DeserializeObject(json.TrimStart('\uFEFF')); }
                        catch (ArgumentException) { throw InvalidResponse(); }
                        catch (InvalidOperationException) { throw InvalidResponse(); }
                    }
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw TimedOut();
                }
                catch (HttpRequestException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw TimedOut();
                    throw ConnectionFailed();
                }
                catch (IOException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw TimedOut();
                    throw ConnectionFailed();
                }
                catch (ObjectDisposedException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw TimedOut();
                    if (disposed) throw;
                    throw ConnectionFailed();
                }
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        { return new JavaScriptSerializer { MaxJsonLength = MaximumResponseBytes, RecursionLimit = 12 }; }

        private static bool IsLanguageCode(string code)
        { return code != null && code.Length <= 35 && LanguageCode.IsMatch(code); }

        private static string GetString(Dictionary<string, object> item, string key)
        { object value; return item != null && item.TryGetValue(key, out value) ? value as string : null; }

        private static bool HasControlCharacters(string text)
        { foreach (char character in text) if (Char.IsControl(character)) return true; return false; }

        private static bool IsRightToLeft(string code)
        {
            try { return CultureInfo.GetCultureInfo(code).TextInfo.IsRightToLeft; }
            catch (CultureNotFoundException)
            {
                string primary = code.Split('-')[0].ToLowerInvariant();
                return primary == "ar" || primary == "he" || primary == "fa" || primary == "ur" || primary == "ps" || primary == "sd" || primary == "ug" || primary == "yi";
            }
        }

        private static void ValidateStatus(HttpStatusCode status)
        {
            int code = (int)status;
            if (code >= 200 && code < 300) return;
            if (code == 401 || code == 402 || code == 403)
                throw new TranslationException(TranslationErrorKind.KeyRequired, "This server requires access or an API key. Choose a free server without a key, or your own local LibreTranslate server.");
            if (code == 429)
                throw new TranslationException(TranslationErrorKind.RateLimited, "This translation server is busy or its free request limit has been reached. Wait before trying again, or choose your local server.");
            if (code >= 300 && code < 400 || code == 404 || code == 405)
                throw new TranslationException(TranslationErrorKind.InvalidEndpoint, "This address did not provide the LibreTranslate API. Check the server address; redirects are not followed.");
            if (code == 400 || code == 413 || code == 422)
                throw new TranslationException(TranslationErrorKind.InvalidRequest, "The server could not translate this text with these language choices. Refresh languages, select a smaller portion, and try again.");
            throw new TranslationException(TranslationErrorKind.ServiceUnavailable, "The translation server is unavailable. Your source text has been kept. Try later or choose another free LibreTranslate server.");
        }

        private static TranslationException InvalidResponse()
        { return new TranslationException(TranslationErrorKind.InvalidResponse, "The server returned an unexpected translation response. Your source text has been kept. Check that this is a LibreTranslate server."); }
        private static TranslationException ResponseTooLarge()
        { return new TranslationException(TranslationErrorKind.ResponseTooLarge, "The translation server returned too much data. Try a smaller selection or a different LibreTranslate server."); }
        private static TranslationException TimedOut()
        { return new TranslationException(TranslationErrorKind.TimedOut, "Translation took too long. Your source text has been kept. Check the server and try again."); }
        private static TranslationException ConnectionFailed()
        { return new TranslationException(TranslationErrorKind.ConnectionFailed, "The translation server could not be reached. Start your local LibreTranslate server, or check the server address and connection."); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            client.Dispose();
        }
    }
}
