#pragma once

#include "common/Config.h"

#include <cstdint>
#include <string>
#include <string_view>

namespace openfinger
{

struct ProvisionRequest
{
    std::string ssid;
    std::string password;
    bool save_credentials = true;
    std::string host_ip;
    int udp_port = 39001;
    int adc_mask = 31;
    HandRole role = HandRole::Unknown;
};

struct DeviceStatusMessage
{
    std::string device_name;
    std::string state;
    std::string message;
    std::string mac;
    std::string sta_ip;
    bool wifi_connected = false;
    std::string host_ip;
    int udp_port = 39001;
    int adc_mask = 31;
    bool adc_streaming = false;
    std::uint64_t seq = 0;
    HandRole role = HandRole::Unknown;
    bool tracking_enabled = true;
    std::string board_target;
    std::string firmware_version;
    int report_hz = 0;
    int thumb_pin = -1;
    int index_pin = -1;
    int middle_pin = -1;
    int ring_pin = -1;
    int pinky_pin = -1;
    int tracking_switch_pin = -1;
    std::string tracking_switch_mode;
    int joystick_vrx_pin = -1;
    int joystick_vry_pin = -1;
    int joystick_sw_pin = -1;
    int battery_adc_pin = -1;
    int battery_charge_pin = -1;
    bool battery_available = false;
    int battery_mv = -1;
    int battery_percent = -1;
    bool battery_charging_known = false;
    bool battery_charging = false;
    std::string protocol_version;
    std::string capabilities;
};

bool ParseDeviceStatusJson(std::string_view text, DeviceStatusMessage* out_status, std::string* out_error = nullptr);
std::string BuildProvisionQuery(const ProvisionRequest& request);
std::string BuildAdcConfigQuery(const ProvisionRequest& request, bool include_network_fields);

} // namespace openfinger
