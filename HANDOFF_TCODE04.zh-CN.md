# MFP 串口 TCode 0.4 交接

日期：2026-10-11。

本次已为 MultiFunPlayer 添加第三种更新类型 **Buffered Update**，对接本地
`ctw_esp32s3_0909` 的 TCode 0.4 时间缓冲协议。按用户最新要求，**仅 Serial
提供该选项**；TCP、WebSocket、UDP、Pipe 均未接入。原 Fixed Update / Polled Update
保留。修改尚未提交 Git，也未推送远端。

上游：`https://github.com/Yoooi0/MultiFunPlayer`

基线提交：`36c08fbb99ac9398a63ff1cca1bbf68cd2228a94`。

## Windows 上从哪里开始

桌面目录：`MultiFunPlayer`。

- `Windows-Release/MultiFunPlayer.exe`：本次生成的 Windows x64 自包含发布版，
  随包带运行时，整个目录一起保留。已生成，不代表已在 Windows 上运行验证。
- `MultiFunPlayer.sln`：开发用解决方案；安装 .NET 9 SDK 和相应 WPF/.NET 桌面开发组件后打开。
- `Source/MultiFunPlayer.TCode.Tests/`：不依赖 WPF 的协议及播放测试项目。
- `Docs/TCode04.md`：详细使用说明和协议行为。
- `Docs/TCode04-validation/`：本次测试、构建和发布日志。
- `.git/`：保留 Git 历史及当前未提交的修改，便于查看 diff；GitVersion 构建也需要它。

桌面副本不包含 Linux 的 `obj`、常规 `bin` 构建缓存；Windows 发布文件单独放在
`Windows-Release`。这些文件属于运行产物，不要作为源码提交。

在项目根目录 PowerShell 中：

```powershell
dotnet build .\Source\MultiFunPlayer\MultiFunPlayer.csproj -c Debug
dotnet test .\Source\MultiFunPlayer.TCode.Tests\MultiFunPlayer.TCode.Tests.csproj
dotnet test .\Source\MultiFunPlayer.Tests\MultiFunPlayer.Tests.csproj
```

需要重新发布时：

```powershell
dotnet publish .\Source\MultiFunPlayer\MultiFunPlayer.csproj -c Release -r win-x64 --self-contained true -o .\Windows-Release
```

## 已完成的实现

入口：Serial 输出的更新设置菜单，断开连接时选择 **Buffered Update**，再连接串口。

1. 从脚本提供器读取六轴脚本及播放快照，而不是把 Fixed/Polled 的实时值直接包装成新报文。
2. `!4,...*CRC32\n` 数字帧，ACK 校验、请求号、启动标识/会话隔离；超时最多发送
   三次相同字节的请求，不生成不同请求来“补发”。
3. 五次时钟查询，采用最短 RTT 的中点估计 P4 时钟偏移。最短 RTT 超过 200 ms 时拒绝启动。
4. 暂停媒体，规划并预装载轨迹；P4 起点在约 3 秒后，随后 1 秒就位，再恢复媒体。
5. 约 6 秒前瞻、每 200 ms 检查续传、每帧最多 5 个节点；按 ACK 中每轴剩余容量续传。
6. 沿用 CTW/xtp-web 的 1% 转折点提取，保留显式片头静止段，由设备执行七次插值。
7. 暂停、跳转、变速、换脚本/范围时停止旧队列；继续播放时重新建立会话。
8. 预装载中再次手动 Pause 会取消准备，不会被计划中的自动 Play 覆盖。
9. 断开连接时使用独立清理超时发送 STOP；丢失 BEGIN ACK 时查询可能已建立的会话再停止。
   缺少 STOP 确认会明确报错，不能把串口断开等同于设备已停止。
10. 状态面板显示阶段、时钟不确定度及未封口轨迹的缓冲时长。

轴映射固定为 `L0/L1/L2/R0/R1/R2 -> 0/1/2/3/4/5`。
无脚本、BypassScript 或输出禁用的轴不会入队，`V0` 等辅助轴不在本协议内。

会应用：全局/每轴脚本时间偏移、播放速度、脚本缩放/反向，以及 Serial 每轴输出范围。
不应用：MFP 实时插值模式、随机运动生成、过渡、自动回中。
对已入队轴启用的脚本速度限制、Smart Limit 或运动混合会明确拒绝启动，以免静默绕过限制。
当前一条 Buffered Update 连接独占媒体播放控制；禁止同时由两个缓冲输出暂停/恢复同一个媒体。

## 代码位置

| 文件 | 职责 |
| --- | --- |
| `Common/IDeviceAxisValueProvider.cs` | 新枚举值 `BufferedUpdate` 与快照接口 |
| `Common/BufferedPlaybackSnapshot.cs` | 脚本/播放快照及时间线变化比较 |
| `UI/Controls/ViewModels/ScriptViewModel.cs` | 暴露脚本、偏移、变换和媒体状态，记录跳转/媒体变化版本 |
| `OutputTarget/ViewModels/SerialOutputTarget.cs` | 唯一接入的新输出通道；仍使用已有串口配置 |
| `OutputTarget/AbstractOutputTarget.cs` | 组装脚本快照、输出范围、状态和媒体控制 |
| `OutputTarget/TCodeBuffered/TCodeBufferedProtocol.cs` | 帧编解码及 CRC32 |
| `OutputTarget/TCodeBuffered/TCodeBufferedClient.cs` | 接收分片、ACK、重试、时钟同步、BEGIN/STOP 所有权 |
| `OutputTarget/TCodeBuffered/TCodeBufferedPlan.cs` | 转折点、初始取样、时间/范围映射和节点检查 |
| `OutputTarget/TCodeBuffered/TCodeBufferedPlayback.cs` | 预装载、ARM、续传、暂停/重建和异常清理 |
| `OutputTarget/TCodeBuffered/BufferedMediaControl.cs` | 区分本模块的播放命令与用户控制请求 |
| `OutputTarget/IUpdateContext.cs`、`Views/UpdateContextTemplates.xaml` | 新类型的状态展示 |
| `OutputTarget/Views/ButtplugOutputTarget.xaml` | 选择框改为实际支持的类型列表，防止新枚举泄漏到 Buttplug |

表中路径均相对 `Source/MultiFunPlayer/`。

## 已验证的结果和边界

Linux 上用临时 .NET SDK **9.0.318** 开启 `EnableWindowsTargeting`，完成了 Windows
Release 交叉编译（0 warnings / 0 errors）和 `win-x64` 自包含发布。

协议测试最终结果：**23 passed，0 failed，0 skipped**。
其中 6 项使用主机 C++ 适配器，直接编译并调用本地固件的真实
`tcode_queue.hpp` / `tcode_v04.hpp`，并非另写一份固件队列模拟逻辑。覆盖：

- 握手、五轴掩码（排除 R0）、APPEND、SEAL、ARM、STOP；
- 实际续传及暂停后跳转/变速重新建会话；
- 准备中取消、用户再次 Pause；
- 媒体时钟漂移时暂停并停止设备；
- 不支持暂停控制的媒体源不能取得设备运动控制；
- CRC/坏帧、分片 ACK、相同请求重发、丢失 BEGIN ACK、启动/会话变更；
- 片头静止段、时间偏移、倍速、范围/反向、缩放裁剪、长间隔与极短间隔。

适配器替代了设备时钟及电机就绪/反馈，**没有连接实体串口，也没有驱动电机**。
Windows WPF 界面、真实 COM 驱动、C3 无线转发、实际媒体播放器响应时间，以及电机反馈/运动，
均未在此次环境验证。原有 Windows 测试项目也未在 Linux 上运行；需要在 Windows 继续执行。

没有设置 `CTW_TCODE_HARNESS` 时，独立测试项目会跳过 6 项固件联测，剩余 17 项仍可运行。
需要重跑固件联测时，按 `Docs/TCode04.md` 用 CTW 的 include 目录编译
`FirmwareHarness.cpp`，将生成的可执行文件路径赋给 `CTW_TCODE_HARNESS`。
固件 checkout 不包含在这个 MFP 项目副本中。

## Windows 联调顺序

1. 启动发布版，确认 Serial 更新菜单有 Fixed Update / Polled Update / Buffered Update；
   切到 Buffered 后重启程序，检查所选类型保存/恢复。其他输出不应出现新类型。
2. 按实际设备选择 COM 和串口参数；波特率、DTR/RTS 继续使用原有可配置项。
   先在 Buffered 状态面板检查握手结果、RTT 和错误信息。
3. 先使用短、小范围单轴脚本，确认媒体确实暂停，约四秒准备后恢复，设备时间线与视频一致。
   再验证实际要使用的五轴/六轴脚本。
4. 验证 Pause、Resume、Seek、倍速、输出范围修改和预装载中再次 Pause。
   预期旧会话 STOP 后建立新会话，不复用旧节点。
5. 观察断开串口、设备重启、故障情况下的行为，特别是 STOP 未确认时的错误提示。
6. 回归 Serial 原 Fixed / Polled 模式和 Buttplug 类型选择。

若真实播放器不能响应 `MediaPlayPauseMessage`，会报 pause/play acknowledgement 错误；
需补齐该媒体源控制，而不是去掉预装载/启动检查。若媒体与设备偏差超过 150 ms，会终止本轮；
先记录媒体源类型、播放倍速、串口配置、错误信息及时间轴，定位时钟/控制延迟。

应用日志在可执行文件所在目录的 `Logs/application.log`。本次没有修改或烧录固件。
