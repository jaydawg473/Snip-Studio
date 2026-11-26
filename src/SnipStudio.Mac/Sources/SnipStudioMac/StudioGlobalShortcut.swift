import Carbon.HIToolbox
import Combine
import Foundation

@MainActor
final class StudioGlobalShortcut: ObservableObject {
    static let preferenceKey = "studioGlobalCaptureShortcutEnabled"
    static let chordPreferenceKey = "studioGlobalCaptureShortcutChord"

    enum Chord: String, CaseIterable, Identifiable {
        case commandShift6, commandShift7, commandShift8, commandShift9, commandOptionC, commandOptionS

        var id: String { rawValue }

        var title: String {
            switch self {
            case .commandShift6: "⌘⇧6"
            case .commandShift7: "⌘⇧7"
            case .commandShift8: "⌘⇧8"
            case .commandShift9: "⌘⇧9"
            case .commandOptionC: "⌘⌥C"
            case .commandOptionS: "⌘⌥S"
            }
        }

        var keyCode: UInt32 {
            switch self {
            case .commandShift6: UInt32(kVK_ANSI_6)
            case .commandShift7: UInt32(kVK_ANSI_7)
            case .commandShift8: UInt32(kVK_ANSI_8)
            case .commandShift9: UInt32(kVK_ANSI_9)
            case .commandOptionC: UInt32(kVK_ANSI_C)
            case .commandOptionS: UInt32(kVK_ANSI_S)
            }
        }

        var modifiers: UInt32 {
            switch self {
            case .commandOptionC, .commandOptionS: UInt32(cmdKey | optionKey)
            default: UInt32(cmdKey | shiftKey)
            }
        }
    }

    @Published private(set) var isRegistered = false
    @Published private(set) var errorMessage: String?

    private static let signature: OSType = 0x534E4950 // SNIP
    private static weak var activeOwner: StudioGlobalShortcut?
    private static var lastRegistrationID: UInt32 = 0

    private var registration: Registration?
    private var action: (@MainActor () -> Void)?

    func update(enabled: Bool, chord: Chord, action: @escaping @MainActor () -> Void) {
        if !enabled || (registration != nil && registration?.chord != chord) {
            self.action = nil
            if Self.activeOwner === self { Self.activeOwner = nil }
            registration = nil
            isRegistered = false
            errorMessage = nil
            if !enabled { return }
        }

        if registration != nil {
            self.action = action
            return
        }

        guard Self.activeOwner == nil else {
            errorMessage = conflictMessage(for: chord)
            return
        }

        let target = GetApplicationEventTarget()
        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )
        var handler: EventHandlerRef?
        let installStatus = InstallEventHandler(target, studioShortcutHandler, 1, &eventType, nil, &handler)
        guard installStatus == noErr, let handler else {
            errorMessage = unavailableMessage(for: chord)
            return
        }

        Self.lastRegistrationID &+= 1
        let key = EventHotKeyID(signature: Self.signature, id: Self.lastRegistrationID)
        var hotKey: EventHotKeyRef?
        let registerStatus = RegisterEventHotKey(
            chord.keyCode, chord.modifiers, key, target,
            UInt32(kEventHotKeyExclusive), &hotKey
        )
        guard registerStatus == noErr, let hotKey else {
            RemoveEventHandler(handler)
            errorMessage = registerStatus == eventHotKeyExistsErr ? conflictMessage(for: chord) : unavailableMessage(for: chord)
            return
        }

        registration = Registration(id: key.id, chord: chord, handles: Handles(hotKey: hotKey, handler: handler))
        self.action = action
        Self.activeOwner = self
        errorMessage = nil
        isRegistered = true
    }

    fileprivate static func dispatch(signature: OSType, identifier: UInt32) -> OSStatus {
        guard signature == Self.signature,
              let owner = activeOwner,
              owner.registration?.id == identifier else { return OSStatus(eventNotHandledErr) }

        Task { @MainActor [weak owner] in
            // A queued press may outlive disabling or re-enabling the shortcut.
            guard let owner, owner.registration?.id == identifier else { return }
            owner.action?()
        }
        return noErr
    }

    private func conflictMessage(for chord: Chord) -> String {
        "Another app is using \(chord.title). Choose a different shortcut or free it and try again."
    }

    private func unavailableMessage(for chord: Chord) -> String {
        "macOS could not enable \(chord.title). You can still start a capture from Snip Desk."
    }

    private final class Registration {
        let id: UInt32
        let chord: Chord
        let handles: Handles

        init(id: UInt32, chord: Chord, handles: Handles) {
            self.id = id
            self.chord = chord
            self.handles = handles
        }

        deinit {
            let handles = self.handles
            if Thread.isMainThread {
                handles.unregister()
            } else {
                DispatchQueue.main.async { handles.unregister() }
            }
        }
    }

    // These opaque references are released only on the main thread.
    private struct Handles: @unchecked Sendable {
        let hotKey: EventHotKeyRef
        let handler: EventHandlerRef

        func unregister() {
            UnregisterEventHotKey(hotKey)
            RemoveEventHandler(handler)
        }
    }
}

private let studioShortcutHandler: EventHandlerUPP = { _, event, _ in
    guard Thread.isMainThread, let event else { return OSStatus(eventNotHandledErr) }
    var key = EventHotKeyID()
    let status = GetEventParameter(
        event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
        MemoryLayout<EventHotKeyID>.size, nil, &key
    )
    guard status == noErr else { return OSStatus(eventNotHandledErr) }

    let signature = key.signature
    let identifier = key.id
    return MainActor.assumeIsolated {
        StudioGlobalShortcut.dispatch(signature: signature, identifier: identifier)
    }
}
