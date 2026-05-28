#pragma once

#include "common/AdcPacket.h"

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

struct ReceivedAdcPacket
{
    AdcPacket packet;
    std::chrono::steady_clock::time_point received_at;
    std::string raw_line;
    std::string source_endpoint;
};

struct ReceiverStats
{
    std::uint64_t received_packets = 0;
    std::uint64_t parse_errors = 0;
    std::uint16_t port = 0;
};

class AdcReceiver
{
public:
    AdcReceiver();
    ~AdcReceiver();

    bool Start(std::uint16_t port, std::string* out_error = nullptr);
    void Stop();

    bool IsRunning() const;

    std::size_t DrainPackets(std::vector<ReceivedAdcPacket>* out_packets);
    ReceiverStats GetStats() const;
    std::string GetLastError() const;

private:
    void Run();
    void PushPacket(ReceivedAdcPacket packet);

    std::atomic<bool> running_ = false;
    std::thread thread_;
    std::uint16_t port_ = 0;
    std::atomic<bool> wsa_started_ = false;

    mutable std::mutex queue_mutex_;
    std::deque<ReceivedAdcPacket> queue_;

    mutable std::mutex stats_mutex_;
    ReceiverStats stats_;
    std::string last_error_;

    std::uintptr_t socket_ = static_cast<std::uintptr_t>(-1);
};

} // namespace openfinger
