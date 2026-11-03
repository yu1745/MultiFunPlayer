using System.IO;
namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// Only replaces host-side, unsent tails. Existing accepted nodes are immutable.
internal static class BufferedFilterSplice
{
    public sealed record Result(List<BufferedNode>[] Nodes,ulong Start,ulong End);
    public static Result Create(List<BufferedNode>[] old,List<BufferedNode>[] target,int[] sent,bool[] sealedAxes,BufferedSessionLimits limits,ulong now)
    {
        foreach(var duration in new[]{1000000UL,2000000UL,4000000UL}) {
            var result=Enumerable.Range(0,6).Select(_=>new List<BufferedNode>()).ToArray();
            ulong start=ulong.MaxValue,end=0;var valid=true;
            var common=old.Select((a,i)=>sent[i]>0?a[sent[i]-1].Microseconds:now).DefaultIfEmpty(now).Max();common=Math.Max(common,now+500000);
            for(var axis=0;axis<6;axis++) {
                var a=old[axis];var b=target[axis];
                if(a.Count==0 && b.Count==0)continue;
                if(a.Count==0 || b.Count==0 || sealedAxes[axis]){valid=false;break;}
                var anchor=a.FindIndex(Math.Max(0,sent[axis]),p=>p.Microseconds>=common);
                if(anchor<0 || anchor==a.Count-1){valid=false;break;}
                var begin=a[anchor].Microseconds;
                var finish=b.FindIndex(p=>p.Microseconds>=begin+duration);
                if(finish<0){valid=false;break;}
                var stop=b[finish].Microseconds;
                result[axis].AddRange(a.Take(anchor+1));
                var times=a.Where(p=>p.Microseconds>begin && p.Microseconds<stop).Select(p=>p.Microseconds)
                    .Concat(b.Where(p=>p.Microseconds>begin && p.Microseconds<=stop).Select(p=>p.Microseconds)).Distinct().OrderBy(t=>t);
                foreach(var t in times) {
                    var w=TCodeBufferedPlan.Smooth((double)(t-begin)/(stop-begin));
                    var value=Value(a,t)*(1-w)+Value(b,t)*w;
                    result[axis].Add(new(axis,t,(ulong)Math.Clamp(Math.Round(value),0,9999)));
                }
                result[axis].AddRange(b.Skip(finish+1));
                BufferedReversalCurve.Assign(result[axis],anchor+1);
                for(var i=anchor+1;i<result[axis].Count;i++) {
                    var left=result[axis][i-1];var right=result[axis][i];
                    var dt=(right.Microseconds-left.Microseconds)/1e6;
                    if(!BufferedReversalCurve.Allowed(left,right) || dt<.001 || dt>60 || Math.Abs((double)right.Position-left.Position)/10000>limits.ForAxis(axis).AllowedDistance(dt)) {valid=false;break;}
                }
                start=Math.Min(start,begin);end=Math.Max(end,stop);
                if(!valid)break;
            }
            if(valid && end>0)return new(result,start,end);
        }
        throw new InvalidDataException("当前缓冲或会话限值无法平滑衔接，请暂停后应用强度。");
    }
    private static double Value(List<BufferedNode> points,ulong t)
    {
        var lo=0;var hi=points.Count-1;
        while(lo<hi){var mid=(lo+hi+1)/2;if(points[mid].Microseconds<=t)lo=mid;else hi=mid-1;}
        if(t<=points[0].Microseconds)return points[0].Position;
        if(lo==points.Count-1)return points[^1].Position;
        var a=points[lo];var b=points[lo+1];return BufferedReversalCurve.Value(a,b,(double)(t-a.Microseconds)/(b.Microseconds-a.Microseconds));
    }
}
