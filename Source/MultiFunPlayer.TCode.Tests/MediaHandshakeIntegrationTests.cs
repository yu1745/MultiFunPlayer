using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Threading.Channels;
namespace MultiFunPlayer.TCode.Tests;

[CollectionDefinition("PlaybackHandshake", DisableParallelization = true)]
public class PlaybackHandshakeCollection { }
[Collection("PlaybackHandshake")]
public class MediaHandshakeIntegrationTests
{
    [Theory]
    [InlineData(40, 20)] // MPV-like events
    [InlineData(1000, 1000)] // HereSphere-like delayed ACK + periodic position
    public async Task StartSeekSwitchAndResumeKeepSameConnection(int period, int delay)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var sim = new Simulation(period, delay, cancel.Token);
        await using var client = sim.Client();
        var playback = new TCodeBufferedPlayback(client, sim.Snapshot, sim.SetPlaying,
            ()=>sim.Controls, ()=>sim.Wants, s=>sim.Status=s.State);
        var run=playback.RunAsync(cancel.Token);
        try
        {
            await Until(()=>sim.Status=="Playing",run,cancel.Token);
            Assert.True(sim.MinReportsBeforeBegin>=1);
            var begins=sim.Begins;
            sim.Seek(100);
            await Until(()=>sim.Begins>begins && sim.Status=="Playing",run,cancel.Token);
            begins=sim.Begins;
            sim.Switch();
            await Until(()=>sim.Begins>begins && sim.Status=="Playing",run,cancel.Token);
            sim.UserPause();
            await Until(()=>sim.Status.StartsWith("Paused"),run,cancel.Token);
            begins=sim.Begins;
            sim.UserPlay();
            await Until(()=>sim.Begins>begins && sim.Status=="Playing",run,cancel.Token);
            sim.SetPlaying(false); // Pause directly in the media player, not MFP.
            await Until(()=>sim.Status.StartsWith("Paused"),run,cancel.Token);
            begins=sim.Begins;
            await Task.Delay(300,cancel.Token);
            Assert.Equal(begins,sim.Begins);
            sim.SetPlaying(true);
            await Until(()=>sim.Begins>begins && sim.Status=="Playing",run,cancel.Token);
            Assert.False(run.IsCompleted);
            Assert.Equal(0,sim.ResumeCommands); // no speculative firmware resume
            Assert.True(sim.Stops>=3);
        }
        finally { cancel.Cancel(); await run; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPlanOrDeviceStopPausesWithoutDisconnectOrAutomaticRestart(bool deviceStop)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sim = new Simulation(40, 20, cancel.Token);
        if (!deviceStop) sim.InvalidScript();
        else sim.FailOnArm = true;
        await using var client = sim.Client();
        var playback = new TCodeBufferedPlayback(client, sim.Snapshot, sim.SetPlaying,
            ()=>sim.Controls, ()=>sim.Wants, s=>sim.Status=s.State);
        var run = playback.RunAsync(cancel.Token);
        try
        {
            await Until(()=>!sim.Snapshot().IsPlaying && sim.Status=="Waiting for playback",run,cancel.Token);
            var begins=sim.Begins;
            await Task.Delay(250,cancel.Token);
            Assert.False(run.IsCompleted);
            Assert.Equal(begins,sim.Begins);
            if (deviceStop) Assert.True(sim.Stops>0);
            else Assert.Equal(0,begins);
            sim.FailOnArm=false;sim.Switch();sim.UserPlay();
            await Until(()=>sim.Status=="Playing",run,cancel.Token);
            Assert.False(run.IsCompleted);
        }
        finally { cancel.Cancel(); await run; }
    }
    private static async Task Until(Func<bool> condition,Task run,CancellationToken token)
    {
        while(!condition()) { if(run.IsCompleted) {await run; Assert.Fail("Playback ended unexpectedly");} await Task.Delay(20,token); }
    }
    private sealed class Simulation
    {
        readonly Channel<string> replies=Channel.CreateUnbounded<string>();
        readonly object mutex=new(); readonly int delay; readonly CancellationToken token;
        BufferedPlaybackSnapshot value; double anchor=10, anchorHost=TCodeBufferedClient.HostMicroseconds;
        bool playing=true; long seq; int reports; ulong state,session; ulong[] tails=new ulong[6];
        public string Status=""; public long Controls; public bool Wants=true;
        public int Begins,Stops,ResumeCommands,MinReportsBeforeBegin=int.MaxValue;
        public bool FailOnArm;
        public void InvalidScript(){lock(mutex){Keyframe[] f=[new(0,.5),new(11,.5),new(11.001,double.NaN),new(11.002,0),new(1000,.5)];value=value with{Axes=[new("L0",f,0,1,false,.5)]};}}
        public Simulation(int period,int delay,CancellationToken token)
        {
            this.delay=delay;this.token=token;
            Keyframe[] frames=[new(0,.5),new(1000,.5)];
            value=new(true,10,1,1,[new("L0",frames,0,1,false,.5)],MediaSample:new(10,anchorHost,++seq));
            _=Task.Run(async()=> {try {while(true){await Task.Delay(period,token);lock(mutex){var now=TCodeBufferedClient.HostMicroseconds;var pos=anchor+(playing?(now-anchorHost)/1e6:0);value=value with{Position=pos,IsPlaying=playing,MediaSample=new(pos,now,++seq)};if(playing)reports++;}}}catch(OperationCanceledException){}},token);
        }
        public BufferedPlaybackSnapshot Snapshot(){lock(mutex)return value;}
        public void SetPlaying(bool yes)
        {
            _=Task.Run(async()=>{try{await Task.Delay(delay,token);lock(mutex){var now=TCodeBufferedClient.HostMicroseconds;anchor+=playing?(now-anchorHost)/1e6:0;anchorHost=now;playing=yes;reports=0;value=value with{IsPlaying=yes,Position=anchor};}}catch(OperationCanceledException){}},token);
        }
        public void Seek(double p){lock(mutex){anchor=p;anchorHost=TCodeBufferedClient.HostMicroseconds;value=value with{Revision=value.Revision+1,Position=p,MediaSample=new(p,anchorHost,++seq)};}}
        public void Switch(){Seek(20);lock(mutex){Keyframe[] f=[new(0,.5),new(1000,.5)];value=value with{Axes=[new("L0",f,0,1,false,.5)]};}}
        public void UserPause(){Controls++;Wants=false;SetPlaying(false);}
        public void UserPlay(){Controls++;Wants=true;SetPlaying(true);}
        public TCodeBufferedClient Client()=>new(Write,async ct=>await replies.Reader.ReadAsync(ct));
        Task Write(string wire,CancellationToken ct)
        {
            if(!TCodeBufferedProtocol.TryDecode(wire,out var f))return Task.CompletedTask;
            var now=(ulong)TCodeBufferedClient.HostMicroseconds;
            if(f[0]==1){session++;state=1;Begins++;lock(mutex){MinReportsBeforeBegin=Math.Min(MinReportsBeforeBegin,reports);}Array.Clear(tails);}
            if(f[0]==2)for(int i=4;i+3<f.Length;i+=4)tails[f[i]]=f[i+1];
            if(f[0]==4)state=FailOnArm?5UL:3UL;
            if(f[0]==5){state=0;Stops++;}
            if(f[0]==8)ResumeCommands++;
            ulong[] r=[100,f[1],99,session,0,state,now,1,512,512,512,512,512,512,512,tails[0],tails[1],tails[2],tails[3],tails[4],tails[5],0];
            if(f[0]==6)r=[101,f[1],99,session,0,state,1,500000,500000,500000,500000,500000,500000];
            if(f[0]==9)r=[102,f[1],99,session,0,state,now,0,0,0,0];
            if(f[0]==10)r=[103,f[1],99,session,0,state,1];
            if(f[0]==11)r=[104,f[1],99,session,0,state,1,32,0,1];
            replies.Writer.TryWrite(TCodeBufferedProtocol.Encode(r));return Task.CompletedTask;
        }
    }
}
