using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
namespace MultiFunPlayer.UI.Controls;

// Native hotkeys consume number presses before MPV receives them. They are registered only
// while recording and a permitted foreground window is active, never for other applications.
internal sealed class AnnotationKeyboard : IDisposable
{
    private readonly Func<bool, bool> enabled;
    private readonly Action<string> annotate;
    private readonly Action<Exception> report;
    private readonly Thread thread;
    private Dispatcher dispatcher;
    private volatile string status = "正在启动";
    public string Status => status;
    private readonly Dictionary<int, int> registered = new();
    private HwndSource source;
    private nint foregroundHook;
    private WinEventProc foregroundCallback;
    public AnnotationKeyboard(Func<bool, bool> enabled, Action<string> annotate, Action<Exception> report = null)
    {
        this.enabled=enabled; this.annotate=annotate; this.report=report;
        thread=new Thread(Run) {IsBackground=true, Name="Annotation native hotkeys"};
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    internal static string Label(int key) => key switch {
        0x31 or 0x61 => "臀模震动不适", 0x32 or 0x62 => "机械感重", 0x33 or 0x63 => "异响",
        0x34 or 0x64 => "抖动", 0x35 or 0x65 => "跟不上", 0x36 or 0x66 => "正常", _ => null };
    private bool Allowed()
    {
        GetWindowThreadProcessId(GetForegroundWindow(),out var pid);
        if(pid==Environment.ProcessId) return enabled(true);
        if(pid==0) return false;
        using var p=Process.GetProcessById((int)pid);
        return p.ProcessName.Equals("mpv",StringComparison.OrdinalIgnoreCase) && enabled(false);
    }
    private void Refresh()
    {
        try {
            if(!Allowed()) { Release(); status="未占用数字键：等待开始记录 / MFP或MPV前台"; return; }
            for(int i=0;i<12;i++) {
                var id=100+i; var key=i<6?0x31+i:0x61+i-6;
                if(registered.ContainsKey(id)) continue;
                if(RegisterHotKey(source.Handle,id,0x4000,(uint)key)) registered.Add(id,key);
                else { status=$"数字键注册失败：键{(i%6)+1}，错误{Marshal.GetLastWin32Error()}"; return; }
            }
            status="数字键已接管：1～6 / 小键盘（不会传给MPV）";
        } catch(Exception ex) { Release(); status="快捷键异常："+ex.Message; }
    }
    private nint Message(nint hwnd,int msg,nint w,nint l,ref bool handled)
    {
        if(msg==0x312 && registered.TryGetValue((int)w,out var key)) {
            handled=true;
            try { if(Allowed()) annotate(Label(key)); } catch(Exception ex) { report?.Invoke(ex); }
        }
        return 0;
    }
    private void Run()
    {
        dispatcher=Dispatcher.CurrentDispatcher;
        try {
            source=new HwndSource(new HwndSourceParameters("MFP annotation hotkeys") {Width=0,Height=0,WindowStyle=unchecked((int)0x80000000)});
            source.AddHook(Message);
            foregroundCallback=(_,_,_,_,_,_,_)=>Refresh();
            foregroundHook=SetWinEventHook(3,3,0,foregroundCallback,0,0,0);
            var timer=new DispatcherTimer(TimeSpan.FromMilliseconds(25),DispatcherPriority.Input,(_,_)=>Refresh(),dispatcher);
            timer.Start(); Refresh(); Dispatcher.Run(); timer.Stop();
        } catch(Exception ex) { status="快捷键启动失败："+ex.Message; report?.Invoke(ex); }
        finally { Release(); if(foregroundHook!=0)UnhookWinEvent(foregroundHook); source?.Dispose(); }
    }
    private void Release() { if(source==null)return; foreach(var id in registered.Keys)UnregisterHotKey(source.Handle,id); registered.Clear(); }
    public void Dispose() => dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
    private delegate void WinEventProc(nint hook,uint evt,nint hwnd,int obj,int child,uint thread,uint time);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(nint hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd,int id);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min,uint max,nint module,WinEventProc callback,uint pid,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
