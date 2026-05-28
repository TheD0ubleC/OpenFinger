#pragma once

#include "common/AdcReceiver.h"
#include "common/Config.h"
#include "common/FingerFilter.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace openfinger
{

struct ServiceDeviceView
{
    std::string id;
    std::string name;
    std::string mac;
    std::string ble_address;
    std::string serial_port;
    std::string sta_ip;
    std::string state;
    std::string message;
    HandRole reported_role = HandRole::Unknown;
    HandRole preferred_role = HandRole::Unknown;
    HandRole effective_role = HandRole::Unknown;
    DeviceTransport transport = DeviceTransport::Unknown;
    bool wifi_connected = false;
    bool adc_streaming = false;
    bool online = false;
    bool remembered = false;
    int udp_port = 39001;
    int adc_mask = 31;
    std::uint64_t seq = 0;
    std::string last_error_user;
    std::string last_error_code;
    std::chrono::steady_clock::time_point last_seen {};
};

class OpenFingerService
{
public:
    OpenFingerService();
    ~OpenFingerService();

    bool Start(std::string* out_error = nullptr);
    void Stop();
    bool IsRunning() const;
    void WaitForExit();

private:
    bool OpenRuntimePublisher(std::string* out_error);
    void CloseRuntimePublisher();
    bool ReloadConfigIfChanged();
    void RefreshConfigWriteTime();
    void RuntimeLoop();
    void DiscoveryLoop();
    void PipeLoop();

    void AppendLog(std::string message);
    std::string BuildSnapshotResponse();
    std::string HandleRequest(std::string_view request_line);
    std::string BuildCommandResponse(
        bool ok,
        std::string_view title,
        std::string_view user_message,
        std::string_view error_code = {},
        bool retryable = false) const;
    bool CanControlDevice(const ServiceDeviceView& device, std::string* out_reason, std::string* out_error_code) const;

    std::string HandleIdentify(std::string_view device_id);
    std::string HandleAssignRole(std::string_view device_id, HandRole role);
    std::string HandleProvision(
        std::string_view device_id,
        std::string_view ssid,
        std::string_view password,
        std::string_view host_ip,
        int udp_port,
        int adc_mask,
        HandRole role);
    std::string HandleProvisionAllUsb(
        std::string_view ssid,
        std::string_view password,
        std::string_view host_ip,
        int udp_port,
        int adc_mask);
    std::string HandleResetDevice(std::string_view device_id);
    std::string HandleForgetDevice(std::string_view device_id);
    std::string HandleCalibrate(HandSide side, FingerName finger);
    std::string HandleResetCenter(HandSide side, FingerName finger);
    std::string HandleCycleDirection(HandSide side, FingerName finger);
    std::string HandleUpdateFingerConfig(HandSide side, FingerName finger, const FingerConfig& config);

    void RefreshDiscoveredDevices();
    HandRole ResolvePacketRole(const ReceivedAdcPacket& packet, std::string* out_mac, std::string* out_ip);
    ServiceDeviceView* FindDeviceById(std::string_view device_id);
    KnownDeviceConfig* UpsertKnownDevice(ServiceDeviceView& device);
    bool SaveConfig();

    ConfigStore config_store_;
    FingerFilter filter_;
    AdcReceiver receiver_;

    std::atomic<bool> running_ = false;
    std::thread runtime_thread_;
    std::thread discovery_thread_;
    std::thread pipe_thread_;

    mutable std::mutex devices_mutex_;
    std::vector<ServiceDeviceView> devices_;

    mutable std::mutex logs_mutex_;
    std::deque<std::string> logs_;

    std::uint64_t runtime_seq_ = 0;
    std::uintptr_t runtime_socket_ = static_cast<std::uintptr_t>(-1);
    std::uintptr_t pipe_handle_ = static_cast<std::uintptr_t>(-1);
    std::string startup_error_;
    std::filesystem::file_time_type config_write_time_ {};
    std::chrono::steady_clock::time_point last_config_poll_ {};
};

} // namespace openfinger
