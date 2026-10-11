using MultiFunPlayer.Common;
using System.IO;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

internal sealed record BufferedPlaybackStatus(string State, double ClockUncertaintyMs = 0, double BufferedSeconds = 0);

internal sealed class TCodeBufferedPlayback(TCodeBufferedClient client,
    Func<BufferedPlaybackSnapshot> snapshot, Action<bool> setPlaying,
    Func<long> controlRevision, Func<bool> wantsPlayback, Action<BufferedPlaybackStatus> report)
{
    private static readonly SemaphoreSlim MediaControlLease = new(1, 1);

    public async Task RunAsync(CancellationToken token)
    {
        if (!await MediaControlLease.WaitAsync(0, token).ConfigureAwait(false))
            throw new IOException("Only one Buffered Update output may control media playback at a time.");
        try
        {
            report(new("Synchronizing"));
            await client.SynchronizeAsync(token).ConfigureAwait(false);
            if (client.Status[5] is 1 or 2 or 3)
                throw new IOException("P4 already has an active trajectory. Stop that player before connecting Buffered Update.");
            while (!token.IsCancellationRequested)
            {
                report(new("Waiting for playback", client.UncertaintyMilliseconds));
                while (!snapshot().IsPlaying)
                    await Task.Delay(30, token).ConfigureAwait(false);
                await PlaySessionAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            report(new("Error: " + e.Message));
            throw;
        }
        finally
        {
            try
            {
                if (client.MayOwnMotion)
                {
                    setPlaying(false);
                    // Disconnect cancels the playback token, but STOP still needs
                    // a live receiver and an independent, bounded cleanup token.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await client.StopAsync(cleanup.Token).ConfigureAwait(false); }
                    catch (Exception e)
                    {
                        report(new("STOP not acknowledged"));
                        throw new IOException("P4 STOP was not acknowledged. Check the device connection before resuming.", e);
                    }
                }
            }
            finally { MediaControlLease.Release(); }
        }
    }

    private async Task PlaySessionAsync(CancellationToken token)
    {
        var controls = controlRevision();
        report(new("Pausing media for preload", client.UncertaintyMilliseconds));
        setPlaying(false);
        var deadline = TCodeBufferedClient.HostMicroseconds + 2000000;
        while (snapshot().IsPlaying)
        {
            if (TCodeBufferedClient.HostMicroseconds > deadline)
                throw new IOException("Media source did not acknowledge pause. Buffered Update requires working play/pause control.");
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        var source = snapshot();
        if (controlRevision() != controls) { RestartPreparation(); return; }
        // Validate the complete trajectory before BEGIN acquires motion.
        var nodes = TCodeBufferedPlan.Create(source, 0);
        var mask = (ulong)nodes.Select((p, i) => p.Count > 0 ? 1 << i : 0).Aggregate((a, b) => a | b);
        await client.SynchronizeAsync(token).ConfigureAwait(false);
        if (client.Status[5] is 1 or 2 or 3)
            throw new IOException("P4 already has an active trajectory.");

        ulong start;
        deadline = TCodeBufferedClient.HostMicroseconds + 5000000;
        while (true)
        {
            if (ChangedWhilePreparing()) { RestartPreparation(); return; }
            start = client.Now + 3000000;
            try { await client.BeginAsync(mask, start, token).ConfigureAwait(false); break; }
            catch (TCodeBufferedException e) when (e.Code == 8 && TCodeBufferedClient.HostMicroseconds < deadline)
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
        var epoch = start + 1000000;
        // Keep the validated relative node times; do not recompute from a later
        // media snapshot after the common start time has been chosen.
        for (var axis = 0; axis < 6; axis++)
            for (var i = 0; i < nodes[axis].Count; i++)
                nodes[axis][i] = nodes[axis][i] with { Microseconds = checked(nodes[axis][i].Microseconds + epoch) };
        var cursor = new int[6];
        var sealedAxes = new bool[6];
        report(new("Preloading", client.UncertaintyMilliseconds));
        if (!await RefillAsync(ChangedWhilePreparing).ConfigureAwait(false)) { await StopAsync(); RestartPreparation(); return; }
        if (ChangedWhilePreparing()) { await StopAsync(); RestartPreparation(); return; }
        await client.SendAsync(4, token).ConfigureAwait(false);
        report(new("Positioning", client.UncertaintyMilliseconds));
        while (client.Now < epoch)
        {
            if (ChangedWhilePreparing()) { await StopAsync(); RestartPreparation(); return; }
            await Task.Delay(10, token).ConfigureAwait(false);
        }
        if (ChangedWhilePreparing()) { await StopAsync(); RestartPreparation(); return; }
        setPlaying(true);
        deadline = TCodeBufferedClient.HostMicroseconds + 1000000;
        while (!snapshot().IsPlaying)
        {
            if (controlRevision() != controls) { await StopAsync(); RestartPreparation(); return; }
            if (TCodeBufferedClient.HostMicroseconds > deadline)
                throw new IOException("Media source did not acknowledge play; buffered playback stopped.");
            await Task.Delay(10, token).ConfigureAwait(false);
        }
        var nextRefill = 0UL;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            if (!current.IsPlaying || !source.HasSameTimeline(current) || controlRevision() != controls)
                break;
            var expected = source.Position + ((double)client.Now - epoch) / 1000000 * source.Speed;
            if (!double.IsFinite(current.Position) || Math.Abs(current.Position - expected) > .150)
                throw new IOException("Media clock diverged from P4 by over 150 ms; buffered playback stopped.");
            if (client.Now >= nextRefill)
            {
                await client.SendAsync(0, token).ConfigureAwait(false);
                if (client.Status[5] is not (2 or 3 or 4))
                    throw new IOException("P4 trajectory stopped or underrun.");
                if (client.Status[5] != 4 && !await RefillAsync(ChangedWhilePlaying).ConfigureAwait(false))
                    break;
                var tails = nodes.Select((p, i) => p.Count > 0 && !sealedAxes[i] ? client.Status[15 + i] : ulong.MaxValue).ToArray();
                var buffered = tails.Min() == ulong.MaxValue ? 0 : Math.Max(0, ((double)tails.Min() - client.Now) / 1000000);
                report(new(client.Status[5] == 4 ? "Complete (holding)" : "Playing", client.UncertaintyMilliseconds, buffered));
                nextRefill = client.Now + 200000;
            }
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        await StopAsync();

        void RestartPreparation()
        {
            if (wantsPlayback() && !token.IsCancellationRequested)
                setPlaying(true);
        }

        bool ChangedWhilePreparing()
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            return current.IsPlaying || !source.HasSameTimeline(current)
                || !double.IsFinite(current.Position) || Math.Abs(current.Position - source.Position) > .001
                || controlRevision() != controls;
        }

        bool ChangedWhilePlaying()
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            if (!current.IsPlaying || !source.HasSameTimeline(current) || controlRevision() != controls)
                return true;
            var expected = source.Position + ((double)client.Now - epoch) / 1000000 * source.Speed;
            if (!double.IsFinite(current.Position) || Math.Abs(current.Position - expected) > .150)
                throw new IOException("Media clock diverged from P4 by over 150 ms; buffered playback stopped.");
            return false;
        }

        async Task StopAsync()
        {
            // A Pause arriving between the final preparation check and our Play
            // must still win once the control revision is observed.
            if (controlRevision() != controls && !wantsPlayback())
                setPlaying(false);
            await client.StopAsync(token).ConfigureAwait(false);
            report(new("Stopped", client.UncertaintyMilliseconds));
        }

        async Task<bool> RefillAsync(Func<bool> changed)
        {
            while (true)
            {
                if (changed()) return false;
                var horizon = client.Now + 6000000;
                var free = client.Status.Skip(9).Take(6).ToArray();
                var tails = client.Status.Skip(15).Take(6).ToArray();
                var next = (int[])cursor.Clone();
                var batch = new List<BufferedNode>(5);
                while (batch.Count < 5)
                {
                    var axis = Enumerable.Range(0, 6)
                        .Where(i => next[i] < nodes[i].Count && free[i] > 0 && tails[i] < horizon)
                        .OrderBy(i => nodes[i][next[i]].Microseconds).DefaultIfEmpty(-1).First();
                    if (axis < 0) break;
                    var node = nodes[axis][next[axis]++];
                    batch.Add(node);
                    free[axis]--;
                    tails[axis] = node.Microseconds;
                }
                if (batch.Count == 0) break;
                await client.SendAsync(2, token, batch.SelectMany(p => new ulong[] { (ulong)p.Axis, p.Microseconds, p.Position }).ToArray()).ConfigureAwait(false);
                cursor = next;
            }
            ulong endMask = 0;
            for (var i = 0; i < 6; i++)
                if (nodes[i].Count > 0 && !sealedAxes[i] && cursor[i] == nodes[i].Count)
                    endMask |= 1UL << i;
            if (endMask != 0)
            {
                if (changed()) return false;
                await client.SendAsync(3, token, endMask).ConfigureAwait(false);
                for (var i = 0; i < 6; i++)
                    if ((endMask & (1UL << i)) != 0) sealedAxes[i] = true;
            }
            return true;
        }
    }
}
