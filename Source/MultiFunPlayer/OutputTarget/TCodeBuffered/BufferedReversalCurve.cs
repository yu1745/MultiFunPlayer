using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

// Profile 2: explicit shared endpoint acceleration. Old firmware rejects the
// profile before accepting motion; profile 1 remains supported by new firmware.
internal static class BufferedReversalCurve
{
    private static readonly Dictionary<string,long[]> JointCache=new();
    private static readonly string CacheFolder=Path.Combine(Environment.GetEnvironmentVariable("CTW_CURVE_CACHE") ?? @"D:\0SR-plus-work\曲线缓存", "septic-v1");
    public static void AssignOptimized(List<BufferedNode> nodes) {
        if(nodes.Count<3){Assign(nodes);return;}
        var input=JsonSerializer.Serialize(nodes.Select(n=>new[]{n.Microseconds,n.Position}));
        var key=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
        lock(JointCache){
            if(!JointCache.TryGetValue(key,out var values)) {
                var cachePath=Path.Combine(CacheFolder,key+".json");
                try { if(File.Exists(cachePath)) {
                    var cached=JsonSerializer.Deserialize<long[]>(File.ReadAllText(cachePath));
                    if(cached?.Length==nodes.Count && cached[0]==0 && cached[^1]==0) {
                        var checkedCache=nodes.Select((n,i)=>n with{AccelerationMicros=cached[i]}).ToList();
                        if(Enumerable.Range(0,checkedCache.Count-1).All(i=>Allowed(checkedCache[i],checkedCache[i+1])))values=cached;
                    }
                }} catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { }
                if(values==null) {
                var helper=Environment.GetEnvironmentVariable("CTW_JOINT_HELPER") ?? Path.Combine(AppContext.BaseDirectory,"MotionPlanner","joint_plan.py");
                if(!File.Exists(helper))throw new InvalidDataException("联合曲线求解器缺失，未启动运动。");
                var psi=new ProcessStartInfo(Environment.GetEnvironmentVariable("CTW_JOINT_PYTHON") ?? @"C:\Users\Administrator\AppData\Local\Programs\Python\Python311\python.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                psi.ArgumentList.Add(helper);psi.Environment["PYTHONUTF8"]="1";psi.Environment["PYTHONDONTWRITEBYTECODE"]="1";
                using var process=Process.Start(psi) ?? throw new InvalidDataException("无法启动联合曲线求解器。");
                var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                process.StandardInput.Write(input);process.StandardInput.Close();
                if(!process.WaitForExit(900000)){process.Kill(true);throw new InvalidDataException("联合曲线预计算超时，未启动运动。");}
                if(process.ExitCode!=0)throw new InvalidDataException("联合曲线计算失败："+error.GetAwaiter().GetResult());
                values=JsonSerializer.Deserialize<long[]>(output.GetAwaiter().GetResult());
                if(values==null||values.Length!=nodes.Count||values[0]!=0||values[^1]!=0)throw new InvalidDataException("联合曲线结果不完整。");
                var checkedNodes=nodes.Select((n,i)=>n with{AccelerationMicros=values[i]}).ToList();
                RepairBoundaries(checkedNodes);values=checkedNodes.Select(n=>n.AccelerationMicros).ToArray();
                try {Directory.CreateDirectory(CacheFolder);var temp=cachePath+"."+Guid.NewGuid()+".tmp";
                    File.WriteAllText(temp,JsonSerializer.Serialize(values));File.Move(temp,cachePath,true);
                } catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
                }
                if(JointCache.Count>=64)JointCache.Clear();
                JointCache[key]=values;
            }
            for(int i=0;i<nodes.Count;i++)nodes[i]=nodes[i] with{AccelerationMicros=values[i]};
        }
    }
    public static ulong Encode(long value) => value < 0 ? checked((ulong)(-value)*2+1) : checked((ulong)value*2);
    public static void Assign(List<BufferedNode> nodes, int firstMutable = 0)
    {
        for(var i=firstMutable;i<nodes.Count;i++) {
            double a=0;
            if(i>0 && i+1<nodes.Count) {
                var before=((double)nodes[i].Position-nodes[i-1].Position)/10000;
                var after=((double)nodes[i+1].Position-nodes[i].Position)/10000;
                if(before*after<0) {
                    var dt0=(nodes[i].Microseconds-nodes[i-1].Microseconds)/1e6;
                    var dt1=(nodes[i+1].Microseconds-nodes[i].Microseconds)/1e6;
                    a=Math.Sign(after)*Math.Min(4*Math.Abs(before)/(dt0*dt0),4*Math.Abs(after)/(dt1*dt1));
                }
            }
            nodes[i]=nodes[i] with { AccelerationMicros=checked((long)(a*.999*1e6)) };
        }
    }
    private static double Choose(int n,int k){double v=1;for(int i=1;i<=k;i++)v=v*(n-i+1)/i;return v;}
    private static bool Bound(double[] b,double lo,double hi,int depth=0){
        if(b.Any(x=>!double.IsFinite(x)))return false;
        if(b.All(x=>x>=lo&&x<=hi))return true;
        int n=b.Length-1;
        if(b[0]<lo||b[0]>hi||b[n]<lo||b[n]>hi||depth>=12)return false;
        var t=(double[])b.Clone();var l=new double[n+1];var r=new double[n+1];l[0]=t[0];r[n]=t[n];
        for(int k=1;k<=n;k++){for(int i=0;i<=n-k;i++)t[i]=(t[i]+t[i+1])*.5;l[k]=t[0];r[n-k]=t[n-k];}
        return Bound(l,lo,hi,depth+1)&&Bound(r,lo,hi,depth+1);
    }
    public static bool Allowed(BufferedNode a,BufferedNode b){
        if(b.Microseconds<=a.Microseconds)return false;
        // Match P4 float storage before certifying: APPEND quantizes position and acceleration.
        var p0=(double)((float)a.Position*.0001f);var p1=(double)((float)b.Position*.0001f);
        var d=p1-p0;var dt=(b.Microseconds-a.Microseconds)/1e6;
        var a0=(double)(float)(a.AccelerationMicros/1e6);var a1=(double)(float)(b.AccelerationMicros/1e6);
        if(d==0)return a0==0&&a1==0;
        var A=a0*dt*dt/d;var B=a1*dt*dt/d;
        if(A<0||B>0||A>7.5132||-B>7.5132)return false;
        if(A<=4*(1+2e-6)&&-B<=4*(1+2e-6))return true;
        double[] c=[0,0,A*.5,0,35-5*A+2.5*B,-84+10*A-7*B,70-7.5*A+6.5*B,-20+2*A-2*B];
        double[] max=[2.1875,7.513189,52.5];int n=7;
        for(int k=0;k<3;k++){for(int i=0;i<n;i++)c[i]=(i+1)*c[i+1];--n;var bern=new double[n+1];
            for(int i=0;i<=n;i++)for(int j=0;j<=i;j++)bern[i]+=c[j]*Choose(i,j)/Choose(n,j);
            if(!Bound(bern,k==0?-1e-5:-max[k]*(1+1e-5),max[k]*(1+1e-5)))return false;
        }return true;
    }
    public static void RepairBoundaries(List<BufferedNode> nodes){
        // Reset both ends of any invalid changed interval. Shared nodes keep C3 continuity.
        // A reset may invalidate the preceding interval, so revisit neighbors.
        for(int i=0;i+1<nodes.Count;i++)if(!Allowed(nodes[i],nodes[i+1])){
            if(nodes[i].AccelerationMicros==0&&nodes[i+1].AccelerationMicros==0)throw new InvalidDataException("联合曲线边界校验失败。");
            nodes[i]=nodes[i] with{AccelerationMicros=0};nodes[i+1]=nodes[i+1] with{AccelerationMicros=0};i=Math.Max(-1,i-2);
        }
    }
    public static double Value(BufferedNode a, BufferedNode b, double u)
    {
        u=Math.Clamp(u,0,1);
        var dt=(b.Microseconds-a.Microseconds)/1e6;
        var p0=(double)a.Position;var p1=(double)b.Position;
        var A=a.AccelerationMicros/1e6*dt*dt*10000;
        var B=b.AccelerationMicros/1e6*dt*dt*10000;
        if(u>.5){u=1-u;(p0,p1)=(p1,p0);(A,B)=(B,A);}
        var delta=p1-p0;
        return p0+u*u*(A*.5+u*u*(35*delta-5*A+2.5*B+u*(-84*delta+10*A-7*B+u*(70*delta-7.5*A+6.5*B+u*(-20*delta+2*A-2*B)))));
    }
}
