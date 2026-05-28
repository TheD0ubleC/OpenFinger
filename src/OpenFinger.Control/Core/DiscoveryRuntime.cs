using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenFinger.Control;

public sealed class DiscoveryDevice
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "OpenFinger";
    public string SerialPort { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string StaIp { get; set; } = string.Empty;
    public string Role { get; set; } = "unknown";
    public string State { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int UdpPort { get; set; } = 39001;
    public int AdcMask { get; set; } = 31;
    public string BoardTarget { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public int ReportHz { get; set; }
    public int ThumbPin { get; set; } = -1;
    public int IndexPin { get; set; } = -1;
    public int MiddlePin { get; set; } = -1;
    public int RingPin { get; set; } = -1;
    public int PinkyPin { get; set; } = -1;
    public int JoystickVrxPin { get; set; } = -1;
    public int JoystickVryPin { get; set; } = -1;
    public int JoystickSwPin { get; set; } = -1;
    public bool BatteryAvailable { get; set; }
    public int BatteryPercent { get; set; } = -1;
    public int BatteryMillivolts { get; set; } = -1;
    public bool BatteryChargingKnown { get; set; }
    public bool BatteryCharging { get; set; }
    public bool TrackingEnabled { get; set; } = true;
    public bool WifiConnected { get; set; }
    public bool WifiActive { get; set; }
    public bool UsbConnected { get; set; }
    public bool Online { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string Transport => WifiActive ? "Wi-Fi 实时" : WifiConnected ? "Wi-Fi" : UsbConnected ? "USB" : "离线";
    public bool UsbPreferred => UsbConnected && !WifiActive;
}

public sealed class UdpRuntimeMonitor : IDisposable
{
    private readonly UdpClient _udp;
    private readonly UdpClient? _forward;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private bool _disposed;

    public UdpRuntimeMonitor(int port, int forwardPort = 0)
    {
        Port = port;
        ForwardPort = forwardPort;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _forward = forwardPort > 0 ? new UdpClient() : null;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public int Port { get; }
    public int ForwardPort { get; }

    public event Action<string, int[], int, bool?, int?, int?, bool?>? PacketReceived;
    public event Action<string, SerialStatusDto>? HeartbeatReceived;

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            try
            {
                if (_forward is not null)
                {
                    await _forward.SendAsync(result.Buffer, result.Buffer.Length, new IPEndPoint(IPAddress.Loopback, ForwardPort)).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            var sourceIp = result.RemoteEndPoint.Address.ToString();
            var text = Encoding.UTF8.GetString(result.Buffer).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (TryParseAdcPacket(text, out var raws, out var mask, out var trackingEnabled, out var joystickRawX, out var joystickRawY, out var joystickPressed))
            {
                PacketReceived?.Invoke(sourceIp, raws, mask, trackingEnabled, joystickRawX, joystickRawY, joystickPressed);
                continue;
            }

            if (TryParseHeartbeatPacket(text, out var heartbeat))
            {
                HeartbeatReceived?.Invoke(sourceIp, heartbeat);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _udp.Dispose();
        _forward?.Dispose();
        _cts.Dispose();
        try
        {
            _receiveLoop.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
        }
    }

    private static bool TryParseAdcPacket(string text, out int[] raws, out int mask, out bool? trackingEnabled, out int? joystickRawX, out int? joystickRawY, out bool? joystickPressed)
    {
        raws = [-1, -1, -1, -1, -1];
        mask = 0;
        trackingEnabled = null;
        joystickRawX = null;
        joystickRawY = null;
        joystickPressed = null;

        var line = text.Trim();
        if (!line.StartsWith("OFADC", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = line.Split(',', StringSplitOptions.None);
        if (parts.Length < 9)
        {
            return false;
        }

        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out mask))
        {
            return false;
        }

        for (var index = 0; index < 5; index++)
        {
            raws[index] = ParseInt(parts, 4 + index, -1);
        }

        if (parts.Length > 9)
        {
            trackingEnabled = ParseBool(parts[9]);
        }

        if (parts.Length > 10)
        {
            var parsed = ParseNullableInt(parts[10]);
            joystickRawX = parsed >= 0 ? parsed : null;
        }

        if (parts.Length > 11)
        {
            var parsed = ParseNullableInt(parts[11]);
            joystickRawY = parsed >= 0 ? parsed : null;
        }

        if (parts.Length > 12)
        {
            joystickPressed = ParseBool(parts[12]);
        }

        return true;
    }

    private static bool TryParseHeartbeatPacket(string text, out SerialStatusDto status)
    {
        status = new SerialStatusDto();
        var line = text.Trim();
        if (!line.StartsWith("OFHB ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var json = line[5..].Trim();
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null)
            {
                return false;
            }

            status = ParseStatus(root);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SerialStatusDto ParseStatus(JsonObject root)
    {
        return new SerialStatusDto
        {
            Device = GetString(root, "device"),
            State = GetString(root, "state"),
            Message = GetString(root, "message"),
            Mac = GetString(root, "mac"),
            StaIp = GetString(root, "sta_ip"),
            WifiConnected = GetBool(root, "wifi_connected"),
            HostIp = GetString(root, "host_ip"),
            UdpPort = GetInt(root, "udp_port", 39001),
            AdcMask = GetInt(root, "adc_mask", 31),
            AdcStreaming = GetBool(root, "adc_streaming"),
            Role = GetString(root, "role"),
            TrackingEnabled = GetBool(root, "tracking_enabled"),
            BoardTarget = GetString(root, "board_target"),
            FirmwareVersion = GetString(root, "firmware_version"),
            ReportHz = GetInt(root, "report_hz", 0),
            ThumbPin = GetInt(root, "thumb_pin", -1),
            IndexPin = GetInt(root, "index_pin", -1),
            MiddlePin = GetInt(root, "middle_pin", -1),
            RingPin = GetInt(root, "ring_pin", -1),
            PinkyPin = GetInt(root, "pinky_pin", -1),
            JoystickVrxPin = GetInt(root, "joystick_vrx_pin", -1),
            JoystickVryPin = GetInt(root, "joystick_vry_pin", -1),
            JoystickSwPin = GetInt(root, "joystick_sw_pin", -1),
            BatteryAdcPin = GetInt(root, "battery_adc_pin", -1),
            BatteryChargePin = GetInt(root, "battery_charge_pin", -1),
            BatteryAvailable = GetBool(root, "battery_available") ?? false,
            BatteryMillivolts = GetInt(root, "battery_mv", -1),
            BatteryPercent = GetInt(root, "battery_percent", -1),
            BatteryChargingKnown = GetBool(root, "battery_charging_known") ?? false,
            BatteryCharging = GetBool(root, "battery_charging") ?? false,
            ProtocolVersion = GetString(root, "protocol_version"),
            Capabilities = GetString(root, "capabilities")
        };
    }

    private static string? GetString(JsonObject root, string name) => root.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;
    private static int GetInt(JsonObject root, string name, int fallback) => root.TryGetPropertyValue(name, out var node) && node is not null && int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static bool? GetBool(JsonObject root, string name) => root.TryGetPropertyValue(name, out var node) ? ParseBool(node?.ToString()) : null;
    private static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static int ParseInt(string[] parts, int index, int fallback)
    {
        if (index >= parts.Length)
        {
            return fallback;
        }

        return int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static int ParseNullableInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
    }
}

public sealed class RuntimeFramePublisher : IDisposable
{
    private sealed class HandState
    {
        public bool Present { get; set; }
        public bool Stale { get; set; }
        public double[] Bends { get; } = new double[5];
        public bool JoystickAvailable { get; set; }
        public double JoystickX { get; set; }
        public double JoystickY { get; set; }
        public bool JoystickPressed { get; set; }
        public bool JoystickTouched { get; set; }
        public int JoystickAxisMode { get; set; }
        public int JoystickClickAction { get; set; }
        public ControllerPoseOffsetConfig PoseOffset { get; set; } = new();
    }

    private readonly object _lock = new();
    private readonly UdpClient _udp = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HandState _left = new();
    private readonly HandState _right = new();
    private ulong _seq;
    private bool _disposed;

    public RuntimeFramePublisher(int port)
    {
        Port = port;
    }

    public int Port { get; private set; }

    public void UpdatePort(int port)
    {
        lock (_lock)
        {
            Port = port;
        }
    }

    public void UpdateHand(string side, bool present, bool stale, IReadOnlyList<double> bends)
    {
        lock (_lock)
        {
            var target = SelectHand(side);
            target.Present = present;
            target.Stale = stale;
            for (var i = 0; i < target.Bends.Length; i++)
            {
                target.Bends[i] = i < bends.Count ? bends[i] : 0.0;
            }

            PublishLocked();
        }
    }

    public void UpdateJoystick(string side, bool available, double axisX, double axisY, bool pressed, bool touched, int axisMode, int clickAction)
    {
        lock (_lock)
        {
            var target = SelectHand(side);
            target.JoystickAvailable = available;
            target.JoystickX = axisX;
            target.JoystickY = axisY;
            target.JoystickPressed = pressed;
            target.JoystickTouched = touched;
            target.JoystickAxisMode = axisMode;
            target.JoystickClickAction = clickAction;
            PublishLocked();
        }
    }

    public void UpdatePoseOffset(string side, ControllerPoseOffsetConfig offset)
    {
        lock (_lock)
        {
            var target = SelectHand(side);
            target.PoseOffset = ClonePoseOffset(offset);
            PublishLocked();
        }
    }

    private HandState SelectHand(string side)
    {
        return string.Equals(side, "left", StringComparison.OrdinalIgnoreCase) ? _left : _right;
    }

    private void PublishLocked()
    {
        if (_disposed || Port <= 0)
        {
            return;
        }

        var text = Serialize();
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            _udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, Port));
        }
        catch
        {
        }
    }

    private string Serialize()
    {
        var left = _left;
        var right = _right;
        var parts = new List<string>(43)
        {
            "OFRUNTIME",
            (++_seq).ToString(CultureInfo.InvariantCulture),
            ((ulong)_clock.ElapsedMilliseconds).ToString(CultureInfo.InvariantCulture),
            Bool01(left.Present),
            Bool01(left.Stale)
        };

        AppendFingerBends(parts, left.Bends);
        AppendJoystick(parts, left);
        parts.Add(Bool01(right.Present));
        parts.Add(Bool01(right.Stale));
        AppendFingerBends(parts, right.Bends);
        AppendJoystick(parts, right);
        if (HasPoseOffset(left.PoseOffset) || HasPoseOffset(right.PoseOffset))
        {
            AppendPoseOffset(parts, left.PoseOffset);
            AppendPoseOffset(parts, right.PoseOffset);
        }

        return string.Join(',', parts);
    }

    private static void AppendFingerBends(List<string> parts, IReadOnlyList<double> bends)
    {
        for (var i = 0; i < 5; i++)
        {
            var bend = i < bends.Count ? bends[i] : 0.0;
            parts.Add(bend.ToString("0.0000", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendJoystick(List<string> parts, HandState hand)
    {
        parts.Add(Bool01(hand.JoystickAvailable));
        parts.Add(hand.JoystickX.ToString("0.0000", CultureInfo.InvariantCulture));
        parts.Add(hand.JoystickY.ToString("0.0000", CultureInfo.InvariantCulture));
        parts.Add(Bool01(hand.JoystickPressed));
        parts.Add(Bool01(hand.JoystickTouched));
        parts.Add(hand.JoystickAxisMode.ToString(CultureInfo.InvariantCulture));
        parts.Add(hand.JoystickClickAction.ToString(CultureInfo.InvariantCulture));
    }

    private static bool HasPoseOffset(ControllerPoseOffsetConfig offset)
    {
        return Math.Abs(offset.PositionX) > 0.00001
            || Math.Abs(offset.PositionY) > 0.00001
            || Math.Abs(offset.PositionZ) > 0.00001
            || Math.Abs(offset.RotationPitch) > 0.00001
            || Math.Abs(offset.RotationYaw) > 0.00001
            || Math.Abs(offset.RotationRoll) > 0.00001;
    }

    private static void AppendPoseOffset(List<string> parts, ControllerPoseOffsetConfig offset)
    {
        parts.Add(offset.PositionX.ToString("0.0000", CultureInfo.InvariantCulture));
        parts.Add(offset.PositionY.ToString("0.0000", CultureInfo.InvariantCulture));
        parts.Add(offset.PositionZ.ToString("0.0000", CultureInfo.InvariantCulture));
        parts.Add(offset.RotationPitch.ToString("0.0000", CultureInfo.InvariantCulture));
        parts.Add(offset.RotationYaw.ToString("0.0000", CultureInfo.InvariantCulture));
        parts.Add(offset.RotationRoll.ToString("0.0000", CultureInfo.InvariantCulture));
    }

    private static ControllerPoseOffsetConfig ClonePoseOffset(ControllerPoseOffsetConfig offset) => new()
    {
        PositionX = Math.Clamp(double.IsFinite(offset.PositionX) ? offset.PositionX : 0.0, -1.0, 1.0),
        PositionY = Math.Clamp(double.IsFinite(offset.PositionY) ? offset.PositionY : 0.0, -1.0, 1.0),
        PositionZ = Math.Clamp(double.IsFinite(offset.PositionZ) ? offset.PositionZ : 0.0, -1.0, 1.0),
        RotationPitch = Math.Clamp(double.IsFinite(offset.RotationPitch) ? offset.RotationPitch : 0.0, -180.0, 180.0),
        RotationYaw = Math.Clamp(double.IsFinite(offset.RotationYaw) ? offset.RotationYaw : 0.0, -180.0, 180.0),
        RotationRoll = Math.Clamp(double.IsFinite(offset.RotationRoll) ? offset.RotationRoll : 0.0, -180.0, 180.0)
    };

    private static string Bool01(bool value) => value ? "1" : "0";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _udp.Dispose();
    }
}
