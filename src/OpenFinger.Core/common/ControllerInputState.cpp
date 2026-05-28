#include "common/ControllerInputState.h"

#include <array>
#include <charconv>
#include <cstdlib>
#include <iomanip>
#include <sstream>

namespace openfinger
{

namespace
{

constexpr std::size_t kFieldCount = 20;

bool ParseUint64(std::string_view token, std::uint64_t* out_value)
{
    const char* begin = token.data();
    const char* end = token.data() + token.size();

    std::uint64_t value = 0;
    const auto result = std::from_chars(begin, end, value);
    if (result.ec != std::errc{} || result.ptr != end)
    {
        return false;
    }

    *out_value = value;
    return true;
}

bool ParseFloat(std::string_view token, float* out_value)
{
    std::string temporary(token);
    char* end = nullptr;
    const float value = std::strtof(temporary.c_str(), &end);
    if (end == nullptr || *end != '\0')
    {
        return false;
    }

    *out_value = value;
    return true;
}

bool ParseBool(std::string_view token, bool* out_value)
{
    if (token == "0")
    {
        *out_value = false;
        return true;
    }

    if (token == "1")
    {
        *out_value = true;
        return true;
    }

    return false;
}

} // namespace

bool ParseForwardedControllerPacket(std::string_view line, ForwardedControllerState* out_state, std::string* out_error)
{
    if (out_state == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "output state pointer was null";
        }
        return false;
    }

    std::array<std::string_view, kFieldCount> parts {};
    std::size_t part_index = 0;
    std::size_t start = 0;

    while (start <= line.size() && part_index < parts.size())
    {
        const std::size_t comma = line.find(',', start);
        if (comma == std::string_view::npos)
        {
            parts[part_index++] = line.substr(start);
            start = line.size() + 1;
            break;
        }

        parts[part_index++] = line.substr(start, comma - start);
        start = comma + 1;
    }

    if (part_index != parts.size() || start <= line.size())
    {
        if (out_error != nullptr)
        {
            *out_error = "expected 20 CSV fields";
        }
        return false;
    }

    if (parts[0] != "OFCTL")
    {
        if (out_error != nullptr)
        {
            *out_error = "packet prefix was not OFCTL";
        }
        return false;
    }

    ForwardedControllerState parsed;
    if (!TryHandSideFromString(parts[1], &parsed.side) || !ParseUint64(parts[2], &parsed.seq))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid hand or seq field";
        }
        return false;
    }

    if (!ParseBool(parts[3], &parsed.connected)
        || !ParseFloat(parts[4], &parsed.trigger_value)
        || !ParseFloat(parts[5], &parsed.grip_value)
        || !ParseFloat(parts[6], &parsed.joystick_x)
        || !ParseFloat(parts[7], &parsed.joystick_y)
        || !ParseBool(parts[8], &parsed.joystick_click)
        || !ParseBool(parts[9], &parsed.joystick_touch)
        || !ParseBool(parts[10], &parsed.trigger_click)
        || !ParseBool(parts[11], &parsed.trigger_touch)
        || !ParseBool(parts[12], &parsed.grip_click)
        || !ParseBool(parts[13], &parsed.grip_touch)
        || !ParseBool(parts[14], &parsed.a_click)
        || !ParseBool(parts[15], &parsed.a_touch)
        || !ParseBool(parts[16], &parsed.b_click)
        || !ParseBool(parts[17], &parsed.b_touch)
        || !ParseBool(parts[18], &parsed.system_click)
        || !ParseBool(parts[19], &parsed.system_touch))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid boolean or float field";
        }
        return false;
    }

    *out_state = parsed;
    return true;
}

std::string SerializeForwardedControllerPacket(const ForwardedControllerState& state)
{
    std::ostringstream stream;
    stream << std::fixed << std::setprecision(4)
           << "OFCTL," << ToString(state.side)
           << "," << state.seq
           << "," << (state.connected ? 1 : 0)
           << "," << state.trigger_value
           << "," << state.grip_value
           << "," << state.joystick_x
           << "," << state.joystick_y
           << "," << (state.joystick_click ? 1 : 0)
           << "," << (state.joystick_touch ? 1 : 0)
           << "," << (state.trigger_click ? 1 : 0)
           << "," << (state.trigger_touch ? 1 : 0)
           << "," << (state.grip_click ? 1 : 0)
           << "," << (state.grip_touch ? 1 : 0)
           << "," << (state.a_click ? 1 : 0)
           << "," << (state.a_touch ? 1 : 0)
           << "," << (state.b_click ? 1 : 0)
           << "," << (state.b_touch ? 1 : 0)
           << "," << (state.system_click ? 1 : 0)
           << "," << (state.system_touch ? 1 : 0);
    return stream.str();
}

bool IsForwardedControllerStateFresh(
    const ForwardedControllerState& state,
    std::chrono::steady_clock::time_point now,
    std::chrono::milliseconds max_age)
{
    return state.received_at.time_since_epoch().count() != 0
        && (now - state.received_at) <= max_age;
}

} // namespace openfinger
