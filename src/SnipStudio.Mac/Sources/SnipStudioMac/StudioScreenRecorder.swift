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
    fileprivate let ownerPID: pid_t?
    fileprivate let adjustmentDisplayID: CGDirectDisplayID?

    var canAdjustArea: Bool { kind == .display || adjustmentDisplayID != nil }

    static func == (lhs: Self, rhs: Self) -> Bool { lhs.id == rhs.id }

    func hash(into hasher: inout Hasher) { hasher.combine(id) }
}

struct StudioMicrophone: Identifiable, Hashable {
    let id: String
    let title: String
}

@available(macOS 15.0, *)
@MainActor
final class StudioScreenRecorder: ObservableObject {
    enum State { case idle, starting, recording, stopping }

    @Published private(set) var state: State = .idle
    @Published private(set) var lastError: String?
    @Published private(set) var recordingFrame: CGRect?
    private var attempt: UUID?
    private var session: RecordingSession?

    static func sources() async throws -> [StudioRecordingSource] {
        try checkScreenAccess()
        let content = try await shareableContent()
        let displayCandidates = content.displays.filter {
            CGDisplayIsActive($0.displayID) != 0 && CGDisplayMirrorsDisplay($0.displayID) == kCGNullDirectDisplay
        }
        let displays = displayCandidates.sorted { lhs, rhs in
            if lhs.displayID == CGMainDisplayID() { return rhs.displayID != CGMainDisplayID() }
            if rhs.displayID == CGMainDisplayID() { return false }
            return lhs.displayID < rhs.displayID
        }.enumerated().compactMap { index, display -> StudioRecordingSource? in
            let size = display.frame.size
            guard hasArea(size) else { return nil }
            let title = display.displayID == CGMainDisplayID() ? "Main Display" : "Display \(index + 1)"
            return StudioRecordingSource(id: "display-\(display.displayID)", title: title,
                                         size: size, kind: .display, captureID: display.displayID,
                                         ownerPID: nil, adjustmentDisplayID: display.displayID)
        }
        let ourPID = ProcessInfo.processInfo.processIdentifier
        let windows = content.windows.compactMap { window -> StudioRecordingSource? in
            guard window.isOnScreen, window.windowLayer == 0, let application = window.owningApplication,
                  application.processID != ourPID, hasArea(window.frame.size) else { return nil }
            let name = window.title?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            let title = name.isEmpty ? application.applicationName : "\(application.applicationName) — \(name)"
            let adjustmentDisplay = displayCandidates.first { display in
                adjustedWindowFilter(window, on: display) != nil
            }
            return StudioRecordingSource(id: "window-\(window.windowID)", title: title,
                                         size: window.frame.size, kind: .window, captureID: window.windowID,
                                         ownerPID: application.processID, adjustmentDisplayID: adjustmentDisplay?.displayID)
        }.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
        return displays + windows
    }

    static func microphones() -> [StudioMicrophone] {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.microphone], mediaType: .audio, position: .unspecified)
            .devices.filter(\.isConnected)
            .map { StudioMicrophone(id: $0.uniqueID, title: $0.localizedName) }
            .sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
    }

    static func preview(source: StudioRecordingSource) async throws -> CGImage {
        try checkScreenAccess()
        let sourceRect = source.canAdjustArea ? CGRect(origin: .zero, size: source.size) : nil
        let target = try await target(for: source, area: sourceRect)
        let config = SCStreamConfiguration()
        let size = target.size
        let factor = min(1, 1200 / max(size.width, size.height))
        config.width = max(2, Int((size.width * factor).rounded()))
        config.height = max(2, Int((size.height * factor).rounded()))
        config.showsCursor = false
        target.configureGeometry(config)
        let result = RecordingSignal<CGImage>()
        let work = Task { @MainActor in
            do {
                result.resolve(.success(try await SCScreenshotManager.captureImage(contentFilter: target.filter, configuration: config)))
            } catch { result.resolve(.failure(error)) }
        }
        defer { work.cancel() }
        return try await result.wait(seconds: 12, onTimeout: .previewTimedOut)
    }

    func start(source: StudioRecordingSource, area: CGRect? = nil, systemAudio: Bool = true,
               microphone: Bool = false, showsPointer: Bool = true, microphoneDeviceID: String? = nil) async throws {
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
                if let microphoneDeviceID {
                    guard Self.microphones().contains(where: { $0.id == microphoneDeviceID }) else {
                        throw RecordingFailure.microphoneUnavailable
                    }
                } else if AVCaptureDevice.default(for: .audio) == nil {
                    throw RecordingFailure.microphoneUnavailable
                }
            }
            let target = try await Self.target(for: source, area: area)
            try checkAttempt(id)
            let config = SCStreamConfiguration()
            let nativeScale = CGFloat(target.filter.pointPixelScale)
            guard nativeScale.isFinite, nativeScale > 0 else { throw RecordingFailure.sourceUnavailable }
            let scale = min(nativeScale, 3840 / max(target.size.width, target.size.height))
            config.width = Self.evenDimension(target.size.width * scale)
            config.height = Self.evenDimension(target.size.height * scale)
            config.minimumFrameInterval = CMTime(value: 1, timescale: 30)
            config.queueDepth = 3
            config.showsCursor = showsPointer
            config.capturesAudio = systemAudio
            config.excludesCurrentProcessAudio = true
            config.captureMicrophone = microphone
            if microphone { config.microphoneCaptureDeviceID = microphoneDeviceID }
            target.configureGeometry(config)

            let output = SCRecordingOutputConfiguration()
            guard output.availableOutputFileTypes.contains(.mp4), output.availableVideoCodecTypes.contains(.h264) else {
                throw RecordingFailure.unsupportedFormat
            }
            let paths = try RecordingPaths.create(id: id)
            output.outputURL = paths.draft
            output.outputFileType = .mp4
            output.videoCodecType = .h264
            let recording = RecordingSession(paths: paths, filter: target.filter, config: config, output: output)
            recording.delegate.failed = { [weak self, weak recording] error in
                guard let self, let recording, self.session === recording else { return }
                recording.fail(error)
                if self.state == .recording {
                    self.lastError = self.keepDraft(recording, after: error).localizedDescription
                }
            }
            session = recording
            try recording.start()
            try await recording.started.wait(seconds: 15, onTimeout: .startTimedOut)
            try checkAttempt(id)
            recordingFrame = target.fixedDesktopFrame
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
            if session === recording { throw keepDraft(recording, after: error) }
            throw error
        }
    }

    private func keepDraft(_ active: RecordingSession, after error: Error) -> RecordingFailure {
        active.cancel(removeDraft: false)
        reset(active, keepingFile: true)
        return .unfinishedDraft(active.paths.draft, error)
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
        recordingFrame = nil
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

    private static func target(for source: StudioRecordingSource, area: CGRect?) async throws -> ScreenTarget {
        let content = try await shareableContent()
        let ourPID = ProcessInfo.processInfo.processIdentifier
        switch source.kind {
        case .display:
            guard let display = content.displays.first(where: { $0.displayID == source.captureID }),
                  hasArea(display.frame.size) else { throw RecordingFailure.sourceUnavailable }
            let filter: SCContentFilter
            let excluded = content.applications.filter { $0.processID == ourPID }
            if excluded.isEmpty {
                let windows = content.windows.filter { $0.owningApplication?.processID == ourPID }
                filter = SCContentFilter(display: display, excludingWindows: windows)
            } else {
                filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
            }
            if area != nil && display.frame.size != source.size { throw RecordingFailure.selectionChanged }
            let selected = try recordingRect(area, within: display.frame.size)
            let boundary = excluded.isEmpty || area == nil ? nil : selected.offsetBy(dx: display.frame.minX, dy: display.frame.minY)
            return ScreenTarget(filter: filter, size: selected.size, sourceRect: area == nil ? nil : selected,
                                isolatesWindow: false, fixedDesktopFrame: boundary)
        case .window:
            guard let window = content.windows.first(where: {
                $0.windowID == source.captureID && $0.isOnScreen && $0.windowLayer == 0 &&
                    $0.owningApplication?.processID == source.ownerPID && source.ownerPID != ourPID
            }), hasArea(window.frame.size) else { throw RecordingFailure.sourceUnavailable }
            guard let area else {
                let filter = SCContentFilter(desktopIndependentWindow: window)
                return ScreenTarget(filter: filter, size: window.frame.size, sourceRect: nil,
                                    isolatesWindow: true, fixedDesktopFrame: nil)
            }
            guard let displayID = source.adjustmentDisplayID else { throw RecordingFailure.windowAdjustment }
            guard let display = content.displays.first(where: { $0.displayID == displayID }),
                  CGDisplayIsActive(displayID) != 0 && CGDisplayMirrorsDisplay(displayID) == kCGNullDirectDisplay,
                  window.frame.size == source.size && display.frame.contains(window.frame) else {
                throw RecordingFailure.selectionChanged
            }
            guard let filter = adjustedWindowFilter(window, on: display) else { throw RecordingFailure.windowAdjustment }
            let displayRect = try displayRect(for: area, windowFrame: window.frame, displayFrame: display.frame)
            let boundary = displayRect.offsetBy(dx: display.frame.minX, dy: display.frame.minY)
            return ScreenTarget(filter: filter, size: displayRect.size, sourceRect: displayRect,
                                isolatesWindow: true, fixedDesktopFrame: boundary)
        }
    }

    private static func adjustedWindowFilter(_ window: SCWindow, on display: SCDisplay) -> SCContentFilter? {
        guard display.frame.contains(window.frame) else { return nil }
        // A display-dependent filter keeps other windows out while exposing display-local geometry.
        // A window-style filter can ignore sourceRect, so require display-sized content explicitly.
        let filter = SCContentFilter(display: display, including: [window])
        guard filter.style == .display, filter.contentRect.size == display.frame.size else { return nil }
        return filter
    }

    static func displayRect(for area: CGRect, windowFrame: CGRect, displayFrame: CGRect) throws -> CGRect {
        guard hasArea(displayFrame.size), hasArea(windowFrame.size), displayFrame.contains(windowFrame) else {
            throw RecordingFailure.windowAdjustment
        }
        let selected = try recordingRect(area, within: windowFrame.size)
        return selected.offsetBy(dx: windowFrame.minX - displayFrame.minX,
                                 dy: windowFrame.minY - displayFrame.minY)
    }

    private static func recordingRect(_ area: CGRect?, within size: CGSize) throws -> CGRect {
        let bounds = CGRect(origin: .zero, size: size)
        guard let area else { return bounds }
        guard area.origin.x.isFinite, area.origin.y.isFinite, hasArea(area.size),
              bounds.contains(area) else { throw RecordingFailure.invalidArea }
        return area
    }

    private static func hasArea(_ size: CGSize) -> Bool {
        size.width.isFinite && size.height.isFinite && size.width >= 2 && size.height >= 2
    }

    private static func evenDimension(_ pixels: CGFloat) -> Int { max(2, Int(pixels.rounded(.down)) & ~1) }

    private struct ScreenTarget {
        let filter: SCContentFilter
        let size: CGSize
        let sourceRect: CGRect?
        let isolatesWindow: Bool
        let fixedDesktopFrame: CGRect?

        func configureGeometry(_ config: SCStreamConfiguration) {
            if isolatesWindow {
                config.ignoreShadowsDisplay = true
                config.ignoreShadowsSingleWindow = true
            }
            if let sourceRect { config.sourceRect = sourceRect }
        }
    }
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

    func cancel(removeDraft: Bool = true) {
        abandoned = true
        fail(CancellationError())
        if calledStart { stream.stopCapture(completionHandler: nil) }
        if removeDraft { paths.removeDraft() }
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
    case screenAccess, microphoneAccess, microphoneUnavailable
    case sourceUnavailable, invalidArea, selectionChanged, windowAdjustment, unsupportedFormat
    case busy, notRecording, sourcesTimedOut, previewTimedOut, startTimedOut, stopTimedOut, noVideo
    case publishFailed(URL, Error)
    case unfinishedDraft(URL, Error)

    var errorDescription: String? {
        switch self {
        case .screenAccess: "Allow Snip Studio in System Settings under Privacy & Security → Screen & System Audio Recording. Then reopen it and try again."
        case .microphoneAccess: "Allow Snip Studio to use the microphone in System Settings, or turn off Microphone and try again."
        case .microphoneUnavailable: "The microphone is unavailable. Choose an available input or turn off Microphone."
        case .sourceUnavailable: "The selected screen or window is unavailable. Refresh the list and choose it again."
        case .invalidArea: "Choose an area at least two points wide and high within the selected source."
        case .selectionChanged: "The selected window or display changed size or moved to another display. Refresh the list and adjust the area again."
        case .windowAdjustment: "This window is only available as a whole-window recording. Move it entirely onto one display and refresh the list, or choose a display region."
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
        case let .unfinishedDraft(file, cause):
            "The recording didn’t finish. Any recoverable video was left at \(file.path). \(cause.localizedDescription)"
        }
    }
}
