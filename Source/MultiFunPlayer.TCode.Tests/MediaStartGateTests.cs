using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
namespace MultiFunPlayer.TCode.Tests;
public class MediaStartGateTests
{
    private static BufferedPlaybackSnapshot Sample(long seq, double host, double position, bool playing = true)
        => new(playing, position, 1, 1, [], MediaSample: new(position, host, seq));
    [Theory]
    [InlineData(40000)] // MPV property notifications
    [InlineData(1000000)] // HereSphere periodic state reports
    public void BaselineNeedsOneRealAdvancingReport(double period)
    {
        var gate = new MediaStartGate(10);
        // Even an advanced UI time and a play flag do not count as a new report.
        Assert.False(gate.Observe(Sample(10, 0, 100) with {Position=101}, 1000000));
        Assert.False(gate.Observe(Sample(11, 2000000, 100), 2000000));
        Assert.True(gate.Observe(Sample(12, 2000000+period, 100+period/1e6), 2000000+period));
    }
    [Theory]
    [InlineData(40000)]
    [InlineData(1000000)]
    public void ExistingBaselineAllowsFirstNewReport(double period)
    {
        var baseline=Sample(10,2000000,100);
        var gate=new MediaStartGate(10,baseline.MediaSample);
        Assert.False(gate.Observe(baseline,2000000));
        Assert.True(gate.Observe(Sample(11,2000000+period,100+period/1e6),2000000+period));
    }
    [Fact]
    public void ExistingBaselineDoesNotAcceptJumpOrBackwardMovement()
    {
        var gate=new MediaStartGate(10,Sample(10,2000000,100).MediaSample);
        Assert.False(gate.Observe(Sample(11,3000000,500),3000000));
        Assert.False(gate.Observe(Sample(12,4000000,400),4000000));
        Assert.True(gate.Observe(Sample(13,5000000,401),5000000));
    }
    [Fact]
    public void DuplicatePollsCannotOpenGate()
    {
        var g=new MediaStartGate(0); var s=Sample(1,1000000,10);
        for(int i=0;i<50;i++) Assert.False(g.Observe(s,1000000+i*10000));
    }
    [Fact]
    public void SeekJumpRestartsConfirmation()
    {
        var g=new MediaStartGate(0);
        Assert.False(g.Observe(Sample(1,1000000,10),1000000));
        Assert.False(g.Observe(Sample(2,2000000,500),2000000));
        Assert.True(g.Observe(Sample(3,3000000,501),3000000));
    }
    [Fact]
    public void PauseAndStallCannotCountAsAdvancement()
    {
        var g=new MediaStartGate(0);
        for(int i=1;i<=5;i++)Assert.False(g.Observe(Sample(i,i*1000000,10),i*1000000));
        Assert.False(g.Observe(Sample(6,6000000,11,false),6000000));
        Assert.False(g.Observe(Sample(7,7000000,12),7000000));
    }
    [Fact]
    public void StalePositionCannotBeExtrapolatedIndefinitely()
    {
        Assert.Equal(10.5,MediaStartGate.PositionNow(Sample(1,1000000,10),1500000),6);
        Assert.Throws<MediaSynchronizationException>(()=>MediaStartGate.PositionNow(Sample(1,1000000,10),3000000));
    }
}
