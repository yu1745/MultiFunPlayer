using Microsoft.Win32;
using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using MultiFunPlayer.Input.TCode;
using MultiFunPlayer.Property;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using MultiFunPlayer.UI.Dialogs.ViewModels;
using System.Text;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using MultiFunPlayer.OutputTarget.Tracking;
using MultiFunPlayer.UI.Tracking;

namespace MultiFunPlayer.OutputTarget.ViewModels;

[DisplayName("Serial")]
internal sealed class SerialOutputTarget(int instanceIndex, IEventAggregator eventAggregator, IDeviceAxisValueProvider valueProvider, IInputProcessorFactory inputProcessorFactory)
    : ThreadAbstractOutputTarget(instanceIndex, eventAggregator, valueProvider)
{
    private CancellationTokenSource _refreshCancellationSource = new();
    private readonly IDeviceAxisValueProvider _filterProvider = valueProvider;
    private readonly object _modeLock = new();
    private CancellationTokenSource _motionCancellation;
    private SerialPresetSession _presetSession;
    private SerialManualSession _manualSession;
    private readonly TrackingMonitor _trackingMonitor = new();
    private TrackingWindow _trackingWindow;
    public bool TrackingEnabled { get; set; } = true;
    protected override TrackingMonitor Tracking => _trackingMonitor;
    protected override bool TrackingRequested => TrackingEnabled;
    // Leave ACK headroom even if a UART user keeps the old 115200 setting.
    protected override int TrackingPeriodMs => BaudRate <= 115200 ? 50 : BaudRate <= 230400 ? 40 : 20;
    public void OpenTracking()
    {
        if (_trackingWindow != null) { _trackingWindow.Activate(); return; }
        _trackingWindow = new TrackingWindow(_trackingMonitor) { Owner = System.Windows.Application.Current.MainWindow };
        _trackingWindow.Closed += (_, _) => _trackingWindow = null;
        _trackingWindow.Show(); // Nonmodal: playback controls remain available.
    }
    public bool IsManualOpen { get; private set; }
    public bool IsFilterOpen { get; private set; }
    public bool CanOpenFilter => !IsFilterOpen && !IsPresetOpen && !IsManualOpen;

    public async Task OpenFilter()
    {
        if (!CanOpenFilter) return;
        IsFilterOpen = true;
        try
        {
            EventAggregator.Publish(new MediaPlayPauseMessage(false));
            await DialogHelper.ShowAsync(new MotionFilterDialog(_filterProvider), "RootDialog").ConfigureAwait(true);
        }
        finally
        {
            EventAggregator.Publish(new MediaPlayPauseMessage(false));
            IsFilterOpen = false;
        }
    }
    public bool CanOpenManual => IsConnected && !IsPresetOpen && !IsManualOpen;

    public async Task OpenManual()
    {
        if (!CanOpenManual) return;
        IsManualOpen = true;
        AutoConnectEnabled = false;
        var session = new SerialManualSession();
        try
        {
            EventAggregator.Publish(new MediaPlayPauseMessage(false));
            lock (_modeLock)
            {
                if (!IsConnected || !_serialRunning || _presetSession != null || _manualSession != null)
                    throw new IOException("串口不可用，请稍后重试。");
                _manualSession = session;
                _motionCancellation?.Cancel();
            }
            await DialogHelper.ShowAsync(new ManualControlDialog(session, AxisSettings, GetValue), "RootDialog");
        }
        catch (Exception error)
        {
            session.Fail(error);
            await DialogHelper.ShowErrorAsync(error, "手动控制失败", "RootDialog");
        }
        finally
        {
            AutoConnectEnabled = false;
            await DisconnectAsync();
            lock (_modeLock) { if (ReferenceEquals(_manualSession, session)) _manualSession = null; }
            IsManualOpen = false;
        }
    }
    private bool _serialRunning;
    public bool IsPresetOpen { get; private set; }
    public bool CanOpenPresets => IsConnected && !IsPresetOpen && !IsManualOpen;

    public async Task OpenPresets()
    {
        if (!CanOpenPresets) return;
        IsPresetOpen = true;
        var session = new SerialPresetSession();
        try
        {
            EventAggregator.Publish(new MediaPlayPauseMessage(false));
            lock (_modeLock)
            {
                if (!IsConnected || !_serialRunning || _presetSession != null) throw new IOException("串口不可用，请稍后重试。");
                _presetSession = session;
                _motionCancellation?.Cancel();
            }
            await DialogHelper.ShowAsync(new DevicePresetsDialog(session), "RootDialog");
        }
        catch (Exception error)
        {
            session.Fail(error);
            await DialogHelper.ShowErrorAsync(error, "无法打开设备预设", "RootDialog");
        }
        finally
        {
            EventAggregator.Publish(new MediaPlayPauseMessage(false));
            session.Close();
            await session.Finished;
            IsPresetOpen = false;
        }
    }

    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy && !IsRefreshBusy && SelectedSerialPortDeviceId != null;

    public DeviceAxisUpdateType UpdateType { get; set; } = DeviceAxisUpdateType.FixedUpdate;
    public bool CanChangeUpdateType => !IsConnectBusy && !IsConnected;

    public ObservableConcurrentCollection<SerialPortInfo> SerialPorts { get; set; } = [];
    public SerialPortInfo SelectedSerialPort { get; set; }
    public string SelectedSerialPortDeviceId { get; set; }

    public int BaudRate { get; set; } = 115200;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public int DataBits { get; set; } = 8;
    public Handshake Handshake { get; set; } = Handshake.None;
    // Keep both modem outputs disabled when opening ESP auto-program ports.
    // Asserting them can introduce a DTR/RTS mismatch and reset the device.
    public bool DtrEnable { get; set; } = false;
    public bool RtsEnable { get; set; } = false;
    public int ReadTimeout { get; set; } = 250;
    public int WriteTimeout { get; set; } = 250;
    public int WriteBufferSize { get; set; } = 2048;
    public int ReadBufferSize { get; set; } = 4096;

    public IReadOnlyCollection<int> AvailableBaudRates { get; } = [50, 75, 110, 134, 150, 200, 300, 600, 1200, 1800, 2400, 4800, 9600, 19200, 28800, 38400, 57600, 76800, 115200, 230400, 460800, 576000, 921600];

    protected override IUpdateContext RegisterUpdateContext(DeviceAxisUpdateType updateType) => updateType switch
    {
        DeviceAxisUpdateType.FixedUpdate => new TCodeThreadFixedUpdateContext(),
        DeviceAxisUpdateType.PolledUpdate => new ThreadPolledUpdateContext(),
        DeviceAxisUpdateType.BufferedUpdate => new TCodeBufferedUpdateContext(),
        _ => null,
    };

    protected override void OnInitialActivate()
    {
        base.OnInitialActivate();
        if (Status == ConnectionStatus.Disconnected)
            _ = RefreshPorts();
    }

    public bool CanChangePort => !IsRefreshBusy && !IsConnectBusy && !IsConnected;
    public bool IsRefreshBusy { get; set; }
    public bool CanRefreshPorts => !IsRefreshBusy && !IsConnectBusy && !IsConnected;

    private bool _isRefreshingFlag;
    public async Task RefreshPorts()
    {
        if (Interlocked.CompareExchange(ref _isRefreshingFlag, true, false))
            return;

        try
        {
            var token = _refreshCancellationSource.Token;
            token.ThrowIfCancellationRequested();

            IsRefreshBusy = true;
            await DoRefreshPorts(token);
        }
        catch (Exception e)
        {
            Logger.Warn(e, $"{Identifier} port refresh failed with exception");
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshingFlag, false);
            IsRefreshBusy = false;
        }

        async Task DoRefreshPorts(CancellationToken token)
        {
            await Task.Delay(250, token);

            var serialPorts = new List<SerialPortInfo>();
            var scope = new ManagementScope("\\\\.\\ROOT\\cimv2");
            var observer = new ManagementOperationObserver();
            using var searcher = new ManagementObjectSearcher(scope, new SelectQuery("Win32_PnPEntity"));

            observer.ObjectReady += (_, e) =>
            {
                var portInfo = SerialPortInfo.FromManagementObject(e.NewObject as ManagementObject);
                if (portInfo == null)
                    return;

                serialPorts.Add(portInfo);
            };

            var taskCompletion = new TaskCompletionSource();
            observer.Completed += (_, _) => taskCompletion.TrySetResult();

            searcher.Get(observer);
            await using (token.Register(() => taskCompletion.TrySetCanceled()))
                await taskCompletion.Task.WaitAsync(token);

            var lastSelectedDeviceId = SelectedSerialPortDeviceId;
            SerialPorts.RemoveRange(SerialPorts.Except(serialPorts).ToList());
            SerialPorts.AddRange(serialPorts.Except(SerialPorts).ToList());

            SelectSerialPortByDeviceId(lastSelectedDeviceId);

            await Task.Delay(250, token);
        }
    }

    private void SelectSerialPortByDeviceId(string deviceId)
    {
        SelectedSerialPort = SerialPorts.FirstOrDefault(p => string.Equals(p.DeviceID, deviceId, StringComparison.Ordinal));
        if (SelectedSerialPort == null)
            SelectedSerialPortDeviceId = deviceId;
    }

    public void OnSelectedSerialPortChanged() => SelectedSerialPortDeviceId = SelectedSerialPort?.DeviceID;

    protected override async ValueTask<bool> OnConnectingAsync(ConnectionType connectionType)
    {
        if (connectionType != ConnectionType.AutoConnect)
            Logger.Info("Connecting to {0} at \"{1}\" [Type: {2}]", Identifier, SelectedSerialPortDeviceId, connectionType);

        if (SelectedSerialPortDeviceId == null)
            return false;
        if (SelectedSerialPort == null)
            await RefreshPorts();

        return SelectedSerialPort != null;
    }

    protected override void Run(ConnectionType connectionType, CancellationToken token)
    {
        var serialPort = default(SerialPort);

        try
        {
            serialPort = new()
            {
                PortName = SelectedSerialPort.PortName,
                Encoding = new UTF8Encoding(false),
                BaudRate = BaudRate,
                Parity = Parity,
                StopBits = StopBits,
                DataBits = DataBits,
                Handshake = Handshake,
                DtrEnable = DtrEnable,
                RtsEnable = RtsEnable,
                ReadTimeout = ReadTimeout,
                WriteTimeout = WriteTimeout,
                WriteBufferSize = WriteBufferSize,
                ReadBufferSize = ReadBufferSize,
            };

            serialPort.Open();
            lock (_modeLock) { _serialRunning = true; }
            Status = ConnectionStatus.Connected;
        }
        catch (Exception e)
        {
            try { serialPort?.Dispose(); }
            catch { }

            if (connectionType != ConnectionType.AutoConnect)
            {
                Logger.Error(e, "Error when connecting to {0} at \"{1}\"", Name, SelectedSerialPortDeviceId);
                _ = DialogHelper.ShowErrorAsync(e, $"Error when connecting to {Name}", "RootDialog");
            }

            return;
        }

        try
        {
            EventAggregator.Publish(new SyncRequestMessage());

            using var tcodeInputProcessor = inputProcessorFactory.GetInputProcessor<TCodeInputProcessor>();
            while (!token.IsCancellationRequested && serialPort.IsOpen)
            {
                SerialPresetSession session;
                SerialManualSession manual;
                CancellationTokenSource motion;
                lock (_modeLock)
                {
                    session = _presetSession;
                    manual = _manualSession;
                    motion = session == null && manual == null ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
                    _motionCancellation = motion;
                }
                if (manual != null)
                {
                    manual.Run(serialPort.Write, () => { if (serialPort.BytesToRead > 0) ReadExisting(); }, token);
                }
                else if (session != null)
                {
                    try
                    {
                        session.RunAsync(serialPort.Write, () =>
                        {
                            var bytes = new byte[serialPort.BytesToRead];
                            if (bytes.Length == 0) return bytes;
                            var count = serialPort.Read(bytes, 0, bytes.Length);
                            return count == bytes.Length ? bytes : bytes[..count];
                        }, token).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        lock (_modeLock) { if (ReferenceEquals(_presetSession, session)) _presetSession = null; }
                    }
                }
                else
                {
                    try { RunMotion(motion.Token); }
                    catch (OperationCanceledException) when (motion.IsCancellationRequested) { }
                    finally
                    {
                        lock (_modeLock) { _motionCancellation = null; motion.Dispose(); }
                    }
                }
            }

            void RunMotion(CancellationToken token)
            {
                if (UpdateType == DeviceAxisUpdateType.BufferedUpdate)
                {
                    BufferedUpdateAsync((wire, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        serialPort.Write(wire);
                        return Task.CompletedTask;
                    }, async ct =>
                    {
                        while (serialPort.IsOpen)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (serialPort.BytesToRead > 0) return serialPort.ReadExisting();
                            await Task.Delay(5, ct).ConfigureAwait(false);
                        }
                        return null;
                    }, token).GetAwaiter().GetResult();
                }
                else if (UpdateType == DeviceAxisUpdateType.FixedUpdate)
                {
                    var currentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
                    var lastSentValues = DeviceAxis.All.ToDictionary(a => a, _ => double.NaN);
                    FixedUpdate<TCodeThreadFixedUpdateContext>(() => !token.IsCancellationRequested && serialPort.IsOpen, (context, elapsed) =>
                    {
                        Logger.Trace("Begin FixedUpdate [Elapsed: {0}]", elapsed);
                        GetValues(currentValues);

                        if (serialPort.IsOpen && serialPort.BytesToRead > 0)
                            ReadExisting();

                        var values = context.SendDirtyValuesOnly ? currentValues.Where(x => DeviceAxis.IsValueDirty(x.Value, lastSentValues[x.Key])) : currentValues;
                        values = values.Where(x => AxisSettings[x.Key].Enabled);

                        var commands = context.OffloadElapsedTime ? DeviceAxis.ToString(values) : DeviceAxis.ToString(values, elapsed * 1000);
                        if (serialPort.IsOpen && !string.IsNullOrWhiteSpace(commands))
                        {
                            Logger.Trace("Sending \"{0}\" to \"{1}\"", commands.Trim(), SelectedSerialPortDeviceId);

                            serialPort.Write(commands);
                            lastSentValues.Merge(values);
                        }
                    });
                }
                else if (UpdateType == DeviceAxisUpdateType.PolledUpdate)
                {
                    var receiveLock = new object();
                    var receiving = true;
                    serialPort.DataReceived += OnDataReceived;
                    try { PolledUpdate(DeviceAxis.All, () => !token.IsCancellationRequested, (_, axis, axisEvent, elapsed) =>
                    {
                        Logger.Trace("Begin PolledUpdate [Axis: {0}, Event: {1}, Elapsed: {2}]", axis, axisEvent, elapsed);

                        var settings = AxisSettings[axis];
                        if (!settings.Enabled)
                            return;
                        if (!double.IsFinite(axisEvent.TargetValue))
                            return;

                        var value = MathUtils.Lerp(settings.Minimum, settings.Maximum, axisEvent.TargetValue);
                        var duration = axisEvent.Duration;

                        var command = DeviceAxis.ToString(axis, value, duration * 1000);
                        if (serialPort.IsOpen && !string.IsNullOrWhiteSpace(command))
                        {
                            Logger.Trace("Sending \"{0}\" to \"{1}\"", command, SelectedSerialPortDeviceId);

                            serialPort.Write($"{command}\n");
                        }
                    }, token); }
                    finally
                    {
                        serialPort.DataReceived -= OnDataReceived;
                        lock (receiveLock) { receiving = false; }
                    }

                    void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
                    {
                        lock (receiveLock)
                        {
                            if (!receiving || token.IsCancellationRequested || !serialPort.IsOpen || e.EventType == SerialData.Eof)
                                return;
                            ReadExisting();
                        }
                    }
                }

            }

            void ReadExisting()
            {
                var receivedString = serialPort.ReadExisting();
                Logger.Debug("Received \"{0}\" from \"{1}\"", receivedString, SelectedSerialPortDeviceId);
                tcodeInputProcessor.Parse(receivedString);
            }
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidDataException)
        {
            Logger.Error(e, $"{Identifier} failed with exception");
            if (!IsPresetOpen)
                _ = DialogHelper.ShowErrorAsync(e, $"{Identifier} failed with exception", "RootDialog");
        }
        catch (Exception e) { Logger.Error(e, $"{Identifier} failed with exception"); }

        lock (_modeLock)
        {
            _serialRunning = false;
            _manualSession?.Fail(new IOException("串口已断开，请关闭手动控制窗口。"));
            _presetSession?.Fail(new IOException("串口已断开，预设操作未完成。"));
            _presetSession = null;
        }
        try { serialPort?.Dispose(); }
        catch { }
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(UpdateType)] = JToken.FromObject(UpdateType);
            settings[nameof(SelectedSerialPort)] = SelectedSerialPortDeviceId;
            settings[nameof(BaudRate)] = BaudRate;
            settings[nameof(TrackingEnabled)] = TrackingEnabled;
            settings[nameof(Parity)] = JToken.FromObject(Parity);
            settings[nameof(StopBits)] = JToken.FromObject(StopBits);
            settings[nameof(DataBits)] = DataBits;
            settings[nameof(Handshake)] = JToken.FromObject(Handshake);
            settings[nameof(DtrEnable)] = DtrEnable;
            settings[nameof(RtsEnable)] = RtsEnable;
            settings[nameof(ReadTimeout)] = ReadTimeout;
            settings[nameof(WriteTimeout)] = WriteTimeout;
            settings[nameof(WriteBufferSize)] = WriteBufferSize;
            settings[nameof(ReadBufferSize)] = ReadBufferSize;
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<DeviceAxisUpdateType>(nameof(UpdateType), out var updateType))
                UpdateType = updateType;
            if (settings.TryGetValue<string>(nameof(SelectedSerialPort), out var selectedSerialPort))
                SelectSerialPortByDeviceId(selectedSerialPort);

            if (settings.TryGetValue<int>(nameof(BaudRate), out var baudRate)) BaudRate = baudRate;
            if (settings.TryGetValue<bool>(nameof(TrackingEnabled), out var trackingEnabled)) TrackingEnabled = trackingEnabled;
            if (settings.TryGetValue<Parity>(nameof(Parity), out var parity)) Parity = parity;
            if (settings.TryGetValue<StopBits>(nameof(StopBits), out var stopBits)) StopBits = stopBits;
            if (settings.TryGetValue<int>(nameof(DataBits), out var dataBits)) DataBits = dataBits;
            if (settings.TryGetValue<Handshake>(nameof(Handshake), out var handshake)) Handshake = handshake;
            if (settings.TryGetValue<bool>(nameof(DtrEnable), out var dtrEnable)) DtrEnable = dtrEnable;
            if (settings.TryGetValue<bool>(nameof(RtsEnable), out var rtsEnable)) RtsEnable = rtsEnable;
            if (settings.TryGetValue<int>(nameof(ReadTimeout), out var readTimeout)) ReadTimeout = readTimeout;
            if (settings.TryGetValue<int>(nameof(WriteTimeout), out var writeTimeout)) WriteTimeout = writeTimeout;
            if (settings.TryGetValue<int>(nameof(WriteBufferSize), out var writeBufferSize)) WriteBufferSize = writeBufferSize;
            if (settings.TryGetValue<int>(nameof(ReadBufferSize), out var readBufferSize)) ReadBufferSize = readBufferSize;
        }
    }

    public override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region SerialPort
        s.RegisterAction<string>($"{Identifier}::SerialPort::Set", s => s.WithLabel("Device ID"), SelectSerialPortByDeviceId);
        #endregion
    }

    public override void UnregisterActions(IShortcutManager s)
    {
        base.UnregisterActions(s);
        s.UnregisterAction($"{Identifier}::SerialPort::Set");
    }

    public override void RegisterProperties(IPropertyManager p)
    {
        base.RegisterProperties(p);
        p.RegisterProperty($"{Identifier}::SerialPort", () => SelectedSerialPort?.PortName);
    }

    public override void UnregisterProperties(IPropertyManager p)
    {
        base.UnregisterProperties(p);
        p.UnregisterProperty($"{Identifier}::SerialPort");
    }

    protected override void Dispose(bool disposing)
    {
        _trackingMonitor.Dispose();
        var window = _trackingWindow;
        if (window != null && !window.Dispatcher.HasShutdownStarted)
            window.Dispatcher.BeginInvoke(() => window.Close());
        _refreshCancellationSource?.Cancel();
        _refreshCancellationSource?.Dispose();
        _refreshCancellationSource = null;

        base.Dispose(disposing);
    }

    public sealed class SerialPortInfo
    {
        private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

        private SerialPortInfo() { }

        public string Caption { get; init; }
        public string ClassGuid { get; init; }
        public string Description { get; init; }
        public string DeviceID { get; init; }
        public string Manufacturer { get; init; }
        public string Name { get; init; }
        public string PNPClass { get; init; }
        public string PNPDeviceID { get; init; }
        public string PortName { get; init; }

        public static SerialPortInfo FromManagementObject(ManagementObject o)
        {
            T GetPropertyValueOrDefault<T>(string propertyName, T defaultValue = default)
            {
                try { return (T)o.GetPropertyValue(propertyName); }
                catch { return defaultValue; }
            }

            try
            {
                var name = o.GetPropertyValue(nameof(Name)) as string;
                if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"\(COM\d+\)"))
                    return null;

                var deviceId = o.GetPropertyValue(nameof(DeviceID)) as string;
                var portName = Registry.GetValue($@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Enum\{deviceId}\Device Parameters", "PortName", "").ToString();

                return new SerialPortInfo()
                {
                    Caption = GetPropertyValueOrDefault<string>(nameof(Caption)),
                    ClassGuid = GetPropertyValueOrDefault<string>(nameof(ClassGuid)),
                    Description = GetPropertyValueOrDefault<string>(nameof(Description)),
                    Manufacturer = GetPropertyValueOrDefault<string>(nameof(Manufacturer)),
                    PNPClass = GetPropertyValueOrDefault<string>(nameof(PNPClass)),
                    PNPDeviceID = GetPropertyValueOrDefault<string>(nameof(PNPDeviceID)),
                    Name = name,
                    DeviceID = deviceId,
                    PortName = portName
                };
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to create SerialPortInfo [{0}]", o?.ToString());
            }

            return null;
        }

        public override bool Equals(object o) => o is SerialPortInfo other && string.Equals(DeviceID, other.DeviceID, StringComparison.Ordinal);
        public override int GetHashCode() => HashCode.Combine(DeviceID);
    }
}
