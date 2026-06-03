#include "common/RuntimeFrameReceiver.h"

#include <sstream>
#include <vector>

#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>

namespace openfinger
{

namespace
{

constexpr std::uintptr_t kInvalidSocketValue = static_cast<std::uintptr_t>(INVALID_SOCKET);
constexpr auto kPreferRuntimeFramesFor = std::chrono::milliseconds(500);

std::string TrimWhitespace(std::string value)
{
    const auto first = value.find_first_not_of(" \r\n\t");
    if (first == std::string::npos)
    {
        return {};
    }

    const auto last = value.find_last_not_of(" \r\n\t");
    return value.substr(first, last - first + 1);
}

std::string PacketSourceIp(std::string_view endpoint)
{
    const std::size_t colon = endpoint.find(':');
    if (colon == std::string_view::npos)
    {
        return std::string(endpoint);
    }

    return std::string(endpoint.substr(0, colon));
}

HandRole ConfiguredRole(const KnownDeviceConfig& device)
{
    return device.saved_role != HandRole::Unknown ? device.saved_role : device.preferred_role;
}

} // namespace

RuntimeFrameReceiver::RuntimeFrameReceiver() = default;

RuntimeFrameReceiver::~RuntimeFrameReceiver()
{
    Stop();
}

bool RuntimeFrameReceiver::Start(const AppConfig& config, std::string* out_error)
{
    Stop();

    config_ = config;
    fallback_filter_.SetConfig(config_);
    raw_fallback_enabled_ = false;
    have_fallback_frame_ = false;
    fallback_seq_ = 0;
    last_runtime_packet_at_ = {};
    latest_frame_received_at_ = {};

    WSADATA data;
    const int startup_result = WSAStartup(MAKEWORD(2, 2), &data);
    if (startup_result != 0)
    {
        if (out_error != nullptr)
        {
            *out_error = "WSAStartup failed with code " + std::to_string(startup_result);
        }
        return false;
    }

    wsa_started_ = true;

    SOCKET udp_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (udp_socket == INVALID_SOCKET)
    {
        const int error = WSAGetLastError();
        WSACleanup();
        wsa_started_ = false;
        if (out_error != nullptr)
        {
            *out_error = "socket() failed with code " + std::to_string(error);
        }
        return false;
    }

    DWORD timeout_ms = 200;
    setsockopt(udp_socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(static_cast<std::uint16_t>(config.runtime.local_runtime_udp_port));

    if (bind(udp_socket, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR)
    {
        const int error = WSAGetLastError();
        closesocket(udp_socket);
        WSACleanup();
        wsa_started_ = false;
        if (out_error != nullptr)
        {
            *out_error = "bind() failed on UDP port "
                + std::to_string(config.runtime.local_runtime_udp_port)
                + " with code "
                + std::to_string(error);
        }
        return false;
    }

    {
        std::lock_guard<std::mutex> stats_lock(stats_mutex_);
        stats_ = {};
        stats_.port = static_cast<std::uint16_t>(config.runtime.local_runtime_udp_port);
        last_error_.clear();
    }

    {
        std::lock_guard<std::mutex> frame_lock(frame_mutex_);
        latest_frame_ = {};
        latest_frame_.left.side = HandSide::Left;
        latest_frame_.right.side = HandSide::Right;
    }

    port_ = static_cast<std::uint16_t>(config.runtime.local_runtime_udp_port);
    socket_ = static_cast<std::uintptr_t>(udp_socket);
    running_ = true;
    thread_ = std::thread(&RuntimeFrameReceiver::Run, this);
    return true;
}

void RuntimeFrameReceiver::Stop()
{
    running_ = false;
    raw_receiver_.Stop();

    SOCKET udp_socket = static_cast<SOCKET>(socket_);
    if (udp_socket != INVALID_SOCKET)
    {
        closesocket(udp_socket);
        socket_ = kInvalidSocketValue;
    }

    if (thread_.joinable())
    {
        thread_.join();
    }

    if (wsa_started_.exchange(false))
    {
        WSACleanup();
    }
}

bool RuntimeFrameReceiver::IsRunning() const
{
    return running_.load();
}

bool RuntimeFrameReceiver::CopyLatestFrame(RuntimeFrame* out_frame) const
{
    if (out_frame == nullptr)
    {
        return false;
    }

    std::lock_guard<std::mutex> lock(frame_mutex_);
    if (latest_frame_.seq == 0)
    {
        return false;
    }

    *out_frame = latest_frame_;
    return true;
}

bool RuntimeFrameReceiver::IsLatestFrameFresh(std::chrono::milliseconds max_age) const
{
    std::lock_guard<std::mutex> lock(frame_mutex_);
    if (latest_frame_.seq == 0 || latest_frame_received_at_.time_since_epoch().count() == 0)
    {
        return false;
    }

    return (std::chrono::steady_clock::now() - latest_frame_received_at_) <= max_age;
}

RuntimeFrameReceiverStats RuntimeFrameReceiver::GetStats() const
{
    std::lock_guard<std::mutex> lock(stats_mutex_);
    return stats_;
}

std::string RuntimeFrameReceiver::GetLastError() const
{
    std::lock_guard<std::mutex> lock(stats_mutex_);
    return last_error_;
}

std::uint64_t RuntimeFrameReceiver::MonotonicMilliseconds()
{
    return static_cast<std::uint64_t>(GetTickCount64());
}

HandRole RuntimeFrameReceiver::ResolveRawPacketRole(const ReceivedAdcPacket& packet) const
{
    const std::string source_ip = PacketSourceIp(packet.source_endpoint);
    for (const auto& device : config_.devices)
    {
        if (!device.sta_ip.empty() && device.sta_ip == source_ip)
        {
            return ConfiguredRole(device);
        }
    }

    HandRole fallback_role = HandRole::Unknown;
    for (const auto& device : config_.devices)
    {
        const HandRole configured_role = ConfiguredRole(device);
        if (configured_role == HandRole::Unknown)
        {
            continue;
        }

        if (fallback_role == HandRole::Unknown)
        {
            fallback_role = configured_role;
            continue;
        }

        if (fallback_role != configured_role)
        {
            return HandRole::Unknown;
        }
    }

    return fallback_role;
}

void RuntimeFrameReceiver::ProcessRawFallback(std::chrono::steady_clock::time_point now)
{
    if (!raw_fallback_enabled_)
    {
        return;
    }

    std::vector<ReceivedAdcPacket> packets;
    raw_receiver_.DrainPackets(&packets);

    if (last_runtime_packet_at_.time_since_epoch().count() != 0 && (now - last_runtime_packet_at_) < kPreferRuntimeFramesFor)
    {
        return;
    }

    bool mapped_packet = false;
    for (const auto& packet : packets)
    {
        const HandRole role = ResolveRawPacketRole(packet);
        if (role == HandRole::Unknown)
        {
            continue;
        }

        const HandSide side = role == HandRole::Left ? HandSide::Left : HandSide::Right;
        fallback_filter_.ProcessPacket(side, packet);
        mapped_packet = true;
    }

    fallback_filter_.Tick(now);
    if (!mapped_packet && !have_fallback_frame_)
    {
        return;
    }

    RuntimeFrame frame = fallback_filter_.BuildRuntimeFrame(++fallback_seq_, MonotonicMilliseconds());
    {
        std::lock_guard<std::mutex> lock(frame_mutex_);
        latest_frame_ = frame;
        latest_frame_received_at_ = now;
    }

    have_fallback_frame_ = true;
}

void RuntimeFrameReceiver::Run()
{
    SOCKET udp_socket = static_cast<SOCKET>(socket_);
    char buffer[2048];

    while (running_)
    {
        sockaddr_in from_addr = {};
        int from_len = sizeof(from_addr);

        const int received = recvfrom(
            udp_socket,
            buffer,
            static_cast<int>(sizeof(buffer) - 1),
            0,
            reinterpret_cast<sockaddr*>(&from_addr),
            &from_len);

        if (received == SOCKET_ERROR)
        {
            const int error = WSAGetLastError();
            if (!running_)
            {
                break;
            }

            if (error == WSAETIMEDOUT || error == WSAEWOULDBLOCK)
            {
                continue;
            }

            std::lock_guard<std::mutex> lock(stats_mutex_);
            last_error_ = "recvfrom() failed with code " + std::to_string(error);
            ProcessRawFallback(std::chrono::steady_clock::now());
            continue;
        }

        buffer[received] = '\0';
        std::string raw_line = TrimWhitespace(std::string(buffer, buffer + received));
        if (raw_line.empty())
        {
            ProcessRawFallback(std::chrono::steady_clock::now());
            continue;
        }

        RuntimeFrame parsed;
        std::string parse_error;
        if (!ParseRuntimeFrame(raw_line, &parsed, &parse_error))
        {
            std::lock_guard<std::mutex> lock(stats_mutex_);
            ++stats_.parse_errors;
            last_error_ = "packet parse failed: " + parse_error + " | line=" + raw_line;
            ProcessRawFallback(std::chrono::steady_clock::now());
            continue;
        }

        {
            std::lock_guard<std::mutex> lock(stats_mutex_);
            ++stats_.received_packets;
            last_error_.clear();
        }

        {
            std::lock_guard<std::mutex> lock(frame_mutex_);
            latest_frame_ = parsed;
            latest_frame_received_at_ = std::chrono::steady_clock::now();
        }

        last_runtime_packet_at_ = latest_frame_received_at_;
        ProcessRawFallback(last_runtime_packet_at_);
    }
}

} // namespace openfinger
