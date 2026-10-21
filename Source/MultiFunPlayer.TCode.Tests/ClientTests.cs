using MultiFunPlayer.OutputTarget.TCodeBuffered;
using System.Threading.Channels;

namespace MultiFunPlayer.TCode.Tests;

public class ClientTests
{
    private static readonly BufferedSessionLimits Limits = new(new(1, 4, 20), new(1, 4, 20));
    [Fact]
    public async Task StartupAllowsBootBeyondNormalRetryBudget()
    {
        var link = new FakeLink { DropWrites = 4, FragmentReplies = true };
        await using var client = link.Client();
        await client.SynchronizeAsync(CancellationToken.None);
        Assert.Equal(10, link.Writes.Count);
        Assert.All(link.Writes.Take(5), wire => Assert.Equal(link.Writes[0], wire));
        Assert.Equal(99UL, client.Boot);
        Assert.False(client.MayOwnMotion);
        link.DropWrites = 3;
        var before = link.Writes.Count;
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(0, CancellationToken.None));
        Assert.Equal(3, link.Writes.Count - before);
    }

    [Fact]
    public async Task PoseQueryDoesNotAdvanceMutationSequence()
    {
        var link = new FakeLink();
        await using var client = link.Client();
        await client.SendAsync(0, CancellationToken.None);
        var pose = await client.QueryPoseAsync(CancellationToken.None);
        Assert.All(pose.Positions, position => Assert.Equal(.5, position));
        Assert.Equal(22, client.Status.Length);
        await client.BeginAsync(1, 4000000, Limits, CancellationToken.None);
        Assert.True(TCodeBufferedProtocol.TryDecode(link.Writes[^1], out var begin));
        Assert.Equal(new ulong[] { 1, 1, 99, 0, 1, 4000000, 1, 1000000, 4000000, 20000000, 1000000, 4000000, 20000000 }, begin);
    }

    [Fact]
    public async Task UnsupportedProfileFailsBeforeAcquiringDeviceMotion()
    {
        var link = new FakeLink { Profile = 2 };
        await using var client = link.Client();
        await client.SendAsync(0, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.QueryPoseAsync(CancellationToken.None));
        Assert.False(client.MayOwnMotion);
        Assert.Equal(0UL, link.Session);
    }

    [Fact]
    public async Task LostAckRetriesIdenticalFrameAndAcceptsFragmentedReply()
    {
        var link = new FakeLink { DropWrites = 1, FragmentReplies = true };
        var diagnostics = new List<string>();
        await using var client = link.Client(diagnostics.Add);
        await client.SendAsync(0, CancellationToken.None);
        Assert.Equal(2, link.Writes.Count);
        Assert.Equal(link.Writes[0], link.Writes[1]);
        Assert.Equal(99UL, client.Boot);
        Assert.Contains(diagnostics, line => line.Contains("ACK TIMEOUT op=0"));
        Assert.Contains(diagnostics, line => line.StartsWith("ACK op=0") && line.Contains("rttMs="));
    }

    [Fact]
    public async Task LostBeginAcknowledgementsCanStillRecoverAndStopOwnedSession()
    {
        var link = new FakeLink();
        await using var client = link.Client();
        await client.SendAsync(0, CancellationToken.None);
        await client.QueryPoseAsync(CancellationToken.None);
        link.DropWrites = 3;
        await Assert.ThrowsAsync<IOException>(() => client.BeginAsync(1, 4000000, Limits, CancellationToken.None));
        Assert.True(client.MayOwnMotion);
        await client.StopAsync(CancellationToken.None);
        Assert.False(client.MayOwnMotion);
        Assert.Equal(0UL, link.State);
        Assert.Equal(1UL, client.Session);
    }

    [Fact]
    public async Task RebootAndForeignSessionAreRejected()
    {
        var link = new FakeLink();
        await using var client = link.Client();
        await client.SendAsync(0, CancellationToken.None);
        link.Boot++;
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(0, CancellationToken.None));
        link.Boot--;
        link.Session++;
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task BadAckStillAttemptsStopOnWire()
    {
        var link = new FakeLink();
        await using var client = link.Client();
        await client.SendAsync(0, CancellationToken.None);
        await client.QueryPoseAsync(CancellationToken.None);
        await client.BeginAsync(1, 4000000, Limits, CancellationToken.None);
        link.CorruptReplies = true;
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(0, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => client.StopAsync(CancellationToken.None));
        Assert.True(TCodeBufferedProtocol.TryDecode(link.Writes[^1], out var stop));
        Assert.Equal(5UL, stop[0]);
    }

    [Fact]
    public async Task WrongReplyIdCannotAcknowledgeTransaction()
    {
        var link = new FakeLink { WrongRequestId = true };
        await using var client = link.Client();
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(0, CancellationToken.None));
        Assert.Equal(3, link.Writes.Count);
    }

    private sealed class FakeLink
    {
        private readonly Channel<string> _received = Channel.CreateUnbounded<string>();
        private string _lastBegin;
        public List<string> Writes { get; } = [];
        public ulong Boot { get; set; } = 99;
        public ulong Session { get; set; }
        public ulong State { get; set; }
        public int DropWrites { get; set; }
        public bool FragmentReplies { get; set; }
        public bool CorruptReplies { get; set; }
        public bool WrongRequestId { get; set; }
        public ulong Profile { get; set; } = 1;
        public TCodeBufferedClient Client(Action<string> diagnostic = null) => new(Write, async ct => await _received.Reader.ReadAsync(ct), TimeSpan.FromMilliseconds(25), diagnostic);

        private Task Write(string wire, CancellationToken token)
        {
            Writes.Add(wire);
            Assert.True(TCodeBufferedProtocol.TryDecode(wire, out var f));
            if (f[0] == 1 && wire != _lastBegin)
            {
                Assert.Equal(13, f.Length);
                Assert.Equal(Limits.Encode(), f.Skip(7));
                Session++;
                State = 1;
                _lastBegin = wire;
            }
            if (f[0] == 5) State = 0;
            if (DropWrites > 0) { DropWrites--; return Task.CompletedTask; }
            ulong[] reply = [100, WrongRequestId ? f[1] + 1 : f[1], Boot, Session, 0, State, 1000000, 1, 512,
                512, 512, 512, 512, 512, 512, 0, 0, 0, 0, 0, 0, 0];
            if (f[0] == 6)
                reply = [101, f[1], Boot, Session, 0, State, Profile, 500000, 500000, 500000, 500000, 500000, 500000];
            var response = TCodeBufferedProtocol.Encode(reply);
            if (CorruptReplies) response = response.Replace("!4,100,", "!4,101,");
            if (FragmentReplies)
            {
                _received.Writer.TryWrite("boot log\r\nsr6> " + response[..7]);
                _received.Writer.TryWrite(response[7..21]);
                _received.Writer.TryWrite(response[21..]);
            }
            else _received.Writer.TryWrite(response);
            return Task.CompletedTask;
        }
    }
}
