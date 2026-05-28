#pragma once

#include "common/Config.h"

#include <array>
#include <chrono>
#include <cstdint>
#include <string>
#include <string_view>

namespace openfinger
{

struct ForwardedControllerState
{
    HandSide side = HandSide::Right;
    std::uint64_t seq = 0;
    bool connected = false;
    float trigger_value = 0.0f;
    float grip_value = 0.0f;
    float joystick_x = 0.0f;
    float joystick_y = 0.0f;
    bool joystick_click = false;
    bool joystick_touch = false;
    bool trigger_click = false;
    bool trigger_touch = false;
    bool grip_click = false;
    bool grip_touch = false;
    bool a_click = false;
    bool a_touch = false;
    bool b_click = false;
    bool b_touch = false;
    bool system_click = false;
    bool system_touch = false;
    std::chrono::steady_clock::time_point received_at {};
    std::string source_endpoint;
};

bool ParseForwardedControllerPacket(std::string_view line, ForwardedControllerState* out_state, std::string* out_error = nullptr);
std::string SerializeForwardedControllerPacket(const ForwardedControllerState& state);
bool IsForwardedControllerStateFresh(
    const ForwardedControllerState& state,
    std::chrono::steady_clock::time_point now,
    std::chrono::milliseconds max_age);

} // namespace openfinger
