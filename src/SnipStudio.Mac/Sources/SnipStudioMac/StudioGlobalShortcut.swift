import Carbon.HIToolbox
import Combine
import Foundation

@MainActor
final class StudioGlobalShortcut: ObservableObject {
    static let preferenceKey = "studioGlobalCaptureShortcutEnabled"
    static let displayName = "⌘⇧6"

    @Published private(set) var isRegistered = false
    @Published private(set) var errorMessage: String?

    private static let signature: OSType = 0x534E4950 // SNIP
    private static weak var activeOwner: StudioGlobalShortcut?
    private static var lastRegistrationID: UInt32 = 0

    private var registration: Registration?
    private var action: (@MainActor () -> Void)?

    func update(enabled: Bool, action: @escaping @MainActor () -> Void) {
        if !enabled {
            self.action = nil
            if Self.activeOwner === self { Self.activeOwner = nil }
            registration = nil
            isRegistered = false
            errorMessage = nil
            return
        }

        if registration != nil {
            self.action = action
            return
        }

        guard Self.activeOwner == nil else {
            errorMessage = conflictMessage
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
            errorMessage = unavailableMessage
            return
        }

        Self.lastRegistrationID &+= 1
        let key = EventHotKeyID(signature: Self.signature, id: Self.lastRegistrationID)
        var hotKey: EventHotKeyRef?
        let registerStatus = RegisterEventHotKey(
            UInt32(kVK_ANSI_6), UInt32(cmdKey | shiftKey), key, target,
            UInt32(kEventHotKeyExclusive), &hotKey
        )
        guard registerStatus == noErr, let hotKey else {
            RemoveEventHandler(handler)
            errorMessage = registerStatus == eventHotKeyExistsErr ? conflictMessage : unavailableMessage
            return
        }

        registration = Registration(id: key.id, handles: Handles(hotKey: hotKey, handler: handler))
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

    private var conflictMessage: String {
        "Another app is using \(Self.displayName). Free that shortcut and turn it on again."
    }

    private var unavailableMessage: String {
        "macOS could not enable \(Self.displayName). You can still start a capture from Snip Studio."
    }

    private final class Registration {
        let id: UInt32
        let handles: Handles

        init(id: UInt32, handles: Handles) {
            self.id = id
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
