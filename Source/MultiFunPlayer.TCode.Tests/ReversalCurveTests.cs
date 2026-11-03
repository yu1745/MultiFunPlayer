using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Text.Json;
namespace MultiFunPlayer.TCode.Tests;
public class ReversalCurveTests {
 [Fact] public void ReversalsShareAccelerationAndPreserveHoldsAndEndpoints() {
  var nodes=new List<BufferedNode>{new(0,0,0),new(0,1000000,9999),new(0,2000000,0),new(0,3000000,0)};
  BufferedReversalCurve.Assign(nodes);
  Assert.True(nodes[1].AccelerationMicros<0);Assert.Equal(0,nodes[2].AccelerationMicros);
  foreach(var pair in nodes.Zip(nodes.Skip(1))) {
   Assert.True(BufferedReversalCurve.Allowed(pair.First,pair.Second));
   Assert.Equal((double)pair.First.Position,BufferedReversalCurve.Value(pair.First,pair.Second,0),7);
   Assert.Equal((double)pair.Second.Position,BufferedReversalCurve.Value(pair.First,pair.Second,1),7);
   var previous=(double)pair.First.Position;
   for(int k=1;k<=1000;k++) {
    var value=BufferedReversalCurve.Value(pair.First,pair.Second,k/1000d);
    Assert.InRange(value,Math.Min(pair.First.Position,pair.Second.Position)-1e-6,Math.Max(pair.First.Position,pair.Second.Position)+1e-6);
    Assert.True((value-previous)*Math.Sign((double)pair.Second.Position-pair.First.Position)>=-1e-6);previous=value;
   }
  }
  // At 10% time the candidate has moved farther from the endpoint, without
  // changing its timing or amplitude. Constant hold remains exactly constant.
  Assert.True(9999-BufferedReversalCurve.Value(nodes[1],nodes[2],.1)>9999*TCodeBufferedPlan.Smooth(.1));
  Assert.Equal(0,BufferedReversalCurve.Value(nodes[2],nodes[3],.5));
 }
 [Fact] public void UnequalStrokeLengthsUseWeakerAdjacentAccelerationAndFiniteWire() {
  var nodes=new List<BufferedNode>{new(0,0,0),new(0,10000,9999),new(0,2010000,5000)};
  BufferedReversalCurve.Assign(nodes);
  Assert.InRange(Math.Abs(nodes[1].AccelerationMicros/1e6),.49,.5);
  Assert.All(nodes.Zip(nodes.Skip(1)),x=>Assert.True(BufferedReversalCurve.Allowed(x.First,x.Second)));
  var wire=TCodeBufferedProtocol.Encode(new ulong[]{2,1,99,1}.Concat(nodes.SelectMany(p=>new[]{(ulong)p.Axis,p.Microseconds,p.Position,BufferedReversalCurve.Encode(p.AccelerationMicros)})));
  Assert.True(TCodeBufferedProtocol.TryDecode(wire,out var fields));Assert.Equal(16,fields.Length);
 }
 [Fact] public void RealScriptPlanIsBoundedAndCanBeExportedForFirmwareReplay() {
  var file=Environment.GetEnvironmentVariable("CTW_REVERSAL_SCRIPT");
  if(string.IsNullOrEmpty(file))return;
  using var doc=JsonDocument.Parse(File.ReadAllText(file));
  var points=doc.RootElement.GetProperty("actions").EnumerateArray().Select(a=>new Keyframe(a.GetProperty("at").GetDouble()/1000,a.GetProperty("pos").GetDouble()/100)).ToArray();
  var plan=TCodeBufferedPlan.CreateSession(ProtocolAndPlanTests.Snapshot(points),new BufferedDevicePose(Enumerable.Repeat(.5,6).ToArray()));
  foreach(var nodes in plan.Nodes)Assert.All(nodes.Zip(nodes.Skip(1)),x=>Assert.True(BufferedReversalCurve.Allowed(x.First,x.Second)));
  var output=Environment.GetEnvironmentVariable("CTW_REVERSAL_PLAN");
  if(!string.IsNullOrEmpty(output))File.WriteAllLines(output,new[]{string.Join(" ",plan.Limits.Encode())}.Concat(plan.Nodes.SelectMany(x=>x).OrderBy(x=>x.Microseconds).Select(x=>$"{x.Axis} {x.Microseconds} {x.Position} {BufferedReversalCurve.Encode(x.AccelerationMicros)}")));
 }
}

