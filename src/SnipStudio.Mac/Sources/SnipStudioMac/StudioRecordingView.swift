import AppKit
import SwiftUI

@available(macOS 15.0, *)
@MainActor
struct StudioRecordingView: View {
    let applicationDelegate: StudioApplicationDelegate

    @Environment(\.openWindow) private var openWindow
    @StateObject private var recorder = StudioScreenRecorder()
    @State private var sources: [StudioRecordingSource] = []
    @State private var selectedID: String?
    @State private var selectedArea: CGRect?
    @State private var loadsSources = false
    @State private var showsAreaPicker = false
    @State private var showsOptions = false
    @State private var systemAudio = true
    @State private var microphone = false
    @State private var microphones: [StudioMicrophone] = []
    @State private var microphoneDeviceID: String?
    @State private var showsPointer = true
    @State private var startedAt: Date?
    @State private var errorMessage: String?

    private var source: StudioRecordingSource? { sources.first { $0.id == selectedID } }
    private var canAdjustArea: Bool { source?.canAdjustArea == true }

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 8) {
                primaryAction
                if let startedAt {
                    TimelineView(.periodic(from: startedAt, by: 1)) { context in
                        let seconds = Int(max(0, context.date.timeIntervalSince(startedAt)))
                        Text(String(format: "%02d:%02d", seconds / 60, seconds % 60))
                            .monospacedDigit()
                    }
                }
                Spacer(minLength: 4)
                Button { microphone.toggle() } label: {
                    Image(systemName: microphone ? "mic" : "mic.slash")
                }
                .buttonStyle(StudioButtonStyle(selected: microphone))
                .accessibilityLabel(microphone ? "Mute microphone" : "Include microphone")
                .help(microphone ? "Mute microphone" : "Include microphone")
                .disabled(isActive)
                Button { systemAudio.toggle() } label: {
                    Image(systemName: systemAudio ? "speaker.wave.2" : "speaker.slash")
                }
                .buttonStyle(StudioButtonStyle(selected: systemAudio))
                .accessibilityLabel(systemAudio ? "Mute system audio" : "Include system audio")
                .help(systemAudio ? "Mute system audio" : "Include system audio")
                .disabled(isActive)
                Button { showsOptions.toggle() } label: { Image(systemName: "gearshape") }
                    .buttonStyle(StudioButtonStyle(selected: showsOptions))
                    .accessibilityLabel("Recording options")
                    .help("Recording options")
                    .disabled(isActive)
                    .popover(isPresented: $showsOptions) {
                        VStack(alignment: .leading, spacing: 12) {
                            Toggle("Show pointer", isOn: $showsPointer)
                            Picker("Microphone device", selection: $microphoneDeviceID) {
                                Text("System default").tag(nil as String?)
                                ForEach(microphones, id: \.id) { device in
                                    Text(device.title).tag(Optional(device.id))
                                }
                            }
                        }
                        .padding(16)
                    }
            }

            HStack(spacing: 6) {
                Picker("Record", selection: $selectedID) {
                    if sources.isEmpty { Text("No source selected").tag(nil as String?) }
                    ForEach(sources, id: \.id) { source in
                        Text(source.title).tag(Optional(source.id))
                    }
                }
                .disabled(isActive || loadsSources || sources.isEmpty)
                .accessibilityLabel("Recording source")
                Button { showsAreaPicker = true } label: { Text("Adjust area") }
                    .disabled(!canAdjustArea || isActive)
                    .help(canAdjustArea ? "Choose the area to record" : "This selection can only record its full bounds")
                Button { Task { await reloadSources() } } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .accessibilityLabel("Refresh recording sources")
                .help("Refresh recording sources")
                .disabled(isActive || loadsSources)
            }
            .controlSize(.small)

            if let source {
                let size = selectedArea?.size ?? source.size
                Text("\(Int(size.width)) × \(Int(size.height)) pt")
                    .monospacedDigit().font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .frame(minWidth: 425)
        .background(StudioColors.toolbar)
        .fixedSize(horizontal: false, vertical: true)
        .task { await reloadSources() }
        .onChange(of: selectedID) { _, _ in selectedArea = nil }
        .onChange(of: isActive) { _, value in applicationDelegate.hasActiveRecording = value }
        .onChange(of: recorder.lastError) { _, failure in
            if let failure {
                startedAt = nil
                errorMessage = failure
            }
        }
        .onDisappear {
            // Closing the controls during recording still finalizes and opens the video.
            if case .recording = recorder.state {
                finish()
            } else if case .starting = recorder.state {
                Task {
                    await recorder.cancel()
                    applicationDelegate.hasActiveRecording = false
                }
            }
        }
        .sheet(isPresented: $showsAreaPicker) {
            if let source {
                StudioRecordingAreaView(source: source, selection: $selectedArea)
            }
        }
        .alert(
            "Snip Studio Recording",
            isPresented: Binding(get: { errorMessage != nil }, set: { if !$0 { errorMessage = nil } })
        ) {
            Button("OK", role: .cancel) { errorMessage = nil }
        } message: {
            Text(errorMessage ?? "")
        }
    }

    private var isActive: Bool {
        if case .idle = recorder.state { return false }
        return true
    }

    @ViewBuilder
    private var primaryAction: some View {
        switch recorder.state {
        case .idle:
            Button { start() } label: { Label("Start", systemImage: "record.circle") }
                .buttonStyle(StudioButtonStyle(primary: true))
                .disabled(source == nil || loadsSources)
        case .starting:
            ProgressView().controlSize(.small).accessibilityLabel("Starting recording")
            Button("Cancel") { Task { await recorder.cancel() } }
        case .recording:
            Button { finish() } label: { Label("Stop", systemImage: "stop.fill") }
                .buttonStyle(StudioButtonStyle(primary: true))
        case .stopping:
            ProgressView().controlSize(.small).accessibilityLabel("Saving recording")
            Text("Saving…").font(.callout)
        }
    }

    private func reloadSources() async {
        guard !loadsSources, !isActive else { return }
        loadsSources = true
        defer { loadsSources = false }
        do {
            sources = try await StudioScreenRecorder.sources()
            microphones = StudioScreenRecorder.microphones()
            if !sources.contains(where: { $0.id == selectedID }) { selectedID = sources.first?.id }
        } catch is CancellationError {
            return
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func start() {
        guard let source, !isActive else { return }
        let area = selectedArea
        Task { @MainActor in
            do {
                try await recorder.start(source: source, area: area, systemAudio: systemAudio,
                                         microphone: microphone, showsPointer: showsPointer,
                                         microphoneDeviceID: microphoneDeviceID)
                startedAt = Date()
            } catch is CancellationError {
                return
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }

    private func finish() {
        Task { @MainActor in
            defer {
                startedAt = nil
                applicationDelegate.hasActiveRecording = false
            }
            do {
                let video = try await recorder.stop()
                openWindow(id: "video-review", value: video)
            } catch is CancellationError {
                return
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }
}

@available(macOS 15.0, *)
private struct StudioRecordingAreaView: View {
    let source: StudioRecordingSource
    @Binding var selection: CGRect?

    @Environment(\.dismiss) private var dismiss
    @State private var bounds = CGRect.null
    @State private var preview: CGImage?
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Adjust recording area").font(.headline)
            GeometryReader { geometry in
                if let preview {
                    let scale = min(geometry.size.width / source.size.width, geometry.size.height / source.size.height)
                    let size = CGSize(width: source.size.width * scale, height: source.size.height * scale)
                    let frame = CGRect(x: (geometry.size.width - size.width) / 2,
                                       y: (geometry.size.height - size.height) / 2, width: size.width, height: size.height)
                    ZStack(alignment: .topLeading) {
                        Image(decorative: preview, scale: 1).resizable()
                            .frame(width: frame.width, height: frame.height)
                            .offset(x: frame.minX, y: frame.minY)
                            .accessibilityHidden(true)
                        StudioCropOverlay(bounds: $bounds, imageFrame: frame, pixelSize: source.size)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let errorMessage {
                    Text(errorMessage).frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    ProgressView("Preparing the recording preview…").frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .background(StudioColors.canvas)
            HStack {
                Button("Entire source") { bounds = CGRect(origin: .zero, size: source.size) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Use area") {
                    selection = bounds == CGRect(origin: .zero, size: source.size) ? nil : bounds
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(preview == nil || bounds.isEmpty)
            }
        }
        .padding(16)
        .frame(width: 620, height: 440)
        .task {
            bounds = selection ?? CGRect(origin: .zero, size: source.size)
            do { preview = try await StudioScreenRecorder.preview(source: source) }
            catch is CancellationError { return }
            catch { errorMessage = error.localizedDescription }
        }
    }
}
