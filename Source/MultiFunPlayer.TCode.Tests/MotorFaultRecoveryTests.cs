using MultiFunPlayer.OutputTarget;
using System.IO;

public class MotorFaultRecoveryTests
{
    [Fact]
    public async Task HealthyDoesNotResetOrDisable()
    {
        var commands = new List<string>();
        var result = await MotorFaultRecovery.RunAsync(c => { commands.Add(c); return Task.FromResult(c == "recover" ? "OK recovery requested" : "DONE already healthy; output unchanged"); }, _=>{});
        Assert.Contains("设备正常", result);
        Assert.Equal(new[]{"recover", "recover status"}, commands);
    }
    [Fact]
    public async Task WaitsForDoneWithoutResendingRecovery()
    {
        var replies = new Queue<string>(new[]{"OK recovery requested", "PENDING evidence saved; checking motors", "RECOVERING restoring hold at current encoder position", "DONE holding current"});
        var commands = new List<string>();
        var result = await MotorFaultRecovery.RunAsync(c => { commands.Add(c); return Task.FromResult(replies.Dequeue()); }, _=>{}, ()=>Task.CompletedTask);
        Assert.Contains("手动恢复播放", result);
        Assert.Equal(1,commands.Count(c=>c=="recover"));
    }
    [Theory]
    [InlineData("ERR unknown command")]
    [InlineData("ERR archive failed")]
    public async Task RefusedDoesNotFallback(string response)
    {
        var calls=0;
        await Assert.ThrowsAsync<IOException>(()=>MotorFaultRecovery.RunAsync(c=>{calls++;return Task.FromResult(response);},_=>{}));
        Assert.Equal(1,calls);
    }
    [Theory]
    [InlineData("FAILED voltage unsafe")]
    [InlineData("IDLE")]
    public async Task FailureIsNotSuccess(string response)
    {
        await Assert.ThrowsAsync<IOException>(()=>MotorFaultRecovery.RunAsync(c=>Task.FromResult(c=="recover"?"OK recovery requested":response),_=>{}));
    }
    [Fact]
    public async Task TimeoutDoesNotRetryMutation()
    {
        var commands=new List<string>();
        await Assert.ThrowsAsync<TimeoutException>(()=>MotorFaultRecovery.RunAsync(c=>{commands.Add(c);return Task.FromResult(c=="recover"?"OK recovery requested":"RECOVERING waiting");},_=>{},()=>Task.CompletedTask,2));
        Assert.Equal(1,commands.Count(c=>c=="recover"));
    }
}
