using System.Text.Json;
using SnipStudio.WinUI;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private static void CheckDesktopShellContracts()
    {
        CheckPrintPageGeometry();
        foreach (int seconds in new[] { 0, 3, 5, 10 })
            Check(DesktopJumpList.TryGetCaptureDelay(["SnipStudio.exe", "--capture", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)], out int delay) &&
                delay == seconds, "Taskbar command preserves its one-shot capture delay");
        Check(DesktopJumpList.TryGetCaptureDelay(["SnipStudio.exe", "--capture"], out int immediate) && immediate == 0,
            "Explicit capture with no specified delay starts immediately");
        Check(!DesktopJumpList.TryGetCaptureDelay(["SnipStudio.exe", "image.png"], out _), "Recent-file launches do not start capture");
        Check(!DesktopJumpList.TryGetCaptureDelay(["SnipStudio.exe", "--capture", "7"], out _), "An unrecognized taskbar delay does not start capture");
        Check(!DesktopJumpList.TryGetCaptureDelay(["SnipStudio.exe", "--capture", "5", "extra"], out _), "Extra taskbar arguments do not start capture");
        Check(DesktopJumpList.IsCloseAllWithoutSavingRequest(["SnipStudio.exe", "--close-all-without-saving"]),
            "The exact taskbar close command requests confirmation");
        Check(!DesktopJumpList.IsCloseAllWithoutSavingRequest(["SnipStudio.exe", "--close-all-without-saving", "force"]) &&
            !DesktopJumpList.IsCloseAllWithoutSavingRequest(["SnipStudio.exe", "image.png", "--close-all-without-saving"]),
            "Close-all takes no force mode and never treats a recent-file launch as a discard request");

        const string releasePage = "https://github.com/jaydawg473/Snip-Studio/releases";
        const string tag = "v0.7.0";
        const string file = "SnipStudio-0.7.0-Setup.exe";
        const string releaseUrl = releasePage + "/tag/" + tag;
        const string setupUrl = releasePage + "/download/" + tag + "/" + file;
        Check(DesktopUpdateService.LatestReleaseEndpoint == "https://api.github.com/repos/jaydawg473/Snip-Studio/releases/latest",
            "The in-app update check uses the original Snip Studio stable API");
        var accepted = DesktopUpdateService.ParseLatestRelease("0.6.24", Latest());
        Check(accepted.NewerPublishedVersion && accepted.PublishedTag == tag && accepted.ReleaseUri.AbsoluteUri == releaseUrl &&
            accepted.SetupDownloadUri?.AbsoluteUri == setupUrl && accepted.PublishedUtc == new DateTimeOffset(2026, 11, 24, 17, 30, 0, TimeSpan.Zero),
            "A stable release upgrades the original version through the exact official tag and Setup asset");
        foreach (string current in new[] { "0.7.0", "0.7.1" })
        {
            var currentInfo = DesktopUpdateService.ParseLatestRelease(current, Latest());
            Check(!currentInfo.NewerPublishedVersion && currentInfo.SetupDownloadUri is null && currentInfo.ReleaseUri.AbsoluteUri == releaseUrl,
                "An equal or older stable release keeps the review link without offering Setup");
        }
        const string predecessorTag = "v0.6.21";
        const string predecessorSetup = "SnipStudio-0.6.21-Setup.exe";
        const string predecessorReleaseUrl = releasePage + "/tag/" + predecessorTag;
        var predecessor = DesktopUpdateService.ParseLatestRelease("0.7.0",
            Latest(releaseTag: predecessorTag, address: predecessorReleaseUrl, assets:
                [Asset(predecessorSetup, releasePage + "/download/" + predecessorTag + "/" + predecessorSetup)]));
        Check(predecessor.PublishedTag == predecessorTag && predecessor.ReleaseUri.AbsoluteUri == predecessorReleaseUrl &&
            !predecessor.NewerPublishedVersion && predecessor.SetupDownloadUri is null,
            "The last original installer is never offered to the newer stable build");
        Check(Unavailable(Latest(draft: true)) && Unavailable(Latest(prerelease: true)),
            "Drafts and prereleases cannot appear on the stable channel");
        Check(Unavailable(Latest(releaseTag: "v0.7.0-preview.38")) && Unavailable(Latest(releaseTag: "v0.07.0")) &&
            Unavailable(Latest(releaseTag: "0.7.0")),
            "Stable tags require the exact three-part form even if the release is advertised as stable");
        Check(Unavailable(Latest(address: releaseUrl.Replace("Snip-Studio", "Snip-Studio-WinUI3", StringComparison.Ordinal))) &&
            Unavailable(Latest(address: releaseUrl + "?extra=1")), "A fork or modified release page is rejected");
        Check(Unavailable(Latest(assets: [Asset(state: "new")])), "An asset still being uploaded stays unavailable");
        Check(Unavailable(Latest(assets: [Asset(), Asset()])), "Duplicate matching Setup assets are rejected");
        Check(Unavailable(Latest(assets: [Asset(name: "SnipStudio-WinUI3-0.7.0-Setup.exe")])),
            "Preview installer names cannot pass the original asset check");
        foreach (string address in new[]
        {
            setupUrl + "?extra=1",
            setupUrl.Replace("Snip-Studio", "Snip-Studio-WinUI3", StringComparison.Ordinal),
            setupUrl.Replace("https://github.com/", "https://example.invalid/", StringComparison.Ordinal),
            setupUrl.Replace(tag, "v0.7.1", StringComparison.Ordinal)
        })
            Check(Unavailable(Latest(assets: [Asset(address: address)])), "A noncanonical installer URL is rejected");
        Check(Unavailable(JsonSerializer.SerializeToElement(new[] { Latest() })) &&
            Unavailable(JsonSerializer.SerializeToElement(new { draft = false })), "Malformed latest release metadata stays unavailable");

        static bool Unavailable(JsonElement metadata)
        {
            var result = DesktopUpdateService.ParseLatestRelease("0.6.24", metadata);
            return !result.NewerPublishedVersion && result.PublishedTag is null && result.SetupDownloadUri is null &&
                result.ReleaseUri.AbsoluteUri == releasePage;
        }

        static JsonElement Latest(string releaseTag = tag, string address = releaseUrl, bool draft = false,
            bool prerelease = false, object[]? assets = null) =>
            JsonSerializer.SerializeToElement(new
            {
                tag_name = releaseTag,
                html_url = address,
                published_at = "2026-11-24T12:30:00-05:00",
                draft,
                prerelease,
                assets = assets ?? [Asset()]
            });

        static object Asset(string name = file, string address = setupUrl, string state = "uploaded") =>
            new { name, state, browser_download_url = address };
    }
}
