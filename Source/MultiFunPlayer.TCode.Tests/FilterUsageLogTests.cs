using MultiFunPlayer.OutputTarget.Tracking;
using System.Text.Json;
namespace MultiFunPlayer.TCode.Tests;
public class FilterUsageLogTests
{
    [Fact] public async Task SeparateSessionPreservesRequestAndEffectiveDistinctionAndDrains()
    {
        var root=Path.Combine(Path.GetTempPath(),"filter-log-"+Guid.NewGuid());
        try {
            FilterUsageLog.Configure(true,root);
            FilterUsageLog.Write("adjustment_requested",new{revision=7,mediaSeconds=100,strength=20});
            FilterUsageLog.Write("transition_scheduled",new{revision=7,startMedia=106,endMedia=107});
            FilterUsageLog.Write("transition_timeline_reached",new{revision=7,mediaSeconds=107});
            var folder=FilterUsageLog.DirectoryPath;
            FilterUsageLog.Configure(false);await FilterUsageLog.Completion;
            var rows=File.ReadAllLines(Path.Combine(folder,"filter-events.jsonl")).Select(l=>JsonDocument.Parse(l).RootElement.Clone()).ToArray();
            Assert.Contains(rows,r=>r.GetProperty("kind").GetString()=="adjustment_requested");
            Assert.Contains(rows,r=>r.GetProperty("kind").GetString()=="transition_timeline_reached");
            Assert.Equal("session_end",rows[^1].GetProperty("kind").GetString());
            Assert.Equal(0,rows[^1].GetProperty("dropped").GetInt64());
        }finally{FilterUsageLog.Configure(false);await FilterUsageLog.Completion;if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
