import AVFoundation
import Darwin
import Foundation

enum StudioVideoExporter {
    static func saveCopy(from source: URL, to destination: URL) async throws {
        let input = try validatePaths(source, destination)
        guard source.pathExtension.caseInsensitiveCompare(destination.pathExtension) == .orderedSame else {
            throw VideoExportError.copyExtensionChanged
        }
        let staged = try StagingFile(for: destination)
        defer { staged.cleanUp() }
        try copyFile(input, to: staged.output)
        try staged.publish()
    }

    static func saveTrimmedCopy(from source: URL, to destination: URL, start: CMTime, end: CMTime) async throws {
        let input = try validatePaths(source, destination, requiresMP4: true)
        let asset = AVURLAsset(url: input, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        let requested = try await inspect(asset, start: start, end: end)
        guard let session = await makeSession(asset, preset: AVAssetExportPresetHighestQuality) else {
            throw VideoExportError.unsupportedVideo
        }
        let staged = try StagingFile(for: destination)
        defer { staged.cleanUp() }
        try await export(session, range: requested.range, to: staged.output)
        _ = try await verify(staged.output, expected: requested)
        try staged.publish()
    }

    @discardableResult
    static func compress(from source: URL, to destination: URL, maximumBytes: Int64,
                         start: CMTime? = nil, end: CMTime? = nil) async throws -> Int64 {
        guard maximumBytes > 0 else { throw VideoExportError.invalidSizeLimit }
        let input = try validatePaths(source, destination, requiresMP4: true)
        let asset = AVURLAsset(url: input, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        let requested = try await inspect(asset, start: start, end: end)

        if requested.isFullClip, input.pathExtension.lowercased() == "mp4", try fileSize(input) <= maximumBytes {
            let staged = try StagingFile(for: destination)
            defer { staged.cleanUp() }
            try copyFile(input, to: staged.output, maximumBytes: maximumBytes)
            // Validate the staged file as well, in case another program edits the source during copying.
            let bytes = try await verify(staged.output, expected: requested)
            guard bytes <= maximumBytes else { throw VideoExportError.cannotFit(maximumBytes) }
            try staged.publish()
            return bytes
        }

        let presets = [AVAssetExportPreset1280x720, AVAssetExportPreset960x540, AVAssetExportPreset640x480,
                       AVAssetExportPresetLowQuality]
        var hadCompatiblePreset = false
        for preset in presets {
            try Task.checkCancellation()
            guard let session = await makeSession(asset, preset: preset) else { continue }
            hadCompatiblePreset = true
            let staged = try StagingFile(for: destination)
            defer { staged.cleanUp() }
            do {
                try await export(session, range: requested.range, to: staged.output, maximumBytes: maximumBytes)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                // AVFoundation may fail an encode if this preset cannot fit its file length limit.
                try Task.checkCancellation()
                if isStorageError(error) { throw error }
                continue
            }
            let bytes: Int64
            do {
                bytes = try await verify(staged.output, expected: requested)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                try Task.checkCancellation()
                continue
            }
            guard bytes <= maximumBytes else { continue }
            try staged.publish()
            return bytes
        }
        guard hadCompatiblePreset else { throw VideoExportError.unsupportedVideo }
        throw VideoExportError.cannotFit(maximumBytes)
    }

    private struct Clip {
        let range: CMTimeRange
        let isFullClip: Bool
        let durationTolerance: TimeInterval
        let audioTrackCount: Int
    }

    private static func inspect(_ asset: AVURLAsset, start: CMTime?, end: CMTime?) async throws -> Clip {
        try Task.checkCancellation()
        let assetDuration = try await asset.load(.duration)
        guard reliableTime(assetDuration), CMTimeCompare(assetDuration, .zero) > 0,
              try await asset.load(.isPlayable),
              let video = try await asset.loadTracks(withMediaType: .video).first else {
            throw VideoExportError.unsupportedVideo
        }
        let lower = start ?? .zero
        let upper = end ?? assetDuration
        guard reliableTime(lower), reliableTime(upper), CMTimeCompare(lower, .zero) >= 0,
              CMTimeCompare(lower, upper) < 0, CMTimeCompare(upper, assetDuration) <= 0 else {
            throw VideoExportError.invalidTrimRange
        }
        let range = CMTimeRange(start: lower, end: upper)
        let videoRange = try await video.load(.timeRange)
        guard overlaps(videoRange, range) else { throw VideoExportError.noVideoInTrimRange }

        var audioTrackCount = 0
        for audio in try await asset.loadTracks(withMediaType: .audio) {
            let audioRange = try await audio.load(.timeRange)
            if overlaps(audioRange, range) { audioTrackCount += 1 }
        }
        let frameRate = try await video.load(.nominalFrameRate)
        let frameTolerance = frameRate.isFinite && frameRate > 0 ? 2 / Double(frameRate) : 0.13
        return Clip(
            range: range,
            isFullClip: CMTimeCompare(lower, .zero) == 0 && CMTimeCompare(upper, assetDuration) == 0,
            durationTolerance: min(0.25, max(0.05, frameTolerance)),
            audioTrackCount: audioTrackCount
        )
    }

    private static func reliableTime(_ time: CMTime) -> Bool {
        time.isNumeric && time.seconds.isFinite && time.epoch == 0
    }

    private static func overlaps(_ first: CMTimeRange, _ second: CMTimeRange) -> Bool {
        let intersection = CMTimeRangeGetIntersection(first, otherRange: second)
        return reliableTime(intersection.duration) && CMTimeCompare(intersection.duration, .zero) > 0
    }

    private static func makeSession(_ asset: AVURLAsset, preset: String) async -> AVAssetExportSession? {
        guard await AVAssetExportSession.compatibility(ofExportPreset: preset, with: asset, outputFileType: .mp4) else {
            return nil
        }
        return AVAssetExportSession(asset: asset, presetName: preset)
    }

    private static func export(_ session: AVAssetExportSession, range: CMTimeRange, to url: URL,
                               maximumBytes: Int64? = nil) async throws {
        try Task.checkCancellation()
        session.timeRange = range
        session.shouldOptimizeForNetworkUse = true
        if let maximumBytes { session.fileLengthLimit = maximumBytes }
        do {
            #if compiler(>=6.2)
            try await session.export(to: url, as: .mp4)
            #elseif compiler(>=6.0)
            if #available(macOS 15, *) {
                try await session.export(to: url, as: .mp4)
            } else {
                try await exportOnSonoma(session, to: url)
            }
            #else
            try await exportOnSonoma(session, to: url)
            #endif
        } catch {
            try Task.checkCancellation()
            throw error
        }
        try Task.checkCancellation()
    }

    private static func exportOnSonoma(_ session: AVAssetExportSession, to url: URL) async throws {
        session.outputURL = url
        session.outputFileType = .mp4
        let lifetime = SonomaExportLifetime(session: session)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (completion: CheckedContinuation<Void, any Error>) in
                if !lifetime.start({
                    switch session.status {
                    case .completed: completion.resume()
                    case .cancelled: completion.resume(throwing: CancellationError())
                    default: completion.resume(throwing: session.error ?? VideoExportError.encoderFailed)
                    }
                }) {
                    completion.resume(throwing: CancellationError())
                }
            }
        } onCancel: {
            lifetime.cancel()
        }
    }

    private static func verify(_ url: URL, expected: Clip) async throws -> Int64 {
        try Task.checkCancellation()
        let bytes = try fileSize(url)
        let written = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
        let duration = try await written.load(.duration)
        let videoTracks = try await written.loadTracks(withMediaType: .video)
        let audioTracks = try await written.loadTracks(withMediaType: .audio)
        guard bytes > 0, reliableTime(duration), CMTimeCompare(duration, .zero) > 0,
              !videoTracks.isEmpty, audioTracks.count >= expected.audioTrackCount,
              abs(duration.seconds - expected.range.duration.seconds) <= expected.durationTolerance,
              try await written.load(.isPlayable) else { throw VideoExportError.incompleteOutput }
        return bytes
    }

    private static func validatePaths(_ source: URL, _ destination: URL, requiresMP4: Bool = false) throws -> URL {
        guard source.isFileURL, destination.isFileURL, !destination.hasDirectoryPath,
              !requiresMP4 || destination.pathExtension.lowercased() == "mp4" else {
            throw VideoExportError.invalidDestination
        }
        let input = source.standardizedFileURL.resolvingSymlinksInPath()
        let inputProperties = try input.resourceValues(forKeys: [.isRegularFileKey, .fileResourceIdentifierKey])
        guard inputProperties.isRegularFile == true else {
            throw VideoExportError.unsupportedVideo
        }
        let output = destination.standardizedFileURL.resolvingSymlinksInPath()
        guard input.path != output.path else {
            throw VideoExportError.sameFile
        }
        let outputProperties = try? output.resourceValues(forKeys: [.fileResourceIdentifierKey])
        if let inputIdentifier = inputProperties.fileResourceIdentifier,
           let outputIdentifier = outputProperties?.fileResourceIdentifier,
           inputIdentifier.isEqual(outputIdentifier) {
            // On a case-insensitive volume, two different URL paths may overwrite the same original.
            throw VideoExportError.sameFile
        }
        return input
    }

    private static func copyFile(_ source: URL, to output: URL, maximumBytes: Int64? = nil) throws {
        try Task.checkCancellation()
        try Data().write(to: output, options: .withoutOverwriting)
        let reader = try FileHandle(forReadingFrom: source)
        defer { try? reader.close() }
        let writer = try FileHandle(forWritingTo: output)
        defer { try? writer.close() }

        var bytes: Int64 = 0
        while let chunk = try reader.read(upToCount: 1024 * 1024), !chunk.isEmpty {
            try Task.checkCancellation()
            bytes += Int64(chunk.count)
            if let maximumBytes, bytes > maximumBytes { throw VideoExportError.cannotFit(maximumBytes) }
            try writer.write(contentsOf: chunk)
        }
        guard bytes > 0 else { throw VideoExportError.incompleteOutput }
        try writer.close()
    }

    private static func fileSize(_ url: URL) throws -> Int64 {
        guard let bytes = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize else {
            throw VideoExportError.incompleteOutput
        }
        return Int64(bytes)
    }

    private static func isStorageError(_ error: Error) -> Bool {
        let error = error as NSError
        if error.domain == NSPOSIXErrorDomain && [ENOSPC, EACCES, EPERM, EROFS].contains(Int32(clamping: error.code)) {
            return true
        }
        if error.domain == NSCocoaErrorDomain && [CocoaError.fileWriteOutOfSpace.rawValue,
                                                  CocoaError.fileWriteNoPermission.rawValue,
                                                  CocoaError.fileWriteVolumeReadOnly.rawValue].contains(error.code) {
            return true
        }
        if let underlying = error.userInfo[NSUnderlyingErrorKey] as? NSError, underlying !== error {
            return isStorageError(underlying)
        }
        return false
    }

    private struct StagingFile {
        let output: URL
        private let destination: URL
        private let directory: URL

        init(for destination: URL) throws {
            self.destination = destination
            directory = destination.deletingLastPathComponent().appendingPathComponent(
                ".snipstudio-video-\(UUID().uuidString)", isDirectory: true
            )
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                                    attributes: [.posixPermissions: 0o700])
            output = directory.appendingPathComponent(destination.lastPathComponent, isDirectory: false)
        }

        func publish() throws {
            try Task.checkCancellation()
            try output.withUnsafeFileSystemRepresentation { source in
                try destination.withUnsafeFileSystemRepresentation { target in
                    guard let source, let target else { throw VideoExportError.invalidDestination }
                    guard Darwin.rename(source, target) == 0 else {
                        throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
                    }
                }
            }
        }

        func cleanUp() {
            try? FileManager.default.removeItem(at: directory)
        }
    }

    private final class SonomaExportLifetime: @unchecked Sendable {
        private let lock = NSLock()
        private let session: AVAssetExportSession
        private var isCanceled = false
        private var started = false

        init(session: AVAssetExportSession) {
            self.session = session
        }

        func start(_ completion: @escaping @Sendable () -> Void) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            guard !isCanceled else { return false }
            session.exportAsynchronously(completionHandler: completion)
            started = true
            return true
        }

        func cancel() {
            lock.lock()
            isCanceled = true
            let shouldCancel = started
            lock.unlock()
            if shouldCancel { session.cancelExport() }
        }
    }

    private enum VideoExportError: LocalizedError {
        case invalidDestination, unsupportedVideo, sameFile, copyExtensionChanged, invalidTrimRange
        case noVideoInTrimRange, invalidSizeLimit, encoderFailed, incompleteOutput, cannotFit(Int64)

        var errorDescription: String? {
            switch self {
            case .invalidDestination: "Choose a local file ending in .mp4 for a converted video."
            case .unsupportedVideo: "This video could not be read or converted to MP4 on this Mac."
            case .sameFile: "Choose a different filename or folder so the original video is preserved."
            case .copyExtensionChanged: "Use the original file extension when saving an exact copy."
            case .invalidTrimRange: "Set a start and end within the video, with the end after the start."
            case .noVideoInTrimRange: "The selected range contains no video. Move the trim handles and try again."
            case .invalidSizeLimit: "Choose a file-size limit greater than zero."
            case .encoderFailed: "The Mac video encoder could not finish this export."
            case .incompleteOutput: "The exported file did not retain the expected video, audio, and duration."
            case .cannotFit(let limit):
                "A complete playable video could not be saved under \(ByteCountFormatter.string(fromByteCount: limit, countStyle: .file)). Choose a shorter clip or raise the limit."
            }
        }
    }
}
