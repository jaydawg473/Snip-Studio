import AppKit
import AVFoundation
import Combine
import CoreGraphics
import Foundation
import ScreenCaptureKit

struct StudioRecordingSource: Identifiable, Hashable {
    enum Kind: Hashable { case display, window }

    let id: String
    let title: String
    let size: CGSize
    let kind: Kind
    fileprivate let captureID: UInt32

    static func == (lhs: Self, rhs: Self) -> Bool { lhs.id == rhs.id }

    func hash(into hasher: inout Hasher) { hasher.combine(id) }
}

@available(macOS 15.0, *)
@MainActor
final class StudioScreenRecorder: ObservableObject {
    enum State { case idle, starting, recording, stopping }

    @Published private(set) var state: State = .idle
    @Published private(set) var lastError: String?
    private var attempt: UUID?
    private var session: RecordingSession?

    static func sources() async throws -> [StudioRecordingSource] {
        try checkScreenAccess()
        let content = try await shareableContent()
        let displays = content.displays.sorted { lhs, rhs in
            if lhs.displayID == CGMainDisplayID() { return rhs.displayID != CGMainDisplayID() }
            if rhs.displayID == CGMainDisplayID() { return false }
            return lhs.displayID < rhs.displayID
        }.enumerated().compactMap { index, display -> StudioRecordingSource? in
            let size = CGDisplayBounds(display.displayID).size
            guard hasArea(size) else { return nil }
            let title = display.displayID == CGMainDisplayID() ? "Main Display" : "Display \(index + 1)"
            return StudioRecordingSource(id: "display-\(display.displayID)", title: title,
                                         size: size, kind: .display, captureID: display.displayID)
        }
        let ourPID = ProcessInfo.processInfo.processIdentifier
        let windows = content.windows.compactMap { window -> StudioRecordingSource? in
            guard window.isOnScreen, window.windowLayer == 0, let application = window.owningApplication,
                  application.processID != ourPID, hasArea(window.frame.size) else { return nil }
            let name = window.title?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            let title = name.isEmpty ? application.applicationName : "\(application.applicationName) — \(name)"
            return StudioRecordingSource(id: "window-\(window.windowID)", title: title,
                                         size: window.frame.size, kind: .window, captureID: window.windowID)
        }.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
        return displays + windows
    }

    static func preview(source: StudioRecordingSource) async throws -> CGImage {
        try checkScreenAccess()
        let filter = try await filter(for: source)
        let config = SCStreamConfiguration()
        let size = filter.contentRect.size
        let factor = min(1, 1200 / max(size.width, size.height))
        config.width = max(2, Int((size.width * factor).rounded()))
        config.height = max(2, Int((size.height * factor).rounded()))
        config.showsCursor = false
        let result = RecordingSignal<CGImage>()
        let work = Task { @MainActor in
            do {
                result.resolve(.success(try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)))
            } catch { result.resolve(.failure(error)) }
        }
        defer { work.cancel() }
        return try await result.wait(seconds: 12, onTimeout: .previewTimedOut)
    }

    func start(source: StudioRecordingSource, area: CGRect? = nil, systemAudio: Bool = true,
               microphone: Bool = false, showsPointer: Bool = true) async throws {
        guard state == .idle else { throw RecordingFailure.busy }
        let id = UUID()
        attempt = id
        state = .starting
        lastError = nil
        do {
            try Self.checkScreenAccess()
            if microphone {
                guard await AVCaptureDevice.requestAccess(for: .audio) else {
                    throw RecordingFailure.microphoneAccess
                }
                try checkAttempt(id)
            }
            let filter = try await Self.filter(for: source)
            try checkAttempt(id)
            let bounds = CGRect(origin: .zero, size: filter.contentRect.size)
            let selected = try Self.recordingRect(area, within: bounds, kind: source.kind)
            let config = SCStreamConfiguration()
            let nativeScale = CGFloat(filter.pointPixelScale)
            guard nativeScale.isFinite, nativeScale > 0 else { throw RecordingFailure.sourceUnavailable }
            let scale = min(nativeScale, 3840 / max(selected.width, selected.height))
            config.width = Self.evenDimension(selected.width * scale)
            config.height = Self.evenDimension(selected.height * scale)
            config.minimumFrameInterval = CMTime(value: 1, timescale: 30)
            config.queueDepth = 3
            config.showsCursor = showsPointer
            config.capturesAudio = systemAudio
            config.excludesCurrentProcessAudio = true
            config.captureMicrophone = microphone
            if area != nil { config.sourceRect = selected }

            let output = SCRecordingOutputConfiguration()
            guard output.availableOutputFileTypes.contains(.mp4), output.availableVideoCodecTypes.contains(.h264) else {
                throw RecordingFailure.unsupportedFormat
            }
            let paths = try RecordingPaths.create(id: id)
            output.outputURL = paths.draft
            output.outputFileType = .mp4
            output.videoCodecType = .h264
            let recording = RecordingSession(paths: paths, filter: filter, config: config, output: output)
            recording.delegate.failed = { [weak self, weak recording] error in
                guard let self, let recording, self.session === recording else { return }
                recording.fail(error)
                if self.state == .recording {
                    self.lastError = error.localizedDescription
                    self.reset(recording, keepingFile: false)
                }
            }
            session = recording
            try recording.start()
            try await recording.started.wait(seconds: 15, onTimeout: .startTimedOut)
            try checkAttempt(id)
            state = .recording
        } catch {
            if attempt == id { reset(session, keepingFile: false) }
            throw error
        }
    }

    func stop() async throws -> URL {
        guard state == .recording, let recording = session else { throw RecordingFailure.notRecording }
        state = .stopping
        do {
            recording.stop()
            try await recording.finished.wait(seconds: 30, onTimeout: .stopTimedOut)
            try Task.checkCancellation()
            guard session === recording else { throw CancellationError() }
            do {
                let url = try recording.paths.publish()
                reset(recording, keepingFile: true)
                return url
            } catch {
                // ScreenCaptureKit finalized the original. Preserve it if moving it fails.
                reset(recording, keepingFile: true)
                throw RecordingFailure.publishFailed(recording.paths.draft, error)
            }
        } catch {
            if session === recording { reset(recording, keepingFile: false) }
            throw error
        }
    }

    func cancel() async {
        reset(session, keepingFile: false)
    }

    private func checkAttempt(_ id: UUID) throws {
        try Task.checkCancellation()
        guard attempt == id else { throw CancellationError() }
    }

    private func reset(_ active: RecordingSession?, keepingFile: Bool) {
        attempt = nil
        session = nil
        state = .idle
        if !keepingFile { active?.cancel() }
    }

    private static func checkScreenAccess() throws {
        guard CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() else {
            throw RecordingFailure.screenAccess
        }
    }

    private static func shareableContent() async throws -> SCShareableContent {
        let result = RecordingSignal<SCShareableContent>()
        SCShareableContent.getExcludingDesktopWindows(true, onScreenWindowsOnly: true) { content, error in
            Task { @MainActor in
                if let content { result.resolve(.success(content)) }
                else { result.resolve(.failure(error ?? RecordingFailure.sourceUnavailable)) }
            }
        }
        return try await result.wait(seconds: 12, onTimeout: .sourcesTimedOut)
    }

    private static func filter(for source: StudioRecordingSource) async throws -> SCContentFilter {
        let content = try await shareableContent()
        let ourPID = ProcessInfo.processInfo.processIdentifier
        let filter: SCContentFilter
        switch source.kind {
        case .display:
            guard let display = content.displays.first(where: { $0.displayID == source.captureID }) else {
                throw RecordingFailure.sourceUnavailable
            }
            let excluded = content.applications.filter { $0.processID == ourPID }
            if excluded.isEmpty {
                let windows = content.windows.filter { $0.owningApplication?.processID == ourPID }
                filter = SCContentFilter(display: display, excludingWindows: windows)
            } else {
                filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
            }
        case .window:
            guard let window = content.windows.first(where: {
                $0.windowID == source.captureID && $0.owningApplication?.processID != ourPID
            }) else { throw RecordingFailure.sourceUnavailable }
            filter = SCContentFilter(desktopIndependentWindow: window)
        }
        guard hasArea(filter.contentRect.size) else { throw RecordingFailure.sourceUnavailable }
        return filter
    }

    private static func recordingRect(_ area: CGRect?, within bounds: CGRect,
                                      kind: StudioRecordingSource.Kind) throws -> CGRect {
        guard let area else { return bounds }
        // ScreenCaptureKit uses the full bounds of single-window filters, regardless of sourceRect.
        guard kind == .display else { throw RecordingFailure.windowAdjustment }
        guard area.origin.x.isFinite, area.origin.y.isFinite, hasArea(area.size),
              bounds.contains(area) else { throw RecordingFailure.invalidArea }
        return area
    }

    private static func hasArea(_ size: CGSize) -> Bool {
        size.width.isFinite && size.height.isFinite && size.width >= 2 && size.height >= 2
    }

    private static func evenDimension(_ pixels: CGFloat) -> Int { max(2, Int(pixels.rounded(.down)) & ~1) }
}

@available(macOS 15.0, *)
@MainActor
private final class RecordingSession {
    let paths: RecordingPaths
    let started = RecordingSignal<Void>()
    let finished = RecordingSignal<Void>()
    let delegate: RecordingDelegate
    private let stream: SCStream
    private let output: SCRecordingOutput
    private var calledStart = false
    private var abandoned = false

    init(paths: RecordingPaths, filter: SCContentFilter, config: SCStreamConfiguration,
         output configuration: SCRecordingOutputConfiguration) {
        self.paths = paths
        let delegate = RecordingDelegate()
        self.delegate = delegate
        stream = SCStream(filter: filter, configuration: config, delegate: delegate)
        output = SCRecordingOutput(configuration: configuration, delegate: delegate)
        delegate.started = { [weak self] in self?.started.resolve(.success(())) }
        delegate.finished = { [weak self] in
            self?.started.resolve(.failure(RecordingFailure.noVideo))
            self?.finished.resolve(.success(()))
        }
    }

    func start() throws {
        try stream.addRecordingOutput(output)
        calledStart = true
        stream.startCapture { [self] error in
            Task { @MainActor in
                if abandoned {
                    // A late start can land after Cancel's stop request.
                    stream.stopCapture(completionHandler: nil)
                } else if let error {
                    delegate.failed?(error)
                }
            }
        }
    }

    func stop() {
        guard !finished.isResolved else { return }
        stream.stopCapture { [weak self] error in
            guard let error else { return }
            Task { @MainActor in self?.finished.resolve(.failure(error)) }
        }
    }

    func fail(_ error: Error) {
        started.resolve(.failure(error))
        finished.resolve(.failure(error))
    }

    func cancel() {
        abandoned = true
        fail(CancellationError())
        if calledStart { stream.stopCapture(completionHandler: nil) }
        paths.removeDraft()
    }
}

@available(macOS 15.0, *)
private final class RecordingDelegate: NSObject, SCRecordingOutputDelegate, SCStreamDelegate, @unchecked Sendable {
    @MainActor var started: (() -> Void)?
    @MainActor var finished: (() -> Void)?
    @MainActor var failed: ((Error) -> Void)?

    func recordingOutputDidStartRecording(_ recordingOutput: SCRecordingOutput) {
        Task { @MainActor in started?() }
    }

    func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) {
        Task { @MainActor in finished?() }
    }

    func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: Error) {
        Task { @MainActor in failed?(error) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in failed?(error) }
    }
}

@MainActor
private final class RecordingSignal<Value> {
    private var result: Result<Value, Error>?
    private var continuation: CheckedContinuation<Value, Error>?
    private var timeoutTask: Task<Void, Never>?
    var isResolved: Bool { result != nil }

    func wait(seconds: Int, onTimeout failure: RecordingFailure) async throws -> Value {
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                if let result {
                    continuation.resume(with: result)
                    return
                }
                precondition(self.continuation == nil)
                self.continuation = continuation
                timeoutTask = Task { @MainActor [weak self] in
                    do {
                        try await Task.sleep(for: .seconds(seconds))
                        self?.resolve(.failure(failure))
                    } catch { return }
                }
            }
        } onCancel: {
            Task { @MainActor in self.resolve(.failure(CancellationError())) }
        }
    }

    func resolve(_ value: Result<Value, Error>) {
        guard result == nil else { return }
        result = value
        timeoutTask?.cancel()
        timeoutTask = nil
        let suspended = continuation
        continuation = nil
        suspended?.resume(with: value)
    }
}

private struct RecordingPaths {
    let workspace: URL
    let draft: URL
    let final: URL

    static func create(id: UUID) throws -> Self {
        let files = FileManager.default
        let movies = try files.url(for: .moviesDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        let folder = movies.appendingPathComponent("Snip Studio", isDirectory: true)
        let workspace = folder.appendingPathComponent(".snipstudio-recording-\(id.uuidString)", isDirectory: true)
        let permissions: [FileAttributeKey: Any] = [.posixPermissions: NSNumber(value: 0o700)]
        try files.createDirectory(at: folder, withIntermediateDirectories: true, attributes: permissions)
        try files.createDirectory(at: workspace, withIntermediateDirectories: false, attributes: permissions)
        let timestamp = Date().ISO8601Format().replacingOccurrences(of: ":", with: "-")
        return Self(workspace: workspace, draft: workspace.appendingPathComponent("capture.mp4"),
                    final: folder.appendingPathComponent("Snip Studio \(timestamp) \(id.uuidString).mp4"))
    }

    func publish() throws -> URL {
        let values = try draft.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
        guard values.isRegularFile == true, let size = values.fileSize, size > 0 else {
            throw RecordingFailure.noVideo
        }
        try FileManager.default.moveItem(at: draft, to: final)
        removeDraft()
        return final
    }

    func removeDraft() { try? FileManager.default.removeItem(at: workspace) }
}

private enum RecordingFailure: LocalizedError {
    case screenAccess, microphoneAccess, sourceUnavailable, invalidArea, windowAdjustment, unsupportedFormat
    case busy, notRecording, sourcesTimedOut, previewTimedOut, startTimedOut, stopTimedOut, noVideo
    case publishFailed(URL, Error)

    var errorDescription: String? {
        switch self {
        case .screenAccess: "Allow Snip Studio in System Settings under Privacy & Security → Screen & System Audio Recording. Then reopen it and try again."
        case .microphoneAccess: "Allow Snip Studio to use the microphone in System Settings, or turn off Microphone and try again."
        case .sourceUnavailable: "The selected screen or window is unavailable. Refresh the list and choose it again."
        case .invalidArea: "Choose an area at least two points wide and high within the display."
        case .windowAdjustment: "Window recording includes the whole window. Choose a display to adjust the area."
        case .unsupportedFormat: "The system recorder could not provide MP4 with H.264."
        case .busy: "A recording is already starting, running, or finishing."
        case .notRecording: "There is no recording to stop."
        case .sourcesTimedOut: "macOS took too long to list the available screens and windows."
        case .previewTimedOut: "The preview timed out. Choose the source again."
        case .startTimedOut: "The recording did not start in time. Check screen recording access and try again."
        case .stopTimedOut: "macOS did not finish saving the recording in time."
        case .noVideo: "No video was recorded. Start a new recording and let it run before stopping."
        case let .publishFailed(file, cause):
            "The recording could not be moved to the Movies folder. The original is at \(file.path). \(cause.localizedDescription)"
        }
    }
}
