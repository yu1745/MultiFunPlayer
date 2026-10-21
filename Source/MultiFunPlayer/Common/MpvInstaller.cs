using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MultiFunPlayer.Common;

// Installation is component-wise: a failed FFmpeg download must never remove MPV.
internal sealed class MpvInstaller(HttpClient client, Action<string> report,
    Func<string, string[], Task> run = null)
{
    private const string Repository = "https://github.com/shinchiro/mpv-winbuild-cmake";
    private readonly Func<string, string[], Task> runProcess = run ?? RunProcessAsync;

    public async Task<string> InstallAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var mpv = Path.Combine(directory, "mpv.exe");
        var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        if (await IsInstalledAsync(mpv, "--version"))
            report("MPV 已安装，保留现有文件，跳过下载。");
        else
        {
            report("正在获取 MPV 下载信息…");
            var rss = await client.GetStringAsync("https://sourceforge.net/projects/mpv-player-windows/rss?path=/64bit-v3");
            var name = XDocument.Parse(rss).Descendants("item").Select(item => item.Element("link")?.Value)
                .Where(link => Uri.TryCreate(link, UriKind.Absolute, out _))
                .Select(link => Uri.UnescapeDataString(new Uri(link).AbsolutePath.Split('/').LastOrDefault(part => part.EndsWith(".7z")) ?? ""))
                .FirstOrDefault(name => Regex.IsMatch(name, @"^mpv-x86_64-v3-[\w.-]+\.7z$"));
            if (name == null) throw new InvalidDataException("MPV 下载列表中没有找到 x86_64-v3 安装包。");
            await InstallArchiveAsync(new Uri("https://download.sourceforge.net/mpv-player-windows/" + name), directory, "mpv", "--version");
        }

        if (await IsInstalledAsync(ffmpeg, "-version"))
            report("FFmpeg 已安装，跳过下载。");
        else
        {
            try
            {
                report("正在获取 FFmpeg 下载信息…（GitHub 发布页面）");
                // Public release pages avoid the unauthenticated REST API's shared-IP quota.
                using var latest = await client.GetAsync(Repository + "/releases/latest");
                latest.EnsureSuccessStatusCode();
                var release = latest.RequestMessage?.RequestUri;
                const string prefix = Repository + "/releases/tag/";
                if (release == null || !release.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("无法确定 FFmpeg 的最新发布版本。");
                var tag = Uri.UnescapeDataString(release.AbsoluteUri[prefix.Length..]);
                var html = await client.GetStringAsync(Repository + "/releases/expanded_assets/" + Uri.EscapeDataString(tag));
                await InstallArchiveAsync(FindFfmpegAsset(html), directory, "ffmpeg", "-version");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException)
            {
                report($"GitHub 下载源不可用：{e.Message}");
                report("正在切换到 Gyan FFmpeg 备用源…");
                await InstallArchiveAsync(new Uri("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z"),
                    directory, "ffmpeg", "-version");
            }
        }
        report("MPV 和 FFmpeg 已就绪。");
        return mpv;
    }

    internal static Uri FindFfmpegAsset(string html)
    {
        foreach (Match match in Regex.Matches(html, "href=\"([^\"]+)\""))
        {
            var link = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (Regex.IsMatch(link, @"^/shinchiro/mpv-winbuild-cmake/releases/download/[^/]+/ffmpeg-x86_64-v3-[\w.-]+\.7z$"))
                return new Uri("https://github.com" + link);
        }
        throw new InvalidDataException("FFmpeg 发布页面中没有找到 x86_64-v3 安装包。");
    }

    private async Task<bool> IsInstalledAsync(string executable, string argument)
    {
        if (!File.Exists(executable) || new FileInfo(executable).Length == 0) return false;
        try { await runProcess(executable, [argument]); return true; }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            report($"现有 {Path.GetFileName(executable)} 未通过运行检查，将重新安装。");
            return false;
        }
    }

    private async Task DownloadAsync(Uri uri, string destination)
    {
        // Only fully downloaded files are renamed into the reusable cache.
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            report($"复用已下载的 {Path.GetFileName(destination)}");
            return;
        }
        report("Downloading " + Path.GetFileName(destination));
        var partial = destination + ".partial";
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"下载 {Path.GetFileName(destination)} 失败：HTTP {(int)response.StatusCode}，服务器 {uri.Host}。已完成的组件已保留。", null, response.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await input.CopyToAsync(output, timeout.Token);
            if (output.Length == 0 || (response.Content.Headers.ContentLength is long size && output.Length != size))
                throw new IOException("下载文件不完整；重试时将重新下载此文件。");
        }
        File.Move(partial, destination, overwrite: true);
    }

    private async Task InstallArchiveAsync(Uri uri, string directory, string component, string versionArgument)
    {
        var extractor = Path.Combine(directory, "7zr.exe");
        await DownloadAsync(new Uri("https://www.7-zip.org/a/7zr.exe"), extractor);
        var archive = Path.Combine(directory, Path.GetFileName(uri.AbsolutePath));
        await DownloadAsync(uri, archive);
        var staging = Path.Combine(directory, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        report("Extracting " + Path.GetFileName(archive));
        try
        {
            try { await runProcess(extractor, ["x", "-y", "-o" + staging, archive]); }
            catch
            {
                // A complete HTTP response can still be a corrupt archive. Do not cache it forever.
                File.Delete(archive);
                throw;
            }
            var executable = Directory.EnumerateFiles(staging, component + ".exe", SearchOption.AllDirectories).SingleOrDefault();
            executable ??= Path.Combine(staging, component + ".exe");
            if (!await IsInstalledAsync(executable, versionArgument))
            {
                File.Delete(archive);
                throw new InvalidDataException($"安装包未提供可运行的 {component}.exe。");
            }
            // Gyan uses a versioned top-level folder and bin/, unlike shinchiro.
            var binaries = Path.GetDirectoryName(executable);
            foreach (var file in Directory.EnumerateFiles(binaries, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(directory, Path.GetRelativePath(binaries, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, overwrite: true);
            }
            if (binaries != staging)
            {
                var notices = Path.Combine(directory, "licenses", component);
                Directory.CreateDirectory(notices);
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(binaries)))
                    File.Copy(file, Path.Combine(notices, Path.GetFileName(file)), overwrite: true);
            }
            File.Delete(archive);
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    private static async Task RunProcessAsync(string executable, string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} 执行失败（{process.ExitCode}）：{await error}\n{await output}");
    }
}
