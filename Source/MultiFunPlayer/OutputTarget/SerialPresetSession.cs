using Newtonsoft.Json.Linq;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace MultiFunPlayer.OutputTarget;

// Same machine API as the firmware browser configurator: $id requests,
// COBS-framed CTWA replies with IEEE CRC32 and JSON bodies. Only this session
// owns the port while open; movement and its receiver have already stopped.
internal sealed class SerialPresetSession
{
    private sealed record Request(string Command, TaskCompletionSource<JObject> Completion);
    private readonly Channel<Request> requests = Channel.CreateUnbounded<Request>();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint nextId = (uint)Random.Shared.Next(1, int.MaxValue);
    public Task Ready => ready.Task;
    public Task Finished => finished.Task;
    public void Close() => requests.Writer.TryComplete();
    public void Fail(Exception error)
    {
        ready.TrySetException(error);
        requests.Writer.TryComplete(error);
        while (requests.Reader.TryRead(out var request)) request.Completion.TrySetException(error);
        finished.TrySetResult();
    }

    public async Task<JObject> ExecuteAsync(string command)
    {
        if (command.Contains('\r') || command.Contains('\n') || Encoding.UTF8.GetByteCount(command) > 220)
            throw new ArgumentException("无效的预设命令。");
        await Ready.ConfigureAwait(false);
        var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await requests.Writer.WriteAsync(new(command, completion)).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    public async Task RunAsync(Action<string> write, Func<byte[]> read, CancellationToken token)
    {
        try
        {
            var hello = await ExchangeAsync("hello").ConfigureAwait(false);
            if ((int?)hello["proto"] != 1 || (string)hello["device"] != "sr6-p4")
                throw new IOException("当前设备不支持 P4 预设配置接口。");
            ready.TrySetResult();
            await foreach (var request in requests.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try { request.Completion.TrySetResult(await ExchangeAsync(request.Command).ConfigureAwait(false)); }
                catch (DeviceReplyException error) { request.Completion.TrySetException(error); }
                catch (Exception error) { request.Completion.TrySetException(error); throw; }
            }
        }
        catch (Exception error) { Fail(error); throw; }
        finally { finished.TrySetResult(); }

        async Task<JObject> ExchangeAsync(string command)
        {
            token.ThrowIfCancellationRequested();
            var id = ++nextId;
            var assembler = new ReplyAssembler(id);
            write($"${id} {command}\n");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var reply = assembler.Push(read());
                    if (reply != null) return reply;
                    await Task.Delay(10, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("设备预设命令超时；串口输出已断开。请确认固件支持配置接口，重连后刷新。");
            }
        }
    }

    private sealed class DeviceReplyException(string message) : IOException(message);

    private sealed class ReplyAssembler(uint id)
    {
        private readonly List<byte> encoded = [];
        private byte[][] chunks;
        private byte status;
        private bool discard;

        public JObject Push(byte[] bytes)
        {
            foreach (var value in bytes)
            {
                if (value != 0)
                {
                    if (!discard) encoded.Add(value);
                    if (encoded.Count > 2048) { encoded.Clear(); discard = true; }
                    continue;
                }
                var data = discard ? null : Decode(encoded);
                encoded.Clear();
                discard = false;
                if (data == null || data.Length < 18 || !data.AsSpan(0, 4).SequenceEqual("CTWA"u8) ||
                    data[4] != 1 || data[5] > 1 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10)) != id)
                    continue;
                if (Crc(data.AsSpan(0, data.Length - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4)))
                    continue;
                var index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
                if (count == 0 || count > 64 || index >= count || data.Length > 1042)
                    throw new IOException("设备配置分包无效。");
                if (chunks == null) { chunks = new byte[count][]; status = data[5]; }
                if (chunks.Length != count || status != data[5]) throw new IOException("设备配置分包不一致。");
                var part = data[14..^4];
                if (chunks[index] != null && !chunks[index].AsSpan().SequenceEqual(part)) throw new IOException("设备配置重复分包不一致。");
                chunks[index] = part;
                if (chunks.Any(c => c == null)) continue;
                var body = JObject.Parse(new UTF8Encoding(false, true).GetString(chunks.SelectMany(c => c).ToArray()));
                if (status != 0) throw new DeviceReplyException((string)body["error"] ?? (string)body["text"] ?? "设备拒绝预设操作。");
                return body;
            }
            return null;
        }

        private static byte[] Decode(List<byte> input)
        {
            if (input.Count == 0) return null;
            var output = new List<byte>();
            for (var at = 0; at < input.Count;)
            {
                var code = input[at++];
                if (code == 0 || at + code - 1 > input.Count) return null;
                for (var i = 1; i < code; i++) output.Add(input[at++]);
                if (code < 255 && at < input.Count) output.Add(0);
            }
            return output.ToArray();
        }
        private static uint Crc(ReadOnlySpan<byte> data)
        {
            uint crc = 0xffffffff;
            foreach (var value in data)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            return ~crc;
        }
    }
}
