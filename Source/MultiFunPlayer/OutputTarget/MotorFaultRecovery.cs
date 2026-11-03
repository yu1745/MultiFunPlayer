using System.IO;

namespace MultiFunPlayer.OutputTarget;

// No retry of the mutating request and no fallback to reload/reset/disable.
internal static class MotorFaultRecovery
{
    public static async Task<string> RunAsync(Func<string, Task<string>> command,
        Action<string> report, Func<Task> delay = null, int polls = 60)
    {
        delay ??= () => Task.Delay(500);
        var accepted = (await command("recover").ConfigureAwait(false)).Trim();
        report(accepted);
        if (!accepted.StartsWith("OK ", StringComparison.Ordinal))
            throw new IOException("设备未接受重载：" + accepted);
        for (var i = 0; i < polls; i++)
        {
            var state = (await command("recover status").ConfigureAwait(false)).Trim();
            report(state);
            if (state.StartsWith("DONE ", StringComparison.Ordinal))
                return state.Contains("already healthy", StringComparison.Ordinal)
                    ? "设备正常，保持原有出力；播放已暂停。"
                    : "重载完成，保持当前位置；请手动恢复播放。";
            if (state.StartsWith("FAILED", StringComparison.Ordinal) || state.StartsWith("ERR", StringComparison.Ordinal))
                throw new IOException("重载失败：" + state);
            if (!state.StartsWith("PENDING ", StringComparison.Ordinal) && !state.StartsWith("RECOVERING ", StringComparison.Ordinal))
                throw new IOException("未能确认重载状态：" + state);
            await delay().ConfigureAwait(false);
        }
        throw new TimeoutException("重载结果超时，未自动重试。请保持暂停并检查设备状态。");
    }
}
