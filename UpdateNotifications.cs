using System;
using System.Reflection;
using System.Threading.Tasks;

namespace SnipStudio
{
    // Shared by every editor and the shortcut listener's notification-area menu.
    // A check never downloads, installs or activates an editor window.
    internal static class UpdateNotifications
    {
        private static readonly object gate = new object();
        private static Task<UpdateCheckResult> pending;
        private static bool pendingAutomatic;
        private static DateTime lastAttemptUtc = DateTime.MinValue;
        private static UpdateCheckResult lastResult, availableUpdate;
        internal static event Action Changed;

        internal static UpdateCheckResult LastResult { get { lock (gate) return lastResult; } }
        internal static UpdateCheckResult AvailableUpdate { get { lock (gate) return availableUpdate; } }

        internal static Task<UpdateCheckResult> CheckAsync(bool automatic)
        {
            lock (gate)
            {
                if (pending != null)
                {
                    // A manual request must still bypass the daily cache if an
                    // automatic attempt was skipped or used older cached data.
                    return !automatic && pendingAutomatic ? CheckManuallyAfterAsync(pending) : pending;
                }
                TaskCompletionSource<UpdateCheckResult> completion = new TaskCompletionSource<UpdateCheckResult>();
                pending = completion.Task;
                pendingAutomatic = automatic;
                Task.Run(delegate { return CompleteCheckAsync(automatic, completion); });
                return completion.Task;
            }
        }

        private static async Task<UpdateCheckResult> CheckManuallyAfterAsync(Task<UpdateCheckResult> previous)
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception) { }
            return await CheckAsync(false).ConfigureAwait(false);
        }

        private static async Task CompleteCheckAsync(bool automatic, TaskCompletionSource<UpdateCheckResult> completion)
        {
            UpdateCheckResult result = null;
            Exception failure = null;
            try
            {
                Preferences saved = Preferences.Load();
                DateTime previous = saved.GetLastUpdateCheckUtc();
                DateTime nowUtc = DateTime.UtcNow;
                lock (gate)
                {
                    if (lastAttemptUtc > nowUtc) lastAttemptUtc = DateTime.MinValue;
                    if (lastAttemptUtc > previous) previous = lastAttemptUtc;
                }
                if (!automatic || UpdateChecker.CheckDue(previous, nowUtc, saved.AutoCheckForUpdates))
                {
                    Version current = Assembly.GetExecutingAssembly().GetName().Version;
                    result = await UpdateChecker.CheckAsync(current, !automatic).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (result.CheckedUtc > lastAttemptUtc) lastAttemptUtc = result.CheckedUtc;
                        lastResult = result;
                        if (result.Status == UpdateCheckStatus.UpdateAvailable) availableUpdate = result;
                        else if (result.Status == UpdateCheckStatus.UpToDate) availableUpdate = null;
                        // Keep a previously verified offer visible while offline.
                    }
                    try { Preferences.RecordUpdateCheckAttempt(result.CheckedUtc); }
                    catch (Exception) { /* A metadata write must not replace settings or hide a verified result. */ }
                }
            }
            catch (Exception error) { failure = error; }
            lock (gate)
            {
                if (Object.ReferenceEquals(pending, completion.Task)) pending = null;
            }
            if (result != null) RaiseChanged();
            if (failure == null) completion.TrySetResult(result);
            else completion.TrySetException(failure);
        }

        private static void RaiseChanged()
        {
            Action handlers = Changed;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception) { /* A closed editor cannot prevent other windows receiving the result. */ }
            }
        }
    }
}
