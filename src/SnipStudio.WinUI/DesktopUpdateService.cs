using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SnipStudio.WinUI;

public sealed record DesktopUpdateInfo(string CurrentVersion, string? PublishedTag, DateTimeOffset? PublishedUtc, Uri ReleaseUri, string Message, bool NewerPublishedVersion = false);

/// <summary>Separate WinUI repository metadata; no installer fetching or execution.</summary>
public static class DesktopUpdateService
{
    private const string ReleasePage = "https://github.com/jaydawg473/Snip-Studio-WinUI3/releases";
    private static readonly HttpClient client = CreateClient();
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static readonly SemaphoreSlim automaticGate = new(1, 1);
    private static DateTimeOffset lastAutomaticAttempt;

    public static async Task<DesktopUpdateInfo?> CheckWhenDueAsync(CancellationToken cancellationToken = default)
    {
        if (!await automaticGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            var store = new DesktopPreferencesStore();
            DesktopPreferences preferences = store.Load();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!preferences.AutoCheckForUpdates || IsRecentAttempt(lastAutomaticAttempt, now) ||
                (DateTimeOffset.TryParse(preferences.LastUpdateCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset saved) &&
                    IsRecentAttempt(saved, now))) return null;
            lastAutomaticAttempt = now;
            DesktopUpdateInfo result = await CheckAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The store merges just the attempt timestamp with current
                // settings, including choices saved during the network request.
                preferences.LastUpdateCheckUtc = now.ToString("O", CultureInfo.InvariantCulture);
                store.Save(preferences);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
            { /* Keep the check result if its optional timestamp cannot be written. */ }
            return result;
        }
        finally { automaticGate.Release(); }
    }

    private static bool IsRecentAttempt(DateTimeOffset value, DateTimeOffset now) => value <= now && now - value < TimeSpan.FromDays(1);

    public static async Task<DesktopUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        string current = typeof(DesktopUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(DesktopUpdateService).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        if (!await gate.WaitAsync(0, cancellationToken))
            return new(current, null, null, new Uri(ReleasePage), "An update check is already running. Try again when it finishes.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/jaydawg473/Snip-Studio-WinUI3/releases?per_page=10");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(current, null, null, new Uri(ReleasePage), "Current build: " + current + ". GitHub does not report a published WinUI release at this endpoint. This is separate from stable Snip Studio releases.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("The release response exceeded the 1 MB limit.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > 1024 * 1024) throw new InvalidDataException("The release response exceeded the 1 MB limit.");
                buffer.Write(bytes, 0, read);
            }
            using JsonDocument json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            ReleaseVersion? currentVersion = ParseReleaseVersion(current);
            bool includePreviews = currentVersion?.PreviewNumber is not null;
            JsonElement item = default;
            ReleaseVersion? publishedVersion = null;
            foreach (JsonElement release in json.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || (!includePreviews && release.GetProperty("prerelease").GetBoolean())) continue;
                string? candidate = release.GetProperty("tag_name").GetString();
                if (candidate is null || ParseReleaseVersion(candidate) is not { } version || (!includePreviews && version.PreviewNumber is not null)) continue;
                if (publishedVersion is null || version.CompareTo(publishedVersion) > 0)
                {
                    item = release;
                    publishedVersion = version;
                }
            }
            if (publishedVersion is null)
                return new(current, null, null, new Uri(ReleasePage), "Current build: " + current + ". No comparable published WinUI version appeared in the recent GitHub releases.");
            string tag = item.GetProperty("tag_name").GetString()!;
            string address = item.GetProperty("html_url").GetString() ?? ReleasePage;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith("/jaydawg473/Snip-Studio-WinUI3/releases/tag/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release address did not match the WinUI repository.");
            DateTimeOffset? published = item.TryGetProperty("published_at", out JsonElement date) && date.TryGetDateTimeOffset(out DateTimeOffset parsed) ? parsed : null;
            string message = "Current build: " + current + ". Latest published WinUI release reported by GitHub: " + tag + (published is null ? "." : ", published " + published.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'."));
            bool newerPublishedVersion = currentVersion is not null && publishedVersion.CompareTo(currentVersion) > 0;
            return new(current, tag, published, uri, message, newerPublishedVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(current, null, null, new Uri(ReleasePage), "The update service did not respond within 12 seconds. Current build: " + current + ". No update was downloaded."); }
        catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(current, null, null, new Uri(ReleasePage), "Update information is unavailable. Current build: " + current + ". " + failure.Message + " No update was downloaded."); }
        finally { gate.Release(); }
    }

    private static ReleaseVersion? ParseReleaseVersion(string tag)
    {
        if (tag.Length > 128 || tag.Any(char.IsControl)) return null;
        string[] parts = tag.TrimStart('v').Split('-', 2);
        if (!Version.TryParse(parts[0], out Version? version) || version.Build < 0 || version.Revision >= 0) return null;
        if (parts.Length == 1) return new(version, null);
        const string previewPrefix = "preview.";
        if (!parts[1].StartsWith(previewPrefix, StringComparison.Ordinal) ||
            !int.TryParse(parts[1].AsSpan(previewPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int preview)) return null;
        return new(version, preview);
    }

    private sealed record ReleaseVersion(Version Number, int? PreviewNumber)
    {
        public int CompareTo(ReleaseVersion other)
        {
            int order = Number.CompareTo(other.Number);
            if (order != 0) return order;
            if (PreviewNumber is null) return other.PreviewNumber is null ? 0 : 1;
            return other.PreviewNumber is null ? -1 : PreviewNumber.Value.CompareTo(other.PreviewNumber.Value);
        }
    }

    private static HttpClient CreateClient()
    {
        var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("SnipStudio-WinUI3/0.7 (Automation-Forge; release-check)");
        return result;
    }
}
