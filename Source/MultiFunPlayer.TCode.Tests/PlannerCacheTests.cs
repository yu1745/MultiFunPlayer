using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Diagnostics;
using System.Text.Json;
namespace MultiFunPlayer.TCode.Tests;
public class PlannerCacheTests {
 [Fact] public void RealScriptRepeatUsesIdenticalCurves() {
  var file=Environment.GetEnvironmentVariable("CTW_TIMING_SCRIPT");if(string.IsNullOrEmpty(file))return;
  var suffix=new[]{"",".surge",".sway",".twist",".roll",".pitch"};var axes=new List<BufferedScriptAxis>();
  for(int i=0;i<6;i++) {
   var path=Path.ChangeExtension(file,null)+suffix[i]+".funscript";if(!File.Exists(path))continue;
   using var d=JsonDocument.Parse(File.ReadAllText(path));
   var f=d.RootElement.GetProperty("actions").EnumerateArray().Select(a=>new Keyframe(a.GetProperty("at").GetDouble()/1000,a.GetProperty("pos").GetDouble()/100)).ToArray();
   axes.Add(new(TCodeBufferedPlan.AxisNames[i],f,0,1,false,.5,OriginalKeyframes:f));
  }
  var s=new BufferedPlaybackSnapshot(false,0,1,1,axes);var w=Stopwatch.StartNew();
  var a0=TCodeBufferedPlan.Create(s,0);var first=w.Elapsed.TotalMilliseconds;w.Restart();
  var a1=TCodeBufferedPlan.Create(s,0);var warm=w.Elapsed.TotalMilliseconds;
  for(int i=0;i<6;i++)Assert.Equal(a0[i],a1[i]);
  var report=Environment.GetEnvironmentVariable("CTW_TIMING_REPORT");
  if(!string.IsNullOrEmpty(report))File.WriteAllText(report,JsonSerializer.Serialize(new{firstMs=first,warmMs=warm,axes=axes.Count,nodes=a0.Sum(x=>x.Count)}));
 }
}
