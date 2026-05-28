using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenFinger.Control;

public sealed class FirmwareTools
{
    public string RepositoryRoot { get; }

    public FirmwareTools(string repositoryRoot)
    {
        RepositoryRoot = repositoryRoot;
    }

    public static string ResolveRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var isSourceTree = File.Exists(Path.Combine(dir.FullName, "CMakeLists.txt"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "firmware"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "drivers"));

            var isPackageTree = File.Exists(Path.Combine(dir.FullName, "VERSION"))
                && Directory.Exists(Path.Combine(dir.FullName, "drivers", "openfinger"));

            if (isSourceTree || isPackageTree)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    public static IReadOnlyList<string> EnumeratePorts()
    {
        try
        {
            return SerialPort.GetPortNames().OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

public sealed class SerialStatusDto
{
    public string? Device { get; set; }
    public string? State { get; set; }
    public string? Message { get; set; }
    public string? Mac { get; set; }
    public string? StaIp { get; set; }
    public bool? WifiConnected { get; set; }
    public string? HostIp { get; set; }
    public int UdpPort { get; set; } = 39001;
    public int AdcMask { get; set; } = 31;
    public bool? AdcStreaming { get; set; }
    public string? Role { get; set; }
    public bool? TrackingEnabled { get; set; }
    public string? BoardTarget { get; set; }
    public string? FirmwareVersion { get; set; }
    public int ReportHz { get; set; }
    public int ThumbPin { get; set; } = -1;
    public int IndexPin { get; set; } = -1;
    public int MiddlePin { get; set; } = -1;
    public int RingPin { get; set; } = -1;
    public int PinkyPin { get; set; } = -1;
    public int JoystickVrxPin { get; set; } = -1;
    public int JoystickVryPin { get; set; } = -1;
    public int JoystickSwPin { get; set; } = -1;
    public int BatteryAdcPin { get; set; } = -1;
    public int BatteryChargePin { get; set; } = -1;
    public bool BatteryAvailable { get; set; }
    public int BatteryMillivolts { get; set; } = -1;
    public int BatteryPercent { get; set; } = -1;
    public bool BatteryChargingKnown { get; set; }
    public bool BatteryCharging { get; set; }
    public string? ProtocolVersion { get; set; }
    public string? Capabilities { get; set; }
}

public static class OpenFingerWire
{
    public static string BuildProvisionCommand(string ssid, string password, string hostIp, int udpPort, int adcMask, string role)
    {
        var fields = new Dictionary<string, string>
        {
            ["ssid"] = ssid,
            ["password"] = password,
            ["save"] = "1",
            ["host_ip"] = hostIp,
            ["udp_port"] = udpPort.ToString(),
            ["adc_mask"] = adcMask.ToString(),
            ["role"] = role
        };
        return "OFPROV " + BuildQuery(fields);
    }

    public static string BuildRoleCommand(string role)
    {
        return "OFADC_CFG " + BuildQuery(new Dictionary<string, string> { ["role"] = role });
    }

    public static string BuildRuntimeConfigCommand(FirmwareConfig firmwareConfig, string? role, string hostIp, int udpPort)
    {
        return BuildRuntimeConfigCommand(
            hostIp,
            udpPort,
            31,
            string.IsNullOrWhiteSpace(role) ? "unknown" : role,
            firmwareConfig.ReportRateHz,
            firmwareConfig.ThumbPin,
            firmwareConfig.IndexPin,
            firmwareConfig.MiddlePin,
            firmwareConfig.RingPin,
            firmwareConfig.PinkyPin,
            firmwareConfig.TrackingSwitchPin,
            firmwareConfig.TrackingSwitchMode,
            firmwareConfig.JoystickVrxPin,
            firmwareConfig.JoystickVryPin,
            firmwareConfig.JoystickSwPin,
            firmwareConfig.BatteryAdcPin,
            firmwareConfig.BatteryChargePin);
    }

    public static string BuildRuntimeConfigCommand(
        string hostIp,
        int udpPort,
        int adcMask,
        string role,
        int reportHz,
        int thumbPin,
        int indexPin,
        int middlePin,
        int ringPin,
        int pinkyPin,
        int trackingSwitchPin,
        string trackingSwitchMode,
        int joystickVrxPin,
        int joystickVryPin,
        int joystickSwPin,
        int batteryAdcPin,
        int batteryChargePin)
    {
        return "OFADC_CFG " + BuildQuery(new Dictionary<string, string>
        {
            ["host_ip"] = hostIp,
            ["udp_port"] = udpPort.ToString(),
            ["adc_mask"] = adcMask.ToString(),
            ["role"] = role,
            ["report_hz"] = reportHz.ToString(),
            ["thumb_pin"] = thumbPin.ToString(),
            ["index_pin"] = indexPin.ToString(),
            ["middle_pin"] = middlePin.ToString(),
            ["ring_pin"] = ringPin.ToString(),
            ["pinky_pin"] = pinkyPin.ToString(),
            ["tracking_switch_pin"] = trackingSwitchPin.ToString(),
            ["tracking_switch_mode"] = trackingSwitchMode,
            ["joystick_vrx_pin"] = joystickVrxPin.ToString(),
            ["joystick_vry_pin"] = joystickVryPin.ToString(),
            ["joystick_sw_pin"] = joystickSwPin.ToString(),
            ["battery_adc_pin"] = batteryAdcPin.ToString(),
            ["battery_charge_pin"] = batteryChargePin.ToString()
        });
    }

    public static async Task SendSerialCommandAsync(string portName, string command, int timeoutMs = 3500)
    {
        await Task.Run(() =>
        {
            using var port = CreatePort(portName, timeoutMs);
            port.Open();
            port.WriteLine(command);
        });
    }

    public static async Task<SerialStatusDto> QuerySerialStatusAsync(string portName, int timeoutMs = 4500)
    {
        return await Task.Run(() =>
        {
            using var port = CreatePort(portName, timeoutMs);
            port.Open();
            port.WriteLine("OFSTATUS");
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                string line;
                try
                {
                    line = port.ReadLine().Trim();
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (line.StartsWith("OFSTATUS ", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseStatusJson(line["OFSTATUS ".Length..]);
                }
            }

            throw new InvalidOperationException("device did not answer OFSTATUS");
        });
    }

    public static string ResolveHostIp(string configuredHostIp, string? deviceIp)
    {
        if (!string.IsNullOrWhiteSpace(configuredHostIp)
            && !string.Equals(configuredHostIp, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configuredHostIp;
        }

        var resolved = ResolveLocalIpv4Near(deviceIp);
        return string.IsNullOrWhiteSpace(resolved) ? "auto" : resolved;
    }

    public static bool IsLocalHostIp(string? value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Any(item => item.Address.AddressFamily == AddressFamily.InterNetwork && item.Address.Equals(address));
    }

    public static bool IsDeprioritizedLocalIp(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            || value.StartsWith("127.", StringComparison.Ordinal)
            || value.StartsWith("169.254.", StringComparison.Ordinal)
            || value == "0.0.0.0";
    }

    private static SerialPort CreatePort(string portName, int timeoutMs) => new(portName, 115200)
    {
        NewLine = "\n",
        ReadTimeout = Math.Max(250, timeoutMs / 4),
        WriteTimeout = timeoutMs,
        DtrEnable = true,
        RtsEnable = true,
        Encoding = Encoding.ASCII
    };

    private static SerialStatusDto ParseStatusJson(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("invalid OFSTATUS json");
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

    private static string BuildQuery(IReadOnlyDictionary<string, string> fields)
    {
        return string.Join("&", fields.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));
    }

    private static string? GetString(JsonObject root, string name) => root.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;
    private static int GetInt(JsonObject root, string name, int fallback) => root.TryGetPropertyValue(name, out var node) && node is not null && int.TryParse(node.ToString(), out var value) ? value : fallback;
    private static bool? GetBool(JsonObject root, string name) => root.TryGetPropertyValue(name, out var node) && node is not null && bool.TryParse(node.ToString(), out var value) ? value : null;

    private static string ResolveLocalIpv4Near(string? deviceIp)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => item.Address.ToString())
            .Where(item => !IsDeprioritizedLocalIp(item))
            .ToList();

        if (IPAddress.TryParse(deviceIp, out var remote))
        {
            var remoteBytes = remote.GetAddressBytes();
            var sameSubnet = candidates.FirstOrDefault(candidate =>
            {
                var localBytes = IPAddress.Parse(candidate).GetAddressBytes();
                return localBytes[0] == remoteBytes[0] && localBytes[1] == remoteBytes[1] && localBytes[2] == remoteBytes[2];
            });
            if (!string.IsNullOrWhiteSpace(sameSubnet))
            {
                return sameSubnet;
            }
        }

        return candidates.FirstOrDefault() ?? string.Empty;
    }
}
