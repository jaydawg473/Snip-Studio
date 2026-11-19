#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

/// <summary>
/// Explicit, user-configured LibreTranslate integration. Constructing this
/// adapter performs no I/O. GetLanguages fetches metadata; only TranslateAsync
/// sends the supplied prose. There is no default endpoint or offline fallback.
/// </summary>
public sealed class ConfiguredTranslationAdapter : IDisposable
{
    private static readonly Regex LanguageCode = new(@"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8}){0,3}\z", RegexOptions.CultureInvariant);
    private readonly global::SnipStudio.LibreTranslateService remote;
    private readonly global::SnipStudio.TechnicalTextTranslationService protectedService;
    private int disposed;

    public Uri Endpoint => remote.BaseUri;

    public ConfiguredTranslationAdapter(Uri endpoint, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
            throw new ArgumentException("Choose an absolute HTTPS server address, or HTTP on this computer.", nameof(endpoint));
        // Validation rejects credentials, query strings, fragments, redirects,
        // non-HTTP schemes and remote plaintext HTTP before any network request.
        remote = new global::SnipStudio.LibreTranslateService(endpoint.AbsoluteUri, apiKey);
        protectedService = new global::SnipStudio.TechnicalTextTranslationService(remote);
    }

    public async Task<IReadOnlyList<TranslationLanguageOption>> GetLanguagesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        IList<global::SnipStudio.TranslationLanguage> languages = await remote.GetLanguagesAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TranslationLanguageOption>(languages.Count);
        foreach (global::SnipStudio.TranslationLanguage language in languages)
            result.Add(new TranslationLanguageOption(language.Code, language.Name, new List<string>(language.Targets)));
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return result.AsReadOnly();
    }

    public async Task<TranslationSnapshot> TranslateAsync(IReadOnlyList<string> segments, string sourceLanguage,
        string targetLanguage, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0 || segments.Count > 256)
            throw new ArgumentException("Select between 1 and 256 lines to translate.", nameof(segments));
        var input = new List<string>(segments.Count);
        foreach (string value in segments)
            input.Add(value ?? throw new ArgumentException("A source line is missing.", nameof(segments)));
        global::SnipStudio.TechnicalTextTranslation.ValidateSegments(input);
        string source = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage.Trim();
        string target = (targetLanguage ?? "").Trim();
        // Validate even if every span is protected and no prose reaches the
        // provider. Auto is a source choice only, never a target language.
        if (!ValidLanguage(source) || !ValidLanguage(target) || target.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            source.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose valid, different original and translation languages.");
        if (source.Equals("auto", StringComparison.OrdinalIgnoreCase)) source = "auto";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        IList<string> translated;
        try
        {
            translated = await protectedService.TranslateSegmentsAsync(input.AsReadOnly(), source, target, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new global::SnipStudio.TranslationException(global::SnipStudio.TranslationErrorKind.TimedOut,
                "Translation exceeded the 90-second limit for the complete selection. Your source text has been kept; try fewer lines.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return new TranslationSnapshot(source, target, input, new List<string>(translated), "LibreTranslate (configured server)");
    }

    private static bool ValidLanguage(string value) => value.Length <= 35 && LanguageCode.IsMatch(value);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        remote.Dispose();
        protectedService.Dispose();
    }
}
