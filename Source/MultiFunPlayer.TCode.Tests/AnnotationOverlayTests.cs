using MultiFunPlayer.OutputTarget.Tracking;
using System.Text.Json;
namespace MultiFunPlayer.TCode.Tests;
public class AnnotationOverlayTests
{
    [Fact] public void ExactVideoMatchingLiveUpdatesAndDeletion()
    {
        var folder=Guid.NewGuid().ToString();var video="D:/tests/"+folder+"/same.mp4";
        AnnotationOverlay.Add(folder,new("a",video,3,"正常",""));
        AnnotationOverlay.Add(folder,new("b",video,1,"机械感重",""));
        AnnotationOverlay.Add(folder,new("c","E:/other/same.mp4",2,"不适",""));
        Assert.Equal(new[]{"b","a"},AnnotationOverlay.ForVideo(video.ToUpperInvariant()).Select(m=>m.Id));
        AnnotationOverlay.Remove(folder,"b");
        AnnotationOverlay.Add(folder,new("b",video,1,"stale background load",""));
        Assert.Single(AnnotationOverlay.ForVideo(video));
        Assert.Empty(AnnotationOverlay.ForVideo(null));
    }
    [Fact] public void HistoryLoadHonorsDeletionOverlayAndIgnoresPartialLastLine()
    {
        var root=Path.Combine(Path.GetTempPath(),"filter-overlay-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try{
            var video=root+"/video.mp4";
            string Line(string id)=>JsonSerializer.Serialize(new{kind="annotation",data=new{annotationId=id,mediaSeconds=2,label="正常",note="",settings=JsonSerializer.Serialize(new{mediaPath=video})}});
            File.WriteAllText(Path.Combine(root,"events.jsonl"),Line("keep")+"\n"+Line("deleted")+"\n{unfinished");
            File.WriteAllText(Path.Combine(root,"annotation-deletions.json"),"[\"deleted\"]");
            Assert.Equal(0,AnnotationOverlay.LoadHistory(root));
            Assert.Equal("keep",Assert.Single(AnnotationOverlay.ForVideo(video)).Id);
        }finally{Directory.Delete(root,true);}
    }
}
