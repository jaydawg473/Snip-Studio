using System.IO.Pipes;
using SnipStudio.WinUI;

internal static partial class Program
{
    private static async Task CheckTaskbarDiscardProtocolAsync()
    {
        await Exercise(TaskbarDiscardStatus.Ready, unchanged: true, closeSucceeds: true, async (client, token, rechecks, discards) =>
        {
            Check(rechecks() == 0 && discards() == 0, "Preparing a close request keeps unsaved editors open");
            await TaskbarDiscardProtocol.RequestRecheckAsync(client, token);
            Check(await TaskbarDiscardProtocol.ReadStatusAsync(client, token) == TaskbarDiscardStatus.Ready &&
                rechecks() == 1 && discards() == 0, "Rechecking the snapshot cannot discard an editor");
            await TaskbarDiscardProtocol.ConfirmDiscardAsync(client, token);
            Check(await TaskbarDiscardProtocol.ReadStatusAsync(client, token) == TaskbarDiscardStatus.Ready && discards() == 1,
                "A confirmed group request discards once and returns the actual close result");
        });

        await Exercise(TaskbarDiscardStatus.Ready, unchanged: true, closeSucceeds: true, (client, _, _, _) =>
        {
            client.Dispose();
            return Task.CompletedTask;
        }, expectedDiscard: false);

        await Exercise(TaskbarDiscardStatus.Blocked, unchanged: true, closeSucceeds: true,
            (_, _, _, _) => Task.CompletedTask, expectedDiscard: false);

        await Exercise(TaskbarDiscardStatus.NoEditors, unchanged: true, closeSucceeds: true,
            (_, _, _, _) => Task.CompletedTask, expectedDiscard: false);

        await Exercise(TaskbarDiscardStatus.Ready, unchanged: true, closeSucceeds: true, async (client, token, _, _) =>
        {
            await TaskbarDiscardProtocol.ConfirmDiscardAsync(client, token);
        }, expectedDiscard: false);

        await Exercise(TaskbarDiscardStatus.Ready, unchanged: false, closeSucceeds: true, async (client, token, _, _) =>
        {
            await TaskbarDiscardProtocol.RequestRecheckAsync(client, token);
            Check(await TaskbarDiscardProtocol.ReadStatusAsync(client, token) == TaskbarDiscardStatus.Blocked,
                "A changed, busy, or unreviewed editor blocks the group before a discard");
        }, expectedDiscard: false);

        await Exercise(TaskbarDiscardStatus.Ready, unchanged: true, closeSucceeds: false, async (client, token, _, _) =>
        {
            await TaskbarDiscardProtocol.RequestRecheckAsync(client, token);
            Check(await TaskbarDiscardProtocol.ReadStatusAsync(client, token) == TaskbarDiscardStatus.Ready,
                "A target can be ready before the final guard");
            await TaskbarDiscardProtocol.ConfirmDiscardAsync(client, token);
            Check(await TaskbarDiscardProtocol.ReadStatusAsync(client, token) == TaskbarDiscardStatus.Blocked,
                "A final UI refusal is reported to the broker rather than treating transfer as success");
        });

        static async Task Exercise(TaskbarDiscardStatus preparation, bool unchanged, bool closeSucceeds,
            Func<NamedPipeClientStream, CancellationToken, Func<int>, Func<int>, Task> request, bool expectedDiscard = true)
        {
            string pipeName = "SnipStudio.DiscardChecks." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            CancellationToken token = deadline.Token;
            Task accepting = server.WaitForConnectionAsync(token);
            await client.ConnectAsync(token);
            await accepting;

            int rechecks = 0, discards = 0;
            Task listener = TaskbarDiscardProtocol.ReceiveAsync(server, () => Task.FromResult(preparation),
                () => { Interlocked.Increment(ref rechecks); return Task.FromResult(unchanged); },
                () => { Interlocked.Increment(ref discards); return Task.FromResult(closeSucceeds); }, token);
            Check(await TaskbarDiscardProtocol.ReadStatusAsync(client, token) == preparation,
                "The group sees each process's actual preparation result");
            await request(client, token, () => Volatile.Read(ref rechecks), () => Volatile.Read(ref discards));
            await listener;
            Check(discards == (expectedDiscard ? 1 : 0), "Only a confirmed, rechecked taskbar request reaches the discard operation");
        }
    }
}
