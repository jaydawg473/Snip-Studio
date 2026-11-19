using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SnipStudio.WinUI;

public sealed record DesktopUpdateInfo(string CurrentVersion, string? PublishedTag, DateTimeOffset? PublishedUtc, Uri ReleaseUri, string Message);

/// <summary>Separate WinUI repository metadata; no installer fetching or execution.</summary>
public static class DesktopUpdateService
{
    private const string ReleasePage = "https://github.com/jaydawg473/Snip-Studio-WinUI3/releases";
    private static readonly HttpClient client = CreateClient();
    private static readonly SemaphoreSlim gate = new(1, 1);

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
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/jaydawg473/Snip-Studio-WinUI3/releases/latest");
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
            JsonElement item = json.RootElement;
            string tag = item.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("The published release has no version tag.");
            if (tag.Length > 128 || tag.Any(char.IsControl)) throw new InvalidDataException("The published release tag is invalid.");
            string address = item.GetProperty("html_url").GetString() ?? ReleasePage;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith("/jaydawg473/Snip-Studio-WinUI3/releases/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release address did not match the WinUI repository.");
            DateTimeOffset? published = item.TryGetProperty("published_at", out JsonElement date) && date.TryGetDateTimeOffset(out DateTimeOffset parsed) ? parsed : null;
            string message = "Current build: " + current + ". Latest published WinUI release reported by GitHub: " + tag + (published is null ? "." : ", published " + published.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'."));
            // A different tag is not sufficient evidence that a release is newer
            // or signed. Show exact metadata for deliberate manual review.
            return new(current, tag, published, uri, message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(current, null, null, new Uri(ReleasePage), "The update service did not respond within 12 seconds. Current build: " + current + ". No update was downloaded."); }
        catch (Exception failure) when (failure is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(current, null, null, new Uri(ReleasePage), "Update information is unavailable. Current build: " + current + ". " + failure.Message + " No update was downloaded."); }
        finally { gate.Release(); }
    }

    private static HttpClient CreateClient()
    {
        var result = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("SnipStudio-WinUI3/0.7 (Automation-Forge; manual-release-review)");
        return result;
    }
}
