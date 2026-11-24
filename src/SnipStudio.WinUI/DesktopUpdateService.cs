using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SnipStudio.WinUI;

public sealed record DesktopUpdateInfo(string CurrentVersion, string? PublishedTag, DateTimeOffset? PublishedUtc,
    Uri ReleaseUri, string Message, bool NewerPublishedVersion = false, Uri? SetupDownloadUri = null);

/// <summary>Checks the published Snip Studio release and offers its official browser links.</summary>
public static class DesktopUpdateService
{
    internal const string LatestReleaseEndpoint = "https://api.github.com/repos/jaydawg473/Snip-Studio/releases/latest";
    private const string ReleasePage = "https://github.com/jaydawg473/Snip-Studio/releases";
    private const int MaximumResponseBytes = 256 * 1024;
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
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(current, null, null, new Uri(ReleasePage), $"Current version: {current}. GitHub does not report a published Snip Studio release.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("The release response exceeded the 256 KB limit.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) throw new InvalidDataException("The release response exceeded the 256 KB limit.");
                buffer.Write(bytes, 0, read);
            }
            using JsonDocument json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            return ParseLatestRelease(current, json.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(current, null, null, new Uri(ReleasePage), "The update service did not respond within 12 seconds. Current build: " + current + ". No update was downloaded."); }
        catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(current, null, null, new Uri(ReleasePage), "Update information is unavailable. Current build: " + current + ". " + failure.Message + " No update was downloaded."); }
        finally { gate.Release(); }
    }

    internal static DesktopUpdateInfo ParseLatestRelease(string current, JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out JsonElement draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out JsonElement prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("tag_name", out JsonElement tagValue) || tagValue.ValueKind != JsonValueKind.String ||
            tagValue.GetString() is not string tag || ParseStableTag(tag) is not Version latest)
            return new(current, null, null, new Uri(ReleasePage), $"Current version: {current}. The published Snip Studio release could not be verified.");

        string releaseLink = $"{ReleasePage}/tag/{tag}";
        if (!HasString(release, "html_url", releaseLink) || FindSetupDownload(release, tag) is not Uri setup)
            return new(current, null, null, new Uri(ReleasePage), $"Current version: {current}. The published Snip Studio release or its installer link could not be verified.");

        DateTimeOffset? published = release.TryGetProperty("published_at", out JsonElement date) &&
            date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out DateTimeOffset parsed) ? parsed.ToUniversalTime() : null;
        string message = $"Current version: {current}. Latest published Snip Studio release: {tag}" +
            (published is DateTimeOffset dateTime ? $", published {dateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}." : ".");
        bool newer = ParseStableTag("v" + current) is Version installed && latest > installed;
        return new(current, tag, published, new Uri(releaseLink), message, newer, newer ? setup : null);
    }

    private static Uri? FindSetupDownload(JsonElement release, string tag)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() is 0 or > 64) return null;
        string setupName = $"SnipStudio-{tag[1..]}-Setup.exe";
        string expected = $"{ReleasePage}/download/{tag}/{setupName}";
        bool found = false;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) return null;
            if (!HasString(asset, "name", setupName)) continue;
            if (found || !HasString(asset, "state", "uploaded") || !HasString(asset, "browser_download_url", expected)) return null;
            found = true;
        }
        return found ? new Uri(expected) : null;
    }

    private static bool HasString(JsonElement item, string key, string expected) =>
        item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static Version? ParseStableTag(string tag)
    {
        if (tag.Length is < 6 or > 30 || tag[0] != 'v') return null;
        string number = tag[1..];
        string[] components = number.Split('.');
        if (components.Length != 3 || components.Any(part => part.Length is < 1 or > 9 || !part.All(char.IsAsciiDigit))) return null;
        return Version.TryParse(number, out Version? version) && tag == "v" + version.ToString(3) ? version : null;
    }

    private static HttpClient CreateClient()
    {
        var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("SnipStudio/0.7 (Automation-Forge; release-check)");
        return result;
    }
}
