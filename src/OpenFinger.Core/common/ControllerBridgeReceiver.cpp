#include "common/ControllerBridgeReceiver.h"

#include <sstream>

#include <winsock2.h>
#include <ws2tcpip.h>

namespace openfinger
{

namespace
{

constexpr std::uintptr_t kInvalidSocketValue = static_cast<std::uintptr_t>(INVALID_SOCKET);

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

std::string MakeEndpointLabel(const sockaddr_in& addr)
{
    char ip_buffer[INET_ADDRSTRLEN] = {};
    inet_ntop(AF_INET, &addr.sin_addr, ip_buffer, sizeof(ip_buffer));

    std::ostringstream stream;
    stream << ip_buffer << ":" << ntohs(addr.sin_port);
    return stream.str();
}

} // namespace

ControllerBridgeReceiver::ControllerBridgeReceiver() = default;

ControllerBridgeReceiver::~ControllerBridgeReceiver()
{
    Stop();
}

bool ControllerBridgeReceiver::Start(std::uint16_t port, std::string* out_error)
{
    Stop();

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
    addr.sin_port = htons(port);

    if (bind(udp_socket, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR)
    {
        const int error = WSAGetLastError();
        closesocket(udp_socket);
        WSACleanup();
        wsa_started_ = false;
        if (out_error != nullptr)
        {
            *out_error = "bind() failed on UDP port " + std::to_string(port) + " with code " + std::to_string(error);
        }
        return false;
    }

    {
        std::lock_guard<std::mutex> stats_lock(stats_mutex_);
        stats_ = {};
        stats_.port = port;
        last_error_.clear();
    }

    {
        std::lock_guard<std::mutex> state_lock(state_mutex_);
        latest_states_ = {};
        latest_states_[0].side = HandSide::Left;
        latest_states_[1].side = HandSide::Right;
    }

    port_ = port;
    socket_ = static_cast<std::uintptr_t>(udp_socket);
    running_ = true;
    thread_ = std::thread(&ControllerBridgeReceiver::Run, this);
    return true;
}

void ControllerBridgeReceiver::Stop()
{
    running_ = false;

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

bool ControllerBridgeReceiver::IsRunning() const
{
    return running_.load();
}

bool ControllerBridgeReceiver::CopyLatestState(HandSide side, ForwardedControllerState* out_state) const
{
    if (out_state == nullptr)
    {
        return false;
    }

    std::lock_guard<std::mutex> lock(state_mutex_);
    const auto& state = latest_states_[side == HandSide::Left ? 0 : 1];
    if (state.received_at.time_since_epoch().count() == 0)
    {
        return false;
    }

    *out_state = state;
    return true;
}

ControllerBridgeStats ControllerBridgeReceiver::GetStats() const
{
    std::lock_guard<std::mutex> lock(stats_mutex_);
    return stats_;
}

std::string ControllerBridgeReceiver::GetLastError() const
{
    std::lock_guard<std::mutex> lock(stats_mutex_);
    return last_error_;
}

void ControllerBridgeReceiver::Run()
{
    SOCKET udp_socket = static_cast<SOCKET>(socket_);
    char buffer[1024];

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
            continue;
        }

        buffer[received] = '\0';
        std::string raw_line = TrimWhitespace(std::string(buffer, buffer + received));
        if (raw_line.empty())
        {
            continue;
        }

        ForwardedControllerState parsed;
        std::string parse_error;
        if (!ParseForwardedControllerPacket(raw_line, &parsed, &parse_error))
        {
            std::lock_guard<std::mutex> lock(stats_mutex_);
            ++stats_.parse_errors;
            last_error_ = "packet parse failed: " + parse_error + " | line=" + raw_line;
            continue;
        }

        parsed.received_at = std::chrono::steady_clock::now();
        parsed.source_endpoint = MakeEndpointLabel(from_addr);

        {
            std::lock_guard<std::mutex> lock(stats_mutex_);
            ++stats_.received_packets;
        }

        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            latest_states_[parsed.side == HandSide::Left ? 0 : 1] = parsed;
        }
    }
}

} // namespace openfinger
