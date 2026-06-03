#pragma once

#include <array>
#include <cstddef>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace openfinger
{

enum class BendDirection
{
    Auto,
    Positive,
    Negative,
    Absolute,
};

enum class HandSide
{
    Left = 0,
    Right = 1,
};

enum class HandRole
{
    Unknown,
    Left,
    Right,
};

enum class DeviceTransport
{
    Unknown,
    Usb,
    Ble,
    Hybrid,
};

enum class FingerName
{
    Thumb = 0,
    Index = 1,
    Middle = 2,
    Ring = 3,
    Pinky = 4,
};

constexpr std::size_t kFingerCount = 5;

std::string_view ToString(BendDirection direction);
BendDirection BendDirectionFromString(std::string_view value);

std::string_view ToString(HandSide side);
HandSide OppositeHand(HandSide side);
bool TryHandSideFromString(std::string_view value, HandSide* out_side);

std::string_view ToString(HandRole role);
HandRole HandRoleFromString(std::string_view value);

std::string_view ToString(DeviceTransport transport);
DeviceTransport DeviceTransportFromString(std::string_view value);

std::string_view ToString(FingerName finger);
FingerName FingerNameFromIndex(std::size_t index);
bool TryFingerNameFromString(std::string_view value, FingerName* out_finger);
std::size_t FingerIndex(FingerName finger);

struct FingerConfig
{
    int adc_channel = 0;
    int center_raw = 2048;
    BendDirection direction = BendDirection::Auto;
    double deadzone = 0.02;
    double smoothing_alpha = 0.25;
    bool enabled = true;
    int calibrated_open_raw = -1;
    int calibrated_closed_raw = -1;
};

struct HandConfig
{
    std::array<FingerConfig, kFingerCount> fingers {};
};

struct RuntimeConfig
{
    int device_udp_port = 39001;
    int local_runtime_udp_port = 39003;
    int publish_hz = 90;
    std::string host_ip = "auto";
};

struct ServiceConfig
{
    std::string pipe_name = "OpenFingerServicePipe";
    int discovery_poll_ms = 1500;
    int snapshot_hz = 20;
    int raw_input_udp_port = 39011;
};

struct SteamVrConfig
{
    int update_hz = 90;
    bool stale_return_to_zero = true;
    struct ControllerStyleConfig
    {
        std::string style_id = "knuckles";
        std::string display_name;
        std::string controller_type_override;
        std::string render_model_override;
    };

    ControllerStyleConfig left_style;
    ControllerStyleConfig right_style;
};

struct ControllerBridgeConfig
{
    int udp_port = 39002;
};

struct HandConfigSet
{
    HandConfig left;
    HandConfig right;
};

struct KnownDeviceConfig
{
    std::string mac;
    std::string name;
    std::string ble_address;
    std::string serial_port;
    std::string sta_ip;
    HandRole preferred_role = HandRole::Unknown;
    HandRole saved_role = HandRole::Unknown;
    DeviceTransport last_transport = DeviceTransport::Unknown;
    int udp_port = 39001;
    int adc_mask = 31;
};

struct AppConfig
{
    int adc_max = 4095;
    RuntimeConfig runtime;
    ServiceConfig service;
    SteamVrConfig steamvr;
    ControllerBridgeConfig controller_bridge;
    HandConfigSet hands;
    std::vector<KnownDeviceConfig> devices;
};

const HandConfig& GetHandConfig(const AppConfig& config, HandSide side);
HandConfig& GetHandConfig(AppConfig& config, HandSide side);
const FingerConfig& GetFingerConfig(const AppConfig& config, HandSide side, FingerName finger);
FingerConfig& GetFingerConfig(AppConfig& config, HandSide side, FingerName finger);

class ConfigStore
{
public:
    ConfigStore();

    const AppConfig& config() const;
    AppConfig& mutable_config();

    const std::filesystem::path& path() const;

    bool LoadOrCreate(std::string* out_error = nullptr);
    bool Save(std::string* out_error = nullptr) const;

private:
    std::filesystem::path path_;
    AppConfig config_;
};

} // namespace openfinger
