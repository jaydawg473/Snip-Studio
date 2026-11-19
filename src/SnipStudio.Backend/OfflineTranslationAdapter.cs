#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio.WinUI.Backend;

public sealed class TranslationLanguageOption
{
    public string Code { get; }
    public string Name { get; }
    public IReadOnlyList<string> Targets { get; }

    public TranslationLanguageOption(string code, string name, IReadOnlyList<string> targets)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ArgumentNullException.ThrowIfNull(targets);
        var copy = new List<string>(targets.Count);
        foreach (string target in targets) copy.Add(target ?? throw new ArgumentException("A language target is missing.", nameof(targets)));
        Targets = copy.AsReadOnly();
    }

    public override string ToString() => Name;
}

public sealed class TranslationSnapshot
{
    public string SourceLanguage { get; }
    public string TargetLanguage { get; }
    public string ProviderName { get; }
    public IReadOnlyList<string> SourceSegments { get; }
    public IReadOnlyList<string> TranslatedSegments { get; }
    public string Text => string.Join(Environment.NewLine, TranslatedSegments);

    public TranslationSnapshot(string sourceLanguage, string targetLanguage,
        IReadOnlyList<string> sourceSegments, IReadOnlyList<string> translatedSegments,
        string providerName = "Offline translation (Bergamot)")
    {
        SourceLanguage = sourceLanguage ?? throw new ArgumentNullException(nameof(sourceLanguage));
        TargetLanguage = targetLanguage ?? throw new ArgumentNullException(nameof(targetLanguage));
        ProviderName = providerName ?? throw new ArgumentNullException(nameof(providerName));
        ArgumentNullException.ThrowIfNull(sourceSegments);
        ArgumentNullException.ThrowIfNull(translatedSegments);
        if (sourceSegments.Count == 0 || sourceSegments.Count > 256 || sourceSegments.Count != translatedSegments.Count)
            throw new ArgumentException("The translated lines must match every source line.");
        SourceSegments = Copy(sourceSegments, nameof(sourceSegments));
        TranslatedSegments = Copy(translatedSegments, nameof(translatedSegments));
    }

    private static IReadOnlyList<string> Copy(IReadOnlyList<string> values, string name)
    {
        var copy = new List<string>(values.Count);
        foreach (string value in values)
            copy.Add(value ?? throw new ArgumentException("A translation line is missing.", name));
        return copy.AsReadOnly();
    }
}

/// <summary>
/// Local-only translation for the WinUI shell. Explicit languages and immutable
/// per-line results keep the original text available for inspection and copying.
/// A result never changes the document implicitly.
/// </summary>
public sealed class OfflineTranslationAdapter : IDisposable
{
    private readonly global::SnipStudio.OfflineTranslationService local;
    private readonly global::SnipStudio.TechnicalTextTranslationService protectedService;
    private int disposed;

    public OfflineTranslationAdapter(string? bundleFolder = null)
    {
        string folder = bundleFolder ?? Path.Combine(AppContext.BaseDirectory, "translation");
        local = new global::SnipStudio.OfflineTranslationService(folder);
        protectedService = new global::SnipStudio.TechnicalTextTranslationService(local);
    }

    public async Task<IReadOnlyList<TranslationLanguageOption>> GetLanguagesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        IList<global::SnipStudio.TranslationLanguage> languages = await local.GetLanguagesAsync(cancellationToken).ConfigureAwait(false);
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
        string source = (sourceLanguage ?? "").Trim().ToLowerInvariant();
        string target = (targetLanguage ?? "").Trim().ToLowerInvariant();
        // Validate languages even if every input character is protected and the
        // model would otherwise receive no prose chunks.
        if (!IsLanguage(source) || !IsLanguage(target) || source == target)
            throw new ArgumentException("Choose different installed original and translation languages.");
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
                "Offline translation exceeded the 90-second limit for the complete selection. Your source text has been kept; try fewer lines.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return new TranslationSnapshot(source, target, input, new List<string>(translated));
    }

    private static bool IsLanguage(string value) => value is "en" or "de" or "es" or "fr";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        local.Dispose();
        protectedService.Dispose();
    }
}
