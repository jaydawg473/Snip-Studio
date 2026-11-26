import AppKit
import SwiftUI
import Translation

@available(macOS 15.0, *)
@MainActor
struct StudioTranslationReview: View {
    let apply: @MainActor ([String]) async throws -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var rows: [ReviewRow]
    @State private var languages: [LanguageChoice] = []
    @State private var sourceID: String?
    @State private var targetID: String?
    @State private var job: TranslationJob?
    @State private var applicationJob: Task<Void, Never>?
    @State private var loadedLanguages = false
    @State private var translatedLanguage: String?
    @State private var hasReviewEdits = false
    @State private var replacingReview = false
    @State private var detail = ""

    init(lines: [RecognizedLine], apply: @escaping @MainActor ([String]) async throws -> Void) {
        self.apply = apply
        _rows = State(initialValue: lines.enumerated().map { index, line in
            ReviewRow(id: index, source: line.text, protected: TranslationRowPolicy.shouldKeepOriginal(line))
        })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Translate recognized text").font(.headline)
            languageControls
            if !detail.isEmpty {
                Text(detail).font(.callout).foregroundStyle(.secondary)
            }
            reviewTable
            HStack {
                Button("Copy translation") { copyTranslation() }
                    .disabled(!canUseReview)
                Spacer()
                if applicationJob == nil {
                    Button("Close") { dismiss() }
                        .keyboardShortcut(.cancelAction)
                } else {
                    Button("Cancel application") { applicationJob?.cancel() }
                        .keyboardShortcut(.cancelAction)
                }
                Button("Apply reviewed translation", action: applyReviewed)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canUseReview || !rows.contains { $0.reviewedText != $0.source })
            }
        }
        .padding(16)
        .frame(minWidth: 660, idealWidth: 760, minHeight: 470, idealHeight: 550)
        .task { await loadLanguages() }
        .onDisappear { applicationJob?.cancel() }
        .overlay(alignment: .topLeading) {
            if let job {
                Color.clear
                    .frame(width: 1, height: 1)
                    .allowsHitTesting(false)
                    .accessibilityHidden(true)
                    .translationTask(job.configuration) { session in
                        await translate(using: session, job: job)
                    }
                    .id(job.id)
            }
        }
        .confirmationDialog("Replace the edited translation?", isPresented: $replacingReview) {
            Button("Translate again", role: .destructive) { beginTranslation() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The edited rows stay available until the new translation finishes.")
        }
    }

    private var languageControls: some View {
        HStack(spacing: 14) {
            Picker("From", selection: $sourceID) {
                Text("Detect language").tag(nil as String?)
                ForEach(languages) { choice in Text(choice.name).tag(Optional(choice.id)) }
            }
            .fixedSize()
            .disabled(job != nil || applicationJob != nil || languages.isEmpty)
            Picker("To", selection: $targetID) {
                if targetID == nil { Text("Select language").tag(nil as String?) }
                ForEach(languages) { choice in Text(choice.name).tag(Optional(choice.id)) }
            }
            .fixedSize()
            .disabled(job != nil || applicationJob != nil || languages.isEmpty)
            if job == nil {
                Button("Translate on this Mac") {
                    if hasReviewEdits { replacingReview = true } else { beginTranslation() }
                }
                .disabled(applicationJob != nil || targetID == nil || sourceID == targetID || !rows.contains { !$0.protected })
            } else {
                ProgressView().controlSize(.small).accessibilityLabel("Translating")
                Button("Cancel translation") { cancelTranslation() }
            }
            Spacer(minLength: 0)
        }
    }

    private var reviewTable: some View {
        VStack(spacing: 0) {
            HStack(spacing: 16) {
                Text("Original OCR").frame(maxWidth: .infinity, alignment: .leading)
                Text(translatedLanguage.map { "Translation · \($0)" } ?? "Translation")
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .font(.subheadline.weight(.semibold))
            .padding(8)
            .background(.quaternary)
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(rows) { row in
                        HStack(alignment: .top, spacing: 16) {
                            Text(row.source)
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                            translationCell(row)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .padding(8)
                        Divider()
                    }
                }
            }
        }
        .background(Color(nsColor: .textBackgroundColor))
        .overlay { RoundedRectangle(cornerRadius: 5).strokeBorder(.quaternary) }
    }

    @ViewBuilder
    private func translationCell(_ row: ReviewRow) -> some View {
        if row.protected {
            VStack(alignment: .leading, spacing: 3) {
                Text(row.source).textSelection(.enabled)
                if !row.source.isEmpty {
                    Text("Original kept to preserve technical text.").font(.caption).foregroundStyle(.secondary)
                }
            }
        } else if row.translation != nil {
            VStack(alignment: .leading, spacing: 3) {
                TextField("Translation", text: Binding(get: { rows[row.id].reviewedText }, set: { value in
                    rows[row.id].translation = value
                    hasReviewEdits = true
                }), axis: .vertical)
                .labelsHidden()
                .lineLimit(1...5)
                .accessibilityLabel("Translated row \(row.id + 1)")
                .disabled(job != nil || applicationJob != nil)
                if row.keptAfterValidation {
                    Text("Review this row. The suggestion changed the original numbers or identifiers.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        } else {
            Color.clear.frame(height: 1).accessibilityHidden(true)
        }
    }

    private var canUseReview: Bool {
        job == nil && applicationJob == nil && translatedLanguage != nil && rows.allSatisfy { row in
            row.source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            !row.reviewedText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
    }

    private func loadLanguages() async {
        guard !loadedLanguages else { return }
        detail = "Loading available languages…"
        let supported = await LanguageAvailability().supportedLanguages
        guard !Task.isCancelled else { return }
        loadedLanguages = true
        var included = Set<String>()
        languages = supported.compactMap { language in
            let id = language.minimalIdentifier
            guard included.insert(id).inserted else { return nil }
            let name = Locale.current.localizedString(forIdentifier: id) ?? id
            return LanguageChoice(id: id, name: name, language: language)
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        if let preferred = Locale.preferredLanguages.first {
            let language = Locale.Language(identifier: preferred)
            targetID = languages.first(where: { $0.language.isEquivalent(to: language) })?.id
        }
        targetID = targetID ?? languages.first?.id
        if languages.isEmpty {
            detail = "macOS did not report any translation languages."
        } else if !rows.contains(where: { !$0.protected }) {
            detail = "These rows contain technical text. They will be kept as recognized."
        } else {
            detail = "macOS may ask to download a language pack. Translation runs on this Mac."
        }
    }

    private func beginTranslation() {
        guard job == nil, let target = languages.first(where: { $0.id == targetID }), sourceID != targetID else { return }
        let requested = rows.filter { !$0.protected }.map { row in
            TranslationSession.Request(sourceText: row.source, clientIdentifier: String(row.id))
        }
        guard !requested.isEmpty else { return }
        let source = languages.first(where: { $0.id == sourceID })?.language
        job = TranslationJob(configuration: .init(source: source, target: target.language), targetName: target.name,
                             requests: requested)
        detail = "Translating…"
    }

    private func cancelTranslation() {
        job = nil
        detail = "Translation canceled."
    }

    private func translate(using session: TranslationSession, job running: TranslationJob) async {
        guard job?.id == running.id, !Task.isCancelled else { return }
        do {
            let responses = try await session.translations(from: running.requests)
            guard job?.id == running.id, !Task.isCancelled else { return }
            var review = rows
            var assigned = Set<Int>()
            for response in responses {
                guard let token = response.clientIdentifier, let id = Int(token), review.indices.contains(id),
                      !review[id].protected, assigned.insert(id).inserted else {
                    throw TranslationReviewIssue.missingRows
                }
                let source = review[id].source
                if let translated = TranslationRowPolicy.reviewedSuggestion(response.targetText, original: source) {
                    review[id].translation = translated
                    review[id].keptAfterValidation = false
                } else {
                    review[id].translation = source
                    review[id].keptAfterValidation = true
                }
            }
            guard assigned.count == running.requests.count else { throw TranslationReviewIssue.missingRows }
            rows = review
            translatedLanguage = running.targetName
            hasReviewEdits = false
            detail = "Compare and edit the translated rows before applying them."
        } catch {
            guard job?.id == running.id, !Task.isCancelled else { return }
            if error is CancellationError || (error as? CocoaError)?.code == .userCancelled {
                detail = "Translation canceled."
            } else {
                detail = "Translation couldn’t finish. \(error.localizedDescription)"
            }
        }
        if job?.id == running.id { job = nil }
    }

    private func copyTranslation() {
        let clipboard = NSPasteboard.general
        clipboard.clearContents()
        detail = clipboard.setString(rows.map(\.reviewedText).joined(separator: "\n"), forType: .string)
            ? "Translation copied."
            : "Couldn’t copy the translation."
    }

    private func applyReviewed() {
        guard canUseReview else { return }
        let reviewed = rows.map(\.reviewedText)
        detail = "Applying reviewed translation…"
        applicationJob = Task { @MainActor in
            defer { applicationJob = nil }
            do {
                try await apply(reviewed)
                dismiss()
            } catch is CancellationError {
                detail = "Application canceled. Your reviewed rows are still available."
            } catch {
                detail = error.localizedDescription
            }
        }
    }

    private struct ReviewRow: Identifiable {
        let id: Int
        let source: String
        let protected: Bool
        var translation: String?
        var keptAfterValidation = false

        var reviewedText: String { translation ?? source }
    }

    private struct LanguageChoice: Identifiable {
        let id: String
        let name: String
        let language: Locale.Language
    }

    private struct TranslationJob: Identifiable {
        let id = UUID()
        let configuration: TranslationSession.Configuration
        let targetName: String
        let requests: [TranslationSession.Request]
    }

    private enum TranslationReviewIssue: LocalizedError {
        case missingRows

        var errorDescription: String? { "macOS returned an incomplete set of translated rows." }
    }
}

private enum TranslationRowPolicy {
    private static let filename = try? NSRegularExpression(pattern: #"[\p{L}\p{N}_.-]+\.[A-Za-z0-9]{2,8}(?=\s|$|[),;:])"#)
    private static let numbers = try? NSRegularExpression(pattern: #"[\p{Nd}]+(?:[.,][\p{Nd}]+)*"#)
    private static let acronyms = try? NSRegularExpression(pattern: #"\b[A-Z]{2,}\b"#)

    static func shouldKeepOriginal(_ line: RecognizedLine) -> Bool {
        let source = line.text
        guard line.contacts.isEmpty, source.contains(where: \.isLetter),
              !source.contains("/"), !source.contains("\\"), !source.contains("@"),
              matches(filename, in: source).isEmpty else { return true }

        let tokens = source.split { $0.isWhitespace || "()[]{}:;,!?".contains($0) }
        if tokens.contains(where: { token in
            token.contains(where: \.isLetter) && token.contains(where: \.isNumber)
        }) { return true }
        return matches(numbers, in: source).contains { $0.count > 1 && $0.hasPrefix("0") && $0.allSatisfy(\.isNumber) }
    }

    static func reviewedSuggestion(_ candidate: String, original: String) -> String? {
        let translated = candidate.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !translated.isEmpty, matches(numbers, in: translated) == matches(numbers, in: original) else { return nil }
        let originalAcronyms = Dictionary(grouping: matches(acronyms, in: original), by: { $0 })
        let translatedAcronyms = Dictionary(grouping: matches(acronyms, in: translated), by: { $0 })
        guard originalAcronyms.allSatisfy({ entry in
            (translatedAcronyms[entry.key]?.count ?? 0) >= entry.value.count
        }) else { return nil }
        let prefix = original.prefix(while: \.isWhitespace)
        let reversedSuffix = original.reversed().prefix(while: \.isWhitespace)
        return String(prefix) + translated + String(reversedSuffix.reversed())
    }

    private static func matches(_ pattern: NSRegularExpression?, in text: String) -> [Substring] {
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        return pattern?.matches(in: text, range: range).compactMap { match in
            Range(match.range, in: text).map { text[$0] }
        } ?? []
    }
}

enum StudioTranslationReviewSelfCheck {
    static func run() throws {
        let protected = [#"C:\Snips\April 0027.png"#, "report.txt", "ZX003", "QTY 0007", "00912"]
        guard protected.allSatisfy({ TranslationRowPolicy.shouldKeepOriginal(.init(text: $0, bounds: .zero)) }),
              !TranslationRowPolicy.shouldKeepOriginal(.init(text: "3 apps found", bounds: .zero)),
              TranslationRowPolicy.reviewedSuggestion("3 apps found", original: "  3 applis trouvées  ") == "  3 apps found  ",
              TranslationRowPolicy.reviewedSuggestion("4 apps found", original: "3 applis trouvées") == nil,
              TranslationRowPolicy.reviewedSuggestion("Open the system logs", original: "Ouvrez les journaux DISM") == nil else {
            throw CheckError.protectedContentChanged
        }
    }

    private enum CheckError: Error {
        case protectedContentChanged
    }
}
