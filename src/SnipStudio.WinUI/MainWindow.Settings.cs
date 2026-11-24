using Microsoft.UI.Xaml;
using SnipStudio.WinUI.Backend;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private static DesktopSettingsView? settingsWindow;

    private void OnSettingsClicked(object sender, RoutedEventArgs args) => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        if (closed || applicationExitPending) return;
        try
        {
            if (settingsWindow is { } existing)
            {
                existing.Show();
                return;
            }

            var store = new DesktopPreferencesStore();
            DesktopPreferences baseline = store.Load();
            DesktopPreferences initial = baseline.Clone();
            if (!baseline.RememberCaptureDelay) initial.CaptureDelaySeconds = sessionCaptureDelaySeconds;
            var settings = new DesktopSettingsView(AppWindow.Id, initial, draft =>
                SaveSettings(store, baseline, initial.CaptureDelaySeconds, draft));
            settings.Closed += (_, _) =>
            {
                if (ReferenceEquals(settingsWindow, settings)) settingsWindow = null;
            };
            settingsWindow = settings;
            settings.Show();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            ReportUnexpectedFailure(failure);
        }
    }

    private static void SaveSettings(DesktopPreferencesStore store, DesktopPreferences baseline, int initialDelay, DesktopPreferences draft)
    {
        int selectedDelay = draft.CaptureDelaySeconds;
        if (!draft.RememberCaptureDelay) draft.CaptureDelaySeconds = 0;
        DesktopPreferences active = desktopOwner?.preferences ?? new DesktopPreferencesStore().Load();
        DesktopPreferences next = active.Clone();
        if (draft.HotkeysEnabled != baseline.HotkeysEnabled) next.HotkeysEnabled = draft.HotkeysEnabled;
        if (draft.UsePrintScreen != baseline.UsePrintScreen) next.UsePrintScreen = draft.UsePrintScreen;
        if (draft.KeepRunningInTray != baseline.KeepRunningInTray) next.KeepRunningInTray = draft.KeepRunningInTray;
        if (draft.KeepShortcutsAfterClose != baseline.KeepShortcutsAfterClose) next.KeepShortcutsAfterClose = draft.KeepShortcutsAfterClose;
        if (draft.StartupWithWindows != baseline.StartupWithWindows) next.StartupWithWindows = draft.StartupWithWindows;
        bool shortcutsChanged = next.HotkeysEnabled != active.HotkeysEnabled ||
            next.UsePrintScreen != active.UsePrintScreen || next.KeepRunningInTray != active.KeepRunningInTray ||
            next.KeepShortcutsAfterClose != active.KeepShortcutsAfterClose;
        bool startupChanged = next.StartupWithWindows != active.StartupWithWindows;
        DesktopIntegrationService? integration = desktopOwner?.desktopIntegration;
        if (shortcutsChanged && desktopOwner is not null && integration is null)
            throw new InvalidOperationException("Windows shortcuts are unavailable. Reopen Snip Studio and try again.");
        // A separately open Settings window can survive the last editor. The
        // next editor applies saved shortcut preferences. The startup entry,
        // which lives outside the process, still needs to change on Save.
        using var standaloneIntegration = startupChanged && integration is null && settingsWindow is { } settings
            ? new DesktopIntegrationService(settings.WindowHandle) : null;
        DesktopIntegrationService? startupIntegration = integration ?? standaloneIntegration;
        DesktopPreferences saved;
        try
        {
            if (shortcutsChanged && integration is not null) ApplyDesktopIntegrationPreferences(integration, next);
            if (startupChanged) startupIntegration!.SetStartupEnabled(next.StartupWithWindows);
            saved = store.SaveAndRead(draft);
            if (selectedDelay != initialDelay || draft.RememberCaptureDelay != baseline.RememberCaptureDelay)
                sessionCaptureDelaySeconds = selectedDelay;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            var rollbackFailures = new List<string>();
            if (shortcutsChanged && integration is not null)
                try { ApplyDesktopIntegrationPreferences(integration, active); } catch (Exception rollback) { rollbackFailures.Add(rollback.Message); }
            if (startupChanged && startupIntegration is not null)
                try { startupIntegration.SetStartupEnabled(active.StartupWithWindows); } catch (Exception rollback) { rollbackFailures.Add(rollback.Message); }
            string detail = rollbackFailures.Count == 0 ? "" : " Review Windows integration settings. " + string.Join(" ", rollbackFailures);
            throw new InvalidOperationException("Settings were not saved. " + failure.Message + detail, failure);
        }

        bool captureShapeChanged = draft.LastScreenshotCaptureKind != baseline.LastScreenshotCaptureKind;
        foreach (MainWindow editor in editorWindows.Where(window => !window.closed).ToArray())
        {
            bool initializeInk = editor.preferences.InkArgb != saved.InkArgb ||
                editor.preferences.HighlighterArgb != saved.HighlighterArgb || editor.preferences.StrokeWidth != saved.StrokeWidth;
            editor.preferences = saved.Clone();
            if (captureShapeChanged) editor.selectedCaptureKind = saved.LastScreenshotCaptureKind;
            try
            {
                editor.ApplyVisualPreferences(initializeInk);
                editor.SetStatus(desktopOwner?.desktopIntegration?.GetPrintScreenSetupHint() ?? "Settings saved.");
            }
            catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
            {
                editor.SetStatus("Settings were saved. Reopen the editor to apply them. " + failure.Message);
            }
        }
        if (desktopOwner is { } owner) _ = owner.CheckForAutomaticUpdateAsync();
    }

    private bool RememberScreenshotCaptureKind(CaptureKind kind)
    {
        if (!Enum.IsDefined(kind)) return false;
        selectedCaptureKind = kind;
        try
        {
            var store = new DesktopPreferencesStore();
            DesktopPreferences saved = store.Load();
            if (saved.LastScreenshotCaptureKind != kind)
            {
                saved.LastScreenshotCaptureKind = kind;
                store.Save(saved);
            }
            preferences.LastScreenshotCaptureKind = kind;
            return true;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            SetStatus("Capture shape changed for this window. It could not be remembered. " + failure.Message);
            return false;
        }
    }
}
