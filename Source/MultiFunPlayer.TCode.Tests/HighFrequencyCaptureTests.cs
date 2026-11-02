using MultiFunPlayer.OutputTarget.TCodeBuffered;
using MultiFunPlayer.OutputTarget.Tracking;
using System.Collections.Concurrent;
using System.IO;
namespace MultiFunPlayer.TCode.Tests;
public class HighFrequencyCaptureTests
{
 [Fact] public async Task DisabledHighFrequencySendsNothingEvenWhenRecording()
 {
  var writes=new ConcurrentQueue<string>();
  await using var hf=new HighFrequencyCapture((s,t)=>{writes.Enqueue(s);return Task.CompletedTask;},()=>true,()=>123,null,()=>true,()=>"unused",()=>false);
  await Task.Delay(450);
  Assert.Empty(writes);
 }
 [Fact] public void CrcMatchesCcittGoldenVector() => Assert.Equal((ushort)0x29b1,HighFrequencyCapture.Crc("123456789"u8));
 [Fact] public async Task DiagnosticSilenceDoesNotSendAnythingWithoutRecording()
 {
  var writes=new ConcurrentQueue<string>();
  await using var hf=new HighFrequencyCapture((s,t)=>{writes.Enqueue(s);return Task.CompletedTask;},()=>true,()=>123,null,()=>false,()=>"unused");
  await Task.Delay(300);
  Assert.Empty(writes);
 }
 [Theory]
 [InlineData(false)]
 [InlineData(true)]
 public async Task ExportYieldsToPlaybackAndReleasesOnlyAfterVerifiedSave(bool compressed)
 {
  var root=Path.Combine(AppContext.BaseDirectory,"TestArtifacts",Guid.NewGuid().ToString("N"));
  var recording=false;var paused=true;var calls=new ConcurrentQueue<string>();
  HighFrequencyCapture hf=null;
  hf=new HighFrequencyCapture((wire,token)=>{
   var p=wire.Trim().Split(' ');var id=p[1];var verb=p[2];calls.Enqueue(verb);
   if(verb is "read" or "readz")Assert.True(hf.IsExporting);
   if(verb=="run")Assert.False(hf.IsExporting);
   string reply;
   if(verb=="readz" && compressed) {
    var kind=int.Parse(p[4]);var offset=int.Parse(p[5]);var n=Math.Min(1024,(kind==0?389:808)-offset);
    var packed=new List<byte>();for(int i=0;i<n;i+=128)packed.Add((byte)(128+Math.Min(128,n-i)-1));
    reply=$"!HF1,{id},ZDATA,{kind},{offset},{HighFrequencyCapture.Crc(new byte[n])},{n},{Convert.ToHexString(packed.ToArray())}";
   } else if(verb=="readz")reply=$"!HF1,{id},ERR,verb";
   else if(verb=="stop")reply=$"!HF1,{id},OK,stop,389,808,0,0";
   else if(verb=="read") {
    var kind=int.Parse(p[4]);var offset=int.Parse(p[5]);
    var bytes=new byte[Math.Min(128,(kind==0?389:808)-offset)];
    reply=$"!HF1,{id},DATA,{kind},{offset},{HighFrequencyCapture.Crc(bytes)},{Convert.ToHexString(bytes)}";
   } else reply=$"!HF1,{id},OK,{verb}";
   hf.Receive(reply);return Task.CompletedTask;
  },()=>paused,()=>123,null,()=>recording,()=>root);
  await using(hf) {
   recording=true;await Until(()=>calls.Contains("start"));
   paused=false;await Until(()=>calls.Contains("run"));
   await Task.Delay(250);Assert.DoesNotContain("read",calls);
   recording=false;paused=true;await Until(()=>calls.Contains("release"));
   var dir=Directory.GetDirectories(Path.Combine(root,"HighFrequency")).Single();
   Assert.Equal(389,new FileInfo(Path.Combine(dir,"motion.bin")).Length);
   Assert.Equal(808,new FileInfo(Path.Combine(dir,"can.bin")).Length);
   Assert.True(File.Exists(Path.Combine(dir,"sha256.json")));
  }
 }
 [Fact] public void DeltaDecodePreservesCrossBlockRecordHistory()
 {
  using var file=new MemoryStream(new byte[]{10,20,30,40,10,20,30,40});
  // Previous-record XOR: four unchanged bytes followed by one changed byte.
  Assert.Equal(new byte[]{10,20,30,40,11},HighFrequencyCapture.DecodeDelta(new byte[]{131,0,1},5,8,4,file));
 }
 [Fact] public void DeltaDecodeRejectsCorruptionAndMissingHistory()
 {
  using var file=new MemoryStream();
  Assert.ThrowsAny<IOException>(()=>HighFrequencyCapture.DecodeDelta(new byte[]{255},1,0,389,file));
  Assert.ThrowsAny<IOException>(()=>HighFrequencyCapture.DecodeDelta(new byte[]{1,9},2,0,389,file));
  Assert.ThrowsAny<IOException>(()=>HighFrequencyCapture.DecodeDelta(new byte[]{128},1,400,389,file));
 }
 private static async Task Until(Func<bool> condition) {
  var until=DateTime.UtcNow.AddSeconds(5);
  while(!condition() && DateTime.UtcNow<until)await Task.Delay(20);
  Assert.True(condition());
 }
}
