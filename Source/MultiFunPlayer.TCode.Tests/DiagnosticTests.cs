using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Threading.Channels;

namespace MultiFunPlayer.TCode.Tests;

public class DiagnosticTests
{
    [Fact]
    public async Task FragmentedFaultEventIsLoggedBeforeAckAndExplainsPauseFailure()
    {
        var link = new Link();
        var logs = new List<string>();
        await using var client = link.Client(logs.Add);
        await client.SendAsync(0, CancellationToken.None);
        await client.EnableDeviceDiagnosticsAsync(CancellationToken.None);
        Assert.True(client.DeviceDiagnosticsEnabled);
        link.Fault = true;
        await client.SendAsync(0, CancellationToken.None);
        var error = client.DeviceStateException("P4 paused trajectory failed before resume.");
        Assert.Contains("暂停收停计算失败", error.Message);
        Assert.Contains("ruckig_solver", error.Message);
        Assert.Contains("solverResult=-1000", error.Message);
        Assert.Contains("underruns=0", error.Message);
        Assert.Contains(logs, line => line.StartsWith("DEVICE ERROR") && line.Contains("reason=22"));
        Assert.Equal(5UL, client.Status[5]);
        Assert.Equal(22, client.Status.Length);
    }

    [Fact]
    public async Task RejectedBeginIncludesMotorReadinessInException()
    {
        var link = new Link { RejectBegin = true };
        await using var client = link.Client();
        await client.SendAsync(0, CancellationToken.None);
        await client.EnableDeviceDiagnosticsAsync(CancellationToken.None);
        var error = await Assert.ThrowsAsync<TCodeBufferedException>(() => client.BeginAsync(1, 4000000,
            new(new(1, 4, 20), new(1, 4, 20)), CancellationToken.None));
        Assert.Equal(8UL, error.Code);
        Assert.Contains("反馈缺失或超过100ms", error.Message);
        Assert.Contains("axis/motor=M2", error.Message);
        Assert.Contains("feedbackAgeMs=150", error.Message);
        Assert.Contains("设备未就绪", error.Message);
    }

    [Fact]
    public async Task LegacyFirmwareContinuesWithoutDiagnostics()
    {
        var link = new Link { Supported = false };
        await using var client = link.Client();
        await client.SendAsync(0, CancellationToken.None);
        await client.EnableDeviceDiagnosticsAsync(CancellationToken.None);
        Assert.False(client.DeviceDiagnosticsEnabled);
        await client.SendAsync(0, CancellationToken.None);
        Assert.Equal(0UL, client.Status[5]);
    }

    [Fact]
    public void DiagnosticFloatBitsPreserveTinyResidualsNegativeNumbersAndNaN()
    {
        var fields = Link.Event(0, 24, 0, 0, 0, [-.000001f, float.NaN, float.PositiveInfinity, -1000, .001f, 0]);
        var diagnostic = TCodeDeviceDiagnostic.Decode(fields);
        Assert.Equal(-.000001f, diagnostic.Values[0]);
        Assert.True(float.IsNaN(diagnostic.Values[1]));
        Assert.True(float.IsPositiveInfinity(diagnostic.Values[2]));
        Assert.Equal(-1000, diagnostic.Values[3]);
    }

    [Fact]
    public void MotorErrorBitmaskDoesNotLoseBitsThroughFloatConversion()
    {
        var diagnostic = TCodeDeviceDiagnostic.Decode(Link.Event(1, 26, ((ulong)uint.MaxValue << 9) | 256, 7, 1,
            [0, 10, 8, (float)uint.MaxValue, .5f, 0]));
        Assert.Contains("axisError=4294967295", diagnostic.Message);
        Assert.Contains("电机报错", diagnostic.Message);
    }

    private sealed class Link
    {
        private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
        public bool Supported = true, Fault, RejectBegin;
        public TCodeBufferedClient Client(Action<string> log = null) => new(Write,
            async token => await _received.Reader.ReadAsync(token), TimeSpan.FromMilliseconds(100), log);
        public static ulong[] Event(ulong request, ulong reason, ulong detail, ulong motor, ulong operation, float[] values)
            => new ulong[] { 105, request, 99, 0, 1, 1000000, reason == 22 ? 5UL : 0, reason, 255, motor, operation, detail }
                .Concat(values.Select(v => (ulong)unchecked((uint)BitConverter.SingleToInt32Bits(v)))).ToArray();
        private Task Write(string text, CancellationToken token)
        {
            Assert.True(TCodeBufferedProtocol.TryDecode(text, out var f));
            var reply = new ulong[] { 100, f[1], 99, 0, f[0] == 1 && RejectBegin ? 8UL : 0, Fault ? 5UL : 0,
                1000000, 55, 512, 512, 512, 512, 512, 512, 512, 8000000, 8000000, 8000000, 0, 8000000, 8000000, 0 };
            if (f[0] == 11 && Supported) reply = [104, f[1], 99, 0, 0, 0, 1, 32, 0, 1];
            var prefix = "";
            if (f[0] == 0 && Fault) prefix = TCodeBufferedProtocol.Encode(Event(0, 22, 11, 0, 0, [0, 0, .001428f, -1000, 3, 0]));
            if (f[0] == 1 && RejectBegin)
                prefix = TCodeBufferedProtocol.Encode(Event(f[1], 26, 64, 2, 1, [0, 150, 8, 0, .5f, 0]))
                    + TCodeBufferedProtocol.Encode(Event(f[1], 8, 64, 0, 1, [6, 3, 63, 63, 2, 0]));
            var wire = prefix + TCodeBufferedProtocol.Encode(reply);
            _received.Writer.TryWrite(wire[..5]);
            _received.Writer.TryWrite(wire[5..]);
            return Task.CompletedTask;
        }
    }
}
