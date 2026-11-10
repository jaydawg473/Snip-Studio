using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SnipStudio
{
    internal enum UpdateCheckStatus { UpToDate, UpdateAvailable, Unavailable }
    internal enum UpdateTransportFailure { Connection, NameResolution, Proxy, SecureConnection, Timeout, Blocked }

    internal sealed class UpdateCheckResult
    {
        internal UpdateCheckStatus Status { get; private set; }
        internal Version CurrentVersion { get; private set; }
        internal Version NewVersion { get; private set; }
        internal string ReleaseUrl { get; private set; }
        internal string DownloadUrl { get; private set; }
        internal string UserMessage { get; private set; }
        internal DateTime CheckedUtc { get; private set; }

        internal UpdateCheckResult(UpdateCheckStatus status, Version currentVersion, Version newVersion,
            string releaseUrl, string downloadUrl, string message, DateTime checkedUtc)
        {
            Status = status;
            CurrentVersion = currentVersion;
            NewVersion = newVersion;
            ReleaseUrl = releaseUrl;
            DownloadUrl = downloadUrl;
            UserMessage = message;
            CheckedUtc = checkedUtc;
        }

        internal UpdateCheckResult At(DateTime checkedUtc)
        {
            return new UpdateCheckResult(Status, CurrentVersion, NewVersion, ReleaseUrl, DownloadUrl,
                UserMessage, checkedUtc);
        }
    }

    // Notification only: this service never downloads or executes an installer.
    // The request is anonymous and contains only the application version in its
    // User-Agent. No preferences, capture data or machine/user identity is read.
    internal static class UpdateChecker
    {
        internal const string LatestReleaseEndpoint = "https://api.github.com/repos/jaydawg473/Snip-Studio/releases/latest";
        internal const int MaximumResponseBytes = 262144;
        private const int RequestTimeoutMilliseconds = 8000;
        private const string ReleaseRoot = "https://github.com/jaydawg473/Snip-Studio/releases/";
        private const string InvalidInformation = "Update information could not be verified. Please try again later.";
        private static readonly Regex ReleaseTag = new Regex(@"\Av(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant);
        private static readonly object sync = new object();
        private static Task<FetchResult> inFlight;
        private static FetchResult cached;

        // Manual checks can retry a transient connection failure after ten
        // seconds. Successful metadata and server rate limits retain their
        // longer cooldowns so repeated clicks cannot flood the release API.
        internal static async Task<UpdateCheckResult> CheckAsync(Version currentVersion, bool force)
        {
            Version current = NormalizeVersion(currentVersion);
            FetchResult fetched = await GetFetchTask(current, force).ConfigureAwait(false);
            UpdateCheckResult result = fetched.Json == null
                ? Unavailable(current, fetched.Message)
                : ParseResponse(current, fetched.Json);
            return result.At(fetched.CheckedUtc);
        }

        // Persist the last attempted automatic check in the application's own
        // preferences; the service itself stores no update history on disk.
        internal static bool CheckDue(DateTime lastAttemptUtc, DateTime nowUtc, bool autoEnabled)
        {
            if (!autoEnabled) return false;
            DateTime now = AsUtc(nowUtc), previous = AsUtc(lastAttemptUtc);
            if (previous == DateTime.MinValue || previous > now) return true;
            return now - previous >= TimeSpan.FromHours(24);
        }

        // Pure validation of public release metadata. MinValue deliberately
        // does not claim that a network check was performed by this parser.
        internal static UpdateCheckResult ParseResponse(Version currentVersion, string json)
        {
            Version current = NormalizeVersion(currentVersion);
            if (String.IsNullOrWhiteSpace(json) || json.Length > MaximumResponseBytes)
                return Unavailable(current, InvalidInformation);
            Dictionary<string, object> release;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer
                { MaxJsonLength = MaximumResponseBytes, RecursionLimit = 12 };
                release = serializer.DeserializeObject(json.TrimStart('\uFEFF')) as Dictionary<string, object>;
            }
            catch (ArgumentException) { return Unavailable(current, InvalidInformation); }
            catch (InvalidOperationException) { return Unavailable(current, InvalidInformation); }
            if (release == null || !IsFalse(release, "draft") || !IsFalse(release, "prerelease"))
                return Unavailable(current, InvalidInformation);
            string tag = GetString(release, "tag_name");
            Version latest;
            if (tag == null || !ReleaseTag.IsMatch(tag) || !Version.TryParse(tag.Substring(1), out latest))
                return Unavailable(current, InvalidInformation);
            latest = NormalizeVersion(latest);
            string version = latest.ToString(3);
            if (!String.Equals(tag, "v" + version, StringComparison.Ordinal))
                return Unavailable(current, InvalidInformation);
            string expectedRelease = ReleaseRoot + "tag/" + tag;
            string releaseUrl = GetString(release, "html_url");
            if (!IsExactOfficialUrl(releaseUrl, expectedRelease)) return Unavailable(current, InvalidInformation);
            object assetValue;
            object[] assets = release.TryGetValue("assets", out assetValue) ? assetValue as object[] : null;
            if (assets == null || assets.Length == 0 || assets.Length > 64) return Unavailable(current, InvalidInformation);
            string installerName = "SnipStudio-" + version + "-Setup.exe";
            string expectedDownload = ReleaseRoot + "download/" + tag + "/" + installerName;
            string downloadUrl = null;
            int matches = 0;
            foreach (object value in assets)
            {
                Dictionary<string, object> asset = value as Dictionary<string, object>;
                if (asset == null) return Unavailable(current, InvalidInformation);
                if (!String.Equals(GetString(asset, "name"), installerName, StringComparison.Ordinal)) continue;
                matches++;
                string candidate = GetString(asset, "browser_download_url");
                if (!String.Equals(GetString(asset, "state"), "uploaded", StringComparison.Ordinal) ||
                    !IsExactOfficialUrl(candidate, expectedDownload)) return Unavailable(current, InvalidInformation);
                downloadUrl = candidate;
            }
            if (matches != 1 || downloadUrl == null) return Unavailable(current, InvalidInformation);
            if (latest > current)
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, current, latest, releaseUrl,
                    downloadUrl, "Snip Studio " + version + " is available.", DateTime.MinValue);
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, latest, releaseUrl, downloadUrl,
                "You're up to date.", DateTime.MinValue);
        }

        private static Task<FetchResult> GetFetchTask(Version currentVersion, bool force)
        {
            lock (sync)
            {
                DateTime now = DateTime.UtcNow;
                if (inFlight != null) return inFlight;
                if (cached != null && ShouldReuseFetch(cached.CheckedUtc, cached.RetryAfterUtc,
                    now, force, cached.TransientTransportFailure))
                    return Task.FromResult(cached);
                Task<FetchResult> pending = FetchBoundedAsync(currentVersion);
                inFlight = pending;
                Task observer = pending.ContinueWith(delegate(Task<FetchResult> completed)
                {
                    lock (sync)
                    {
                        if (completed.Status == TaskStatus.RanToCompletion) cached = completed.Result;
                        if (Object.ReferenceEquals(inFlight, completed)) inFlight = null;
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                GC.KeepAlive(observer);
                return pending;
            }
        }

        private static async Task<FetchResult> FetchBoundedAsync(Version currentVersion)
        {
            using (CancellationTokenSource timeout = new CancellationTokenSource(RequestTimeoutMilliseconds))
            {
                // The caller also has a bounded wait because older Framework
                // DNS stacks can take longer than an HTTP cancellation token.
                CancellationToken cancellation = timeout.Token;
                Task<FetchResult> request = Task.Run(delegate { return FetchCoreAsync(currentVersion, cancellation); });
                Task finished = await Task.WhenAny(request, Task.Delay(RequestTimeoutMilliseconds)).ConfigureAwait(false);
                if (!Object.ReferenceEquals(finished, request))
                {
                    timeout.Cancel();
                    Task observer = request.ContinueWith(delegate(Task<FetchResult> completed) { GC.KeepAlive(completed.Exception); },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    GC.KeepAlive(observer);
                    return FetchResult.TransportFailed(UpdateTransportFailure.Timeout);
                }
                return await request.ConfigureAwait(false);
            }
        }

        private static async Task<FetchResult> FetchCoreAsync(Version currentVersion, CancellationToken cancellation)
        {
            try
            {
                using (HttpClientHandler handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseDefaultCredentials = false,
                    SslProtocols = System.Security.Authentication.SslProtocols.None,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                })
                using (HttpClient client = new HttpClient(handler, false))
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint))
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    client.MaxResponseContentBufferSize = MaximumResponseBytes;
                    request.Headers.TryAddWithoutValidation("User-Agent", "SnipStudio/" + currentVersion.ToString(3));
                    request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                    using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                            return FetchResult.Failed("GitHub could not complete the update check. Please try again later.", ReadRetryAfter(response));
                        if (response.StatusCode != HttpStatusCode.OK)
                            return FetchResult.Failed("The update service is unavailable. Please try again later.");
                        if (response.Content == null || response.Content.Headers.ContentLength > MaximumResponseBytes)
                            return FetchResult.Failed(InvalidInformation);
                        string mediaType = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType;
                        if (!String.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                            !String.Equals(mediaType, "application/vnd.github+json", StringComparison.OrdinalIgnoreCase))
                            return FetchResult.Failed(InvalidInformation);
                        byte[] bytes;
                        using (cancellation.Register(delegate { response.Dispose(); }))
                        using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            byte[] chunk = new byte[8192];
                            for (;;)
                            {
                                int count = await stream.ReadAsync(chunk, 0, chunk.Length, cancellation).ConfigureAwait(false);
                                if (count == 0) break;
                                if (buffer.Length + count > MaximumResponseBytes) return FetchResult.Failed(InvalidInformation);
                                buffer.Write(chunk, 0, count);
                            }
                            cancellation.ThrowIfCancellationRequested();
                            bytes = buffer.ToArray();
                        }
                        string json = new UTF8Encoding(false, true).GetString(bytes);
                        return new FetchResult(json, null, DateTime.UtcNow, DateTime.MinValue);
                    }
                }
            }
            catch (OperationCanceledException) { return FetchResult.TransportFailed(UpdateTransportFailure.Timeout); }
            catch (HttpRequestException error) { return FetchResult.TransportFailed(ClassifyTransportFailure(error, cancellation.IsCancellationRequested)); }
            catch (IOException error) { return FetchResult.TransportFailed(ClassifyTransportFailure(error, cancellation.IsCancellationRequested)); }
            catch (AuthenticationException error) { return FetchResult.TransportFailed(ClassifyTransportFailure(error, cancellation.IsCancellationRequested)); }
            catch (SocketException error) { return FetchResult.TransportFailed(ClassifyTransportFailure(error, cancellation.IsCancellationRequested)); }
            catch (ObjectDisposedException error) { return FetchResult.TransportFailed(ClassifyTransportFailure(error, cancellation.IsCancellationRequested)); }
            catch (DecoderFallbackException) { return FetchResult.Failed(InvalidInformation); }
            catch (System.Security.SecurityException) { return FetchResult.TransportFailed(UpdateTransportFailure.Blocked); }
            catch (UnauthorizedAccessException) { return FetchResult.TransportFailed(UpdateTransportFailure.Blocked); }
        }

        // Pure policy seam: transport failures should not trap a user behind
        // an old error, while a rate-limit deadline must still be respected.
        internal static bool ShouldReuseFetch(DateTime checkedUtc, DateTime retryAfterUtc,
            DateTime nowUtc, bool force, bool transientTransportFailure)
        {
            DateTime now = AsUtc(nowUtc), checkedAt = AsUtc(checkedUtc);
            // A clock adjustment must not allow a server-issued cooldown to
            // be bypassed just because the cached check now looks future-dated.
            if (now < AsUtc(retryAfterUtc)) return true;
            if (checkedAt == DateTime.MinValue || checkedAt > now) return false;
            TimeSpan cooldown = !force ? TimeSpan.FromHours(24) :
                transientTransportFailure ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(1);
            return now - checkedAt < cooldown;
        }

        // Classify only known exception types and numeric status values. Never
        // display exception messages: those can contain proxy or account data.
        internal static UpdateTransportFailure ClassifyTransportFailure(Exception error, bool cancellationRequested)
        {
            if (cancellationRequested) return UpdateTransportFailure.Timeout;
            for (int depth = 0; error != null && depth < 12; depth++, error = error.InnerException)
            {
                if (error is OperationCanceledException) return UpdateTransportFailure.Timeout;
                if (error is AuthenticationException) return UpdateTransportFailure.SecureConnection;
                if (error is System.Security.SecurityException || error is UnauthorizedAccessException)
                    return UpdateTransportFailure.Blocked;
                WebException webError = error as WebException;
                if (webError != null)
                {
                    switch (webError.Status)
                    {
                        case WebExceptionStatus.NameResolutionFailure: return UpdateTransportFailure.NameResolution;
                        case WebExceptionStatus.ProxyNameResolutionFailure:
                        case WebExceptionStatus.RequestProhibitedByProxy: return UpdateTransportFailure.Proxy;
                        case WebExceptionStatus.SecureChannelFailure:
                        case WebExceptionStatus.TrustFailure: return UpdateTransportFailure.SecureConnection;
                        case WebExceptionStatus.Timeout: return UpdateTransportFailure.Timeout;
                    }
                }
                SocketException socketError = error as SocketException;
                if (socketError != null)
                {
                    switch (socketError.SocketErrorCode)
                    {
                        case SocketError.HostNotFound:
                        case SocketError.TryAgain:
                        case SocketError.NoData:
                        case SocketError.NoRecovery: return UpdateTransportFailure.NameResolution;
                        case SocketError.TimedOut: return UpdateTransportFailure.Timeout;
                        case SocketError.AccessDenied: return UpdateTransportFailure.Blocked;
                    }
                }
            }
            return UpdateTransportFailure.Connection;
        }

        internal static string DescribeTransportFailure(UpdateTransportFailure failure)
        {
            switch (failure)
            {
                case UpdateTransportFailure.NameResolution:
                    return "Couldn't find GitHub's update service (DNS). Check your connection and try again.";
                case UpdateTransportFailure.Proxy:
                    return "The network proxy could not reach GitHub's update service. Check your proxy connection and try again.";
                case UpdateTransportFailure.SecureConnection:
                    return "Couldn't establish a secure connection to GitHub (TLS or certificate). Check Windows and network security settings.";
                case UpdateTransportFailure.Timeout:
                    return "The update check timed out. Please try again later.";
                case UpdateTransportFailure.Blocked:
                    return "Windows or a network policy did not allow the update check. Please try again later.";
                default:
                    return "Couldn't connect to GitHub's update service. Check your connection and try again.";
            }
        }

        private static DateTime ReadRetryAfter(HttpResponseMessage response)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan delay = TimeSpan.FromMinutes(1);
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue) delay = response.Headers.RetryAfter.Delta.Value;
                else if (response.Headers.RetryAfter.Date.HasValue) delay = response.Headers.RetryAfter.Date.Value.UtcDateTime - now;
            }
            else
            {
                IEnumerable<string> values;
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out values))
                    foreach (string value in values)
                    {
                        long seconds;
                        if (Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) && seconds >= 0 && seconds <= 253402300799)
                            delay = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds) - now;
                        break;
                    }
            }
            if (delay < TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
            // Preserve valid server cooldowns, including those longer than a
            // day. Clamp only to the largest timestamp DateTime can represent.
            long remainingTicks = DateTime.MaxValue.Ticks - now.Ticks;
            long ticks = Math.Min(delay.Ticks, remainingTicks);
            return new DateTime(now.Ticks + ticks, DateTimeKind.Utc);
        }

        private static bool IsExactOfficialUrl(string value, string expected)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 512 || !String.Equals(value, expected, StringComparison.Ordinal)) return false;
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
                String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Query) && String.IsNullOrEmpty(uri.Fragment);
        }

        private static Version NormalizeVersion(Version value)
        {
            if (value == null) throw new ArgumentNullException("currentVersion");
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        }

        private static DateTime AsUtc(DateTime value)
        {
            if (value == DateTime.MinValue) return DateTime.MinValue;
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static bool IsFalse(Dictionary<string, object> item, string name)
        {
            object value;
            return item.TryGetValue(name, out value) && value is bool && !(bool)value;
        }

        private static string GetString(Dictionary<string, object> item, string name)
        {
            object value;
            return item.TryGetValue(name, out value) ? value as string : null;
        }

        private static UpdateCheckResult Unavailable(Version current, string message)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, current, null, null, null,
                message ?? InvalidInformation, DateTime.MinValue);
        }

        private sealed class FetchResult
        {
            internal readonly string Json;
            internal readonly string Message;
            internal readonly DateTime CheckedUtc;
            internal readonly DateTime RetryAfterUtc;
            internal readonly bool TransientTransportFailure;
            internal FetchResult(string json, string message, DateTime checkedUtc, DateTime retryAfterUtc)
                : this(json, message, checkedUtc, retryAfterUtc, false) { }
            private FetchResult(string json, string message, DateTime checkedUtc, DateTime retryAfterUtc, bool transientTransportFailure)
            {
                Json = json; Message = message; CheckedUtc = checkedUtc; RetryAfterUtc = retryAfterUtc;
                TransientTransportFailure = transientTransportFailure;
            }
            internal static FetchResult Failed(string message) { return Failed(message, DateTime.MinValue); }
            internal static FetchResult Failed(string message, DateTime retryAfterUtc)
            { return new FetchResult(null, message, DateTime.UtcNow, retryAfterUtc); }
            internal static FetchResult TransportFailed(UpdateTransportFailure failure)
            { return new FetchResult(null, DescribeTransportFailure(failure), DateTime.UtcNow, DateTime.MinValue, true); }
        }
    }
}
