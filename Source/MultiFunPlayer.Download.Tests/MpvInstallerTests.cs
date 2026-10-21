using MultiFunPlayer.Common;
using System.Net;

namespace MultiFunPlayer.Download.Tests;

public class MpvInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mfp-download-test-" + Guid.NewGuid().ToString("N"));
    private const string Assets = "<a href=\"/shinchiro/mpv-winbuild-cmake/releases/download/20261021/ffmpeg-x86_64-v3-git-abc.7z\">download</a>";
    public MpvInstallerTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }
    private Task FakeRun(string executable, string[] args)
    {
        if (args[0] == "x")
        {
            var staging = args.Single(s => s.StartsWith("-o"))[2..];
            File.WriteAllText(Path.Combine(staging, "ffmpeg.exe"), "complete");
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FailedFfmpegRetryPreservesMpvAndSkipsItsDownload()
    {
        File.WriteAllText(Path.Combine(root, "mpv.exe"), "existing-mpv");
        var fail = true;
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri.AbsoluteUri;
            requests.Add(url);
            Assert.DoesNotContain("api.github.com", url);
            Assert.DoesNotContain("sourceforge", url);
            if (url.EndsWith("/latest"))
            {
                var response = Response("release");
                response.RequestMessage = new(HttpMethod.Get, "https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261021");
                return response;
            }
            if (url.Contains("expanded_assets")) return Response(Assets);
            if (url.EndsWith(".7z") && fail) return Response("forbidden", HttpStatusCode.Forbidden);
            return Response("complete archive or extractor");
        }));
        var installer = new MpvInstaller(client, _ => { }, FakeRun);
        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(root));
        Assert.Equal("existing-mpv", File.ReadAllText(Path.Combine(root, "mpv.exe")));
        fail = false;
        await installer.InstallAsync(root);
        Assert.Equal(1, requests.Count(url => url.EndsWith("7zr.exe")));
        Assert.True(File.Exists(Path.Combine(root, "ffmpeg.exe")));
        var count = requests.Count;
        await installer.InstallAsync(root);
        Assert.Equal(count, requests.Count);
    }

    [Fact]
    public async Task CompletedInstallRequiresNoNetwork()
    {
        foreach (var name in new[] { "mpv.exe", "ffmpeg.exe" }) File.WriteAllText(Path.Combine(root, name), "complete");
        using var client = new HttpClient(new Handler(_ => throw new Exception("Network must not be used")));
        Assert.Equal(Path.Combine(root, "mpv.exe"), await new MpvInstaller(client, _ => { }, FakeRun).InstallAsync(root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GithubMetadataOrArchiveFailureFallsBackToGyan(bool failMetadata)
    {
        File.WriteAllText(Path.Combine(root, "mpv.exe"), "existing-mpv");
        var log = new List<string>();
        var usedFallback = false;
        using var client = new HttpClient(new Handler(request =>
        {
            var url = request.RequestUri.AbsoluteUri;
            if (url.EndsWith("/latest"))
            {
                if (failMetadata) return Response("forbidden", HttpStatusCode.Forbidden);
                var response = Response("release");
                response.RequestMessage = new(HttpMethod.Get, "https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261021");
                return response;
            }
            if (url.Contains("expanded_assets")) return Response(Assets);
            if (request.RequestUri.Host == "github.com") return Response("forbidden", HttpStatusCode.Forbidden);
            if (request.RequestUri.Host == "www.gyan.dev") usedFallback = true;
            return Response("complete");
        }));
        await new MpvInstaller(client, log.Add, (executable, args) =>
        {
            if (args[0] == "x")
            {
                var staging = args.Single(s => s.StartsWith("-o"))[2..];
                var bin = Path.Combine(staging, "ffmpeg-release", "bin");
                Directory.CreateDirectory(bin);
                File.WriteAllText(Path.Combine(bin, "ffmpeg.exe"), "complete");
                File.WriteAllText(Path.Combine(staging, "ffmpeg-release", "LICENSE"), "license");
            }
            return Task.CompletedTask;
        }).InstallAsync(root);
        Assert.True(usedFallback);
        Assert.True(File.Exists(Path.Combine(root, "ffmpeg.exe")));
        Assert.Equal("existing-mpv", File.ReadAllText(Path.Combine(root, "mpv.exe")));
        Assert.Contains(log, line => line.Contains("切换到 Gyan"));
        Assert.True(File.Exists(Path.Combine(root, "licenses", "ffmpeg", "LICENSE")));
    }

    [Theory]
    [InlineData("<a href=\"https://evil.example/ffmpeg-x86_64-v3-abc.7z\">x</a>")]
    [InlineData("<a href=\"/shinchiro/mpv-winbuild-cmake/releases/download/v1/ffmpeg-i686-abc.7z\">x</a>")]
    public void RejectsWrongHostOrArchitecture(string html)
        => Assert.Throws<InvalidDataException>(() => MpvInstaller.FindFfmpegAsset(html));

    [Fact]
    public void SelectsExpectedReleaseAsset()
        => Assert.Equal("https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261021/ffmpeg-x86_64-v3-git-abc.7z",
            MpvInstaller.FindFfmpegAsset(Assets).AbsoluteUri);

    [LiveInstallFact]
    public async Task LiveInstallReusesExistingMpv()
    {
        var directory = Environment.GetEnvironmentVariable("MFP_LIVE_INSTALL_ROOT");
        static async Task<byte[]> Hash(string path)
        {
            await using var input = File.OpenRead(path);
            return await System.Security.Cryptography.SHA256.HashDataAsync(input);
        }
        var original = await Hash(Path.Combine(directory, "mpv.exe"));
        using var client = new HttpClient(new ForceGithubFailureHandler()) { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MultiFunPlayer/1.0");
        var log = new List<string>();
        try { await new MpvInstaller(client, message => { log.Add(message); Console.WriteLine(message); }).InstallAsync(directory); }
        finally { await File.WriteAllLinesAsync(Path.Combine(directory, "install-verification.log"), log); }
        Assert.Equal(original, await Hash(Path.Combine(directory, "mpv.exe")));
        Assert.DoesNotContain(log, message => message.StartsWith("Downloading mpv-"));
        Assert.True(File.Exists(Path.Combine(directory, "ffmpeg.exe")));
    }

    private sealed class ForceGithubFailureHandler : DelegatingHandler
    {
        public ForceGithubFailureHandler() : base(new HttpClientHandler()) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Environment.GetEnvironmentVariable("MFP_LIVE_FORCE_GITHUB_FAILURE") == "1" && request.RequestUri.Host == "github.com")
                return Task.FromResult(Response("Simulated GitHub rate limit", HttpStatusCode.Forbidden));
            return base.SendAsync(request, token);
        }
    }
}

public sealed class LiveInstallFactAttribute : FactAttribute
{
    public LiveInstallFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MFP_LIVE_INSTALL_ROOT")))
            Skip = "Set MFP_LIVE_INSTALL_ROOT to explicitly verify a real installation.";
    }
}
