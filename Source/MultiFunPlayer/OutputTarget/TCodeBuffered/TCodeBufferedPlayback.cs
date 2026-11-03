using MultiFunPlayer.Common;
using System.IO;
using MultiFunPlayer.OutputTarget.Tracking;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// Only locally validated trajectory failures and decoded device stops are recoverable.
// Transport/protocol errors still escape: never assume STOP succeeded.
internal sealed class PlaybackHoldException(string message) : IOException(message);

internal sealed record BufferedPlaybackStatus(string State, double ClockUncertaintyMs = 0, double BufferedSeconds = 0, BufferedSessionLimits Limits = null);

internal sealed class TCodeBufferedPlayback(TCodeBufferedClient client,
    Func<BufferedPlaybackSnapshot> snapshot, Action<bool> setPlaying,
    Func<long> controlRevision, Func<bool> wantsPlayback, Action<BufferedPlaybackStatus> report,
    Action<string> diagnostic = null,
    Func<BufferedPlaybackSnapshot,BufferedDevicePose,ulong,BufferedSessionPlan> planFactory = null)
{
    private static readonly SemaphoreSlim MediaControlLease = new(1, 1);
    private bool _prepareAgain;
    private long _preparation;
    private Task _warmPlan=Task.CompletedTask;
    private BufferedPlaybackSnapshot _warmed;

    public async Task RunAsync(CancellationToken token)
    {
        if (!await MediaControlLease.WaitAsync(0, token).ConfigureAwait(false))
            throw new IOException("Only one Buffered Update output may control media playback at a time.");
        Interlocked.Increment(ref FilterRuntime.BufferedConnections);
        try
        {
            report(new("Synchronizing"));
            await client.SynchronizeAsync(token).ConfigureAwait(false);
            await client.EnableDeviceDiagnosticsAsync(token).ConfigureAwait(false);
            client.StartTracking();
            client.StartHighFrequency(() => !snapshot().IsPlaying);
            await client.QueryTimingAsync(token).ConfigureAwait(false);
            if (client.Status[5] is 1 or 2 or 3 or 6 or 7)
                throw new IOException("P4 already has an active trajectory. Stop that player before connecting Buffered Update.");
            while (!token.IsCancellationRequested)
            {
                report(new("Waiting for playback", client.UncertaintyMilliseconds));
                while ((!snapshot().IsPlaying && !_prepareAgain) || snapshot().Axes.Count == 0 || !double.IsFinite(snapshot().Position)) {
                    var idle=snapshot();
                    if(!idle.IsPlaying && idle.Axes.Count>0 && _warmPlan.IsCompleted &&
                        (_warmed==null || !_warmed.HasSameTimeline(idle) || _warmed.FilterRevision!=idle.FilterRevision)) {
                        _warmed=idle;
                        _warmPlan=Task.Run(()=> {
                            var began=TCodeBufferedClient.HostMicroseconds;
                            try {
                                _=TCodeBufferedPlan.Create(idle,0);
                                if(idle.Axes.Any(a=>a.OriginalKeyframes!=null&&!ReferenceEquals(a.Keyframes,a.OriginalKeyframes)))
                                    _=TCodeBufferedPlan.Create(idle with{Axes=idle.Axes.Select(a=>a with{Keyframes=a.OriginalKeyframes??a.Keyframes}).ToArray()},0);
                                diagnostic?.Invoke($"PRECOMPUTE ready elapsedMs={(TCodeBufferedClient.HostMicroseconds-began)/1000:F2}");
                            } catch(Exception e) {diagnostic?.Invoke("PRECOMPUTE deferred: "+e.Message);}
                        },token);
                    }
                    await Task.Delay(30, token).ConfigureAwait(false);
                }
                _prepareAgain = false;
                try { await PlaySessionAsync(token).ConfigureAwait(false); }
                catch (Exception e) when (e is MediaSynchronizationException or PlaybackHoldException)
                {
                    // A media-clock transition is not a broken serial transport.
                    setPlaying(false);
                    if (client.MayOwnMotion) await client.StopAsync(token).ConfigureAwait(false);
                    report(new("Paused — " + e.Message));
                    diagnostic?.Invoke("PLAYBACK HOLD transport retained; " + e.Message);
                    _prepareAgain = false;
                    // Do not retry motion while a delayed play/pause ACK is in flight.
                    while (snapshot().IsPlaying) await Task.Delay(30, token).ConfigureAwait(false);
                }
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
            finally { MediaControlLease.Release(); Interlocked.Decrement(ref FilterRuntime.BufferedConnections); FilterUsageLog.Write("output_disconnected",new{}); if(FilterRuntime.Enabled) FilterRuntime.Status("滤波已启用 · 输出未连接"); }
        }
    }

    private BufferedSessionPlan ValidatedPlan(BufferedPlaybackSnapshot source, BufferedDevicePose pose, ulong axes)
    {
        try { return planFactory?.Invoke(source,pose,axes) ?? TCodeBufferedPlan.CreateSession(source, pose, axes); }
        catch (InvalidDataException e)
        {
            throw new PlaybackHoldException("轨迹未通过校验，已暂停，串口保持连接；请更换片段或调整设置。" + e.Message);
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
        var deadline = TCodeBufferedClient.HostMicroseconds + 2000000;
        while (snapshot().IsPlaying)
        {
            if (TCodeBufferedClient.HostMicroseconds > deadline)
                throw new MediaSynchronizationException("Media source did not acknowledge pause. Device remains stopped.");
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
        // Validate locally before requesting playback; no BEGIN/ARM during this gate.
        report(new("正在预计算联合曲线（首次载入可能需要数分钟）", client.UncertaintyMilliseconds));
        var planningStarted=TCodeBufferedClient.HostMicroseconds;
        var planning=Task.Run(()=>ValidatedPlan(source, pose, supportedAxes),token);
        var nextPauseRequest=0d;
        while(!planning.IsCompleted) {
            var preparing=snapshot();
            if(preparing.IsPlaying && TCodeBufferedClient.HostMicroseconds>=nextPauseRequest) {
                setPlaying(false);nextPauseRequest=TCodeBufferedClient.HostMicroseconds+1500000;
            }
            await Task.WhenAny(planning,Task.Delay(30,token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        _ = await planning.ConfigureAwait(false);
        diagnostic?.Invoke($"PREP {preparation} planning complete elapsedMs={(TCodeBufferedClient.HostMicroseconds-planningStarted)/1000:F2}");
        // A direct play click during computation must not silently skip seconds.
        // Use the current media cursor for the subsequent future start gate.
        if(!snapshot().IsPlaying && snapshot().MediaSample is { } pausedSample)
            source=source with{Position=pausedSample.Position};
        token.ThrowIfCancellationRequested();
        if(controlRevision()!=controls || !source.HasSameTimeline(snapshot())) { RestartPreparation(); return; }
        await client.SynchronizeAsync(token).ConfigureAwait(false);
        pose = await client.QueryPoseAsync(token).ConfigureAwait(false);
        MediaStartGate gate = null;
        report(new("Waiting for media clock to advance", client.UncertaintyMilliseconds));
        Trace("request play before device BEGIN", source);
        setPlaying(true);
        deadline = TCodeBufferedClient.HostMicroseconds + 12000000;
        BufferedPlaybackSnapshot observed;
        while (true)
        {
            observed = snapshot();
            if (!source.HasSameTimeline(observed) || source.FilterRevision != observed.FilterRevision || controlRevision() != controls)
            { RestartPreparation(); return; }
            if (!observed.IsPlaying) gate = null;
            else if (gate == null) gate = new MediaStartGate(observed.MediaSample?.Sequence ?? 0, observed.MediaSample);
            else if (gate.Observe(observed, TCodeBufferedClient.HostMicroseconds)) break;
            if (TCodeBufferedClient.HostMicroseconds > deadline)
                throw new MediaSynchronizationException("Media position did not advance reliably; device was not started.");
            await Task.Delay(10, token).ConfigureAwait(false);
        }
        Trace("raw media clock advancement confirmed", observed);
        var anchorDevice = client.Now;
        var anchorPosition = MediaStartGate.PositionNow(observed, TCodeBufferedClient.HostMicroseconds);
        // Pick a future script point, allowing the existing validated positioning
        // envelope to reach it. Never replay the skipped startup interval faster.
        var horizon = 0UL;
        BufferedSessionPlan plan;
        ulong start, epoch;
        var readyDeadline = TCodeBufferedClient.HostMicroseconds + 5000000;
        while (true)
        {
            epoch = checked(anchorDevice + horizon);
            source = observed with { Position = anchorPosition + horizon / 1e6 * observed.Speed };
            plan = ValidatedPlan(source, pose, supportedAxes);
            var lead = PreloadLeadMicroseconds(plan, client.UncertaintyMilliseconds);
            var required = checked((client.Now - anchorDevice) + plan.PositioningMicroseconds + lead + 100000);
            if (required <= horizon)
            {
                start = epoch - plan.PositioningMicroseconds;
                if (ChangedWhilePreparing()) { if(client.MayOwnMotion) await StopAsync(); RestartPreparation(); return; }
                var beginMask = (ulong)plan.Nodes.Select((n, i) => n.Count > 0 ? 1 << i : 0).Aggregate((a, b) => a | b);
                try { await client.BeginAsync(beginMask, start, plan.Limits, token).ConfigureAwait(false); break; }
                catch (TCodeBufferedException e) when (e.Code == 8)
                {
                    if (TCodeBufferedClient.HostMicroseconds > readyDeadline)
                        throw new MediaSynchronizationException("Device is still settling; start was not armed.");
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue; // Recompute a future epoch instead of reusing an expired start.
                }
            }
            horizon = required + 50000;
            if (horizon > 30000000)
                throw new MediaSynchronizationException("Positioning needs a longer startup window; device was not started.");
        }
        var nodes = plan.Nodes;
        diagnostic?.Invoke(FormattableString.Invariant($"PREP {preparation} media-gated anchorPosition={anchorPosition:F6} anchorDeviceUs={anchorDevice} startUs={start} mediaEpochUs={epoch} sourceSeconds={source.Position:F6}"));
        // Keep the validated relative node times; do not recompute from a later
        // media snapshot after the common start time has been chosen.
        for (var axis = 0; axis < 6; axis++)
            for (var i = 0; i < nodes[axis].Count; i++)
                nodes[axis][i] = nodes[axis][i] with { Microseconds = checked(nodes[axis][i].Microseconds + start) };
        var handledFilterRevision=source.FilterRevision;
        ulong filterEffectiveAt=0;
        long scheduledFilterRevision=source.FilterRevision;
        var mediaClock=new MediaClockSlew();
        var cursor = new int[6];
        var sealedAxes = new bool[6];
        report(new("Preloading", client.UncertaintyMilliseconds, Limits: plan.Limits));
        if (!await RefillAsync(ChangedWhilePreparing, start + 1000000).ConfigureAwait(false)) { await StopAsync(); RestartPreparation(); return; }
        if (ChangedWhilePreparing()) { await StopAsync(); RestartPreparation(); return; }
        await client.SendAsync(4, token).ConfigureAwait(false);
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
        var nextRefill = 0UL;
        var nextClockSample = 0UL;
        var nextClockCorrection=0UL;
        bool clockCorrectionSupported=true;
        var nextRangeUpdate=0UL;
        var rangeNotice="";
        var appliedRanges=source.Axes.ToDictionary(a=>a.Name,a=>(a.Minimum,a.Maximum));



        FilterUsageLog.Write("buffer_session",new{revision=source.FilterRevision,epoch,start,boot=client.Boot,session=client.Session,source.Position,source.Speed,limits=plan.Limits,axes=source.Axes.Select(a=>new{a.Name,a.Scale,a.Minimum,a.Maximum,a.Offset})});
        if(FilterRuntime.Enabled)FilterRuntime.Status("滤波已生效");
        Trace("play acknowledged", snapshot());
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            if (!source.HasSameMotionTimeline(current))
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
            await CorrectMediaClock(current).ConfigureAwait(false);
            CheckMediaClock(current, "playback");
            if(client.Now>=nextRangeUpdate) {
                foreach(var a in current.Axes) {
                    if(!appliedRanges.TryGetValue(a.Name,out var applied) || applied==(a.Minimum,a.Maximum))continue;
                    var original=source.Axes.First(b=>b.Name==a.Name);
                    int axis=Array.IndexOf(TCodeBufferedPlan.AxisNames,a.Name);
                    try {
                        if(original.Maximum-original.Minimum<.0001)throw new InvalidDataException("本次启动时范围为零，扩大范围需暂停后重新播放");
                        ulong Encode(double v)=>checked((ulong)Math.Round(v*9999,MidpointRounding.AwayFromZero));
                        await client.SendAsync(15,token,(ulong)axis,Encode(original.Minimum),Encode(original.Maximum),Encode(a.Minimum),Encode(a.Maximum)).ConfigureAwait(false);
                        rangeNotice="";
                        diagnostic?.Invoke($"LIVE RANGE applied axis={a.Name} min={a.Minimum:F4} max={a.Maximum:F4} deviceUs={client.Now}");
                        TestRecording.Record("live_range_applied",new{a.Name,a.Minimum,a.Maximum,current.Position,deviceUs=client.Now});
                    } catch(InvalidDataException ex) {
                        rangeNotice="范围调整未应用："+ex.Message;
                        report(new(rangeNotice,client.UncertaintyMilliseconds));
                        diagnostic?.Invoke("LIVE RANGE rejected: "+ex.Message);
                    } catch(TCodeBufferedException ex) {
                        rangeNotice="范围调整未应用：需要匹配的实时范围固件";
                        report(new(rangeNotice,client.UncertaintyMilliseconds));
                        diagnostic?.Invoke("LIVE RANGE rejected: "+ex.Message);
                    }
                    appliedRanges[a.Name]=(a.Minimum,a.Maximum);
                }
                nextRangeUpdate=client.Now+100000;
            }
            if(current.FilterRevision!=handledFilterRevision) {
                handledFilterRevision=current.FilterRevision;
                try {
                    var targetSnapshot=current with {Position=source.Position,Axes=current.Axes.Select(a=> {var b=source.Axes.FirstOrDefault(b=>b.Name==a.Name);return b==null?a:a with{Minimum=b.Minimum,Maximum=b.Maximum};}).Where(a=>Array.IndexOf(TCodeBufferedPlan.AxisNames,a.Name) is var i && i>=0 && (supportedAxes & (1UL<<i))!=0).ToArray()};
                    var target=TCodeBufferedPlan.Create(targetSnapshot,epoch);
                    var splice=BufferedFilterSplice.Create(nodes,target,cursor,sealedAxes,plan.Limits,client.Now);
                    if(filterEffectiveAt>0)FilterUsageLog.Write("transition_superseded",new{previousRevision=scheduledFilterRevision,newRevision=current.FilterRevision,previousEnd=filterEffectiveAt,deviceNow=client.Now});
                    nodes=splice.Nodes;filterEffectiveAt=splice.End;scheduledFilterRevision=current.FilterRevision;
                    FilterRuntime.Status("滤波调整中 · 等待缓冲衔接",true);
                    FilterUsageLog.Write("transition_scheduled",new{revision=current.FilterRevision,splice.Start,splice.End,deviceNow=client.Now,boot=client.Boot,session=client.Session,
                        startMedia=source.Position+((double)splice.Start-epoch)/1e6*source.Speed,endMedia=source.Position+((double)splice.End-epoch)/1e6*source.Speed});
                } catch(InvalidDataException ex) {
                    FilterRuntime.Status("滤波调整待应用 · 需暂停",true);
                    FilterUsageLog.Write("transition_deferred",new{revision=current.FilterRevision,current.Position,reason=ex.Message});
                }
            }
            if (client.Now >= nextRefill)
            {
                await client.SendAsync(0, token).ConfigureAwait(false);
                if (client.Status[5] is not (2 or 3 or 4))
                    throw client.DeviceStateException("P4 trajectory stopped.");
                if (client.Status[5] != 4 && !await RefillAsync(ChangedWhilePlaying).ConfigureAwait(false))
                    continue; // The main loop distinguishes pause from timeline replacement.
                if(filterEffectiveAt>0 && client.Now>=filterEffectiveAt && nodes.Select((a,i)=>a.Count==0 || (cursor[i]>0 && a[cursor[i]-1].Microseconds>=Math.Min(filterEffectiveAt,a[^1].Microseconds))).All(x=>x)) {
                FilterUsageLog.Write("transition_timeline_reached",new{revision=scheduledFilterRevision,deviceNow=client.Now,current.Position,boot=client.Boot,session=client.Session,meaning="scheduled command time reached; not measured motor response"});
                if(scheduledFilterRevision==current.FilterRevision)FilterRuntime.Status("滤波已生效");
                filterEffectiveAt=0;
            }

                var tails = nodes.Select((p, i) => p.Count > 0 && !sealedAxes[i] ? client.Status[15 + i] : ulong.MaxValue).ToArray();
                var buffered = tails.Min() == ulong.MaxValue ? 0 : Math.Max(0, ((double)tails.Min() - client.Now) / 1000000);
                report(new((client.Status[5] == 4 ? "Complete (holding)" : "Playing") + (rangeNotice.Length>0?" · "+rangeNotice:""), client.UncertaintyMilliseconds, buffered, plan.Limits));
                nextRefill = client.Now + 200000;
            }
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        await StopAsync();
        RestartPreparation();

        async Task<bool> PauseAndResumeAsync(BufferedPlaybackSnapshot paused)
        {
            await StopAsync();
            var pauseControls = controlRevision();
            var pauseDeadline = TCodeBufferedClient.HostMicroseconds + 12000000;
            while (snapshot().IsPlaying)
            {
                if (controlRevision() != pauseControls) { RestartPreparation(); return false; }
                if (TCodeBufferedClient.HostMicroseconds > pauseDeadline)
                    throw new MediaSynchronizationException("Media pause acknowledgement timed out; device is stopped.");
                await Task.Delay(10, token).ConfigureAwait(false);
            }
            report(new("Paused (waiting for media)", client.UncertaintyMilliseconds));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var current = snapshot();
                if (!source.HasSameTimeline(current) || current.IsPlaying || (controlRevision() != pauseControls && wantsPlayback()))
                { RestartPreparation(); return false; }
                await Task.Delay(30, token).ConfigureAwait(false);
            }
        }

        void RestartPreparation()
        {
            // Rebuild while paused. Briefly playing merely to re-enter the outer
            // loop advances media again and can cause an endless prepare cycle.
            _prepareAgain = wantsPlayback() && snapshot().IsPlaying && !token.IsCancellationRequested;
            Trace($"restart preparation requested={_prepareAgain}", snapshot());
        }

        bool ChangedWhilePreparing()
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            var changed = !current.IsPlaying || current.FilterRevision != source.FilterRevision || !source.HasSameTimeline(current)
                || !double.IsFinite(current.Position) || controlRevision() != controls;
            if (!changed)
            {
                var actual = MediaStartGate.PositionNow(current, TCodeBufferedClient.HostMicroseconds);
                var expected = source.Position + ((double)client.Now - epoch) / 1e6 * source.Speed;
                if (Math.Abs(actual - expected) > .200)
                    throw new MediaSynchronizationException("Media clock changed during preload; re-synchronization required.");
            }
            if (changed)
                Trace(FormattableString.Invariant($"preparation invalidated sameTimeline={source.HasSameTimeline(current)} sourcePosition={source.Position:F6} deltaMs={(current.Position - source.Position) * 1000:F3} controlsAtStart={controls}"), current);
            return changed;
        }

        bool ChangedWhilePlaying()
        {
            token.ThrowIfCancellationRequested();
            var current = snapshot();
            if (!current.IsPlaying || current.FilterRevision!=handledFilterRevision || !source.HasSameMotionTimeline(current) || controlRevision() != controls)
                return true;
            CheckMediaClock(current, "refill");
            return false;
        }

        async Task CorrectMediaClock(BufferedPlaybackSnapshot current)
        {
            var now=client.Now;
            if(!clockCorrectionSupported || now<nextClockCorrection || now<mediaClock.End || client.Status[5]!=3)return;
            nextClockCorrection=now+1000000;
            var expected=source.Position+((double)mediaClock.Time(now)-epoch)/1e6*source.Speed;
            var drift=(MediaStartGate.PositionNow(current,TCodeBufferedClient.HostMicroseconds)-expected)/source.Speed;
            // Large changes remain seek/clock faults, never chase them as drift.
            if(!double.IsFinite(drift)||Math.Abs(drift)<.030||Math.Abs(drift)>.150)return;
            var delta=Math.Clamp(drift*.8,-.040,.040);
            if(Math.Abs(mediaClock.Base+mediaClock.Delta+delta)>2)return;
            var at=now+250000;
            var change=(long)Math.Round(delta*1e6);
            try {
                await client.SendAsync(16,token,at,(ulong)(change+40000)).ConfigureAwait(false);
                mediaClock.Set(at,change/1e6);nextClockCorrection=mediaClock.End+250000;
                diagnostic?.Invoke($"CLOCK SLEW startUs={at} deltaUs={change} durationUs=5000000 driftMs={drift*1000:F3}");
                FilterUsageLog.Write("clock_slew",new{at,change,drift,boot=client.Boot,session=client.Session});
            } catch(TCodeBufferedException e) {
                clockCorrectionSupported=false;
                diagnostic?.Invoke("CLOCK SLEW unavailable; original 200ms protection retained: "+e.Message);
            }
        }

        void CheckMediaClock(BufferedPlaybackSnapshot current, string phase)
        {
            var now = client.Now;
            var expected = source.Position + ((double)mediaClock.Time(now) - epoch) / 1000000 * source.Speed;
            var actualPosition = MediaStartGate.PositionNow(current, TCodeBufferedClient.HostMicroseconds);
            var drift = actualPosition - expected;
            var failed = !double.IsFinite(current.Position) || Math.Abs(drift) > .200;
            if (failed || now >= nextClockSample)
            {
                var direction = !double.IsFinite(drift) ? "invalid" : drift >= 0 ? "media-ahead" : "media-behind";
                diagnostic?.Invoke(FormattableString.Invariant($"CLOCK {(failed ? "ERROR" : "SAMPLE")} prep={preparation} phase={phase} actualSeconds={actualPosition:F6} expectedSeconds={expected:F6} driftMs={drift * 1000:+0.000;-0.000;0.000} direction={direction} thresholdMs=200 sourceSeconds={source.Position:F6} speed={source.Speed:F6} currentSpeed={current.Speed:F6} playing={current.IsPlaying} hostNowUs={TCodeBufferedClient.HostMicroseconds} deviceNowUs={now} mediaEpochUs={epoch} elapsedMs={(TCodeBufferedClient.HostMicroseconds - preparationStarted) / 1000d:F3} uncertaintyMs={client.UncertaintyMilliseconds:F3} boot={client.Boot} session={client.Session} timelineRevision={current.Revision} controlRevision={controlRevision()} status=[{string.Join(',', client.Status)}]"));
                FilterUsageLog.Write("clock_alignment",new{actualSeconds=actualPosition,expectedSeconds=expected,driftMs=drift*1000,deviceNowUs=now,mediaEpochUs=epoch,hostNowUs=TCodeBufferedClient.HostMicroseconds,boot=client.Boot,session=client.Session,failed});
                nextClockSample = now + 1000000;
            }
            if (failed)
                throw new MediaSynchronizationException(FormattableString.Invariant($"Media clock diverged from P4 by over 200 ms; buffered playback stopped. Actual={actualPosition:F6}s expected={expected:F6}s drift={drift * 1000:+0.000;-0.000;0.000}ms phase={phase}."));
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
            FilterUsageLog.Write("output_stopped",new{position=snapshot().Position});
            Trace("STOP acknowledged", snapshot());
        }

        void Trace(string reason, BufferedPlaybackSnapshot current)
            => diagnostic?.Invoke(FormattableString.Invariant($"PREP {preparation} {reason} elapsedMs={(TCodeBufferedClient.HostMicroseconds - preparationStarted) / 1000:F2} playing={current.IsPlaying} position={current.Position:F6} speed={current.Speed:F3} timelineRevision={current.Revision} controlRevision={controlRevision()} wantsPlayback={wantsPlayback()} deviceState={client.Status?[5]} session={client.Session}"));

        async Task<bool> RefillAsync(Func<bool> changed, ulong? preloadHorizon = null)
        {
            while (true)
            {
                if (changed()) return false;
                var horizon = preloadHorizon ?? mediaClock.Time(client.Now) + 6000000;
                var free = client.Status.Skip(9).Take(6).ToArray();
                var tails = client.Status.Skip(15).Take(6).ToArray();
                var next = (int[])cursor.Clone();
                var batch = new List<BufferedNode>(4);
                while (batch.Count < 4)
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
                await client.SendAsync(2, token, batch.SelectMany(p => new ulong[] { (ulong)p.Axis, p.Microseconds, p.Position, BufferedReversalCurve.Encode(p.AccelerationMicros) }).ToArray()).ConfigureAwait(false);
                cursor = next;
                if(filterEffectiveAt>0)FilterUsageLog.Write("transition_batch_ack",new{revision=scheduledFilterRevision,deviceNow=client.Now,nodes=batch});
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
        var transactions = (points + 3) / 4 + 3; // BEGIN, optional SEAL, ARM
        var milliseconds = Math.Max(550, 100 + transactions * Math.Max(15, uncertaintyMs * 2 + 5) * 2);
        if (milliseconds > 9000)
            throw new IOException("Initial trajectory is too dense for the measured connection latency.");
        return (ulong)Math.Ceiling(milliseconds * 1000);
    }
}

