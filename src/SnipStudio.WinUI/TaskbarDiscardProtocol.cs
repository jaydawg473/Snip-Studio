namespace SnipStudio.WinUI;

internal enum TaskbarDiscardStatus : byte
{
    Disconnected,
    Ready = 1,
    Blocked = 2,
    NoEditors = 3
}

internal static class TaskbarDiscardProtocol
{
    private const byte Recheck = 1;
    private const byte Discard = 2;

    internal static async Task ReceiveAsync(Stream connection, Func<Task<TaskbarDiscardStatus>> prepare,
        Func<Task<bool>> recheck, Func<Task<bool>> discard, CancellationToken cancellation)
    {
        TaskbarDiscardStatus status = await prepare().ConfigureAwait(false);
        await WriteAsync(connection, (byte)status, cancellation).ConfigureAwait(false);
        if (status != TaskbarDiscardStatus.Ready ||
            await ReadAsync(connection, cancellation).ConfigureAwait(false) != Recheck)
            return;

        bool unchanged = await recheck().ConfigureAwait(false);
        await WriteAsync(connection, (byte)(unchanged ? TaskbarDiscardStatus.Ready : TaskbarDiscardStatus.Blocked),
            cancellation).ConfigureAwait(false);
        if (unchanged && await ReadAsync(connection, cancellation).ConfigureAwait(false) == Discard)
        {
            cancellation.ThrowIfCancellationRequested();
            bool closed = await discard().ConfigureAwait(false);
            await WriteAsync(connection, (byte)(closed ? TaskbarDiscardStatus.Ready : TaskbarDiscardStatus.Blocked),
                cancellation).ConfigureAwait(false);
        }
    }

    internal static async Task<TaskbarDiscardStatus> ReadStatusAsync(Stream connection, CancellationToken cancellation) =>
        (await ReadAsync(connection, cancellation).ConfigureAwait(false)) switch
        {
            -1 => TaskbarDiscardStatus.Disconnected,
            (byte)TaskbarDiscardStatus.Ready => TaskbarDiscardStatus.Ready,
            (byte)TaskbarDiscardStatus.NoEditors => TaskbarDiscardStatus.NoEditors,
            _ => TaskbarDiscardStatus.Blocked
        };

    internal static Task RequestRecheckAsync(Stream connection, CancellationToken cancellation) =>
        WriteAsync(connection, Recheck, cancellation);

    internal static Task ConfirmDiscardAsync(Stream connection, CancellationToken cancellation) =>
        WriteAsync(connection, Discard, cancellation);

    private static async Task<int> ReadAsync(Stream connection, CancellationToken cancellation)
    {
        byte[] value = new byte[1];
        return await connection.ReadAsync(value.AsMemory(), cancellation).ConfigureAwait(false) == 1 ? value[0] : -1;
    }

    private static Task WriteAsync(Stream connection, byte value, CancellationToken cancellation) =>
        connection.WriteAsync(new byte[] { value }.AsMemory(), cancellation).AsTask();
}
