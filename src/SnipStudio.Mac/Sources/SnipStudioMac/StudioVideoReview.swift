import AppKit
import AVFoundation
import Combine
import SwiftUI
import UniformTypeIdentifiers

@MainActor
struct StudioVideoReview: View {
    let url: URL

    @StateObject private var model = StudioVideoReviewModel()
    @State private var exportTask: Task<Void, Never>?
    @State private var exportStatus = ""
    @State private var exportError: String?
    @State private var showCompression = false
    @State private var maximumSize = "20"
    @State private var compressSelection = false
    @State private var compressionRequest: VideoSaveAction?

    private var compressionByteLimit: Int64? {
        guard let size = Int64(maximumSize.trimmingCharacters(in: .whitespacesAndNewlines)),
              (1...1_000).contains(size) else { return nil }
        return size * 1_000_000
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            preview
                .padding(.horizontal, 14)
                .padding(.top, 12)
            transport
                .padding(.horizontal, 14)
            trimControls
                .padding(.horizontal, 14)
                .padding(.bottom, 10)
            footer
        }
        .frame(minWidth: 650, minHeight: 440)
        .background(StudioColors.canvas)
        .task(id: url) {
            exportStatus = ""
            await model.load(url)
        }
        .onDisappear {
            model.stop()
            exportTask?.cancel()
        }
        .sheet(isPresented: $showCompression, onDismiss: chooseCompressionDestination) {
            compressionOptions
        }
        .alert("Couldn’t finish", isPresented: Binding(
            get: { exportError != nil }, set: { if !$0 { exportError = nil } }
        )) {
            Button("OK", role: .cancel) { exportError = nil }
        } message: {
            Text(exportError ?? "")
        }
    }

    private var header: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 3) {
                Text(url.lastPathComponent)
                    .font(.headline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if let details = model.details {
                    Text(details).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 8)
            Button("Copy video file", systemImage: "doc.on.doc", action: copyVideoFile)
                .buttonStyle(StudioButtonStyle())
                .disabled(!model.isReady || exportTask != nil)
            Button("Save a copy…", systemImage: "square.and.arrow.down") { saveVideo(.original) }
                .buttonStyle(StudioButtonStyle())
                .disabled(!model.isReady || exportTask != nil)
            Button("Compress for email…", systemImage: "arrow.down.right.and.arrow.up.left") {
                compressSelection = model.hasTrim
                showCompression = true
            }
            .buttonStyle(StudioButtonStyle())
            .disabled(!model.isReady || exportTask != nil)
        }
        .padding(.horizontal, 14)
        .frame(height: 54)
        .background(StudioColors.toolbar)
    }

    @ViewBuilder
    private var preview: some View {
        if let failure = model.loadError {
            ContentUnavailableView("Can’t open this video", systemImage: "video.slash", description: Text(failure))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if model.isReady {
            StudioPlayerSurface(player: model.player)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(.black)
                .clipShape(RoundedRectangle(cornerRadius: 4))
                .accessibilityLabel("Video preview")
        } else {
            ProgressView("Opening video…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var transport: some View {
        HStack(spacing: 10) {
            Button(action: model.togglePlayback) {
                Image(systemName: model.wantsPlayback ? "pause.fill" : "play.fill")
                    .frame(width: 17, height: 20)
            }
            .buttonStyle(StudioButtonStyle(selected: model.wantsPlayback))
            .accessibilityLabel(model.wantsPlayback ? "Pause video" : "Play video")
            .help(model.wantsPlayback ? "Pause (Space)" : "Play (Space)")
            .keyboardShortcut(.space, modifiers: [])

            Slider(
                value: Binding(get: { model.currentTime }, set: { model.seek(.scrub, to: $0, precise: true) }),
                in: 0...max(0.001, model.duration)
            )
            .accessibilityLabel("Playback position")

            Text("\(StudioVideoReviewModel.timeText(model.currentTime)) / \(StudioVideoReviewModel.timeText(model.duration))")
                .monospacedDigit()
                .fixedSize()

            Button { model.toggleMute() } label: {
                Image(systemName: model.isMuted ? "speaker.slash" : "speaker.wave.2")
                    .frame(width: 17, height: 20)
            }
            .buttonStyle(StudioButtonStyle(selected: model.isMuted))
            .accessibilityLabel(model.isMuted ? "Unmute video" : "Mute video")
            .disabled(!model.hasAudio)
        }
        .frame(height: 43)
        .font(.system(size: 12))
        .disabled(!model.isReady)
    }

    private var trimControls: some View {
        VStack(alignment: .leading, spacing: 7) {
            if model.isReady {
                StudioFilmstrip(
                    frames: model.thumbnails, duration: model.duration, currentTime: model.currentTime,
                    trimStart: model.trimStart, trimEnd: model.trimEnd
                ) { interaction, seconds, precise in
                    model.seek(interaction, to: seconds, precise: precise)
                }
            }

            HStack(spacing: 8) {
                Button("Set start") { model.seek(.trimStart, to: model.currentTime, precise: true) }
                    .help("Set trim start at the playhead")
                Text(StudioVideoReviewModel.timeText(model.trimStart)).monospacedDigit()
                    .accessibilityLabel("Trim start time")
                Button("Set end") { model.seek(.trimEnd, to: model.currentTime, precise: true) }
                    .help("Set trim end at the playhead")
                Text(StudioVideoReviewModel.timeText(model.trimEnd)).monospacedDigit()
                    .accessibilityLabel("Trim end time")
                Button("Reset", action: model.resetTrim).disabled(!model.hasTrim)
                Spacer(minLength: 0)
                Button("Save trimmed copy…") { saveVideo(.trimmed) }
                    .buttonStyle(StudioButtonStyle(primary: true))
                    .disabled(!model.hasTrim || exportTask != nil)
            }
            .controlSize(.small)
            .font(.system(size: 12))
            .disabled(!model.isReady)
        }
    }

    private var compressionOptions: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Compress for email").font(.headline)
            HStack {
                TextField("Maximum size", text: $maximumSize)
                    .frame(maxWidth: 175)
                Text("MB").foregroundStyle(.secondary)
            }
            if model.hasTrim {
                Toggle("Use the selected trim range", isOn: $compressSelection)
            }
            HStack {
                Spacer()
                Button("Cancel") { showCompression = false }.keyboardShortcut(.cancelAction)
                Button("Choose destination…") {
                    guard let maximumBytes = compressionByteLimit else { return }
                    compressionRequest = .compressed(
                        maximumBytes: maximumBytes,
                        useTrim: model.hasTrim && compressSelection
                    )
                    showCompression = false
                }
                .keyboardShortcut(.defaultAction)
                .disabled(compressionByteLimit == nil)
            }
        }
        .padding(20)
        .frame(width: 380)
    }

    private func chooseCompressionDestination() {
        guard let action = compressionRequest else { return }
        compressionRequest = nil
        saveVideo(action)
    }

    private var footer: some View {
        HStack(spacing: 8) {
            if exportTask != nil {
                ProgressView().controlSize(.small).accessibilityLabel("Saving video")
            }
            Text(exportStatus.isEmpty ? model.hint : exportStatus)
                .lineLimit(1)
                .foregroundStyle(.secondary)
            Spacer(minLength: 0)
            if exportTask != nil {
                Button("Cancel") { exportTask?.cancel() }.controlSize(.small)
            }
        }
        .font(.system(size: 12))
        .padding(.horizontal, 14)
        .frame(height: 34)
        .background(StudioColors.toolbar)
    }

    private func copyVideoFile() {
        let clipboard = NSPasteboard.general
        clipboard.clearContents()
        guard clipboard.writeObjects([url as NSURL]) else {
            exportError = "The clipboard could not accept the video file."
            return
        }
        exportStatus = "Video file copied."
    }

    private func saveVideo(_ action: VideoSaveAction) {
        guard model.isReady, exportTask == nil else { return }
        model.pause()

        let panel = NSSavePanel()
        let stem = url.deletingPathExtension().lastPathComponent
        let suffix = action.outputsMP4 ? "mp4" : url.pathExtension
        if action.outputsMP4 {
            panel.allowedContentTypes = [.mpeg4Movie]
        } else if !suffix.isEmpty, let originalType = UTType(filenameExtension: suffix) {
            panel.allowedContentTypes = [originalType]
        }
        panel.nameFieldStringValue = "\(stem) \(action.filenameSuffix)" + (suffix.isEmpty ? "" : ".\(suffix)")
        guard panel.runModal() == .OK, let destination = panel.url else { return }

        let range = model.trimTimes
        exportStatus = action.status
        exportTask = Task { @MainActor in
            defer { exportTask = nil }
            do {
                switch action {
                case .original:
                    try await StudioVideoExporter.saveCopy(from: url, to: destination)
                    exportStatus = "Saved \(destination.lastPathComponent)."
                case .trimmed:
                    try await StudioVideoExporter.saveTrimmedCopy(from: url, to: destination, start: range.start, end: range.end)
                    exportStatus = "Saved \(destination.lastPathComponent)."
                case let .compressed(maximumBytes, useTrim):
                    let bytes = try await StudioVideoExporter.compress(
                        from: url, to: destination, maximumBytes: maximumBytes,
                        start: useTrim ? range.start : nil, end: useTrim ? range.end : nil
                    )
                    let size = ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
                    exportStatus = "Saved \(destination.lastPathComponent) (\(size))."
                }
            } catch is CancellationError {
                exportStatus = "Video export canceled."
            } catch {
                exportStatus = "Video was not saved."
                exportError = error.localizedDescription
            }
        }
    }
}

private enum VideoSaveAction {
    case original, trimmed, compressed(maximumBytes: Int64, useTrim: Bool)

    var outputsMP4: Bool {
        switch self {
        case .original: false
        case .trimmed, .compressed: true
        }
    }

    var filenameSuffix: String {
        switch self {
        case .original: "copy"
        case .trimmed: "trimmed"
        case .compressed: "compressed"
        }
    }

    var status: String {
        switch self {
        case .original: "Copying video…"
        case .trimmed: "Saving trimmed video…"
        case .compressed: "Compressing video…"
        }
    }
}

@MainActor
private final class StudioVideoReviewModel: ObservableObject {
    let player = AVPlayer()

    @Published private(set) var details: String?
    @Published private(set) var currentTime = 0.0
    @Published private(set) var duration = 0.0
    @Published private(set) var trimStart = 0.0
    @Published private(set) var trimEnd = 0.0
    @Published private(set) var isReady = false
    @Published private(set) var loadError: String?
    @Published private(set) var wantsPlayback = false
    @Published private(set) var isMuted = false
    @Published private(set) var hasAudio = false
    @Published private(set) var thumbnails = Array<CGImage?>(repeating: nil, count: 12)

    private var loadingID = UUID()
    private var exactDuration: CMTime = .zero
    private var seekID: UUID?
    private var thumbnailGenerator: AVAssetImageGenerator?
    private var timeObserver: Any?
    private var endObserver: NSObjectProtocol?
    private var statusObserver: NSKeyValueObservation?

    var hasTrim: Bool { isReady && (trimStart > 0.001 || duration - trimEnd > 0.001) }
    var trimTimes: (start: CMTime, end: CMTime) {
        let start = Self.time(trimStart)
        let requestedEnd = Self.time(trimEnd)
        return (start, trimEnd >= duration || CMTimeCompare(requestedEnd, exactDuration) > 0 ? exactDuration : requestedEnd)
    }
    var hint: String {
        guard isReady else { return loadError == nil ? "Opening video…" : "Open a supported video file to try again." }
        return hasTrim ? "Trim selected. Save a separate copy to keep it." : "Drag the filmstrip handles to trim."
    }

    deinit {
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
    }

    func load(_ url: URL) async {
        stop()
        let id = loadingID
        details = nil
        loadError = nil
        isReady = false
        hasAudio = false
        currentTime = 0
        exactDuration = .zero
        duration = 0
        trimStart = 0
        trimEnd = 0
        thumbnails = Array(repeating: nil, count: thumbnails.count)

        do {
            guard url.isFileURL else { throw VideoReviewError.unsupported }
            let asset = AVURLAsset(url: url, options: [AVURLAssetPreferPreciseDurationAndTimingKey: true])
            async let loadedDuration = asset.load(.duration)
            async let loadedTracks = asset.loadTracks(withMediaType: .video)
            async let playable = asset.load(.isPlayable)
            let (time, tracks, canPlay) = try await (loadedDuration, loadedTracks, playable)
            let seconds = time.seconds
            guard canPlay, time.isNumeric, seconds.isFinite, 0 < seconds, seconds < 1_000_000_000,
                  let video = tracks.first else {
                throw VideoReviewError.unsupported
            }
            let (size, transform) = try await video.load(.naturalSize, .preferredTransform)
            let audio = try await asset.loadTracks(withMediaType: .audio)
            try Task.checkCancellation()
            guard loadingID == id else { return }

            let displaySize = CGRect(origin: .zero, size: size).applying(transform).standardized.size
            let dimensions = displaySize.width.isFinite && displaySize.height.isFinite &&
                displaySize.width > 0 && displaySize.height > 0 &&
                displaySize.width < 1_000_000 && displaySize.height < 1_000_000
                ? "\(Int(displaySize.width.rounded())) × \(Int(displaySize.height.rounded())) · " : ""
            hasAudio = !audio.isEmpty
            exactDuration = time
            duration = seconds
            trimEnd = seconds
            details = "\(dimensions)\(Self.timeText(seconds)) · \(hasAudio ? "Audio" : "No audio")"

            let item = AVPlayerItem(asset: asset)
            player.replaceCurrentItem(with: item)
            observe(item)
            isReady = true
            await buildFilmstrip(asset: asset, duration: seconds, id: id)
        } catch is CancellationError {
            return
        } catch {
            guard loadingID == id, !Task.isCancelled else { return }
            loadError = error.localizedDescription
        }
    }

    func stop() {
        loadingID = UUID()
        seekID = nil
        thumbnailGenerator?.cancelAllCGImageGeneration()
        thumbnailGenerator = nil
        pause()
        if let timeObserver {
            player.removeTimeObserver(timeObserver)
            self.timeObserver = nil
        }
        if let endObserver {
            NotificationCenter.default.removeObserver(endObserver)
            self.endObserver = nil
        }
        statusObserver?.invalidate()
        statusObserver = nil
        player.replaceCurrentItem(with: nil)
    }

    func pause() {
        player.pause()
        wantsPlayback = false
    }

    func togglePlayback() {
        guard isReady else { return }
        if wantsPlayback {
            pause()
            return
        }
        wantsPlayback = true
        if currentTime < trimStart || currentTime >= trimEnd - min(0.01, (trimEnd - trimStart) / 4) {
            positionPlayer(at: trimStart, precise: true, resume: true)
        } else {
            player.play()
        }
    }

    func toggleMute() {
        player.isMuted.toggle()
        isMuted = player.isMuted
    }

    func seek(_ interaction: StudioTimelineInteraction, to requested: Double, precise: Bool) {
        guard isReady, requested.isFinite else { return }
        pause()
        let minimumLength = min(0.05, duration / 4)
        let position: Double
        switch interaction {
        case .scrub:
            position = min(duration, max(0, requested))
        case .trimStart:
            trimStart = min(trimEnd - minimumLength, max(0, requested))
            position = trimStart
        case .trimEnd:
            trimEnd = min(duration, max(trimStart + minimumLength, requested))
            position = trimEnd
        }
        player.currentItem?.forwardPlaybackEndTime = trimEnd < duration ? Self.time(trimEnd) : .invalid
        positionPlayer(at: position, precise: precise)
    }

    func resetTrim() {
        trimStart = 0
        trimEnd = duration
        player.currentItem?.forwardPlaybackEndTime = .invalid
    }

    static func time(_ seconds: Double) -> CMTime { CMTime(seconds: seconds, preferredTimescale: 60_000) }

    static func timeText(_ seconds: Double) -> String {
        let tenths = Int((max(0, seconds) * 10).rounded(.down))
        return "\(tenths / 600):\(String(format: "%02d", tenths / 10 % 60)).\(tenths % 10)"
    }

    private func positionPlayer(at seconds: Double, precise: Bool, resume: Bool = false) {
        let id = UUID()
        seekID = id
        currentTime = seconds
        let tolerance: CMTime = precise ? .zero : Self.time(0.1)
        player.seek(to: Self.time(seconds), toleranceBefore: tolerance, toleranceAfter: tolerance) { [weak self] completed in
            Task { @MainActor in
                guard let self, self.seekID == id else { return }
                self.seekID = nil
                if resume && self.wantsPlayback {
                    if completed { self.player.play() } else { self.pause() }
                }
            }
        }
    }

    private func observe(_ item: AVPlayerItem) {
        timeObserver = player.addPeriodicTimeObserver(forInterval: Self.time(0.1), queue: .main) { [weak self] time in
            MainActor.assumeIsolated {
                guard let self, self.player.currentItem === item, self.seekID == nil, time.seconds.isFinite else { return }
                self.currentTime = min(self.duration, max(0, time.seconds))
            }
        }
        endObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.player.currentItem === item else { return }
                self.pause()
                if self.seekID == nil { self.currentTime = self.trimEnd }
            }
        }
        statusObserver = item.observe(\.status, options: [.new, .initial]) { [weak self] item, _ in
            Task { @MainActor in
                guard let self, self.player.currentItem === item, item.status == .failed else { return }
                self.pause()
                self.isReady = false
                self.loadError = item.error?.localizedDescription ?? VideoReviewError.unsupported.localizedDescription
                self.thumbnailGenerator?.cancelAllCGImageGeneration()
            }
        }
    }

    private func buildFilmstrip(asset: AVURLAsset, duration: Double, id: UUID) async {
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: 200, height: 128)
        let tolerance = min(0.25, duration / (Double(thumbnails.count) * 2))
        generator.requestedTimeToleranceBefore = Self.time(tolerance)
        generator.requestedTimeToleranceAfter = Self.time(tolerance)
        thumbnailGenerator = generator

        await withTaskCancellationHandler {
            for index in thumbnails.indices {
                guard loadingID == id, !Task.isCancelled else { break }
                let at = (Double(index) + 0.5) * duration / Double(thumbnails.count)
                guard let frame = try? await generator.image(at: Self.time(at)),
                      loadingID == id, !Task.isCancelled else { continue }
                thumbnails[index] = frame.image
            }
        } onCancel: {
            generator.cancelAllCGImageGeneration()
        }
        if loadingID == id { thumbnailGenerator = nil }
    }
}

private enum VideoReviewError: LocalizedError {
    case unsupported

    var errorDescription: String? { "This file has no playable video track with a known duration." }
}

private struct StudioPlayerSurface: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> StudioPlayerHost {
        let view = StudioPlayerHost(frame: .zero)
        view.videoLayer.player = player
        return view
    }

    func updateNSView(_ view: StudioPlayerHost, context: Context) {
        if view.videoLayer.player !== player { view.videoLayer.player = player }
    }
}

private final class StudioPlayerHost: NSView {
    let videoLayer = AVPlayerLayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        videoLayer.videoGravity = .resizeAspect
        videoLayer.backgroundColor = NSColor.black.cgColor
        layer = videoLayer
        wantsLayer = true
    }

    required init?(coder: NSCoder) { nil }
}
