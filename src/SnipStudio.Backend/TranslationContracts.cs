#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SnipStudio;

// Shared contracts without the Framework-only System.Web serializer or an
// implicitly enabled network provider. The WinUI adapter uses local translation.
public sealed class TranslationLanguage
{
    public string Code { get; }
    public string Name { get; }
    public bool IsRightToLeft { get; }
    public IList<string> Targets { get; }

    public TranslationLanguage(string code, string name, bool isRightToLeft, IEnumerable<string> targets)
    {
        Code = code ?? "";
        Name = name ?? Code;
        IsRightToLeft = isRightToLeft;
        Targets = new List<string>(targets ?? Array.Empty<string>()).AsReadOnly();
    }

    public override string ToString() => Name;
}

public sealed class TranslationResult
{
    public string Text { get; }
    public string SourceCode { get; }
    public string TargetCode { get; }
    public string ProviderName { get; }
    public double? DetectedConfidence { get; }

    public TranslationResult(string text, string sourceCode, string targetCode, double? detectedConfidence,
        string providerName = "LibreTranslate")
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

public sealed class TranslationException : Exception
{
    public TranslationErrorKind Kind { get; }
    public TranslationException(TranslationErrorKind kind, string message) : base(message) => Kind = kind;
}
