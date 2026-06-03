#include "service/OpenFingerService.h"
#include "openfinger/OpenFingerVersion.h"

#include "common/RuntimeState.h"
#include "service/SerialDevice.h"
#include "service/ServiceProtocol.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstring>
#include <cstdio>
#include <sstream>

#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>

namespace openfinger
{

namespace
{


constexpr auto kConfigPollInterval = std::chrono::milliseconds(500);
constexpr std::size_t kMaxLogLines = 120;
constexpr auto kDeviceVisibilityGrace = std::chrono::milliseconds(5000);

std::string EscapeJson(std::string_view value)
{
    std::string escaped;
    escaped.reserve(value.size() + 8);
    for (const char ch : value)
    {
        switch (ch)
        {
        case '\\':
            escaped += "\\\\";
            break;
        case '"':
            escaped += "\\\"";
            break;
        case '\r':
            escaped += "\\r";
            break;
        case '\n':
            escaped += "\\n";
            break;
        case '\t':
            escaped += "\\t";
            break;
        default:
            escaped.push_back(ch);
            break;
        }
    }
    return escaped;
}

std::string JsonBool(bool value)
{
    return value ? "true" : "false";
}

std::uint64_t MonotonicMilliseconds()
{
    return static_cast<std::uint64_t>(GetTickCount64());
}

std::string ExtractJsonStringField(std::string_view text, std::string_view key)
{
    const std::string needle = "\"" + std::string(key) + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return {};
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return {};
    }

    const std::size_t quote = text.find('"', colon + 1);
    if (quote == std::string::npos)
    {
        return {};
    }

    std::string result;
    bool escaping = false;
    for (std::size_t index = quote + 1; index < text.size(); ++index)
    {
        const char ch = text[index];
        if (escaping)
        {
            result.push_back(ch);
            escaping = false;
            continue;
        }

        if (ch == '\\')
        {
            escaping = true;
            continue;
        }

        if (ch == '"')
        {
            return result;
        }

        result.push_back(ch);
    }

    return {};
}

int ExtractJsonIntField(std::string_view text, std::string_view key, int fallback)
{
    const std::string needle = "\"" + std::string(key) + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return fallback;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return fallback;
    }

    std::size_t start = colon + 1;
    while (start < text.size() && std::isspace(static_cast<unsigned char>(text[start])))
    {
        ++start;
    }

    std::size_t end = start;
    while (end < text.size() && (std::isdigit(static_cast<unsigned char>(text[end])) || text[end] == '-'))
    {
        ++end;
    }

    if (start == end)
    {
        return fallback;
    }

    return std::stoi(std::string(text.substr(start, end - start)));
}

double ExtractJsonDoubleField(std::string_view text, std::string_view key, double fallback)
{
    const std::string needle = "\"" + std::string(key) + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return fallback;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return fallback;
    }

    std::size_t start = colon + 1;
    while (start < text.size() && std::isspace(static_cast<unsigned char>(text[start])))
    {
        ++start;
    }

    std::size_t end = start;
    while (end < text.size())
    {
        const char ch = text[end];
        if (!(std::isdigit(static_cast<unsigned char>(ch)) || ch == '-' || ch == '+' || ch == '.'))
        {
            break;
        }
        ++end;
    }

    if (start == end)
    {
        return fallback;
    }

    try
    {
        return std::stod(std::string(text.substr(start, end - start)));
    }
    catch (...)
    {
        return fallback;
    }
}

bool ExtractJsonBoolField(std::string_view text, std::string_view key, bool fallback)
{
    const std::string needle = "\"" + std::string(key) + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return fallback;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return fallback;
    }

    std::size_t start = colon + 1;
    while (start < text.size() && std::isspace(static_cast<unsigned char>(text[start])))
    {
        ++start;
    }

    if (text.compare(start, 4, "true") == 0)
    {
        return true;
    }

    if (text.compare(start, 5, "false") == 0)
    {
        return false;
    }

    return fallback;
}

std::string PacketSourceIp(std::string_view endpoint)
{
    const std::size_t colon = endpoint.find(':');
    if (colon == std::string::npos)
    {
        return std::string(endpoint);
    }

    return std::string(endpoint.substr(0, colon));
}

std::string PipeNamePath(std::string_view pipe_name)
{
    return "\\\\.\\pipe\\" + std::string(pipe_name);
}

bool IsAutoHostValue(std::string_view value)
{
    if (value.empty())
    {
        return true;
    }

    std::string lowered(value);
    std::transform(lowered.begin(), lowered.end(), lowered.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return lowered == "auto";
}

std::string DetectLocalIpv4Address()
{
    char host_name[256] = {};
    if (gethostname(host_name, sizeof(host_name)) != 0)
    {
        return {};
    }

    addrinfo hints = {};
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_DGRAM;

    addrinfo* results = nullptr;
    if (getaddrinfo(host_name, nullptr, &hints, &results) != 0)
    {
        return {};
    }

    std::string selected;
    for (addrinfo* node = results; node != nullptr; node = node->ai_next)
    {
        if (node->ai_family != AF_INET || node->ai_addr == nullptr)
        {
            continue;
        }

        const sockaddr_in* addr = reinterpret_cast<const sockaddr_in*>(node->ai_addr);
        char buffer[INET_ADDRSTRLEN] = {};
        if (inet_ntop(AF_INET, &addr->sin_addr, buffer, sizeof(buffer)) == nullptr)
        {
            continue;
        }

        std::string candidate(buffer);
        if (candidate == "127.0.0.1" || candidate == "0.0.0.0")
        {
            continue;
        }

        selected = candidate;
        if (candidate.rfind("192.168.", 0) == 0 || candidate.rfind("10.", 0) == 0
            || candidate.rfind("172.", 0) == 0)
        {
            break;
        }
    }

    freeaddrinfo(results);
    return selected;
}

std::string ResolveProvisionHostIp(std::string_view requested_host_ip, const AppConfig& config)
{
    if (!IsAutoHostValue(requested_host_ip))
    {
        return std::string(requested_host_ip);
    }

    if (!IsAutoHostValue(config.runtime.host_ip))
    {
        return config.runtime.host_ip;
    }

    return DetectLocalIpv4Address();
}

std::string NormalizeDeviceName(std::string_view value)
{
    std::string normalized;
    normalized.reserve(value.size());
    for (const char ch : value)
    {
        if (!std::isspace(static_cast<unsigned char>(ch)))
        {
            normalized.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
        }
    }
    return normalized;
}

bool IsSpecificHardwareName(std::string_view value)
{
    const std::string normalized = NormalizeDeviceName(value);
    return normalized.rfind("openfinger-", 0) == 0 && normalized.size() > std::strlen("openfinger-");
}

bool NamesReferSamePhysical(std::string_view left, std::string_view right)
{
    return IsSpecificHardwareName(left) && IsSpecificHardwareName(right)
        && NormalizeDeviceName(left) == NormalizeDeviceName(right);
}

bool DevicesReferSamePhysical(const ServiceDeviceView& left, const ServiceDeviceView& right)
{
    return (!left.mac.empty() && !right.mac.empty() && left.mac == right.mac)
        || (!left.serial_port.empty() && !right.serial_port.empty() && left.serial_port == right.serial_port)
        || NamesReferSamePhysical(left.name, right.name);
}

bool DeviceMatchesSaved(const ServiceDeviceView& device, const KnownDeviceConfig& saved)
{
    return (!device.mac.empty() && saved.mac == device.mac)
        || (!device.serial_port.empty() && saved.serial_port == device.serial_port)
        || NamesReferSamePhysical(device.name, saved.name);
}

} // namespace

OpenFingerService::OpenFingerService()
    : filter_(config_store_.config())
{
}

OpenFingerService::~OpenFingerService()
{
    Stop();
}

bool OpenFingerService::Start(std::string* out_error)
{
    if (running_)
    {
        return true;
    }

    if (!config_store_.LoadOrCreate(&startup_error_))
    {
        if (out_error != nullptr)
        {
            *out_error = startup_error_;
        }
        return false;
    }

    filter_.SetConfig(config_store_.config());
    RefreshConfigWriteTime();

    std::string receiver_error;
    if (!receiver_.Start(static_cast<std::uint16_t>(config_store_.config().service.raw_input_udp_port), &receiver_error))
    {
        startup_error_ = receiver_error;
        if (out_error != nullptr)
        {
            *out_error = startup_error_;
        }
        return false;
    }

    running_ = true;
    AppendLog(
        "service started raw_input="
        + std::to_string(config_store_.config().service.raw_input_udp_port)
        + " runtime_publish=control_only");
    runtime_thread_ = std::thread(&OpenFingerService::RuntimeLoop, this);
    discovery_thread_ = std::thread(&OpenFingerService::DiscoveryLoop, this);
    pipe_thread_ = std::thread(&OpenFingerService::PipeLoop, this);
    return true;
}

void OpenFingerService::Stop()
{
    running_ = false;

    HANDLE pipe = reinterpret_cast<HANDLE>(pipe_handle_);
    if (pipe != nullptr && pipe != INVALID_HANDLE_VALUE)
    {
        CancelIoEx(pipe, nullptr);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        pipe_handle_ = static_cast<std::uintptr_t>(-1);
    }

    if (runtime_thread_.joinable())
    {
        runtime_thread_.join();
    }
    if (discovery_thread_.joinable())
    {
        discovery_thread_.join();
    }
    if (pipe_thread_.joinable())
    {
        pipe_thread_.join();
    }
    receiver_.Stop();
}

bool OpenFingerService::IsRunning() const
{
    return running_.load();
}

void OpenFingerService::WaitForExit()
{
    while (running_)
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
}

void OpenFingerService::RefreshConfigWriteTime()
{
    std::error_code ec;
    config_write_time_ = std::filesystem::last_write_time(config_store_.path(), ec);
    if (ec)
    {
        config_write_time_ = {};
    }
}

bool OpenFingerService::ReloadConfigIfChanged()
{
    const auto now = std::chrono::steady_clock::now();
    if (last_config_poll_.time_since_epoch().count() != 0 && (now - last_config_poll_) < kConfigPollInterval)
    {
        return true;
    }

    last_config_poll_ = now;

    std::error_code ec;
    const auto current_write_time = std::filesystem::last_write_time(config_store_.path(), ec);
    if (ec || current_write_time == config_write_time_)
    {
        return true;
    }

    std::string error;
    if (!config_store_.LoadOrCreate(&error))
    {
        AppendLog("config reload failed: " + error);
        return false;
    }

    filter_.SetConfig(config_store_.config());
    config_write_time_ = current_write_time;
    AppendLog("config reloaded from disk");
    return true;
}

void OpenFingerService::RuntimeLoop()
{
    using clock = std::chrono::steady_clock;

    std::vector<ReceivedAdcPacket> packets;
    const int publish_hz = std::max(10, config_store_.config().runtime.publish_hz);
    const auto frame_interval = std::chrono::duration_cast<clock::duration>(std::chrono::duration<double>(1.0 / publish_hz));

    while (running_)
    {
        const auto frame_start = clock::now();

        ReloadConfigIfChanged();

        packets.clear();
        receiver_.DrainPackets(&packets);
        bool config_changed = false;
        for (const auto& packet : packets)
        {
            std::string source_mac;
            std::string source_ip;
            const HandRole role = ResolvePacketRole(packet, &source_mac, &source_ip);
            if (role == HandRole::Unknown)
            {
                continue;
            }

            const HandSide side = role == HandRole::Left ? HandSide::Left : HandSide::Right;
            config_changed = filter_.ProcessPacket(side, packet) || config_changed;
        }

        filter_.Tick(frame_start);
        if (config_changed)
        {
            config_store_.mutable_config() = filter_.config();
            SaveConfig();
        }

        filter_.BuildRuntimeFrame(++runtime_seq_, MonotonicMilliseconds());

        std::this_thread::sleep_until(frame_start + frame_interval);
    }
}

void OpenFingerService::DiscoveryLoop()
{
    while (running_)
    {
        RefreshDiscoveredDevices();
        std::this_thread::sleep_for(std::chrono::milliseconds(config_store_.config().service.discovery_poll_ms));
    }
}

void OpenFingerService::PipeLoop()
{
    const std::string pipe_name = PipeNamePath(config_store_.config().service.pipe_name);

    while (running_)
    {
        HANDLE pipe = CreateNamedPipeA(
            pipe_name.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            1,
            8192,
            8192,
            500,
            nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            AppendLog("failed to create named pipe");
            std::this_thread::sleep_for(std::chrono::milliseconds(1000));
            continue;
        }

        pipe_handle_ = reinterpret_cast<std::uintptr_t>(pipe);
        const BOOL connected = ConnectNamedPipe(pipe, nullptr) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED ? TRUE : FALSE);
        if (!connected)
        {
            CloseHandle(pipe);
            pipe_handle_ = static_cast<std::uintptr_t>(-1);
            if (running_)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            }
            continue;
        }

        char buffer[8192] = {};
        DWORD bytes_read = 0;
        std::string response = "{\"ok\":false,\"message\":\"empty request\"}";
        if (ReadFile(pipe, buffer, sizeof(buffer) - 1, &bytes_read, nullptr) && bytes_read > 0)
        {
            buffer[bytes_read] = '\0';
            response = HandleRequest(buffer);
        }

        DWORD bytes_written = 0;
        WriteFile(pipe, response.c_str(), static_cast<DWORD>(response.size()), &bytes_written, nullptr);
        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        pipe_handle_ = static_cast<std::uintptr_t>(-1);
    }
}

void OpenFingerService::AppendLog(std::string message)
{
    std::lock_guard<std::mutex> lock(logs_mutex_);
    if (logs_.size() >= kMaxLogLines)
    {
        logs_.pop_front();
    }

    SYSTEMTIME local_time {};
    GetLocalTime(&local_time);
    char prefix[32] = {};
    std::snprintf(
        prefix,
        sizeof(prefix),
        "%02u:%02u:%02u",
        static_cast<unsigned>(local_time.wHour),
        static_cast<unsigned>(local_time.wMinute),
        static_cast<unsigned>(local_time.wSecond));

    logs_.push_back(std::string(prefix) + "  " + message);
}

std::string OpenFingerService::BuildCommandResponse(
    bool ok,
    std::string_view title,
    std::string_view user_message,
    std::string_view error_code,
    bool retryable) const
{
    std::ostringstream stream;
    stream << "{"
           << "\"ok\":" << JsonBool(ok) << ","
           << "\"title\":\"" << EscapeJson(title) << "\","
           << "\"user_message\":\"" << EscapeJson(user_message) << "\","
           << "\"error_code\":\"" << EscapeJson(error_code) << "\","
           << "\"retryable\":" << JsonBool(retryable) << ","
           << "\"message\":\"" << EscapeJson(user_message) << "\""
           << "}";
    return stream.str();
}

bool OpenFingerService::CanControlDevice(
    const ServiceDeviceView& device,
    std::string* out_reason,
    std::string* out_error_code) const
{
    if (!device.online)
    {
        if (out_reason != nullptr)
        {
            *out_reason = "设备当前离线，先让它重新连接。";
        }
        if (out_error_code != nullptr)
        {
            *out_error_code = "device_offline";
        }
        return false;
    }

    if (!device.serial_port.empty())
    {
        if (out_reason != nullptr)
        {
            out_reason->clear();
        }
        if (out_error_code != nullptr)
        {
            out_error_code->clear();
        }
        return true;
    }

    if (out_reason != nullptr)
    {
        *out_reason = "当前只支持 USB 配网和设备控制，请先连接 USB。";
    }
    if (out_error_code != nullptr)
    {
        *out_error_code = "usb_required";
    }
    return false;
}

std::string OpenFingerService::BuildSnapshotResponse()
{
    const auto now = std::chrono::steady_clock::now();
    const HandRuntimeState& left = filter_.hand_state(HandSide::Left);
    const HandRuntimeState& right = filter_.hand_state(HandSide::Right);

    std::vector<ServiceDeviceView> devices_copy;
    {
        std::lock_guard<std::mutex> lock(devices_mutex_);
        devices_copy = devices_;
    }

    std::deque<std::string> logs_copy;
    {
        std::lock_guard<std::mutex> lock(logs_mutex_);
        logs_copy = logs_;
    }

    const auto display_name = [](const ServiceDeviceView& device) -> std::string {
        if (!device.name.empty())
        {
            return device.name;
        }
        if (!device.mac.empty())
        {
            return "openfinger-" + device.mac.substr(device.mac.size() > 4 ? device.mac.size() - 4 : 0);
        }
        if (!device.serial_port.empty())
        {
            return "openfinger-" + device.serial_port;
        }
        return "OpenFinger";
    };

    const auto transport_label = [](const ServiceDeviceView& device) -> std::string {
        if (!device.serial_port.empty())
        {
            return "USB";
        }

        if (!device.sta_ip.empty() || device.wifi_connected || device.adc_streaming)
        {
            return "Wi-Fi";
        }

        return "未知";
    };

    const auto role_label = [](HandRole role) -> std::string {
        switch (role)
        {
        case HandRole::Left:
            return "左手";
        case HandRole::Right:
            return "右手";
        default:
            return "未指定";
        }
    };

    const auto is_configured = [](const ServiceDeviceView& device) -> bool {
        const bool have_role = device.effective_role != HandRole::Unknown || device.preferred_role != HandRole::Unknown;
        const bool have_network = !device.sta_ip.empty() || device.wifi_connected || device.adc_streaming;
        return device.remembered || (have_role && have_network);
    };

    auto serialize_hand = [&](const HandRuntimeState& hand) -> std::string {
        std::ostringstream stream;
        stream << "{";
        stream << "\"present\":" << JsonBool(hand.present) << ",";
        stream << "\"stale\":" << JsonBool(hand.stale) << ",";
        stream << "\"packet_fps\":" << hand.packet_fps << ",";
        stream << "\"fingers\":{";
        for (std::size_t index = 0; index < kFingerCount; ++index)
        {
            const auto& finger = hand.fingers[index];
            const FingerName finger_name = FingerNameFromIndex(index);
            const FingerConfig& finger_config = GetFingerConfig(
                config_store_.config(),
                hand.side,
                finger_name);
            stream << "\"" << ToString(FingerNameFromIndex(index)) << "\":{"
                   << "\"bend\":" << finger.bend_smoothed << ","
                   << "\"raw\":" << finger.raw << ","
                   << "\"adc_channel\":" << finger_config.adc_channel << ","
                   << "\"center_raw\":" << finger.center_raw << ","
                   << "\"direction\":\"" << ToString(finger.direction) << "\","
                   << "\"deadzone\":" << finger_config.deadzone << ","
                   << "\"smoothing_alpha\":" << finger_config.smoothing_alpha << ","
                   << "\"enabled\":" << JsonBool(finger.enabled) << ","
                   << "\"stale\":" << JsonBool(finger.stale) << "}";
            if (index + 1 != kFingerCount)
            {
                stream << ",";
            }
        }
        stream << "}}";
        return stream.str();
    };

    std::ostringstream stream;
    stream << "{\"ok\":true,\"snapshot\":{";
    stream << "\"version\":\"" << OPENFINGER_VERSION << "\",";
    stream << "\"protocol_version\":" << OPENFINGER_PROTOCOL_VERSION << ",";
    stream << "\"config_path\":\"" << EscapeJson(config_store_.path().string()) << "\",";
    stream << "\"host_ip\":\"" << EscapeJson(config_store_.config().runtime.host_ip) << "\",";
    stream << "\"runtime_port\":" << config_store_.config().runtime.device_udp_port << ",";
    stream << "\"local_runtime_port\":" << config_store_.config().runtime.local_runtime_udp_port << ",";
    stream << "\"controller_bridge_port\":" << config_store_.config().controller_bridge.udp_port << ",";
    stream << "\"service_pipe_name\":\"" << EscapeJson(config_store_.config().service.pipe_name) << "\",";
    stream << "\"hands\":{\"left\":" << serialize_hand(left) << ",\"right\":" << serialize_hand(right) << "},";
    stream << "\"devices\":[";
    for (std::size_t index = 0; index < devices_copy.size(); ++index)
    {
        const auto& device = devices_copy[index];
        const auto age_ms = device.online
            ? std::chrono::duration_cast<std::chrono::milliseconds>(now - device.last_seen).count()
            : -1;
        const bool configured = is_configured(device);
        std::string action_block_reason;
        std::string action_block_code;
        const bool can_control = CanControlDevice(device, &action_block_reason, &action_block_code);
        const bool can_identify = can_control;
        const bool can_assign_role = can_control;
        const bool can_provision = can_control;
        const bool can_reset = can_control;

        std::string ui_state;
        std::string ui_status_title;
        std::string ui_status_detail;
        std::string setup_stage;
        if (!device.online && configured)
        {
            ui_state = "offline";
            ui_status_title = "当前离线";
            ui_status_detail = "设备已配对，等待重新连接。";
            setup_stage = "done";
        }
        else if (!device.online)
        {
            ui_state = "offline";
            ui_status_title = "当前离线";
            ui_status_detail = "设备没有连接到电脑。";
            setup_stage = "discover";
        }
        else if (!can_control)
        {
            ui_state = "blocked";
            ui_status_title = "已发现，等待连接";
            ui_status_detail = action_block_reason;
            setup_stage = "discover";
        }
        else if (configured)
        {
            ui_state = "configured";
            ui_status_title = "已配对";
            if (device.adc_streaming)
            {
                ui_status_detail = "正在发送手指数据。";
            }
            else if (device.wifi_connected)
            {
                ui_status_detail = "网络已连接，等待开始发送数据。";
            }
            else
            {
                ui_status_detail = "配置已保存，可以重新识别或改写网络。";
            }
            setup_stage = "done";
        }
        else if (device.effective_role == HandRole::Unknown && device.preferred_role == HandRole::Unknown)
        {
            ui_state = "new";
            ui_status_title = "已发现";
            ui_status_detail = "先确认设备，再设置左右手。";
            setup_stage = "identify";
        }
        else
        {
            ui_state = "ready";
            ui_status_title = "等待写入";
            ui_status_detail = "角色已确定，可以写入网络。";
            setup_stage = "network";
        }

        stream << "{"
               << "\"id\":\"" << EscapeJson(device.id) << "\","
               << "\"name\":\"" << EscapeJson(device.name) << "\","
               << "\"display_name\":\"" << EscapeJson(display_name(device)) << "\","
               << "\"mac\":\"" << EscapeJson(device.mac) << "\","
               << "\"ble_address\":\"" << EscapeJson(device.ble_address) << "\","
               << "\"serial_port\":\"" << EscapeJson(device.serial_port) << "\","
               << "\"sta_ip\":\"" << EscapeJson(device.sta_ip) << "\","
               << "\"state\":\"" << EscapeJson(device.state) << "\","
               << "\"message\":\"" << EscapeJson(device.message) << "\","
               << "\"reported_role\":\"" << ToString(device.reported_role) << "\","
               << "\"preferred_role\":\"" << ToString(device.preferred_role) << "\","
               << "\"effective_role\":\"" << ToString(device.effective_role) << "\","
               << "\"transport\":\"" << ToString(device.transport) << "\","
               << "\"online\":" << JsonBool(device.online) << ","
               << "\"remembered\":" << JsonBool(device.remembered) << ","
               << "\"wifi_connected\":" << JsonBool(device.wifi_connected) << ","
               << "\"adc_streaming\":" << JsonBool(device.adc_streaming) << ","
               << "\"ui_state\":\"" << EscapeJson(ui_state) << "\","
               << "\"ui_status_title\":\"" << EscapeJson(ui_status_title) << "\","
               << "\"ui_status_detail\":\"" << EscapeJson(ui_status_detail) << "\","
               << "\"setup_stage\":\"" << EscapeJson(setup_stage) << "\","
               << "\"is_configured\":" << JsonBool(configured) << ","
               << "\"can_identify\":" << JsonBool(can_identify) << ","
               << "\"can_provision\":" << JsonBool(can_provision) << ","
               << "\"can_assign_role\":" << JsonBool(can_assign_role) << ","
               << "\"can_reset\":" << JsonBool(can_reset) << ","
               << "\"action_block_reason\":\"" << EscapeJson(action_block_reason) << "\","
               << "\"last_error_user\":\"" << EscapeJson(device.last_error_user) << "\","
               << "\"transport_label\":\"" << EscapeJson(transport_label(device)) << "\","
               << "\"role_label\":\"" << EscapeJson(role_label(device.effective_role)) << "\","
               << "\"udp_port\":" << device.udp_port << ","
               << "\"adc_mask\":" << device.adc_mask << ","
               << "\"seq\":" << device.seq << ","
               << "\"last_seen_ms\":" << age_ms << ","
               << "\"diagnostics\":{"
               << "\"raw_state\":\"" << EscapeJson(device.state) << "\","
               << "\"raw_message\":\"" << EscapeJson(device.message) << "\","
               << "\"last_error_code\":\"" << EscapeJson(device.last_error_code) << "\""
               << "}"
               << "}";
        if (index + 1 != devices_copy.size())
        {
            stream << ",";
        }
    }
    stream << "],\"logs\":[";
    for (std::size_t index = 0; index < logs_copy.size(); ++index)
    {
        stream << "\"" << EscapeJson(logs_copy[index]) << "\"";
        if (index + 1 != logs_copy.size())
        {
            stream << ",";
        }
    }
    stream << "]}}";
    return stream.str();
}

std::string OpenFingerService::HandleRequest(std::string_view request_line)
{
    const std::string command = ExtractJsonStringField(request_line, "command");
    if (command == "ping")
    {
        return BuildCommandResponse(true, "Service 已连接", "Service 已连接。");
    }

    if (command == "get_snapshot")
    {
        return BuildSnapshotResponse();
    }

    if (command == "identify")
    {
        return HandleIdentify(ExtractJsonStringField(request_line, "device_id"));
    }

    if (command == "assign_role")
    {
        return HandleAssignRole(
            ExtractJsonStringField(request_line, "device_id"),
            HandRoleFromString(ExtractJsonStringField(request_line, "role")));
    }

    if (command == "provision")
    {
        return HandleProvision(
            ExtractJsonStringField(request_line, "device_id"),
            ExtractJsonStringField(request_line, "ssid"),
            ExtractJsonStringField(request_line, "password"),
            ExtractJsonStringField(request_line, "host_ip"),
            ExtractJsonIntField(request_line, "udp_port", config_store_.config().runtime.device_udp_port),
            ExtractJsonIntField(request_line, "adc_mask", 31),
            HandRoleFromString(ExtractJsonStringField(request_line, "role")));
    }

    if (command == "provision_all_usb")
    {
        return HandleProvisionAllUsb(
            ExtractJsonStringField(request_line, "ssid"),
            ExtractJsonStringField(request_line, "password"),
            ExtractJsonStringField(request_line, "host_ip"),
            ExtractJsonIntField(request_line, "udp_port", config_store_.config().runtime.device_udp_port),
            ExtractJsonIntField(request_line, "adc_mask", 31));
    }

    if (command == "reset_device")
    {
        return HandleResetDevice(ExtractJsonStringField(request_line, "device_id"));
    }

    if (command == "forget_device")
    {
        return HandleForgetDevice(ExtractJsonStringField(request_line, "device_id"));
    }

    if (command == "calibrate")
    {
        HandSide side = HandSide::Left;
        FingerName finger = FingerName::Index;
        TryHandSideFromString(ExtractJsonStringField(request_line, "hand"), &side);
        TryFingerNameFromString(ExtractJsonStringField(request_line, "finger"), &finger);
        return HandleCalibrate(side, finger);
    }

    if (command == "reset_center")
    {
        HandSide side = HandSide::Left;
        FingerName finger = FingerName::Index;
        TryHandSideFromString(ExtractJsonStringField(request_line, "hand"), &side);
        TryFingerNameFromString(ExtractJsonStringField(request_line, "finger"), &finger);
        return HandleResetCenter(side, finger);
    }

    if (command == "cycle_direction")
    {
        HandSide side = HandSide::Left;
        FingerName finger = FingerName::Index;
        TryHandSideFromString(ExtractJsonStringField(request_line, "hand"), &side);
        TryFingerNameFromString(ExtractJsonStringField(request_line, "finger"), &finger);
        return HandleCycleDirection(side, finger);
    }

    if (command == "update_finger")
    {
        HandSide side = HandSide::Left;
        FingerName finger = FingerName::Index;
        TryHandSideFromString(ExtractJsonStringField(request_line, "hand"), &side);
        TryFingerNameFromString(ExtractJsonStringField(request_line, "finger"), &finger);

        FingerConfig config = GetFingerConfig(config_store_.config(), side, finger);
        config.adc_channel = ExtractJsonIntField(request_line, "adc_channel", config.adc_channel);
        config.center_raw = ExtractJsonIntField(request_line, "center_raw", config.center_raw);

        const std::string direction_text = ExtractJsonStringField(request_line, "direction");
        if (!direction_text.empty())
        {
            config.direction = BendDirectionFromString(direction_text);
        }

        config.deadzone = ExtractJsonDoubleField(request_line, "deadzone", config.deadzone);
        config.smoothing_alpha = ExtractJsonDoubleField(request_line, "smoothing_alpha", config.smoothing_alpha);
        config.enabled = ExtractJsonBoolField(request_line, "enabled", config.enabled);
        return HandleUpdateFingerConfig(side, finger, config);
    }

    if (command == "rescan")
    {
        RefreshDiscoveredDevices();
        return BuildCommandResponse(true, "已刷新", "设备列表已更新。");
    }

    return BuildCommandResponse(false, "无法识别", "当前命令不受支持。", "unknown_command", false);
}

std::string OpenFingerService::HandleIdentify(std::string_view device_id)
{
    std::lock_guard<std::mutex> lock(devices_mutex_);
    ServiceDeviceView* device = FindDeviceById(device_id);
    if (device == nullptr)
    {
        return BuildCommandResponse(false, "找不到设备", "没有找到这个设备。", "device_not_found", false);
    }

    std::string block_reason;
    std::string block_code;
    if (!CanControlDevice(*device, &block_reason, &block_code))
    {
        device->last_error_user = block_reason;
        device->last_error_code = block_code;
        return BuildCommandResponse(false, "暂时不能识别", block_reason, block_code, true);
    }

    std::string error;
    const bool ok = SendIdentifyOverSerial(device->serial_port, &error);
    if (!ok)
    {
        device->last_error_user = "设备没有响应，请重试。";
        device->last_error_code = "identify_failed";
        if (!error.empty())
        {
            AppendLog("identify failed for " + device->id + ": " + error);
        }
        return BuildCommandResponse(false, "识别失败", device->last_error_user, device->last_error_code, true);
    }

    device->last_error_user.clear();
    device->last_error_code.clear();
    AppendLog("identify " + device->id);
    return BuildCommandResponse(true, "识别已发送", "设备识别灯指令已发送。");
}

std::string OpenFingerService::HandleAssignRole(std::string_view device_id, HandRole role)
{
    if (role == HandRole::Unknown)
    {
        return BuildCommandResponse(false, "角色无效", "请先选择左手或右手。", "invalid_role", false);
    }

    std::lock_guard<std::mutex> lock(devices_mutex_);
    ServiceDeviceView* device = FindDeviceById(device_id);
    if (device == nullptr)
    {
        return BuildCommandResponse(false, "找不到设备", "没有找到这个设备。", "device_not_found", false);
    }

    std::string block_reason;
    std::string block_code;
    if (!CanControlDevice(*device, &block_reason, &block_code))
    {
        device->last_error_user = block_reason;
        device->last_error_code = block_code;
        return BuildCommandResponse(false, "暂时不能设置", block_reason, block_code, true);
    }

    std::string error;
    const bool ok = SendRoleOverSerial(device->serial_port, role, &error);
    if (!ok)
    {
        device->last_error_user = "角色没有写入成功，请再试一次。";
        device->last_error_code = "assign_role_failed";
        if (!error.empty())
        {
            AppendLog("assign role failed for " + device->id + ": " + error);
        }
        return BuildCommandResponse(false, "设置失败", device->last_error_user, device->last_error_code, true);
    }

    device->preferred_role = role;
    device->effective_role = role;
    device->last_error_user.clear();
    device->last_error_code.clear();
    if (KnownDeviceConfig* saved = UpsertKnownDevice(*device))
    {
        saved->preferred_role = role;
        saved->saved_role = role;
    }
    SaveConfig();
    AppendLog("role " + device->id + " -> " + std::string(ToString(role)));
    return BuildCommandResponse(true, "角色已保存", "左右手设置已保存到设备。");
}

std::string OpenFingerService::HandleProvision(
    std::string_view device_id,
    std::string_view ssid,
    std::string_view password,
    std::string_view host_ip,
    int udp_port,
    int adc_mask,
    HandRole role)
{
    if (ssid.empty())
    {
        return BuildCommandResponse(false, "需要 Wi-Fi 名称", "请先填写 Wi-Fi 名称。", "ssid_required", false);
    }

    std::lock_guard<std::mutex> lock(devices_mutex_);
    ServiceDeviceView* device = FindDeviceById(device_id);
    if (device == nullptr)
    {
        return BuildCommandResponse(false, "找不到设备", "没有找到这个设备。", "device_not_found", false);
    }

    std::string block_reason;
    std::string block_code;
    if (!CanControlDevice(*device, &block_reason, &block_code))
    {
        device->last_error_user = block_reason;
        device->last_error_code = block_code;
        return BuildCommandResponse(false, "暂时不能写入", block_reason, block_code, true);
    }

    const std::string resolved_host_ip = ResolveProvisionHostIp(host_ip, config_store_.config());
    if (resolved_host_ip.empty())
    {
        device->last_error_user = "没有找到可用的本机局域网 IP。";
        device->last_error_code = "host_ip_unresolved";
        return BuildCommandResponse(false, "找不到主机地址", device->last_error_user, device->last_error_code, true);
    }

    ProvisionRequest request;
    request.ssid = std::string(ssid);
    request.password = std::string(password);
    request.host_ip = resolved_host_ip;
    request.udp_port = udp_port;
    request.adc_mask = adc_mask;
    request.role = role;

    std::string error;
    const bool ok = SendProvisionOverSerial(device->serial_port, request, &error);
    if (!ok)
    {
        device->last_error_user = "配置没有写入成功，请重试。";
        device->last_error_code = "provision_failed";
        if (!error.empty())
        {
            AppendLog("provision failed for " + device->id + ": " + error);
        }
        return BuildCommandResponse(false, "写入失败", device->last_error_user, device->last_error_code, true);
    }

    device->preferred_role = role;
    device->effective_role = role;
    device->udp_port = udp_port;
    device->adc_mask = adc_mask;
    device->remembered = true;
    device->last_error_user.clear();
    device->last_error_code.clear();
    if (KnownDeviceConfig* saved = UpsertKnownDevice(*device))
    {
        saved->preferred_role = role;
        saved->saved_role = role;
        saved->udp_port = udp_port;
        saved->adc_mask = adc_mask;
    }
    config_store_.mutable_config().runtime.host_ip = resolved_host_ip;
    SaveConfig();
    AppendLog("provision " + device->id);
    return BuildCommandResponse(true, "写入已发送", "网络和角色设置已发送到设备。");
}

std::string OpenFingerService::HandleProvisionAllUsb(
    std::string_view ssid,
    std::string_view password,
    std::string_view host_ip,
    int udp_port,
    int adc_mask)
{
    if (ssid.empty())
    {
        return BuildCommandResponse(false, "需要 Wi-Fi 名称", "请先填写 Wi-Fi 名称。", "ssid_required", false);
    }

    std::lock_guard<std::mutex> lock(devices_mutex_);
    const std::string resolved_host_ip = ResolveProvisionHostIp(host_ip, config_store_.config());
    if (resolved_host_ip.empty())
    {
        return BuildCommandResponse(false, "找不到主机地址", "没有找到可用的本机局域网 IP。", "host_ip_unresolved", true);
    }

    int sent_count = 0;
    for (auto& device : devices_)
    {
        if (!device.online || device.serial_port.empty())
        {
            continue;
        }

        ProvisionRequest request;
        request.ssid = std::string(ssid);
        request.password = std::string(password);
        request.host_ip = resolved_host_ip;
        request.udp_port = udp_port;
        request.adc_mask = adc_mask;
        request.role = device.effective_role != HandRole::Unknown ? device.effective_role : device.preferred_role;

        std::string error;
        if (!SendProvisionOverSerial(device.serial_port, request, &error))
        {
            AppendLog("provision all failed for " + device.id + ": " + error);
            continue;
        }

        device.udp_port = udp_port;
        device.adc_mask = adc_mask;
        device.remembered = true;
        device.last_error_user.clear();
        device.last_error_code.clear();
        if (KnownDeviceConfig* saved = UpsertKnownDevice(device))
        {
            saved->udp_port = udp_port;
            saved->adc_mask = adc_mask;
        }
        ++sent_count;
    }

    config_store_.mutable_config().runtime.host_ip = resolved_host_ip;
    SaveConfig();

    if (sent_count <= 0)
    {
        return BuildCommandResponse(false, "没有可写入的设备", "没有找到当前在线的 USB 设备。", "no_online_usb_devices", true);
    }

    AppendLog("provision all usb count=" + std::to_string(sent_count));
    return BuildCommandResponse(true, "批量写入已发送", "已向 " + std::to_string(sent_count) + " 台设备发送配置。");
}

std::string OpenFingerService::HandleResetDevice(std::string_view device_id)
{
    std::lock_guard<std::mutex> lock(devices_mutex_);
    ServiceDeviceView* device = FindDeviceById(device_id);
    if (device == nullptr)
    {
        return BuildCommandResponse(false, "找不到设备", "没有找到这个设备。", "device_not_found", false);
    }

    std::string block_reason;
    std::string block_code;
    if (!CanControlDevice(*device, &block_reason, &block_code))
    {
        device->last_error_user = block_reason;
        device->last_error_code = block_code;
        return BuildCommandResponse(false, "暂时不能重置", block_reason, block_code, true);
    }

    std::string error;
    const bool ok = ResetDeviceOverSerial(device->serial_port, &error);
    if (!ok)
    {
        device->last_error_user = "重置指令没有发送成功，请重试。";
        device->last_error_code = "reset_failed";
        if (!error.empty())
        {
            AppendLog("reset failed for " + device->id + ": " + error);
        }
        return BuildCommandResponse(false, "重置失败", device->last_error_user, device->last_error_code, true);
    }

    device->last_error_user.clear();
    device->last_error_code.clear();
    AppendLog("reset " + device->id);
    return BuildCommandResponse(true, "重置已发送", "重置指令已发送到设备。");
}

std::string OpenFingerService::HandleForgetDevice(std::string_view device_id)
{
    std::lock_guard<std::mutex> lock(devices_mutex_);

    auto device_it = std::find_if(devices_.begin(), devices_.end(), [&](const ServiceDeviceView& device) {
        return device.id == device_id;
    });
    if (device_it == devices_.end())
    {
        return BuildCommandResponse(false, "找不到设备", "没有找到这个设备。", "device_not_found", false);
    }

    auto& saved_devices = config_store_.mutable_config().devices;
    const auto saved_it = std::remove_if(saved_devices.begin(), saved_devices.end(), [&](const KnownDeviceConfig& saved) {
        return (!device_it->mac.empty() && saved.mac == device_it->mac)
            || (!device_it->serial_port.empty() && saved.serial_port == device_it->serial_port)
            || (!device_it->ble_address.empty() && saved.ble_address == device_it->ble_address)
            || device_it->id == saved.name;
    });
    saved_devices.erase(saved_it, saved_devices.end());

    devices_.erase(device_it);
    SaveConfig();
    AppendLog("forget " + std::string(device_id));
    return BuildCommandResponse(true, "记录已移除", "本机保存的设备记录已移除。");
}

std::string OpenFingerService::HandleCalibrate(HandSide side, FingerName finger)
{
    if (!filter_.CalibrateCenterFromCurrentRaw(side, finger))
    {
        return BuildCommandResponse(false, "还没有数据", "先让这根手指发送数据，再做校准。", "no_sample_yet", true);
    }

    config_store_.mutable_config() = filter_.config();
    SaveConfig();
    AppendLog("calibrate " + std::string(ToString(side)) + "." + std::string(ToString(finger)));
    return BuildCommandResponse(true, "校准完成", "当前手指的伸直位置已保存。");
}

std::string OpenFingerService::HandleResetCenter(HandSide side, FingerName finger)
{
    filter_.ResetCenter(side, finger);
    config_store_.mutable_config() = filter_.config();
    SaveConfig();
    AppendLog("reset " + std::string(ToString(side)) + "." + std::string(ToString(finger)));
    return BuildCommandResponse(true, "已重置", "这根手指已恢复默认中心值。");
}

std::string OpenFingerService::HandleCycleDirection(HandSide side, FingerName finger)
{
    filter_.CycleDirection(side, finger);
    config_store_.mutable_config() = filter_.config();
    SaveConfig();
    AppendLog("direction " + std::string(ToString(side)) + "." + std::string(ToString(finger)));
    return BuildCommandResponse(true, "方向已切换", "这根手指的方向设置已更新。");
}

std::string OpenFingerService::HandleUpdateFingerConfig(HandSide side, FingerName finger, const FingerConfig& config)
{
    config_store_.mutable_config() = filter_.config();
    FingerConfig& saved = GetFingerConfig(config_store_.mutable_config(), side, finger);
    saved = config;
    filter_.UpdateFingerConfig(side, finger, saved);
    config_store_.mutable_config() = filter_.config();
    SaveConfig();

    AppendLog(
        "finger "
        + std::string(ToString(side))
        + "."
        + std::string(ToString(finger))
        + " adc="
        + std::to_string(saved.adc_channel)
        + " center="
        + std::to_string(saved.center_raw)
        + " dz="
        + std::to_string(saved.deadzone)
        + " ema="
        + std::to_string(saved.smoothing_alpha)
        + " enabled="
        + std::string(saved.enabled ? "1" : "0"));
    return BuildCommandResponse(true, "手指设置已更新", "这根手指的参数已经保存。");
}

void OpenFingerService::RefreshDiscoveredDevices()
{
    std::vector<ServiceDeviceView> previous_devices;
    {
        std::lock_guard<std::mutex> lock(devices_mutex_);
        previous_devices = devices_;
    }

    std::vector<ServiceDeviceView> discovered;
    const auto now = std::chrono::steady_clock::now();
    for (const auto& port : EnumerateSerialPorts())
    {
        DeviceStatusMessage status;
        std::string error;
        if (!ReadDeviceStatusFromSerial(port, &status, &error))
        {
            continue;
        }

        ServiceDeviceView device;
        device.id = !status.mac.empty() ? status.mac : ("usb:" + port);
        device.name = !status.device_name.empty() ? status.device_name : "OpenFinger";
        device.mac = status.mac;
        device.serial_port = port;
        device.sta_ip = status.sta_ip;
        device.state = status.state;
        device.message = status.message;
        device.reported_role = status.role;
        device.effective_role = status.role;
        device.transport = DeviceTransport::Usb;
        device.wifi_connected = status.wifi_connected;
        device.adc_streaming = status.adc_streaming;
        device.online = true;
        device.udp_port = status.udp_port;
        device.adc_mask = status.adc_mask;
        device.seq = status.seq;
        device.last_seen = now;

        for (const auto& saved : config_store_.config().devices)
        {
            if (DeviceMatchesSaved(device, saved))
            {
                device.remembered = true;
                device.preferred_role = saved.preferred_role;
                if (device.effective_role == HandRole::Unknown)
                {
                    device.effective_role = saved.saved_role != HandRole::Unknown ? saved.saved_role : saved.preferred_role;
                }
                if (device.name.empty() && !saved.name.empty())
                {
                    device.name = saved.name;
                }
                if (device.sta_ip.empty())
                {
                    device.sta_ip = saved.sta_ip;
                }
                break;
            }
        }

        if (device.effective_role == HandRole::Unknown)
        {
            device.effective_role = device.reported_role;
        }

        discovered.push_back(std::move(device));
    }

    for (const auto& previous : previous_devices)
    {
        if (!previous.online)
        {
            continue;
        }

        if (now - previous.last_seen > kDeviceVisibilityGrace)
        {
            continue;
        }

        const auto existing = std::find_if(discovered.begin(), discovered.end(), [&](const ServiceDeviceView& current) {
            return DevicesReferSamePhysical(current, previous);
        });
        if (existing != discovered.end())
        {
            continue;
        }

        discovered.push_back(previous);
    }

    {
        std::lock_guard<std::mutex> lock(devices_mutex_);
        devices_ = std::move(discovered);
        for (auto& device : devices_)
        {
            UpsertKnownDevice(device);
        }

        std::vector<ServiceDeviceView> remembered;
        remembered.reserve(config_store_.config().devices.size());
        for (const auto& saved : config_store_.config().devices)
        {
            const auto match = std::find_if(devices_.begin(), devices_.end(), [&](const ServiceDeviceView& device) {
                return DeviceMatchesSaved(device, saved);
            });
            if (match != devices_.end())
            {
                continue;
            }

            ServiceDeviceView offline;
            offline.id = !saved.mac.empty() ? saved.mac : (!saved.serial_port.empty() ? saved.serial_port : saved.name);
            offline.name = !saved.name.empty() ? saved.name : "OpenFinger";
            offline.mac = saved.mac;
            offline.serial_port = saved.serial_port;
            offline.sta_ip = saved.sta_ip;
            offline.state = "offline";
            offline.message = "not connected";
            offline.reported_role = HandRole::Unknown;
            offline.preferred_role = saved.preferred_role;
            offline.effective_role = saved.saved_role != HandRole::Unknown ? saved.saved_role : saved.preferred_role;
            offline.transport = !saved.serial_port.empty() ? DeviceTransport::Usb : DeviceTransport::Unknown;
            offline.online = false;
            offline.remembered = true;
            offline.udp_port = saved.udp_port;
            offline.adc_mask = saved.adc_mask;
            offline.last_seen = now;
            remembered.push_back(std::move(offline));
        }

        devices_.insert(devices_.end(), remembered.begin(), remembered.end());
        std::sort(devices_.begin(), devices_.end(), [](const ServiceDeviceView& left, const ServiceDeviceView& right) {
            if (left.online != right.online)
            {
                return left.online > right.online;
            }

            const bool left_has_usb = !left.serial_port.empty();
            const bool right_has_usb = !right.serial_port.empty();
            if (left_has_usb != right_has_usb)
            {
                return left_has_usb > right_has_usb;
            }

            const auto rank_role = [](HandRole role) {
                switch (role)
                {
                case HandRole::Left:
                    return 0;
                case HandRole::Right:
                    return 1;
                default:
                    return 2;
                }
            };

            const int left_role_rank = rank_role(left.effective_role);
            const int right_role_rank = rank_role(right.effective_role);
            if (left_role_rank != right_role_rank)
            {
                return left_role_rank < right_role_rank;
            }

            return left.name < right.name;
        });
    }
}

HandRole OpenFingerService::ResolvePacketRole(const ReceivedAdcPacket& packet, std::string* out_mac, std::string* out_ip)
{
    const std::string source_ip = PacketSourceIp(packet.source_endpoint);
    if (out_ip != nullptr)
    {
        *out_ip = source_ip;
    }

    std::lock_guard<std::mutex> lock(devices_mutex_);
    for (const auto& device : devices_)
    {
        if (!device.sta_ip.empty() && device.sta_ip == source_ip)
        {
            if (out_mac != nullptr)
            {
                *out_mac = device.mac;
            }
            return device.effective_role;
        }
    }

    for (const auto& device : config_store_.config().devices)
    {
        if (!device.sta_ip.empty() && device.sta_ip == source_ip)
        {
            if (out_mac != nullptr)
            {
                *out_mac = device.mac;
            }
            return device.saved_role != HandRole::Unknown ? device.saved_role : device.preferred_role;
        }
    }

    return HandRole::Unknown;
}

ServiceDeviceView* OpenFingerService::FindDeviceById(std::string_view device_id)
{
    for (auto& device : devices_)
    {
        if (device.id == device_id)
        {
            return &device;
        }
    }
    return nullptr;
}

KnownDeviceConfig* OpenFingerService::UpsertKnownDevice(ServiceDeviceView& device)
{
    auto& saved_devices = config_store_.mutable_config().devices;
    for (auto& saved : saved_devices)
    {
        if (DeviceMatchesSaved(device, saved))
        {
            saved.name = device.name;
            saved.mac = device.mac;
            saved.ble_address.clear();
            saved.serial_port = device.serial_port;
            saved.sta_ip = device.sta_ip;
            saved.preferred_role = device.preferred_role;
            saved.saved_role = device.effective_role;
            saved.last_transport = device.serial_port.empty() ? DeviceTransport::Unknown : DeviceTransport::Usb;
            saved.udp_port = device.udp_port;
            saved.adc_mask = device.adc_mask;
            return &saved;
        }
    }

    KnownDeviceConfig created;
    created.name = device.name;
    created.mac = device.mac;
    created.ble_address.clear();
    created.serial_port = device.serial_port;
    created.sta_ip = device.sta_ip;
    created.preferred_role = device.preferred_role;
    created.saved_role = device.effective_role;
    created.last_transport = device.serial_port.empty() ? DeviceTransport::Unknown : DeviceTransport::Usb;
    created.udp_port = device.udp_port;
    created.adc_mask = device.adc_mask;
    saved_devices.push_back(created);
    return &saved_devices.back();
}

bool OpenFingerService::SaveConfig()
{
    std::string error;
    const bool ok = config_store_.Save(&error);
    if (!ok)
    {
        AppendLog("config save failed: " + error);
        return false;
    }

    RefreshConfigWriteTime();
    return ok;
}

} // namespace openfinger
