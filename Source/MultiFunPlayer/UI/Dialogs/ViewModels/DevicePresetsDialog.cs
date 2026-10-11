using MultiFunPlayer.OutputTarget;
using Newtonsoft.Json.Linq;
using Stylet;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace MultiFunPlayer.UI.Dialogs.ViewModels;

internal sealed class DevicePresetsDialog(SerialPresetSession session) : Screen
{
    public sealed record Preset(string Name, string Velocity, string Acceleration, string Jerk, string Match);
    private readonly string[] lastLoaded = new string[2];
    private bool started;
    public bool IsBusy { get; private set; }
    public bool IsReady { get; private set; }
    public bool CanEdit => IsReady && !IsBusy;
    public bool CanDismiss => !IsBusy;
    public bool CanLoad => CanEdit && SelectedPreset != null;
    public bool CanDelete => CanLoad && ConfirmDelete;
    public int GroupIndex { get; set; }
    public string[] Groups { get; } = ["平移 · L0 / L1 / L2", "旋转 · R0 / R1 / R2"];
    private string Root => GroupIndex == 0 ? "Lpreset" : "Rpreset";
    public ObservableCollection<Preset> Presets { get; } = [];
    public Preset SelectedPreset { get; set; }
    public string Current { get; private set; } = "尚未读取";
    public string Velocity { get; set; }
    public string Acceleration { get; set; }
    public string Jerk { get; set; }
    public string PresetName { get; set; }
    public bool Persist { get; set; }
    public bool Replace { get; set; }
    public bool ConfirmDelete { get; set; }
    public string Status { get; private set; } = "正在暂停输出并连接设备预设…";
    public string Log { get; private set; } = "";

    protected override async void OnViewLoaded()
    {
        base.OnViewLoaded();
        if (started) return;
        started = true;
        await Run(async () =>
        {
            await session.Ready.ConfigureAwait(true);
            IsReady = true;
            await ReadPresets().ConfigureAwait(true);
            Status = "已读取设备预设。";
        }).ConfigureAwait(true);
    }

    public async void OnGroupIndexChanged()
    {
        ConfirmDelete = false;
        Replace = false;
        PresetName = "";
        if (CanEdit) await RefreshPresets().ConfigureAwait(true);
    }
    public void OnSelectedPresetChanged()
    {
        ConfirmDelete = false;
        Replace = false;
        if (SelectedPreset != null) PresetName = SelectedPreset.Name;
    }
    public Task RefreshPresets() => Run(async () =>
    {
        await ReadPresets().ConfigureAwait(true);
        Status = "已刷新设备当前参数和预设。";
    });
    public Task Load() => Run(async () =>
    {
        if (SelectedPreset == null) return;
        await Command($"{Root} load {ValidateName(SelectedPreset.Name)}{(Persist ? " persist" : "")}").ConfigureAwait(true);
        lastLoaded[GroupIndex] = SelectedPreset.Name;
        await ReadPresets().ConfigureAwait(true);
        Status = Persist ? "已应用预设，并保存为开机参数。" : "已应用预设，仅本次运行生效。";
    });
    public Task Save() => Run(async () =>
    {
        await Command($"{Root} save {ValidateName(PresetName)}{(Replace ? " replace" : "")}").ConfigureAwait(true);
        lastLoaded[GroupIndex] = PresetName;
        await ReadPresets().ConfigureAwait(true);
        Replace = false;
        Status = "已将设备当前参数保存为预设。";
    });
    public Task Delete() => Run(async () =>
    {
        if (SelectedPreset == null || !ConfirmDelete) return;
        await Command($"{Root} delete {ValidateName(SelectedPreset.Name)}").ConfigureAwait(true);
        ConfirmDelete = false;
        await ReadPresets().ConfigureAwait(true);
        Status = "已删除预设；设备当前参数保持不变。";
    });
    public Task ApplyValues() => WriteValues(false);
    public Task ApplyStartupValues() => WriteValues(true);
    private Task WriteValues(bool persist) => Run(async () =>
    {
        var v = Number(Velocity, 100, "速度");
        var a = Number(Acceleration, 1000, "加速度");
        var j = Number(Jerk, GroupIndex == 0 ? 38000 : 22000, "加加速度");
        var prefix = GroupIndex == 0 ? "TRANSLATION" : "ROTATION";
        var verb = persist ? "set" : "settemp";
        try
        {
            await Request($"{verb} TRAJECTORY_{prefix}_MAX_VELOCITY {v}").ConfigureAwait(true);
            await Request($"{verb} TRAJECTORY_{prefix}_MAX_ACCELERATION {a}").ConfigureAwait(true);
            await Request($"{verb} TRAJECTORY_{prefix}_MAX_JERK {j}").ConfigureAwait(true);
        }
        catch (Exception e)
        {
            // Firmware exposes three individual writes, so report partial
            // application explicitly and reread rather than inventing rollback.
            try { await ReadPresets().ConfigureAwait(true); } catch { }
            throw new IOException("参数可能仅部分生效，请以设备回读为准。" + e.Message, e);
        }
        await ReadPresets().ConfigureAwait(true);
        Status = persist ? "已应用 V/A/J 并保存为开机参数。" : "已应用 V/A/J，仅本次运行生效。可将设备当前值保存为预设。";
    });

    private async Task ReadPresets()
    {
        Presets.Clear();
        SelectedPreset = null;
        Current = "正在读取…";
        var group = GroupIndex == 0 ? "L" : "R";
        var response = await Request("presets " + group).ConfigureAwait(true);
        if ((string)response["group"] != group || response["current"] is not JObject current ||
            response["presets"] is not JArray entries || (int?)response["max"] != 32 || entries.Count > 32)
            throw new IOException("设备预设响应格式不兼容。");
        var live = ReadLimits(current);
        var parsed = entries.Select(entry =>
        {
            var name = ValidateName((string)entry["name"]);
            if (entry["limits"] is not JObject limits) throw new IOException("设备预设参数缺失。");
            return (Name: name, Limits: ReadLimits(limits));
        }).ToArray();
        if (parsed.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != parsed.Length)
            throw new IOException("设备返回重复的预设名称。");
        var matching = parsed.Where(p => Same(p.Limits, live)).Select(p => p.Name).ToArray();
        var loaded = matching.Contains(lastLoaded[GroupIndex]) ? lastLoaded[GroupIndex]
            : matching.Length == 1 ? matching[0] : null;
        Current = $"V={live[0]:G} · A={live[1]:G} · J={live[2]:G}";
        Velocity = Format(live[0]); Acceleration = Format(live[1]); Jerk = Format(live[2]);
        foreach (var entry in parsed)
            Presets.Add(new(entry.Name, Format(entry.Limits[0]), Format(entry.Limits[1]), Format(entry.Limits[2]),
                entry.Name == loaded ? "当前" : matching.Contains(entry.Name) ? "同值" : ""));

        static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);
        static bool Same(double[] a, double[] b) => a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= 1e-6 * Math.Max(1, p.First));
        static double[] ReadLimits(JObject limits)
        {
            var values = new[] { (double?)limits["v"], (double?)limits["a"], (double?)limits["j"] };
            if (values.Any(v => !v.HasValue || !double.IsFinite(v.Value) || v <= 0))
                throw new IOException("设备预设参数无效。");
            return values.Select(v => v.Value).ToArray();
        }
    }

    private async Task<string> Command(string command)
    {
        var body = await Request("sh " + command).ConfigureAwait(true);
        var text = (string)body["text"] ?? throw new IOException("设备未返回操作结果。");
        if (!text.StartsWith("OK", StringComparison.Ordinal)) throw new IOException(text);
        return text;
    }
    private async Task<JObject> Request(string command)
    {
        var response = await session.ExecuteAsync(command).ConfigureAwait(true);
        Log = Log + "> " + command + "\n" + response.ToString(Newtonsoft.Json.Formatting.None) + "\n";
        if (Log.Length > 16000) Log = Log[^16000..];
        return response;
    }

    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action().ConfigureAwait(true); }
        catch (Exception e) { Status = e.Message; }
        finally { IsBusy = false; }
    }
    private static string ValidateName(string value)
    {
        if (string.IsNullOrEmpty(value) || new UTF8Encoding(false, true).GetByteCount(value) > 32 ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c < 160 && !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("预设名需为 1–32 个 UTF-8 字节；支持中文、字母、数字、下划线和短横线，不允许空格。");
        return value;
    }
    private static string Number(string text, double maximum, string name)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value < .001 || value > maximum)
            throw new ArgumentException($"{name}范围为 0.001–{maximum}，小数请使用小数点。");
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }
    public void Dismiss() { if (CanDismiss) DialogHelper.CloseByModel(this); }
}
