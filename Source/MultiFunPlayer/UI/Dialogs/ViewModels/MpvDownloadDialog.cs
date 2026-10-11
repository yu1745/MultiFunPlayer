using Stylet;
using System.Diagnostics;
using System.IO;

namespace MultiFunPlayer.UI.Dialogs.ViewModels;

internal sealed class MpvDownloadDialog(Func<MpvDownloadDialog, Task> download) : Screen
{
    private bool started;
    private string downloadFile;
    private readonly Stopwatch downloadTimer = new();
    private long previousBytes;
    private double previousSeconds;
    private double lastDataSeconds;
    public bool IsBusy { get; private set; } = true;
    public bool CanRetry => !IsBusy && !Succeeded;
    public bool CanDismiss => !IsBusy;
    public bool Succeeded { get; private set; }
    public string Status { get; private set; } = "正在准备下载…";
    public string ProgressText { get; private set; }
    public string Log { get; private set; } = "";

    protected override void OnViewLoaded()
    {
        base.OnViewLoaded();
        if (started)
            return;
        started = true;
        OnRetry();
    }

    public void Report(string message) => Execute.OnUIThreadSync(() =>
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        var trimmed = message.Trim();
        if (trimmed.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase))
        {
            BeginDownloadCore(trimmed[12..].Trim());
        }
        else if (trimmed.StartsWith("Extracting ", StringComparison.OrdinalIgnoreCase))
        {
            downloadFile = null;
            Status = "正在解压安装包…";
            ProgressText = "下载完成，正在解压，请稍候…";
        }
        else if (downloadFile == null)
        {
            Status = message;
        }
        Log += message + Environment.NewLine;
        if (Log.Length > 24000)
            Log = Log[^24000..];
    });

    public void BeginDownload(string fileName) => Execute.OnUIThreadSync(() => BeginDownloadCore(fileName));

    private void BeginDownloadCore(string fileName)
    {
        downloadFile = fileName;
        previousBytes = 0;
        previousSeconds = 0;
        lastDataSeconds = 0;
        downloadTimer.Restart();
        Status = $"正在下载 {fileName}";
        ProgressText = "正在连接下载服务器…（已等待 0 秒）";
    }

    public void UpdateDownloadProgress(string directory) => Execute.OnUIThreadSync(() =>
    {
        if (downloadFile == null)
            return;

        var fileName = Path.GetFileName(downloadFile);
        var file = new FileInfo(Path.Combine(directory, fileName));
        long bytes;
        try { bytes = file.Exists ? file.Length : previousBytes; }
        catch (IOException) { return; }
        var seconds = downloadTimer.Elapsed.TotalSeconds;
        if (bytes == 0)
        {
            ProgressText = $"正在连接下载服务器…（已等待 {seconds:F0} 秒）";
        }
        else
        {
            var speed = Math.Max(0, bytes - previousBytes) / Math.Max(0.001, seconds - previousSeconds);
            if (bytes > previousBytes)
                lastDataSeconds = seconds;
            ProgressText = $"已下载 {bytes / 1048576.0:F1} MB · {speed / 1048576.0:F1} MB/s";
            if (seconds - lastDataSeconds >= 3)
                ProgressText += $" · 等待数据 {seconds - lastDataSeconds:F0} 秒";
        }
        previousBytes = bytes;
        previousSeconds = seconds;
    });

    public async void OnRetry()
    {
        IsBusy = true;
        Log = "";
        downloadFile = null;
        ProgressText = "正在获取下载信息…";
        try
        {
            await Task.Run(() => download(this));
            Execute.OnUIThreadSync(() =>
            {
                Succeeded = true;
                IsBusy = false;
                DialogHelper.CloseByModel(this);
            });
        }
        catch (Exception e)
        {
            Report(e.ToString());
            Execute.OnUIThreadSync(() =>
            {
                Status = "下载失败，请查看下方错误信息。";
                IsBusy = false;
            });
        }
    }

    public void OnDismiss()
    {
        if (CanDismiss)
            DialogHelper.CloseByModel(this);
    }
}
