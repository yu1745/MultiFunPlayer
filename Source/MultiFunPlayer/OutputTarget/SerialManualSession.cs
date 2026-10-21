using MultiFunPlayer.Common;


namespace MultiFunPlayer.OutputTarget;

internal sealed class SerialManualSession
{
    private readonly object gate = new();
    private readonly Dictionary<DeviceAxis, (double Value, int Duration)> pending = [];
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public Task<Exception> Ended => ended.Task;
    public void Set(DeviceAxis axis, double value, int duration)
    {
        if (!double.IsFinite(value)) return;
        lock (gate) pending[axis] = (Math.Clamp(value, 0, 1), Math.Clamp(duration, 100, 10000));
    }
    public void Fail(Exception error)
    {
        ready.TrySetException(error);
        ended.TrySetResult(error);
    }
    public void Run(Action<string> write, Action read, CancellationToken token)
    {
        ready.TrySetResult();
        while (!token.IsCancellationRequested)
        {
            KeyValuePair<DeviceAxis, (double Value, int Duration)>[] commands;
            lock (gate) { commands = pending.ToArray(); pending.Clear(); }
            if (commands.Length > 0)
                write(string.Join(" ", commands.Select(c => DeviceAxis.ToString(c.Key, c.Value.Value, c.Value.Duration))) + "\n");
            read();
            if (token.WaitHandle.WaitOne(40)) break;
        }
    }
}
