using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnipStudio.WinUI.Backend;

internal static partial class Program
{
    private sealed class TranslationSmokeEvidence
    {
        public string BundleManifestSHA256 { get; set; } = "";
        public string NodeSHA256 { get; set; } = "";
        public IReadOnlyList<TranslationLanguageOption>? Languages { get; set; }
        public string[] SourceSegments { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string>? ObservedSourceSnapshot { get; set; }
        public IReadOnlyList<string>? TranslatedSegments { get; set; }
        public string? ProviderName { get; set; }
        public List<object> RowChecks { get; } = new();
        public double LanguageDiscoveryMilliseconds { get; set; }
        public double TranslationMilliseconds { get; set; }
        public List<object> ParentProcessMemorySamples { get; } = new();
    }

    private sealed record TranslationSmokeRow(string Text, string[] ExactLiterals, bool ShouldTranslateProse);

    private static int RunTranslationSmoke(string bundlePath, string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            Console.Error.WriteLine("Use a new empty offline translation smoke evidence directory.");
            return 2;
        }
        Directory.CreateDirectory(output);
        bool passed = false;
        string? failure = null;
        var observed = new TranslationSmokeEvidence();
        Stopwatch clock = Stopwatch.StartNew();
        using StaContext context = new();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            if (!Path.IsPathFullyQualified(bundlePath)) throw new ArgumentException("Use the absolute path to the reviewed offline translation bundle.");
            if (!Directory.Exists(bundlePath)) throw new DirectoryNotFoundException("The reviewed offline translation bundle is unavailable.");
            context.Run(CheckTranslationSmokeAsync(bundlePath, observed));
            passed = true;
            return 0;
        }
        catch (Exception error)
        {
            failure = error.ToString();
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            clock.Stop();
            string json = JsonSerializer.Serialize(new
            {
                CheckedUtc = DateTimeOffset.UtcNow,
                Passed = passed,
                Assertions = assertions,
                ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
                OverallCancellationBudgetSeconds = 85,
                SourceLanguage = "de", TargetLanguage = "en",
                Scope = "Actual reviewed local OfflineTranslationAdapter language discovery and one German-to-English model batch, exact source/identifier/quantity/duplicate/blank-row fidelity. Node executes only through the adapter's existing signature/hash/lease gate. No configured server or intentional network request is invoked by this test. Packet traffic, translation semantic quality, other model directions, child-process peak memory and native WinUI acceptance are not established.",
                Observations = observed,
                Failure = failure
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "translation-smoke-checks.json"), json, new UTF8Encoding(false));
            Console.WriteLine(json);
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    private static async Task CheckTranslationSmokeAsync(string bundlePath, TranslationSmokeEvidence observed)
    {
        const string filename = "000042_Aa-01.pdf";
        const string model = "AB-0007xY";
        const string repeatedTechnical = "Die Teilenummer ist 000042_Aa-01.pdf; bitte zweimal prüfen: 000042_Aa-01.pdf.";
        TranslationSmokeRow[] rows =
        {
            new("Guten Morgen. Das ist ein Test.", Array.Empty<string>(), true),
            new(repeatedTechnical, new[] { filename, filename }, true),
            new(filename, new[] { filename }, false),
            new("", Array.Empty<string>(), false),
            new(" \t ", Array.Empty<string>(), false),
            new(filename, new[] { filename }, false),
            new("Bitte prüfen Sie das Modell AB-0007xY und das Dokument 000042_Aa-01.pdf.", new[] { model, filename }, true),
            new(repeatedTechnical, new[] { filename, filename }, true),
            new("Die Menge beträgt 0007 Stück.", new[] { "0007" }, true),
            new("Vielen Dank für Ihre Hilfe.", Array.Empty<string>(), true)
        };
        string[] values = rows.Select(row => row.Text).ToArray();
        string[] unchangedInput = (string[])values.Clone();
        IReadOnlyList<string> source = Array.AsReadOnly(values);
        observed.SourceSegments = unchangedInput;
        observed.ParentProcessMemorySamples.Add(SampleMemory("Before offline translation smoke"));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        string manifest = Path.Combine(bundlePath, "bundle.json"), runtime = Path.Combine(bundlePath, "runtime", "node.exe");
        observed.BundleManifestSHA256 = await HashSmokeFileAsync(manifest, budget.Token);
        observed.NodeSHA256 = await HashSmokeFileAsync(runtime, budget.Token);

        using var adapter = new OfflineTranslationAdapter(bundlePath);
        Stopwatch step = Stopwatch.StartNew();
        IReadOnlyList<TranslationLanguageOption> languages;
        try { languages = await adapter.GetLanguagesAsync(budget.Token); }
        finally { step.Stop(); observed.LanguageDiscoveryMilliseconds = step.Elapsed.TotalMilliseconds; }
        observed.Languages = languages;
        Check(languages.Any(language => language.Code == "de" && language.Targets.Contains("en", StringComparer.Ordinal)),
            "Reviewed offline bundle advertises an installed German-to-English pair");
        step.Restart();
        TranslationSnapshot result;
        try { result = await adapter.TranslateAsync(source, "de", "en", budget.Token); }
        finally { step.Stop(); observed.TranslationMilliseconds = step.Elapsed.TotalMilliseconds; }
        observed.ObservedSourceSnapshot = result.SourceSegments;
        observed.TranslatedSegments = result.TranslatedSegments;
        observed.ProviderName = result.ProviderName;
        observed.ParentProcessMemorySamples.Add(SampleMemory("After local model batch and owned adapter cleanup pending"));
        Check(source.SequenceEqual(unchangedInput, StringComparer.Ordinal) && result.SourceSegments.SequenceEqual(unchangedInput, StringComparer.Ordinal),
            "Actual offline translation preserves the caller's read-only input and exact source snapshot");
        Check(result.SourceLanguage == "de" && result.TargetLanguage == "en" && result.TranslatedSegments.Count == rows.Length,
            "Actual local model returns every ordered source row for the selected pair");

        bool allRowsValid = true;
        for (int index = 0; index < rows.Length; index++)
        {
            TranslationSmokeRow row = rows[index];
            string translated = result.TranslatedSegments[index];
            bool blankPreserved = !string.IsNullOrWhiteSpace(row.Text) || translated == row.Text;
            bool nonempty = string.IsNullOrWhiteSpace(row.Text) || !string.IsNullOrWhiteSpace(translated);
            bool changedProse = !row.ShouldTranslateProse || !string.Equals(row.Text, translated, StringComparison.Ordinal);
            bool exactLiterals = ExactSmokeLiteralsMatch(row.Text, translated, row.ExactLiterals);
            bool valid = blankPreserved && nonempty && changedProse && exactLiterals;
            observed.RowChecks.Add(new { Index = index, Source = row.Text, Translation = translated,
                RequiredExactLiteralsInOrder = row.ExactLiterals, BlankWhitespacePreserved = blankPreserved,
                NonemptyForNonblankSource = nonempty, ProseChangedWhereExpected = changedProse,
                ExactLiteralCountsAndOrderPreserved = exactLiterals, Passed = valid });
            allRowsValid &= valid;
        }
        // Keep all observed row outputs even if a protected quantity or another
        // row fails. No guessed translation or corrected identifier is inserted.
        Check(allRowsValid, "Actual local model translates prose while preserving exact identifiers, quantity, blank rows and literal order");
        Check(result.TranslatedSegments[2] == filename && result.TranslatedSegments[5] == filename,
            "Duplicate identifier-only rows retain their exact value at both original indices");
        Check(result.TranslatedSegments[1] == result.TranslatedSegments[7],
            "Duplicate technical rows remain separate, ordered and identically translated by the deterministic local model");
        Check(source.SequenceEqual(unchangedInput, StringComparer.Ordinal), "Source input remains unchanged after result verification");
    }

    private static bool ExactSmokeLiteralsMatch(string source, string translated, IReadOnlyList<string> literals)
    {
        int cursor = 0;
        foreach (string literal in literals)
        {
            int found = translated.IndexOf(literal, cursor, StringComparison.Ordinal);
            if (found < 0) return false;
            cursor = found + literal.Length;
        }
        foreach (string literal in literals.Distinct(StringComparer.Ordinal))
            if (CountSmokeLiteral(source, literal) != CountSmokeLiteral(translated, literal)) return false;
        return true;
    }

    private static int CountSmokeLiteral(string text, string literal)
    {
        int count = 0, cursor = 0;
        while (cursor <= text.Length - literal.Length)
        {
            int found = text.IndexOf(literal, cursor, StringComparison.Ordinal);
            if (found < 0) break;
            count++; cursor = found + literal.Length;
        }
        return count;
    }

    private static async Task<string> HashSmokeFileAsync(string path, CancellationToken token)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
