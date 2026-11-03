using MultiFunPlayer.OutputTarget.TCodeBuffered;
namespace MultiFunPlayer.TCode.Tests;
public class MediaClockSlewTests {
 [Theory][InlineData(.04)][InlineData(-.04)]
 public void SlewIsContinuousMonotonicAndBounded(double delta) {
  var s=new MediaClockSlew();s.Set(1000000,delta);
  Assert.Equal(0,s.Offset(1000000));Assert.Equal(delta,s.Offset(6000000),9);
  for(ulong t=1001000;t<=6000000;t+=1000) {
   var rate=(s.Time(t)-s.Time(t-1000))/1000d;
   Assert.InRange(rate,.981,1.019);
  }
  s.Set(7000000,-delta);Assert.Equal(delta,s.Offset(7000000),9);Assert.Equal(0,s.Offset(12000000),9);
 }
}
