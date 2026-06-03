#pragma once

#include "common/Config.h"

#include <array>
#include <chrono>
#include <cstdint>
#include <string>
#include <string_view>

namespace openfinger
{

struct FingerRuntimeState
{
    bool enabled = true;
    bool has_valid_sample = false;
    bool stale = true;
    int raw = 0;
    int center_raw = 2048;
    BendDirection direction = BendDirection::Auto;
    double bend_raw = 0.0;
    double bend_smoothed = 0.0;
};

struct ControllerPoseOffset
{
    float position_x = 0.0f;
    float position_y = 0.0f;
    float position_z = 0.0f;
    float rotation_pitch = 0.0f;
    float rotation_yaw = 0.0f;
    float rotation_roll = 0.0f;
};

struct RuntimeVirtualButtons
{
    bool trigger_click = false;
    bool grip_click = false;
    bool primary_click = false;
    bool secondary_click = false;
    bool system_click = false;
};

struct HandRuntimeState
{
    HandSide side = HandSide::Left;
    bool present = false;
    bool stale = true;
    bool connected = false;
    double packet_fps = 0.0;
    std::uint64_t device_ms = 0;
    std::string source_mac;
    std::string source_ip;
    std::chrono::steady_clock::time_point last_packet_at {};
    std::chrono::steady_clock::time_point last_valid_at {};
    std::array<FingerRuntimeState, kFingerCount> fingers {};
    bool joystick_available = false;
    float joystick_x = 0.0f;
    float joystick_y = 0.0f;
    bool joystick_click = false;
    bool joystick_touch = false;
    int joystick_axis_mode = 0;
    int joystick_click_action = 0;
    RuntimeVirtualButtons virtual_buttons;
    ControllerPoseOffset pose_offset;
};

struct RuntimeFrame
{
    std::uint64_t seq = 0;
    std::uint64_t monotonic_ms = 0;
    HandRuntimeState left;
    HandRuntimeState right;
};

std::string SerializeRuntimeFrame(const RuntimeFrame& frame);
bool ParseRuntimeFrame(std::string_view line, RuntimeFrame* out_frame, std::string* out_error = nullptr);

} // namespace openfinger
