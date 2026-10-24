using MultiFunPlayer.Common;
using System.IO;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

internal sealed record BufferedPlaybackStatus(string State, double ClockUncertaintyMs = 0, double BufferedSeconds = 0, BufferedSessionLimits Limits = null);

internal sealed class TCodeBufferedPlayback(TCodeBufferedClient client,
    Func<BufferedPlaybackSnapshot> snapshot, Action<bool> setPlaying,
    Func<long> controlRevision, Func<bool> wantsPlayback, Action<BufferedPlaybackStatus> report,
    Action<string> diagnostic = null)
{
    private static readonly SemaphoreSlim MediaControlLease = new(1, 1);
    private bool _prepareAgain;
    private long _preparation;

    public async Task RunAsync(CancellationToken token)
    {
        if (!await MediaControlLease.WaitAsync(0, token).ConfigureAwait(false))
            throw new IOException("Only one Buffered Update output may control media playback at a time.");
        try
        {
            report(new("Synchronizing"));
            await client.SynchronizeAsync(token).ConfigureAwait(false);
            await client.EnableDeviceDiagnosticsAsync(token).ConfigureAwait(false);
            client.StartTracking();
            await client.QueryTimingAsync(token).ConfigureAwait(false);
            if (client.Status[5] is 1 or 2 or 3 or 6 or 7)
                throw new IOException("P4 already has an active trajectory. Stop that player before connecting Buffered Update.");
            while (!token.IsCancellationRequested)
            {
                report(new("Waiting for playback", client.UncertaintyMilliseconds));
                while (!snapshot().IsPlaying && !_prepareAgain)
                    await Task.Delay(30, token).ConfigureAwait(false);
                _prepareAgain = false;
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
        var preparation = ++_preparation;
        var preparationStarted = TCodeBufferedClient.HostMicroseconds;
        Trace("start", snapshot());
        report(new("Pausing media for preload", client.UncertaintyMilliseconds));
        if (snapshot().IsPlaying) setPlaying(false);
        var deadline = TCodeBufferedClient.HostMicroseconds + 15000000;
        while (snapshot().IsPlaying)
        {
            if (TCodeBufferedClient.HostMicroseconds > deadline)
                throw new IOException("Media source did not acknowledge pause. Buffered Update requires working play/pause control.");
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        var source = snapshot();
        Trace("pause acknowledged", source);
        if (controlRevision() != controls) { RestartPreparation(); return; }
        // Validate the complete trajectory before BEGIN acquires motion.
        await client.SynchronizeAsync(token).ConfigureAwait(false);
        if (client.Status[5] is 1 or 2 or 3)
            throw new IOException("P4 already has an active trajectory.");

        report(new("Reading device pose", client.UncertaintyMilliseconds));
        var pose = await client.QueryPoseAsync(token).ConfigureAwait(false);
        var supportedAxes = await client.QuerySupportedAxesAsync(token).ConfigureAwait(false);
        var ignored = source.Axes.Where(a => Array.IndexOf(TCodeBufferedPlan.AxisNames, a.Name) is var i &&
            (i < 0 || (supportedAxes & (1UL << i)) == 0)).Select(a => a.Name);
        diagnostic?.Invoke($"PREP {preparation} supportedAxes={supportedAxes} ignoredAxes=[{string.Join(',', ignored)}]");
        var plan = TCodeBufferedPlan.CreateSession(source, pose, supportedAxes);
        diagnostic?.Invoke(FormattableString.Invariant($"PREP {preparation} plan positioningMs={plan.PositioningMicroseconds / 1000d:F2} leadMs={PreloadLeadMicroseconds(plan, client.UncertaintyMilliseconds) / 1000d:F2} pose=[{string.Join(',', pose.Positions)}] first=[{string.Join(',', plan.Nodes.Select(a => a.Count > 0 ? a[0].Position.ToString() : "-"))}] counts=[{string.Join(',', plan.Nodes.Select(a => a.Count))}] limits=[{string.Join(',', plan.Limits.Encode())}]"));
        var nodes = plan.Nodes;
        var mask = (ulong)nodes.Select((p, i) => p.Count > 0 ? 1 << i : 0).Aggregate((a, b) => a | b);

        ulong start;
        deadline = TCodeBufferedClient.HostMicroseconds + 5000000;
        while (true)
        {
            if (ChangedWhilePreparing()) { RestartPreparation(); return; }
            start = client.Now + PreloadLeadMicroseconds(plan, client.UncertaintyMilliseconds);
            try { await client.BeginAsync(mask, start, plan.Limits, token).ConfigureAwait(false); break; }
            catch (TCodeBufferedException e) when (e.Code == 8 && TCodeBufferedClient.HostMicroseconds < deadline)
            { Trace("BEGIN not ready; retrying", snapshot()); await Task.Delay(100, token).ConfigureAwait(false); }
        }
        var epoch = start + plan.PositioningMicroseconds;
        // Keep the validated relative node times; do not recompute from a later
        // media snapshot after the common start time has been chosen.
        for (var axis = 0; axis < 6; axis++)
            for (var i = 0; i < nodes[axis].Count; i++)
                nodes[axis][i] = nodes[axis][i] with { Microseconds = checked(nodes[axis][i].Microseconds + start) };
        var cursor = new int[6];
        var sealedAxes = new bool[6];
        report(new("Preloading", client.UncertaintyMilliseconds, Limits: plan.Limits));
        if (!await RefillAsync(ChangedWhilePreparing, start + 1000000).ConfigureAwait(false)) { await StopAsync(); RestartPreparation(); return; }
        if (ChangedWhilePreparing()) { await StopAsync(); RestartPreparation(); return; }
        // New firmware positions and freezes its timeline at this exact node.
        // An old firmware rejects op 13 before any motion is armed.
        await client.ArmMediaAsync(epoch, token).ConfigureAwait(false);
        diagnostic?.Invoke($"PREP {preparation} armed nowUs={client.Now} startUs={start} mediaEpochUs={epoch}");
        report(new("Positioning", client.UncertaintyMilliseconds, Limits: plan.Limits));
        var nextPositioningRefill = 0UL;
        while (client.Now < epoch)
        {
            if (ChangedWhilePreparing()) { await StopAsync(); RestartPreparation(); return; }
            if (client.Now >= nextPositioningRefill)
            {
                await client.SendAsync(0, token).ConfigureAwait(false);
                if (client.Status[5] is not (2 or 3 or 4))
                    throw client.DeviceStateException("P4 positioning stopped or configuration changed.");
                if (!await RefillAsync(ChangedWhilePreparing, Math.Max(start + 1000000, client.Now + 1000000)).ConfigureAwait(false))
                { await StopAsync(); RestartPreparation(); return; }
                nextPositioningRefill = client.Now + 200000;
            }
            await Task.Delay(10, token).ConfigureAwait(false);
        }
        if (ChangedWhilePreparing()) { await StopAsync(); RestartPreparation(); return; }
        Trace("request play", snapshot());
        setPlaying(true);
        deadline = TCodeBufferedClient.HostMicroseconds + 15000000;
        var nextMediaQuery = 0UL;
        while (!snapshot().IsPlaying)
        {
            if (controlRevision() != controls || !source.HasSameTimeline(snapshot()))
            { await StopAsync(); RestartPreparation(); return; }
            if (TCodeBufferedClient.HostMicroseconds > deadline)
                throw new IOException("Media source did not acknowledge play; buffered playback stopped.");
            if (client.Now >= nextMediaQuery)
            {
                await client.SendAsync(0, token).ConfigureAwait(false);
                if (client.Status[5] is not (2 or 3))
                    throw client.DeviceStateException("P4 media-start hold failed.");
                nextMediaQuery = client.Now + 200000;
            }
            await Task.Delay(10, token).ConfigureAwait(false);
        }
        if (controlRevision() != controls || !source.HasSameTimeline(snapshot()))
        { await StopAsync(); RestartPreparation(); return; }
        var released = await client.ReleaseMediaAsync(token).ConfigureAwait(false);
        var releaseShift = released[8];
        epoch = checked(epoch + releaseShift);
        for (var axis = 0; axis < 6; axis++)
            for (var i = 0; i < nodes[axis].Count; i++)
                nodes[axis][i] = nodes[axis][i] with { Microseconds = checked(nodes[axis][i].Microseconds + releaseShift) };
        diagnostic?.Invoke($"MEDIA released shiftUs={releaseShift} mediaEpochUs={epoch}");
        var nextRefill = 0UL;
        var nextClockSample = 0UL;
        Trace("play acknowledged", snapshot());
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            if (!source.HasSameTimeline(current))
            {
                Trace($"playback interrupted sameTimeline={source.HasSameTimeline(current)} controlsAtStart={controls}", current);
                break;
            }
            if (!current.IsPlaying || (controlRevision() != controls && !wantsPlayback()))
            {
                if (!await PauseAndResumeAsync(current).ConfigureAwait(false)) break;
                nextRefill = 0;
                continue;
            }
            controls = controlRevision();
            CheckMediaClock(current, "playback");
            if (client.Now >= nextRefill)
            {
                await client.SendAsync(0, token).ConfigureAwait(false);
                if (client.Status[5] is not (2 or 3 or 4))
                    throw client.DeviceStateException("P4 trajectory stopped.");
                if (client.Status[5] != 4 && !await RefillAsync(ChangedWhilePlaying).ConfigureAwait(false))
                    continue; // The main loop distinguishes pause from timeline replacement.
                var tails = nodes.Select((p, i) => p.Count > 0 && !sealedAxes[i] ? client.Status[15 + i] : ulong.MaxValue).ToArray();
                var buffered = tails.Min() == ulong.MaxValue ? 0 : Math.Max(0, ((double)tails.Min() - client.Now) / 1000000);
                report(new(client.Status[5] == 4 ? "Complete (holding)" : "Playing", client.UncertaintyMilliseconds, buffered, plan.Limits));
                nextRefill = client.Now + 200000;
            }
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        await StopAsync();

        async Task<bool> PauseAndResumeAsync(BufferedPlaybackSnapshot paused)
        {
            // Ordinary pause retains the session, accepted envelope, queue and
            // upload cursors. Seek/script/speed changes still invalidate it.
            if (paused.IsPlaying)
            {
                setPlaying(false);
                var until = TCodeBufferedClient.HostMicroseconds + 2000000;
                while (snapshot().IsPlaying)
                {
                    if (TCodeBufferedClient.HostMicroseconds > until)
                        throw new IOException("Media source did not acknowledge pause.");
                    await Task.Delay(10, token).ConfigureAwait(false);
                }
                paused = snapshot();
            }
            var cursorUs = checked((ulong)Math.Max(0, epoch + (paused.Position - source.Position) / source.Speed * 1000000));
            Trace($"retaining queue at media cursor {cursorUs}", paused);
            await client.PauseAsync(cursorUs, token).ConfigureAwait(false);
            var pendingPlay = false;
            var pendingControls = controlRevision();
            var nextQuery = 0UL;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var current = snapshot();
                if (!source.HasSameTimeline(current) ||
                    (!current.IsPlaying && Math.Abs(current.Position - paused.Position) > .150))
                { Trace("paused session invalidated", current); return false; }
                if (pendingPlay && controlRevision() != pendingControls) pendingPlay = false;
                if (client.Now >= nextQuery)
                {
                    await client.RefreshPausedStatusAsync(token).ConfigureAwait(false);
                    if (client.Status[5] is not (6 or 7))
                        throw client.DeviceStateException("P4 paused trajectory failed before resume.");
                    report(new(client.Status[5] == 7 ? "Paused (queue retained)" : "Pausing (settling)", client.UncertaintyMilliseconds));
                    nextQuery = client.Now + 100000;
                }
                if (current.IsPlaying || pendingPlay)
                {
                    if (client.Status[5] != 7)
                    {
                        if (current.IsPlaying)
                        {
                            pendingPlay = true; pendingControls = controlRevision();
                            setPlaying(false);
                            Trace("resume waiting for braking to settle", current);
                        }
                    }
                    else
                    {
                        var requested = TCodeBufferedClient.HostMicroseconds;
                        var resumeControls = controlRevision();
                        var lead = (ulong)Math.Max(50000, client.UncertaintyMilliseconds * 2000 + 20000);
                        var timing = await client.ResumeAsync(client.Now + lead, token).ConfigureAwait(false);
                        var shift = timing[8];
                        epoch = checked(epoch + shift);
                        for (var axis = 0; axis < 6; axis++)
                            for (var i = 0; i < nodes[axis].Count; i++)
                                nodes[axis][i] = nodes[axis][i] with { Microseconds = checked(nodes[axis][i].Microseconds + shift) };
                        diagnostic?.Invoke($"RESUME retained session={client.Session} shiftUs={shift} startUs={timing[9]} mediaUs={timing[10]} physicalLeadUs={timing[10] - timing[9]}");
                        // Most resumes need no media intervention. A very fast
                        // interrupted segment may need extra time to restart
                        // within its original envelope; only then hold media.
                        var gated = pendingPlay || timing[10] > client.Now + 100000;
                        if (gated)
                        {
                            if (snapshot().IsPlaying) setPlaying(false);
                            while (client.Now < timing[10])
                            {
                                if (controlRevision() != resumeControls || !source.HasSameTimeline(snapshot()))
                                    return false;
                                await Task.Delay(5, token).ConfigureAwait(false);
                            }
                            if (controlRevision() == resumeControls) setPlaying(true);
                        }
                        controls = resumeControls;
                        await client.SendAsync(0, token).ConfigureAwait(false);
                        Trace(FormattableString.Invariant($"retained resume completed in {(TCodeBufferedClient.HostMicroseconds - requested) / 1000:F2} ms gated={gated}"), snapshot());
                        report(new("Resumed (queue retained)", client.UncertaintyMilliseconds));
                        return true;
                    }
                }
                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }

        void RestartPreparation()
        {
            // Rebuild while paused. Briefly playing merely to re-enter the outer
            // loop advances media again and can cause an endless prepare cycle.
            _prepareAgain = wantsPlayback() && !token.IsCancellationRequested;
            Trace($"restart preparation requested={_prepareAgain}", snapshot());
        }

        bool ChangedWhilePreparing()
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            var changed = current.IsPlaying || !source.HasSameTimeline(current)
                || !double.IsFinite(current.Position) || Math.Abs(current.Position - source.Position) > .001
                || controlRevision() != controls;
            if (changed)
                Trace(FormattableString.Invariant($"preparation invalidated sameTimeline={source.HasSameTimeline(current)} sourcePosition={source.Position:F6} deltaMs={(current.Position - source.Position) * 1000:F3} controlsAtStart={controls}"), current);
            return changed;
        }

        bool ChangedWhilePlaying()
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            if (!current.IsPlaying || !source.HasSameTimeline(current) || controlRevision() != controls)
                return true;
            CheckMediaClock(current, "refill");
            return false;
        }

        void CheckMediaClock(BufferedPlaybackSnapshot current, string phase)
        {
            var now = client.Now;
            var expected = source.Position + ((double)now - epoch) / 1000000 * source.Speed;
            var drift = current.Position - expected;
            var failed = !double.IsFinite(current.Position) || Math.Abs(drift) > .150;
            if (failed || now >= nextClockSample)
            {
                var direction = !double.IsFinite(drift) ? "invalid" : drift >= 0 ? "media-ahead" : "media-behind";
                diagnostic?.Invoke(FormattableString.Invariant($"CLOCK {(failed ? "ERROR" : "SAMPLE")} prep={preparation} phase={phase} actualSeconds={current.Position:F6} expectedSeconds={expected:F6} driftMs={drift * 1000:+0.000;-0.000;0.000} direction={direction} thresholdMs=150 sourceSeconds={source.Position:F6} speed={source.Speed:F6} currentSpeed={current.Speed:F6} playing={current.IsPlaying} deviceNowUs={now} mediaEpochUs={epoch} elapsedMs={(TCodeBufferedClient.HostMicroseconds - preparationStarted) / 1000d:F3} uncertaintyMs={client.UncertaintyMilliseconds:F3} boot={client.Boot} session={client.Session} timelineRevision={current.Revision} controlRevision={controlRevision()} status=[{string.Join(',', client.Status)}]"));
                nextClockSample = now + 1000000;
            }
            if (failed)
                throw new IOException(FormattableString.Invariant($"Media clock diverged from P4 by over 150 ms; buffered playback stopped. Actual={current.Position:F6}s expected={expected:F6}s drift={drift * 1000:+0.000;-0.000;0.000}ms phase={phase}."));
        }

        async Task StopAsync()
        {
            Trace("request STOP", snapshot());
            // A Pause arriving between the final preparation check and our Play
            // must still win once the control revision is observed.
            if (controlRevision() != controls && !wantsPlayback())
                setPlaying(false);
            await client.StopAsync(token).ConfigureAwait(false);
            report(new("Stopped", client.UncertaintyMilliseconds));
            Trace("STOP acknowledged", snapshot());
        }

        void Trace(string reason, BufferedPlaybackSnapshot current)
            => diagnostic?.Invoke(FormattableString.Invariant($"PREP {preparation} {reason} elapsedMs={(TCodeBufferedClient.HostMicroseconds - preparationStarted) / 1000:F2} playing={current.IsPlaying} position={current.Position:F6} speed={current.Speed:F3} timelineRevision={current.Revision} controlRevision={controlRevision()} wantsPlayback={wantsPlayback()} deviceState={client.Status?[5]} session={client.Session}"));

        async Task<bool> RefillAsync(Func<bool> changed, ulong? preloadHorizon = null)
        {
            while (true)
            {
                if (changed()) return false;
                var horizon = preloadHorizon ?? client.Now + 6000000;
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

    internal static ulong PreloadLeadMicroseconds(BufferedSessionPlan plan, double uncertaintyMs)
    {
        // ARM needs one second on each unsealed axis. Budget only that initial
        // prefix; fill the longer rolling buffer after ARM. Include the first
        // point beyond the horizon, as RefillAsync does.
        var points = plan.Nodes.Sum(axis => axis.Count == 0 ? 0 :
            Math.Min(axis.Count, axis.TakeWhile(n => n.Microseconds < 1000000).Count() + 1));
        var transactions = (points + 4) / 5 + 3; // BEGIN, optional SEAL, ARM
        var milliseconds = Math.Max(750, 200 + transactions * Math.Max(25, uncertaintyMs * 2 + 5) * 2);
        if (milliseconds > 9000)
            throw new IOException("Initial trajectory is too dense for the measured connection latency.");
        return (ulong)Math.Ceiling(milliseconds * 1000);
    }
}
