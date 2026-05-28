#include "service/ServiceProtocol.h"

#include <algorithm>
#include <cctype>
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

bool ExtractBool(const std::string& text, const char* key, bool* out_value)
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

bool ExtractInt64(const std::string& text, const char* key, std::uint64_t* out_value)
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
    while (end < text.size() && std::isdigit(static_cast<unsigned char>(text[end])))
    {
        ++end;
    }

    if (start == end)
    {
        return false;
    }

    *out_value = static_cast<std::uint64_t>(std::stoull(text.substr(start, end - start)));
    return true;
}

bool ExtractInt(const std::string& text, const char* key, int* out_value)
{
    std::uint64_t value = 0;
    if (!ExtractInt64(text, key, &value))
    {
        return false;
    }

    *out_value = static_cast<int>(value);
    return true;
}

std::string PercentEncode(std::string_view value)
{
    std::ostringstream stream;
    stream << std::uppercase << std::hex;
    for (const unsigned char ch : value)
    {
        if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_'
            || ch == '.' || ch == '~')
        {
            stream << static_cast<char>(ch);
        }
        else
        {
            stream << '%' << static_cast<int>(ch >> 4) << static_cast<int>(ch & 0x0F);
        }
    }
    return stream.str();
}

void AppendQueryField(std::ostringstream* stream, bool* first, std::string_view key, std::string_view value)
{
    if (stream == nullptr || first == nullptr)
    {
        return;
    }

    if (!*first)
    {
        *stream << "&";
    }
    *first = false;
    *stream << key << "=" << PercentEncode(value);
}

} // namespace

bool ParseDeviceStatusJson(std::string_view text, DeviceStatusMessage* out_status, std::string* out_error)
{
    if (out_status == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "output status pointer was null";
        }
        return false;
    }

    DeviceStatusMessage status;
    const std::string copy(text);
    ExtractString(copy, "device", &status.device_name);
    ExtractString(copy, "state", &status.state);
    ExtractString(copy, "message", &status.message);
    ExtractString(copy, "mac", &status.mac);
    ExtractString(copy, "sta_ip", &status.sta_ip);
    ExtractString(copy, "host_ip", &status.host_ip);
    ExtractBool(copy, "wifi_connected", &status.wifi_connected);
    ExtractInt(copy, "udp_port", &status.udp_port);
    ExtractInt(copy, "adc_mask", &status.adc_mask);
    ExtractBool(copy, "adc_streaming", &status.adc_streaming);
    ExtractBool(copy, "tracking_enabled", &status.tracking_enabled);
    ExtractInt64(copy, "seq", &status.seq);
    ExtractString(copy, "board_target", &status.board_target);
    ExtractString(copy, "firmware_version", &status.firmware_version);
    ExtractInt(copy, "report_hz", &status.report_hz);
    ExtractInt(copy, "thumb_pin", &status.thumb_pin);
    ExtractInt(copy, "index_pin", &status.index_pin);
    ExtractInt(copy, "middle_pin", &status.middle_pin);
    ExtractInt(copy, "ring_pin", &status.ring_pin);
    ExtractInt(copy, "pinky_pin", &status.pinky_pin);
    ExtractInt(copy, "tracking_switch_pin", &status.tracking_switch_pin);
    ExtractString(copy, "tracking_switch_mode", &status.tracking_switch_mode);
    ExtractInt(copy, "joystick_vrx_pin", &status.joystick_vrx_pin);
    ExtractInt(copy, "joystick_vry_pin", &status.joystick_vry_pin);
    ExtractInt(copy, "joystick_sw_pin", &status.joystick_sw_pin);
    ExtractInt(copy, "battery_adc_pin", &status.battery_adc_pin);
    ExtractInt(copy, "battery_charge_pin", &status.battery_charge_pin);
    ExtractBool(copy, "battery_available", &status.battery_available);
    ExtractInt(copy, "battery_mv", &status.battery_mv);
    ExtractInt(copy, "battery_percent", &status.battery_percent);
    ExtractBool(copy, "battery_charging_known", &status.battery_charging_known);
    ExtractBool(copy, "battery_charging", &status.battery_charging);
    ExtractString(copy, "protocol_version", &status.protocol_version);
    ExtractString(copy, "capabilities", &status.capabilities);

    std::string role_text;
    if (ExtractString(copy, "role", &role_text))
    {
        status.role = HandRoleFromString(role_text);
    }

    if (status.device_name.empty() && status.mac.empty())
    {
        if (out_error != nullptr)
        {
            *out_error = "status json did not include device or mac";
        }
        return false;
    }

    *out_status = status;
    return true;
}

std::string BuildProvisionQuery(const ProvisionRequest& request)
{
    std::ostringstream stream;
    bool first = true;
    AppendQueryField(&stream, &first, "ssid", request.ssid);
    AppendQueryField(&stream, &first, "password", request.password);
    AppendQueryField(&stream, &first, "save", request.save_credentials ? "1" : "0");
    AppendQueryField(&stream, &first, "host_ip", request.host_ip);
    AppendQueryField(&stream, &first, "udp_port", std::to_string(request.udp_port));
    AppendQueryField(&stream, &first, "adc_mask", std::to_string(request.adc_mask));
    if (request.role != HandRole::Unknown)
    {
        AppendQueryField(&stream, &first, "role", ToString(request.role));
    }
    return stream.str();
}

std::string BuildAdcConfigQuery(const ProvisionRequest& request, bool include_network_fields)
{
    std::ostringstream stream;
    bool first = true;
    if (include_network_fields)
    {
        AppendQueryField(&stream, &first, "host_ip", request.host_ip);
        AppendQueryField(&stream, &first, "udp_port", std::to_string(request.udp_port));
        AppendQueryField(&stream, &first, "adc_mask", std::to_string(request.adc_mask));
    }

    if (request.role != HandRole::Unknown)
    {
        AppendQueryField(&stream, &first, "role", ToString(request.role));
    }
    return stream.str();
}

} // namespace openfinger
