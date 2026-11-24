using Microsoft.UI.Dispatching;

namespace SnipStudio.WinUI;

public sealed partial class MainWindow
{
    private static bool taskbarCloseQueued;
    private sealed record TaskbarCloseSnapshot(MainWindow Editor, long Revision, TextEditDraft? Draft);

    internal static bool QueueTaskbarEditorClose(DispatcherQueue dispatcher, MainWindow? source)
    {
        if (!taskbarCloseQueued)
            taskbarCloseQueued = dispatcher.TryEnqueue(() => _ = AcceptTaskbarEditorCloseAsync(dispatcher, source));
        return taskbarCloseQueued;
    }

    private static async Task AcceptTaskbarEditorCloseAsync(DispatcherQueue dispatcher, MainWindow? source)
    {
        MainWindow? owner = null;
        TaskbarCloseSnapshot[] reviewed = [];
        bool holdingEditors = false;
        try
        {
            await TaskbarEditorCloseRequest.ReceiveAsync(
                () => OnTaskbarDispatcherAsync(dispatcher, Prepare),
                () => OnTaskbarDispatcherAsync(dispatcher, Recheck),
                () => OnTaskbarDispatcherAsync(dispatcher, Discard)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            // A cancelled, unavailable, or expired broker cannot discard work.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            dispatcher.TryEnqueue(() =>
            {
                if ((owner ?? source) is { closed: false } active) active.ReportUnexpectedFailure(exception);
            });
        }
        finally
        {
            dispatcher.TryEnqueue(() =>
            {
                taskbarCloseQueued = false;
                if (!holdingEditors) return;
                applicationExitPending = false;
                foreach (MainWindow editor in editorWindows.ToArray())
                    if (!editor.closed) editor.UpdateCommands();
            });
        }

        TaskbarDiscardStatus Prepare()
        {
            MainWindow[] editors = editorWindows.Where(editor => !editor.closed).ToArray();
            owner = source is { closed: false } ? source : editors.FirstOrDefault();
            if (owner is null) return TaskbarDiscardStatus.NoEditors;
            if (applicationExitPending || owner.ShowTaskbarCloseBlocker(editors)) return TaskbarDiscardStatus.Blocked;
            reviewed = editors.Select(editor => new TaskbarCloseSnapshot(editor, editor.session.Revision,
                editor.Viewport.CaptureTextDraft())).ToArray();
            return TaskbarDiscardStatus.Ready;
        }

        bool Recheck()
        {
            if (applicationExitPending || !ReviewStillApplies()) return false;
            applicationExitPending = holdingEditors = true;
            foreach (TaskbarCloseSnapshot item in reviewed)
                if (!item.Editor.closed) item.Editor.UpdateCommands();
            return true;
        }

        bool Discard()
        {
            if (!holdingEditors || !ReviewStillApplies()) return false;
            foreach (TaskbarCloseSnapshot item in reviewed)
            {
                MainWindow editor = item.Editor;
                if (editor.closed) continue;
                editor.ocrReviewWindow?.Close();
                editor.translationWindow?.Close();
                bool previousExit = editor.explicitExit, previousAllow = editor.allowClose;
                editor.explicitExit = true;
                editor.allowClose = true;
                try { editor.Close(); }
                finally
                {
                    if (!editor.closed)
                    {
                        editor.explicitExit = previousExit;
                        editor.allowClose = previousAllow;
                    }
                }
            }
            return reviewed.All(item => item.Editor.closed);
        }

        bool ReviewStillApplies()
        {
            MainWindow[] editors = editorWindows.Where(editor => !editor.closed).ToArray();
            MainWindow? active = owner is { closed: false } ? owner : editors.FirstOrDefault();
            if (active is null || active.ShowTaskbarCloseBlocker(editors)) return false;
            if (editors.Any(editor => !reviewed.Any(item => item.Editor == editor)) ||
                reviewed.Any(item => !item.Editor.closed && (item.Editor.session.Revision != item.Revision ||
                    item.Editor.Viewport.CaptureTextDraft() != item.Draft)))
            {
                active.SetStatus("A snip changed while the close request was open. Your windows are still open.");
                return false;
            }
            return true;
        }
    }

    private static Task<T> OnTaskbarDispatcherAsync<T>(DispatcherQueue dispatcher, Func<T> callback)
    {
        var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { completed.SetResult(callback()); }
            catch (Exception exception) { completed.SetException(exception); }
        }))
            completed.SetException(new OperationCanceledException("The editor has closed."));
        return completed.Task;
    }

    private bool ShowTaskbarCloseBlocker(IEnumerable<MainWindow> editors)
    {
        if (editors.Any(editor => !editor.closed && (editor.busy || editor.closePending || editor.closePromptOpen ||
                editor.session.IsSaving)) ||
            allRecordingWindows.Any(recording => !recording.IsClosed && !recording.CanCloseWithoutPrompt))
        {
            SetStatus("Finish or cancel the current action or recording before closing all snips.");
            return true;
        }
        if (settingsWindow is { CanCloseWithoutPrompt: false } settings)
        {
            settings.ShowPendingExit();
            SetStatus("Save or cancel Settings before using Close all without saving.");
            return true;
        }
        MainWindow? review = editors.FirstOrDefault(editor => !editor.closed && editor.HasPendingTextToolWork);
        if (review is not null)
        {
            review.ShowPendingTextToolWork();
            SetStatus("Finish or close the text review before using Close all without saving.");
            return true;
        }
        return false;
    }
}
