#pragma once

#include "common/Config.h"
#include "common/ControllerInputState.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>

namespace openfinger
{

struct ControllerBridgeStats
{
    std::uint64_t received_packets = 0;
    std::uint64_t parse_errors = 0;
    std::uint16_t port = 0;
};

class ControllerBridgeReceiver
{
public:
    ControllerBridgeReceiver();
    ~ControllerBridgeReceiver();

    bool Start(std::uint16_t port, std::string* out_error = nullptr);
    void Stop();

    bool IsRunning() const;
    bool CopyLatestState(HandSide side, ForwardedControllerState* out_state) const;
    ControllerBridgeStats GetStats() const;
    std::string GetLastError() const;

private:
    void Run();

    std::atomic<bool> running_ = false;
    std::thread thread_;
    std::uint16_t port_ = 0;
    std::atomic<bool> wsa_started_ = false;

    mutable std::mutex state_mutex_;
    std::array<ForwardedControllerState, 2> latest_states_ {};

    mutable std::mutex stats_mutex_;
    ControllerBridgeStats stats_;
    std::string last_error_;

    std::uintptr_t socket_ = static_cast<std::uintptr_t>(-1);
};

} // namespace openfinger
