#include "common/RuntimeState.h"

#include <algorithm>
#include <array>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <iomanip>
#include <sstream>

namespace openfinger
{

namespace
{

constexpr std::size_t kBaseFieldCount = 31;
constexpr std::size_t kVirtualButtonFieldCount = 41;
constexpr std::size_t kPoseOffsetFieldCount = 43;
constexpr std::size_t kVirtualButtonPoseOffsetFieldCount = 53;

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

bool ParseInt(std::string_view token, int* out_value)
{
    const char* begin = token.data();
    const char* end = token.data() + token.size();

    int value = 0;
    const auto result = std::from_chars(begin, end, value);
    if (result.ec != std::errc{} || result.ptr != end)
    {
        return false;
    }

    *out_value = value;
    return true;
}

void FillFingerState(float bend, FingerRuntimeState* state)
{
    if (state == nullptr)
    {
        return;
    }

    state->has_valid_sample = true;
    state->stale = false;
    state->bend_raw = bend;
    state->bend_smoothed = bend;
}

void ApplyHandCsv(
    HandSide side,
    bool present,
    bool stale,
    const std::array<float, kFingerCount>& bends,
    bool joystick_available,
    float joystick_x,
    float joystick_y,
    bool joystick_click,
    bool joystick_touch,
    int joystick_axis_mode,
    int joystick_click_action,
    const RuntimeVirtualButtons& virtual_buttons,
    const ControllerPoseOffset& pose_offset,
    HandRuntimeState* hand)
{
    if (hand == nullptr)
    {
        return;
    }

    hand->side = side;
    hand->present = present;
    hand->connected = present;
    hand->stale = stale;
    hand->joystick_available = joystick_available;
    hand->joystick_x = joystick_x;
    hand->joystick_y = joystick_y;
    hand->joystick_click = joystick_click;
    hand->joystick_touch = joystick_touch;
    hand->joystick_axis_mode = joystick_axis_mode;
    hand->joystick_click_action = joystick_click_action;
    hand->virtual_buttons = virtual_buttons;
    hand->pose_offset = pose_offset;
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        FillFingerState(bends[index], &hand->fingers[index]);
    }
}

template <std::size_t N>
ControllerPoseOffset ParsePoseOffset(
    const std::array<std::string_view, N>& parts,
    std::size_t start_index,
    bool* out_ok)
{
    ControllerPoseOffset offset;
    bool ok = true;
    ok = ok && ParseFloat(parts[start_index + 0], &offset.position_x);
    ok = ok && ParseFloat(parts[start_index + 1], &offset.position_y);
    ok = ok && ParseFloat(parts[start_index + 2], &offset.position_z);
    ok = ok && ParseFloat(parts[start_index + 3], &offset.rotation_pitch);
    ok = ok && ParseFloat(parts[start_index + 4], &offset.rotation_yaw);
    ok = ok && ParseFloat(parts[start_index + 5], &offset.rotation_roll);

    offset.position_x = std::clamp(offset.position_x, -1.0f, 1.0f);
    offset.position_y = std::clamp(offset.position_y, -1.0f, 1.0f);
    offset.position_z = std::clamp(offset.position_z, -1.0f, 1.0f);
    offset.rotation_pitch = std::clamp(offset.rotation_pitch, -180.0f, 180.0f);
    offset.rotation_yaw = std::clamp(offset.rotation_yaw, -180.0f, 180.0f);
    offset.rotation_roll = std::clamp(offset.rotation_roll, -180.0f, 180.0f);

    if (out_ok != nullptr)
    {
        *out_ok = ok;
    }
    return offset;
}

bool HasPoseOffset(const ControllerPoseOffset& offset)
{
    constexpr float epsilon = 0.00001f;
    return std::fabs(offset.position_x) > epsilon || std::fabs(offset.position_y) > epsilon
        || std::fabs(offset.position_z) > epsilon || std::fabs(offset.rotation_pitch) > epsilon
        || std::fabs(offset.rotation_yaw) > epsilon || std::fabs(offset.rotation_roll) > epsilon;
}

void AppendPoseOffset(std::ostringstream& stream, const ControllerPoseOffset& offset)
{
    stream
        << "," << offset.position_x
        << "," << offset.position_y
        << "," << offset.position_z
        << "," << offset.rotation_pitch
        << "," << offset.rotation_yaw
        << "," << offset.rotation_roll;
}

void AppendVirtualButtons(std::ostringstream& stream, const RuntimeVirtualButtons& buttons)
{
    stream
        << "," << (buttons.trigger_click ? 1 : 0)
        << "," << (buttons.grip_click ? 1 : 0)
        << "," << (buttons.primary_click ? 1 : 0)
        << "," << (buttons.secondary_click ? 1 : 0)
        << "," << (buttons.system_click ? 1 : 0);
}

} // namespace

std::string SerializeRuntimeFrame(const RuntimeFrame& frame)
{
    std::ostringstream stream;
    stream << std::fixed << std::setprecision(4);
    stream
        << "OFRUNTIME"
        << "," << frame.seq
        << "," << frame.monotonic_ms
        << "," << (frame.left.present ? 1 : 0)
        << "," << (frame.left.stale ? 1 : 0);

    for (const auto& finger : frame.left.fingers)
    {
        stream << "," << finger.bend_smoothed;
    }

    stream
        << "," << (frame.left.joystick_available ? 1 : 0)
        << "," << frame.left.joystick_x
        << "," << frame.left.joystick_y
        << "," << (frame.left.joystick_click ? 1 : 0)
        << "," << (frame.left.joystick_touch ? 1 : 0)
        << "," << frame.left.joystick_axis_mode
        << "," << frame.left.joystick_click_action;
    AppendVirtualButtons(stream, frame.left.virtual_buttons);
    stream
        << "," << (frame.right.present ? 1 : 0)
        << "," << (frame.right.stale ? 1 : 0);

    for (const auto& finger : frame.right.fingers)
    {
        stream << "," << finger.bend_smoothed;
    }

    stream
        << "," << (frame.right.joystick_available ? 1 : 0)
        << "," << frame.right.joystick_x
        << "," << frame.right.joystick_y
        << "," << (frame.right.joystick_click ? 1 : 0)
        << "," << (frame.right.joystick_touch ? 1 : 0)
        << "," << frame.right.joystick_axis_mode
        << "," << frame.right.joystick_click_action;
    AppendVirtualButtons(stream, frame.right.virtual_buttons);

    if (HasPoseOffset(frame.left.pose_offset) || HasPoseOffset(frame.right.pose_offset))
    {
        AppendPoseOffset(stream, frame.left.pose_offset);
        AppendPoseOffset(stream, frame.right.pose_offset);
    }

    return stream.str();
}

bool ParseRuntimeFrame(std::string_view line, RuntimeFrame* out_frame, std::string* out_error)
{
    if (out_frame == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "output frame pointer was null";
        }
        return false;
    }

    std::array<std::string_view, kVirtualButtonPoseOffsetFieldCount> parts {};
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

    const bool has_virtual_buttons = part_index == kVirtualButtonFieldCount || part_index == kVirtualButtonPoseOffsetFieldCount;
    const bool has_pose_offset = part_index == kPoseOffsetFieldCount || part_index == kVirtualButtonPoseOffsetFieldCount;
    if ((part_index != kBaseFieldCount && part_index != kVirtualButtonFieldCount
            && part_index != kPoseOffsetFieldCount && part_index != kVirtualButtonPoseOffsetFieldCount)
        || start <= line.size())
    {
        if (out_error != nullptr)
        {
            *out_error = "expected 31, 41, 43, or 53 CSV fields";
        }
        return false;
    }

    if (parts[0] != "OFRUNTIME")
    {
        if (out_error != nullptr)
        {
            *out_error = "packet prefix was not OFRUNTIME";
        }
        return false;
    }

    RuntimeFrame parsed;
    if (!ParseUint64(parts[1], &parsed.seq) || !ParseUint64(parts[2], &parsed.monotonic_ms))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid seq or timestamp field";
        }
        return false;
    }

    bool left_present = false;
    bool left_stale = false;
    bool right_present = false;
    bool right_stale = false;
    bool left_joystick_available = false;
    bool left_joystick_click = false;
    bool left_joystick_touch = false;
    bool right_joystick_available = false;
    bool right_joystick_click = false;
    bool right_joystick_touch = false;
    const std::size_t left_virtual_start = 17;
    const std::size_t right_presence_start = has_virtual_buttons ? 22 : 17;
    const std::size_t right_bend_start = has_virtual_buttons ? 24 : 19;
    const std::size_t right_joystick_start = has_virtual_buttons ? 29 : 24;
    const std::size_t right_virtual_start = has_virtual_buttons ? 36 : 0;

    if (!ParseBool(parts[3], &left_present) || !ParseBool(parts[4], &left_stale)
        || !ParseBool(parts[right_presence_start], &right_present)
        || !ParseBool(parts[right_presence_start + 1], &right_stale))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid presence or stale field";
        }
        return false;
    }

    if (!ParseBool(parts[10], &left_joystick_available) || !ParseBool(parts[13], &left_joystick_click)
        || !ParseBool(parts[14], &left_joystick_touch)
        || !ParseBool(parts[right_joystick_start], &right_joystick_available)
        || !ParseBool(parts[right_joystick_start + 3], &right_joystick_click)
        || !ParseBool(parts[right_joystick_start + 4], &right_joystick_touch))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid joystick state field";
        }
        return false;
    }

    std::array<float, kFingerCount> left_bends {};
    std::array<float, kFingerCount> right_bends {};
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        if (!ParseFloat(parts[5 + index], &left_bends[index]) || !ParseFloat(parts[right_bend_start + index], &right_bends[index]))
        {
            if (out_error != nullptr)
            {
                *out_error = "invalid bend field";
            }
            return false;
        }
    }

    float left_joystick_x = 0.0f;
    float left_joystick_y = 0.0f;
    float right_joystick_x = 0.0f;
    float right_joystick_y = 0.0f;
    if (!ParseFloat(parts[11], &left_joystick_x) || !ParseFloat(parts[12], &left_joystick_y)
        || !ParseFloat(parts[right_joystick_start + 1], &right_joystick_x)
        || !ParseFloat(parts[right_joystick_start + 2], &right_joystick_y))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid joystick axis field";
        }
        return false;
    }

    int left_joystick_axis_mode = 0;
    int left_joystick_click_action = 0;
    int right_joystick_axis_mode = 0;
    int right_joystick_click_action = 0;
    if (!ParseInt(parts[15], &left_joystick_axis_mode) || !ParseInt(parts[16], &left_joystick_click_action)
        || !ParseInt(parts[right_joystick_start + 5], &right_joystick_axis_mode)
        || !ParseInt(parts[right_joystick_start + 6], &right_joystick_click_action))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid joystick mapping field";
        }
        return false;
    }

    RuntimeVirtualButtons left_virtual_buttons;
    RuntimeVirtualButtons right_virtual_buttons;
    if (has_virtual_buttons)
    {
        if (!ParseBool(parts[left_virtual_start], &left_virtual_buttons.trigger_click)
            || !ParseBool(parts[left_virtual_start + 1], &left_virtual_buttons.grip_click)
            || !ParseBool(parts[left_virtual_start + 2], &left_virtual_buttons.primary_click)
            || !ParseBool(parts[left_virtual_start + 3], &left_virtual_buttons.secondary_click)
            || !ParseBool(parts[left_virtual_start + 4], &left_virtual_buttons.system_click)
            || !ParseBool(parts[right_virtual_start], &right_virtual_buttons.trigger_click)
            || !ParseBool(parts[right_virtual_start + 1], &right_virtual_buttons.grip_click)
            || !ParseBool(parts[right_virtual_start + 2], &right_virtual_buttons.primary_click)
            || !ParseBool(parts[right_virtual_start + 3], &right_virtual_buttons.secondary_click)
            || !ParseBool(parts[right_virtual_start + 4], &right_virtual_buttons.system_click))
        {
            if (out_error != nullptr)
            {
                *out_error = "invalid virtual button field";
            }
            return false;
        }
    }

    ControllerPoseOffset left_pose_offset;
    ControllerPoseOffset right_pose_offset;
    if (has_pose_offset)
    {
        bool left_pose_ok = false;
        bool right_pose_ok = false;
        const std::size_t left_pose_start = has_virtual_buttons ? 41 : 31;
        const std::size_t right_pose_start = has_virtual_buttons ? 47 : 37;
        left_pose_offset = ParsePoseOffset(parts, left_pose_start, &left_pose_ok);
        right_pose_offset = ParsePoseOffset(parts, right_pose_start, &right_pose_ok);
        if (!left_pose_ok || !right_pose_ok)
        {
            if (out_error != nullptr)
            {
                *out_error = "invalid pose offset field";
            }
            return false;
        }
    }

    ApplyHandCsv(
        HandSide::Left,
        left_present,
        left_stale,
        left_bends,
        left_joystick_available,
        left_joystick_x,
        left_joystick_y,
        left_joystick_click,
        left_joystick_touch,
        left_joystick_axis_mode,
        left_joystick_click_action,
        left_virtual_buttons,
        left_pose_offset,
        &parsed.left);
    ApplyHandCsv(
        HandSide::Right,
        right_present,
        right_stale,
        right_bends,
        right_joystick_available,
        right_joystick_x,
        right_joystick_y,
        right_joystick_click,
        right_joystick_touch,
        right_joystick_axis_mode,
        right_joystick_click_action,
        right_virtual_buttons,
        right_pose_offset,
        &parsed.right);

    *out_frame = parsed;
    return true;
}

} // namespace openfinger
