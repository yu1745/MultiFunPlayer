using MultiFunPlayer.Common;
using MultiFunPlayer.OutputTarget;
using MultiFunPlayer.UI;
using Stylet;

namespace MultiFunPlayer.UI.Dialogs.ViewModels;

internal sealed class ManualControlDialog : Screen
{
    private readonly SerialManualSession session;
    public bool IsReady { get; private set; }
    public string Status { get; private set; } = "正在暂停脚本输出…";
    public int Duration { get; set; } = 1000;
    public List<ManualAxis> Axes { get; }
    public ManualControlDialog(SerialManualSession session, Dictionary<DeviceAxis, MultiFunPlayer.OutputTarget.DeviceAxisSettings> settings,
        Func<DeviceAxis, double> getValue)
    {
        this.session = session;
        Axes = new[] { "L0", "L1", "L2", "R0", "R1", "R2" }.Select(name =>
        {
            var axis = DeviceAxis.Parse(name);
            var enabled = axis != null && settings.TryGetValue(axis, out var s) && s.Enabled;
            var value = axis == null ? 0.5 : getValue(axis);
            return new ManualAxis(name, double.IsFinite(value) ? Math.Clamp(value, 0, 1) * 100 : 50,
                enabled, position =>
                {
                    if (!IsReady || !enabled) return;
                    var range = settings[axis];
                    var target = MathUtils.Lerp(range.Minimum, range.Maximum, position / 100);
                    session.Set(axis, target, Duration);
                });
        }).ToList();
    }
    protected override async void OnViewLoaded()
    {
        base.OnViewLoaded();
        try { await session.Ready.ConfigureAwait(true); IsReady = true; Status = "手动控制已就绪，拖动滑块发送指令。";
            var error = await session.Ended.ConfigureAwait(true);
            IsReady = false;
            Status = error.Message; }
        catch (Exception e) { Status = e.Message; }
    }
    public void Dismiss() => DialogHelper.CloseByModel(this);
    public sealed class ManualAxis(string name, double position, bool enabled, Action<double> changed) : PropertyChangedBase
    {
        public string Name { get; } = name;
        public bool Enabled { get; } = enabled;
        public double Position { get; set; } = position;
        public void OnPositionChanged() => changed(Position);
    }
}
