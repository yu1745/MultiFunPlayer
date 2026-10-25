namespace MultiFunPlayer.OutputTarget.Tracking;

// Read-only evidence of powered motor feedback, NOT a motor power switch sensor or uptime counter.
internal sealed class MotorPowerObservation
{
    private sealed class Motor
    {
        public string Segment;
        public double Start, Last = double.NegativeInfinity;
        public DateTimeOffset StartUtc, LastUtc;
    }
    private readonly object gate = new();
    private readonly Motor[] motors = Enumerable.Range(0,7).Select(_=>new Motor()).ToArray();
    private uint boot;
    private byte mask;
    public const double GapSeconds = 2;
    public void Reset() { lock(gate) { foreach(var m in motors) {m.Segment=null;m.Last=double.NegativeInfinity;} boot=0;mask=0; } }
    public void Observe(MotorHealthPacket packet, double host, DateTimeOffset utc, double deliveryAgeMs)
    {
        lock(gate)
        {
            if(boot!=packet.Boot) { foreach(var m in motors) {m.Segment=null;m.Last=double.NegativeInfinity;} }
            boot=packet.Boot; mask=packet.MotorMask;
            for(int i=0;i<7;i++)
            {
                var m=motors[i]; var h=packet.Motors[i];
                if((mask & (1<<i))==0) {m.Segment=null;m.Last=double.NegativeInfinity;continue;}
                // Speed feedback or drive-state heartbeat proves a recent reply. Cached temperature/voltage alone does not.
                double age=h.StateAgeMs;
                if((h.Valid & 1)!=0) age=Math.Min(age,h.AgesMs[0]);
                age+=Math.Max(0,deliveryAgeMs);
                if(!double.IsFinite(age)||age>500) continue;
                var seen=host-age/1000;var seenUtc=utc-TimeSpan.FromMilliseconds(age);
                if(seen<=m.Last) continue;
                if(m.Segment==null || seen-m.Last>GapSeconds)
                {m.Segment=Guid.NewGuid().ToString("N");m.Start=seen;m.StartUtc=seenUtc;}
                m.Last=seen;m.LastUtc=seenUtc;
            }
        }
    }
    public object Snapshot(double host)
    {
        lock(gate) return new {
            method="fresh_motor_reply_observation", actualPowerOnUtc=(string)null,
            warning="Feedback absence is not proof of power off. Start is first observed reply, not actual power-on. Offline intervals are unknown.",
            gapThresholdSeconds=GapSeconds,controllerBoot=boot,
            motors=motors.Select((m,i)=>new {
                motor=i+1,enabled=(mask&(1<<i))!=0,
                state=(mask&(1<<i))==0?"not_observed_or_disabled":m.Segment!=null && host-m.Last<=GapSeconds?"feedback_present":"feedback_missing_power_unknown",
                segmentId=m.Segment,firstObservedUtc=m.Segment==null?(DateTimeOffset?)null:m.StartUtc,
                lastObservedUtc=m.Segment==null?(DateTimeOffset?)null:m.LastUtc,
                observedSpanSeconds=m.Segment==null?0:Math.Max(0,m.Last-m.Start),
                secondsSinceLastReply=m.Segment==null?(double?)null:Math.Max(0,host-m.Last)
            }).ToArray()
        };
    }
}
