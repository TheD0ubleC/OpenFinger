#pragma once

#include "common/AdcReceiver.h"
#include "common/Config.h"
#include "common/FingerFilter.h"
#include "common/RuntimeState.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>

namespace openfinger
{

struct RuntimeFrameReceiverStats
{
    std::uint64_t received_packets = 0;
    std::uint64_t parse_errors = 0;
    std::uint16_t port = 0;
};

class RuntimeFrameReceiver
{
public:
    RuntimeFrameReceiver();
    ~RuntimeFrameReceiver();

    bool Start(const AppConfig& config, std::string* out_error = nullptr);
    void Stop();

    bool IsRunning() const;
    bool CopyLatestFrame(RuntimeFrame* out_frame) const;
    RuntimeFrameReceiverStats GetStats() const;
    std::string GetLastError() const;

private:
    void Run();
    void ProcessRawFallback(std::chrono::steady_clock::time_point now);
    HandRole ResolveRawPacketRole(const ReceivedAdcPacket& packet) const;
    static std::uint64_t MonotonicMilliseconds();

    std::atomic<bool> running_ = false;
    std::thread thread_;
    std::uint16_t port_ = 0;
    std::atomic<bool> wsa_started_ = false;

    AppConfig config_;
    AdcReceiver raw_receiver_;
    FingerFilter fallback_filter_;
    bool raw_fallback_enabled_ = false;
    bool have_fallback_frame_ = false;
    std::uint64_t fallback_seq_ = 0;
    std::chrono::steady_clock::time_point last_runtime_packet_at_ {};

    mutable std::mutex frame_mutex_;
    RuntimeFrame latest_frame_;

    mutable std::mutex stats_mutex_;
    RuntimeFrameReceiverStats stats_;
    std::string last_error_;

    std::uintptr_t socket_ = static_cast<std::uintptr_t>(-1);
};

} // namespace openfinger
