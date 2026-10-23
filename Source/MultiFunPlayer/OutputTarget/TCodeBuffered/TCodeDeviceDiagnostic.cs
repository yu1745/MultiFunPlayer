using System.Globalization;

namespace MultiFunPlayer.OutputTarget.TCodeBuffered;

internal sealed record TCodeDeviceDiagnostic(ulong Request, ulong Boot, ulong Session, ulong Sequence,
    ulong Microseconds, ulong State, ulong Reason, ulong Axis, ulong Motor, ulong Operation, ulong Detail, float[] Values)
{
    public static TCodeDeviceDiagnostic Decode(ulong[] fields) => new(fields[1], fields[2], fields[3], fields[4],
        fields[5], fields[6], fields[7], fields[8], fields[9], fields[10], fields[11],
        fields.Skip(12).Select(x => BitConverter.Int32BitsToSingle(unchecked((int)(uint)x))).ToArray());

    public bool IsContext => Reason is 24 or 25;
    public string Message
    {
        get
        {
            var reason = Reason switch
            {
                1 => "帧格式或 CRC 错误", 2 => "设备启动标识或会话不匹配", 3 => "命令序号不匹配",
                4 => "当前队列状态不允许此命令", 5 => "命令参数无效", 6 => "轨迹点或命令到达过晚",
                7 => "轴队列已满", 8 => "设备未就绪", 9 => "轨迹段超过会话 V/A/J 限制",
                10 => "不支持的轨迹配置", 11 => "起始规划状态未静止或无效", 12 => "ARM 前缓存不足",
                13 => "ARM 前规划姿态发生变化", 14 => "暂停游标超出可保留的时间范围",
                15 => "恢复时尚未收停或姿态不匹配", 20 => "轨迹队列耗尽（underrun）",
                21 => "执行器离开运行状态、运动被暂停或平滑模式改变",
                22 => "暂停收停计算失败", 23 => "播放轨迹采样校验失败",
                24 => "轴状态快照", 25 => "轴限值快照", 26 => "电机未就绪",
                27 => "运行时电机反馈失效", 28 => "CAN 电机指令发送失败", 29 => "电机故障已锁定",
                30 => "运行时诊断缓存溢出", _ => $"未知设备诊断 {Reason}"
            };
            var axis = Axis < 6 ? new[] { "L0", "L1", "L2", "R0", "R1", "R2" }[Axis] : "-";
            var subject = Motor != 0 ? $"M{Motor}" : axis;
            var fields = Reason switch
            {
                8 => $"原因=[{Readiness(Detail)}] initState={V(0)} smoothingMode={V(1)} requiredMotors={V(2)} enabledMotors={V(3)} badMotors={V(4)}",
                9 => $"V={V(0)}/{V(1)} A={V(2)}/{V(3)} J={V(4)}/{V(5)}",
                11 => $"p={V(0)} v={V(1)} a={V(2)} velocityTolerance={V(4)} accelerationTolerance={V(5)}",
                12 => $"points={V(0)} horizonUs={V(1)}",
                13 or 15 => $"p={V(0)} expectedP={V(1)} v={V(2)} a={V(3)}",
                20 => $"tailUs={V(0)} overdueUs={V(1)} points={V(2)}",
                21 => $"initState={V(0)} motionPaused={V(1)} previewRunning={V(2)} smoothingMode={V(3)}",
                22 or 23 => $"check={Smoother(Detail)} measured={V(0)} limit={V(1)} dtSeconds={V(2)} solverResult={V(3)} smoothingMode={V(4)}",
                24 => $"p={V(0)} v={V(1)} a={V(2)} targetP={V(3)} targetV={V(4)} targetA={V(5)}",
                25 => $"maxV={V(0)} maxA={V(1)} maxJ={V(2)} referenceJ={V(3)} streamed={Detail}",
                26 or 27 or 28 or 29 => $"flags=[{(Reason == 26 ? Readiness(Detail & 511) : "-")}] result={V(0)} feedbackAgeMs={V(1)} axisState={V(2)} axisError={(Reason == 26 ? Detail >> 9 : Detail)} controllerPosition={V(4)} controllerVelocity={V(5)}",
                _ => $"detail={Detail} values=[{string.Join(',', Values.Select(x => x.ToString("G9", CultureInfo.InvariantCulture)))}]"
            };
            return $"{reason} axis/motor={subject} {fields}";
        }
    }
    public string LogLine => $"DEVICE {(IsContext ? "CONTEXT" : "ERROR")} boot={Boot} session={Session} event={Sequence} timeUs={Microseconds} state={State} op={Operation} request={Request} reason={Reason}: {Message}";
    private string V(int index) => Values[index].ToString("G9", CultureInfo.InvariantCulture);
    private static string Smoother(ulong reason) => reason switch
    {
        0 => "unspecified", 1 => "uninitialized", 2 => "control_dt_out_of_range", 3 => "non_finite_state",
        4 => "invalid_envelope", 5 => "velocity_limit", 6 => "acceleration_limit", 7 => "jerk_limit",
        8 => "position_step", 9 => "velocity_step", 10 => "acceleration_step", 11 => "ruckig_solver", 12 => "legacy_smoother",
        _ => $"unknown_{reason}"
    };
    private static string Readiness(ulong flags)
    {
        var names = new[] { "执行器未RUNNING", "运动已暂停", "预览正在运行", "未使用Ruckig", "所需电机未启用",
            "反馈读取失败", "反馈缺失或超过100ms", "电机未闭环", "电机报错" };
        return flags == 0 ? "未提供细分原因" : string.Join(',', names.Where((_, i) => (flags & (1UL << i)) != 0));
    }
}
