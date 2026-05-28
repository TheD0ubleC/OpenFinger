#include "common/Config.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <fstream>
#include <sstream>

namespace openfinger
{

namespace
{

std::string ToLowerCopy(std::string_view value)
{
    std::string lowered(value);
    std::transform(lowered.begin(), lowered.end(), lowered.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return lowered;
}

int ClampInt(int value, int min_value, int max_value)
{
    return std::clamp(value, min_value, max_value);
}

double ClampDouble(double value, double min_value, double max_value)
{
    return std::clamp(value, min_value, max_value);
}

bool ExtractString(const std::string& text, const char* key, std::string* out_value)
{
    const std::string needle = std::string("\"") + key + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return false;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return false;
    }

    const std::size_t quote_start = text.find('"', colon + 1);
    if (quote_start == std::string::npos)
    {
        return false;
    }

    std::string result;
    bool escaping = false;
    for (std::size_t index = quote_start + 1; index < text.size(); ++index)
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
            *out_value = result;
            return true;
        }

        result.push_back(ch);
    }

    return false;
}

bool ExtractInt(const std::string& text, const char* key, int* out_value)
{
    const std::string needle = std::string("\"") + key + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return false;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return false;
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
        return false;
    }

    *out_value = std::stoi(text.substr(start, end - start));
    return true;
}

bool ExtractDouble(const std::string& text, const char* key, double* out_value)
{
    const std::string needle = std::string("\"") + key + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return false;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return false;
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
        return false;
    }

    *out_value = std::stod(text.substr(start, end - start));
    return true;
}

bool ExtractBool(const std::string& text, const char* key, bool* out_value)
{
    std::string value;
    if (!ExtractString(text, key, &value))
    {
        const std::string needle = std::string("\"") + key + "\"";
        const std::size_t key_pos = text.find(needle);
        if (key_pos == std::string::npos)
        {
            return false;
        }

        const std::size_t colon = text.find(':', key_pos + needle.size());
        if (colon == std::string::npos)
        {
            return false;
        }

        std::size_t start = colon + 1;
        while (start < text.size() && std::isspace(static_cast<unsigned char>(text[start])))
        {
            ++start;
        }

        if (text.compare(start, 4, "true") == 0)
        {
            *out_value = true;
            return true;
        }

        if (text.compare(start, 5, "false") == 0)
        {
            *out_value = false;
            return true;
        }

        return false;
    }

    const std::string lowered = ToLowerCopy(value);
    if (lowered == "true" || lowered == "1")
    {
        *out_value = true;
        return true;
    }
    if (lowered == "false" || lowered == "0")
    {
        *out_value = false;
        return true;
    }

    return false;
}

bool ExtractSection(const std::string& text, const char* key, char open_char, char close_char, std::string* out_value)
{
    const std::string needle = std::string("\"") + key + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return false;
    }

    const std::size_t section_start = text.find(open_char, key_pos + needle.size());
    if (section_start == std::string::npos)
    {
        return false;
    }

    int depth = 0;
    bool in_string = false;
    bool escaping = false;
    for (std::size_t index = section_start; index < text.size(); ++index)
    {
        const char ch = text[index];
        if (in_string)
        {
            if (escaping)
            {
                escaping = false;
            }
            else if (ch == '\\')
            {
                escaping = true;
            }
            else if (ch == '"')
            {
                in_string = false;
            }
            continue;
        }

        if (ch == '"')
        {
            in_string = true;
            continue;
        }

        if (ch == open_char)
        {
            ++depth;
        }
        else if (ch == close_char)
        {
            --depth;
            if (depth == 0)
            {
                *out_value = text.substr(section_start, index - section_start + 1);
                return true;
            }
        }
    }

    return false;
}

bool ExtractObjectSection(const std::string& text, const char* key, std::string* out_value)
{
    return ExtractSection(text, key, '{', '}', out_value);
}

bool ExtractArraySection(const std::string& text, const char* key, std::string* out_value)
{
    return ExtractSection(text, key, '[', ']', out_value);
}

std::vector<std::string> ExtractArrayObjects(const std::string& text)
{
    std::vector<std::string> objects;
    bool in_string = false;
    bool escaping = false;
    int depth = 0;
    std::size_t current_start = std::string::npos;

    for (std::size_t index = 0; index < text.size(); ++index)
    {
        const char ch = text[index];
        if (in_string)
        {
            if (escaping)
            {
                escaping = false;
            }
            else if (ch == '\\')
            {
                escaping = true;
            }
            else if (ch == '"')
            {
                in_string = false;
            }
            continue;
        }

        if (ch == '"')
        {
            in_string = true;
            continue;
        }

        if (ch == '{')
        {
            if (depth == 0)
            {
                current_start = index;
            }
            ++depth;
        }
        else if (ch == '}')
        {
            --depth;
            if (depth == 0 && current_start != std::string::npos)
            {
                objects.push_back(text.substr(current_start, index - current_start + 1));
                current_start = std::string::npos;
            }
        }
    }

    return objects;
}

std::filesystem::path BuildDefaultConfigPath()
{
    const char* local_app_data = std::getenv("LOCALAPPDATA");
    if (local_app_data != nullptr && local_app_data[0] != '\0')
    {
        return std::filesystem::path(local_app_data) / "OpenFinger" / "openfinger_config.json";
    }

    return std::filesystem::current_path() / "OpenFinger" / "openfinger_config.json";
}

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

FingerConfig DefaultFingerConfig(std::size_t adc_channel)
{
    FingerConfig config;
    config.adc_channel = static_cast<int>(adc_channel);
    return config;
}

AppConfig DefaultConfig()
{
    AppConfig config;
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        config.hands.left.fingers[index] = DefaultFingerConfig(index);
        config.hands.right.fingers[index] = DefaultFingerConfig(index);
    }
    return config;
}

void LoadFingerConfig(const std::string& text, FingerConfig* finger)
{
    if (finger == nullptr)
    {
        return;
    }

    ExtractInt(text, "adc_channel", &finger->adc_channel);
    ExtractInt(text, "center_raw", &finger->center_raw);
    std::string direction_text;
    if (ExtractString(text, "direction", &direction_text))
    {
        finger->direction = BendDirectionFromString(direction_text);
    }
    ExtractDouble(text, "deadzone", &finger->deadzone);
    ExtractDouble(text, "smoothing_alpha", &finger->smoothing_alpha);
    ExtractBool(text, "enabled", &finger->enabled);
    ExtractInt(text, "calibrated_open_raw", &finger->calibrated_open_raw);
    ExtractInt(text, "calibrated_closed_raw", &finger->calibrated_closed_raw);
}

void ClampFingerConfig(FingerConfig* finger, int adc_max)
{
    if (finger == nullptr)
    {
        return;
    }

    finger->adc_channel = ClampInt(finger->adc_channel, 0, static_cast<int>(kFingerCount - 1));
    finger->center_raw = ClampInt(finger->center_raw, 0, adc_max);
    finger->deadzone = ClampDouble(finger->deadzone, 0.0, 1.0);
    finger->smoothing_alpha = ClampDouble(finger->smoothing_alpha, 0.0, 1.0);
    finger->calibrated_open_raw = ClampInt(finger->calibrated_open_raw, -1, adc_max);
    finger->calibrated_closed_raw = ClampInt(finger->calibrated_closed_raw, -1, adc_max);
}

void LoadHandConfig(const std::string& text, HandConfig* hand)
{
    if (hand == nullptr)
    {
        return;
    }

    std::string fingers_text;
    if (ExtractObjectSection(text, "fingers", &fingers_text))
    {
        for (std::size_t index = 0; index < kFingerCount; ++index)
        {
            std::string finger_text;
            if (ExtractObjectSection(fingers_text, std::string(ToString(FingerNameFromIndex(index))).c_str(), &finger_text))
            {
                LoadFingerConfig(finger_text, &hand->fingers[index]);
            }
        }
        return;
    }

    // Backward-compatible shape: hand object directly contains thumb/index/middle/ring/pinky.
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        std::string finger_text;
        if (ExtractObjectSection(text, std::string(ToString(FingerNameFromIndex(index))).c_str(), &finger_text))
        {
            LoadFingerConfig(finger_text, &hand->fingers[index]);
        }
    }
}

void ClampHandConfig(HandConfig* hand, int adc_max)
{
    if (hand == nullptr)
    {
        return;
    }

    for (auto& finger : hand->fingers)
    {
        ClampFingerConfig(&finger, adc_max);
    }
}

bool LooksLikeLegacyHalfMigratedHandConfig(const AppConfig& config)
{
    const auto& left = config.hands.left.fingers;
    const auto& right = config.hands.right.fingers;

    const bool left_thumb_index_disabled = !left[0].enabled && !left[1].enabled;
    const bool left_tail_default =
        left[2].enabled && left[3].enabled && left[4].enabled
        && left[2].adc_channel == 2 && left[3].adc_channel == 3 && left[4].adc_channel == 4;
    const bool right_duplicate_front =
        right[0].enabled && right[1].enabled
        && right[0].adc_channel == 0 && right[1].adc_channel == 0;
    const bool right_tail_default =
        right[2].adc_channel == 2 && right[3].adc_channel == 3 && right[4].adc_channel == 4;

    return left_thumb_index_disabled && left_tail_default && right_duplicate_front && right_tail_default;
}

void NormalizeLegacyHandConfig(AppConfig* config)
{
    if (config == nullptr || !LooksLikeLegacyHalfMigratedHandConfig(*config))
    {
        return;
    }

    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        auto& left = config->hands.left.fingers[index];
        auto& right = config->hands.right.fingers[index];
        left.adc_channel = static_cast<int>(index);
        right.adc_channel = static_cast<int>(index);
        left.enabled = true;
        right.enabled = true;
    }
}

std::string SerializeFingerConfig(const FingerConfig& finger, int indent)
{
    const std::string pad(indent, ' ');
    std::ostringstream stream;
    stream
        << pad << "{\n"
        << pad << "  \"adc_channel\": " << finger.adc_channel << ",\n"
        << pad << "  \"center_raw\": " << finger.center_raw << ",\n"
        << pad << "  \"direction\": \"" << ToString(finger.direction) << "\",\n"
        << pad << "  \"deadzone\": " << finger.deadzone << ",\n"
        << pad << "  \"smoothing_alpha\": " << finger.smoothing_alpha << ",\n"
        << pad << "  \"enabled\": " << (finger.enabled ? "true" : "false") << ",\n"
        << pad << "  \"calibrated_open_raw\": " << finger.calibrated_open_raw << ",\n"
        << pad << "  \"calibrated_closed_raw\": " << finger.calibrated_closed_raw << "\n"
        << pad << "}";
    return stream.str();
}

std::string SerializeHandConfig(const HandConfig& hand, int indent)
{
    const std::string pad(indent, ' ');
    std::ostringstream stream;
    stream << pad << "{\n";
    stream << pad << "  \"fingers\": {\n";
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        stream << pad << "    \"" << ToString(FingerNameFromIndex(index)) << "\": ";
        stream << SerializeFingerConfig(hand.fingers[index], indent + 4);
        if (index + 1 != kFingerCount)
        {
            stream << ",";
        }
        stream << "\n";
    }
    stream << pad << "  }\n";
    stream << pad << "}";
    return stream.str();
}

std::string SerializeDevices(const std::vector<KnownDeviceConfig>& devices, int indent)
{
    const std::string pad(indent, ' ');
    std::ostringstream stream;
    stream << pad << "[\n";
    for (std::size_t index = 0; index < devices.size(); ++index)
    {
        const auto& device = devices[index];
        stream
            << pad << "  {\n"
            << pad << "    \"mac\": \"" << EscapeJson(device.mac) << "\",\n"
            << pad << "    \"name\": \"" << EscapeJson(device.name) << "\",\n"
            << pad << "    \"ble_address\": \"" << EscapeJson(device.ble_address) << "\",\n"
            << pad << "    \"serial_port\": \"" << EscapeJson(device.serial_port) << "\",\n"
            << pad << "    \"sta_ip\": \"" << EscapeJson(device.sta_ip) << "\",\n"
            << pad << "    \"preferred_role\": \"" << ToString(device.preferred_role) << "\",\n"
            << pad << "    \"saved_role\": \"" << ToString(device.saved_role) << "\",\n"
            << pad << "    \"last_transport\": \"" << ToString(device.last_transport) << "\",\n"
            << pad << "    \"udp_port\": " << device.udp_port << ",\n"
            << pad << "    \"adc_mask\": " << device.adc_mask << "\n"
            << pad << "  }";
        if (index + 1 != devices.size())
        {
            stream << ",";
        }
        stream << "\n";
    }
    stream << pad << "]";
    return stream.str();
}

std::string SerializeConfig(const AppConfig& config)
{
    std::ostringstream stream;
    stream
        << "{\n"
        << "  \"adc_max\": " << config.adc_max << ",\n"
        << "  \"runtime\": {\n"
        << "    \"device_udp_port\": " << config.runtime.device_udp_port << ",\n"
        << "    \"local_runtime_udp_port\": " << config.runtime.local_runtime_udp_port << ",\n"
        << "    \"publish_hz\": " << config.runtime.publish_hz << ",\n"
        << "    \"host_ip\": \"" << EscapeJson(config.runtime.host_ip) << "\"\n"
        << "  },\n"
        << "  \"service\": {\n"
        << "    \"pipe_name\": \"" << EscapeJson(config.service.pipe_name) << "\",\n"
        << "    \"discovery_poll_ms\": " << config.service.discovery_poll_ms << ",\n"
        << "    \"snapshot_hz\": " << config.service.snapshot_hz << ",\n"
        << "    \"raw_input_udp_port\": " << config.service.raw_input_udp_port << "\n"
        << "  },\n"
        << "  \"steamvr\": {\n"
        << "    \"update_hz\": " << config.steamvr.update_hz << ",\n"
        << "    \"stale_return_to_zero\": " << (config.steamvr.stale_return_to_zero ? "true" : "false") << "\n"
        << "  },\n"
        << "  \"controller_bridge\": {\n"
        << "    \"udp_port\": " << config.controller_bridge.udp_port << "\n"
        << "  },\n"
        << "  \"hands\": {\n"
        << "    \"left\": " << SerializeHandConfig(config.hands.left, 4) << ",\n"
        << "    \"right\": " << SerializeHandConfig(config.hands.right, 4) << "\n"
        << "  },\n"
        << "  \"devices\": " << SerializeDevices(config.devices, 2) << "\n"
        << "}\n";
    return stream.str();
}

KnownDeviceConfig LoadDeviceConfig(const std::string& text)
{
    KnownDeviceConfig device;
    ExtractString(text, "mac", &device.mac);
    ExtractString(text, "name", &device.name);
    ExtractString(text, "ble_address", &device.ble_address);
    ExtractString(text, "serial_port", &device.serial_port);
    ExtractString(text, "sta_ip", &device.sta_ip);

    std::string role_text;
    if (ExtractString(text, "preferred_role", &role_text))
    {
        device.preferred_role = HandRoleFromString(role_text);
    }
    if (ExtractString(text, "saved_role", &role_text))
    {
        device.saved_role = HandRoleFromString(role_text);
    }

    std::string transport_text;
    if (ExtractString(text, "last_transport", &transport_text))
    {
        device.last_transport = DeviceTransportFromString(transport_text);
    }

    ExtractInt(text, "udp_port", &device.udp_port);
    ExtractInt(text, "adc_mask", &device.adc_mask);
    device.udp_port = ClampInt(device.udp_port, 1024, 65535);
    device.adc_mask = ClampInt(device.adc_mask, 0, 31);
    return device;
}

} // namespace

std::string_view ToString(BendDirection direction)
{
    switch (direction)
    {
    case BendDirection::Auto:
        return "auto";
    case BendDirection::Positive:
        return "positive";
    case BendDirection::Negative:
        return "negative";
    case BendDirection::Absolute:
        return "absolute";
    }

    return "auto";
}

BendDirection BendDirectionFromString(std::string_view value)
{
    const std::string lowered = ToLowerCopy(value);
    if (lowered == "positive")
    {
        return BendDirection::Positive;
    }
    if (lowered == "negative")
    {
        return BendDirection::Negative;
    }
    if (lowered == "absolute")
    {
        return BendDirection::Absolute;
    }
    return BendDirection::Auto;
}

std::string_view ToString(HandSide side)
{
    return side == HandSide::Left ? "left" : "right";
}

HandSide OppositeHand(HandSide side)
{
    return side == HandSide::Left ? HandSide::Right : HandSide::Left;
}

bool TryHandSideFromString(std::string_view value, HandSide* out_side)
{
    const std::string lowered = ToLowerCopy(value);
    if (lowered == "left")
    {
        if (out_side != nullptr)
        {
            *out_side = HandSide::Left;
        }
        return true;
    }

    if (lowered == "right")
    {
        if (out_side != nullptr)
        {
            *out_side = HandSide::Right;
        }
        return true;
    }

    return false;
}

std::string_view ToString(HandRole role)
{
    switch (role)
    {
    case HandRole::Left:
        return "left";
    case HandRole::Right:
        return "right";
    case HandRole::Unknown:
    default:
        return "unknown";
    }
}

HandRole HandRoleFromString(std::string_view value)
{
    const std::string lowered = ToLowerCopy(value);
    if (lowered == "left")
    {
        return HandRole::Left;
    }
    if (lowered == "right")
    {
        return HandRole::Right;
    }
    return HandRole::Unknown;
}

std::string_view ToString(DeviceTransport transport)
{
    switch (transport)
    {
    case DeviceTransport::Usb:
        return "usb";
    case DeviceTransport::Ble:
        return "ble";
    case DeviceTransport::Hybrid:
        return "hybrid";
    case DeviceTransport::Unknown:
    default:
        return "unknown";
    }
}

DeviceTransport DeviceTransportFromString(std::string_view value)
{
    const std::string lowered = ToLowerCopy(value);
    if (lowered == "usb")
    {
        return DeviceTransport::Usb;
    }
    if (lowered == "ble")
    {
        return DeviceTransport::Ble;
    }
    if (lowered == "hybrid")
    {
        return DeviceTransport::Hybrid;
    }
    return DeviceTransport::Unknown;
}

std::string_view ToString(FingerName finger)
{
    switch (finger)
    {
    case FingerName::Thumb:
        return "thumb";
    case FingerName::Index:
        return "index";
    case FingerName::Middle:
        return "middle";
    case FingerName::Ring:
        return "ring";
    case FingerName::Pinky:
    default:
        return "pinky";
    }
}

FingerName FingerNameFromIndex(std::size_t index)
{
    switch (index)
    {
    case 0:
        return FingerName::Thumb;
    case 1:
        return FingerName::Index;
    case 2:
        return FingerName::Middle;
    case 3:
        return FingerName::Ring;
    case 4:
    default:
        return FingerName::Pinky;
    }
}

bool TryFingerNameFromString(std::string_view value, FingerName* out_finger)
{
    const std::string lowered = ToLowerCopy(value);
    if (lowered == "thumb")
    {
        if (out_finger != nullptr)
        {
            *out_finger = FingerName::Thumb;
        }
        return true;
    }
    if (lowered == "index")
    {
        if (out_finger != nullptr)
        {
            *out_finger = FingerName::Index;
        }
        return true;
    }
    if (lowered == "middle")
    {
        if (out_finger != nullptr)
        {
            *out_finger = FingerName::Middle;
        }
        return true;
    }
    if (lowered == "ring")
    {
        if (out_finger != nullptr)
        {
            *out_finger = FingerName::Ring;
        }
        return true;
    }
    if (lowered == "pinky")
    {
        if (out_finger != nullptr)
        {
            *out_finger = FingerName::Pinky;
        }
        return true;
    }
    return false;
}

std::size_t FingerIndex(FingerName finger)
{
    return static_cast<std::size_t>(finger);
}

const HandConfig& GetHandConfig(const AppConfig& config, HandSide side)
{
    return side == HandSide::Left ? config.hands.left : config.hands.right;
}

HandConfig& GetHandConfig(AppConfig& config, HandSide side)
{
    return side == HandSide::Left ? config.hands.left : config.hands.right;
}

const FingerConfig& GetFingerConfig(const AppConfig& config, HandSide side, FingerName finger)
{
    return GetHandConfig(config, side).fingers[FingerIndex(finger)];
}

FingerConfig& GetFingerConfig(AppConfig& config, HandSide side, FingerName finger)
{
    return GetHandConfig(config, side).fingers[FingerIndex(finger)];
}

ConfigStore::ConfigStore()
    : path_(BuildDefaultConfigPath()),
      config_(DefaultConfig())
{
}

const AppConfig& ConfigStore::config() const
{
    return config_;
}

AppConfig& ConfigStore::mutable_config()
{
    return config_;
}

const std::filesystem::path& ConfigStore::path() const
{
    return path_;
}

bool ConfigStore::LoadOrCreate(std::string* out_error)
{
    std::error_code ec;
    std::filesystem::create_directories(path_.parent_path(), ec);
    if (ec)
    {
        if (out_error != nullptr)
        {
            *out_error = "failed to create config directory: " + ec.message();
        }
        return false;
    }

    if (!std::filesystem::exists(path_))
    {
        return Save(out_error);
    }

    std::ifstream input(path_, std::ios::binary);
    if (!input)
    {
        if (out_error != nullptr)
        {
            *out_error = "failed to open config file for reading";
        }
        return false;
    }

    const std::string text((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    AppConfig loaded = DefaultConfig();

    ExtractInt(text, "adc_max", &loaded.adc_max);

    std::string runtime_text;
    if (ExtractObjectSection(text, "runtime", &runtime_text))
    {
        ExtractInt(runtime_text, "device_udp_port", &loaded.runtime.device_udp_port);
        ExtractInt(runtime_text, "local_runtime_udp_port", &loaded.runtime.local_runtime_udp_port);
        ExtractInt(runtime_text, "publish_hz", &loaded.runtime.publish_hz);
        ExtractString(runtime_text, "host_ip", &loaded.runtime.host_ip);
    }

    std::string service_text;
    if (ExtractObjectSection(text, "service", &service_text))
    {
        ExtractString(service_text, "pipe_name", &loaded.service.pipe_name);
        ExtractInt(service_text, "discovery_poll_ms", &loaded.service.discovery_poll_ms);
        ExtractInt(service_text, "snapshot_hz", &loaded.service.snapshot_hz);
        ExtractInt(service_text, "raw_input_udp_port", &loaded.service.raw_input_udp_port);
    }

    std::string steamvr_text;
    if (ExtractObjectSection(text, "steamvr", &steamvr_text))
    {
        ExtractInt(steamvr_text, "update_hz", &loaded.steamvr.update_hz);
        ExtractBool(steamvr_text, "stale_return_to_zero", &loaded.steamvr.stale_return_to_zero);
    }

    std::string controller_bridge_text;
    if (ExtractObjectSection(text, "controller_bridge", &controller_bridge_text))
    {
        ExtractInt(controller_bridge_text, "udp_port", &loaded.controller_bridge.udp_port);
    }

    std::string hands_text;
    if (ExtractObjectSection(text, "hands", &hands_text))
    {
        std::string left_text;
        if (ExtractObjectSection(hands_text, "left", &left_text))
        {
            LoadHandConfig(left_text, &loaded.hands.left);
        }

        std::string right_text;
        if (ExtractObjectSection(hands_text, "right", &right_text))
        {
            LoadHandConfig(right_text, &loaded.hands.right);
        }
    }
    else
    {
        // Migrate the old single-finger schema into the new right-hand config.
        int legacy_udp_port = 39001;
        ExtractInt(text, "udp_port", &legacy_udp_port);
        loaded.runtime.device_udp_port = legacy_udp_port;

        std::string right_index_text;
        if (ExtractObjectSection(text, "right_index", &right_index_text))
        {
            LoadFingerConfig(right_index_text, &loaded.hands.right.fingers[FingerIndex(FingerName::Index)]);
        }
    }

    std::string devices_text;
    if (ExtractArraySection(text, "devices", &devices_text))
    {
        loaded.devices.clear();
        for (const auto& device_text : ExtractArrayObjects(devices_text))
        {
            loaded.devices.push_back(LoadDeviceConfig(device_text));
        }
    }

    loaded.adc_max = ClampInt(loaded.adc_max, 1, 65535);
    loaded.runtime.device_udp_port = ClampInt(loaded.runtime.device_udp_port, 1024, 65535);
    loaded.runtime.local_runtime_udp_port = ClampInt(loaded.runtime.local_runtime_udp_port, 1024, 65535);
    loaded.runtime.publish_hz = ClampInt(loaded.runtime.publish_hz, 10, 240);
    if (loaded.runtime.host_ip.empty())
    {
        loaded.runtime.host_ip = "auto";
    }

    if (loaded.service.pipe_name.empty())
    {
        loaded.service.pipe_name = "OpenFingerServicePipe";
    }
    loaded.service.discovery_poll_ms = ClampInt(loaded.service.discovery_poll_ms, 250, 10000);
    loaded.service.snapshot_hz = ClampInt(loaded.service.snapshot_hz, 1, 60);
    loaded.service.raw_input_udp_port = ClampInt(loaded.service.raw_input_udp_port, 1024, 65535);

    loaded.steamvr.update_hz = ClampInt(loaded.steamvr.update_hz, 10, 240);
    loaded.controller_bridge.udp_port = ClampInt(loaded.controller_bridge.udp_port, 1024, 65535);

    ClampHandConfig(&loaded.hands.left, loaded.adc_max);
    ClampHandConfig(&loaded.hands.right, loaded.adc_max);
    NormalizeLegacyHandConfig(&loaded);

    config_ = loaded;
    return true;
}

bool ConfigStore::Save(std::string* out_error) const
{
    std::ofstream output(path_, std::ios::binary | std::ios::trunc);
    if (!output)
    {
        if (out_error != nullptr)
        {
            *out_error = "failed to open config file for writing";
        }
        return false;
    }

    output << SerializeConfig(config_);
    if (!output.good())
    {
        if (out_error != nullptr)
        {
            *out_error = "failed to write config file";
        }
        return false;
    }

    return true;
}

} // namespace openfinger
