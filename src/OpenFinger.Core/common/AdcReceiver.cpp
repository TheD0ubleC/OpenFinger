#include "common/AdcReceiver.h"

#include <algorithm>
#include <sstream>

#include <winsock2.h>
#include <ws2tcpip.h>

namespace openfinger
{

namespace
{

constexpr std::size_t kMaxQueueDepth = 256;
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

bool TryExtractProxyPacket(const std::string& line, std::string* out_source_endpoint, std::string* out_inner_line)
{
    constexpr std::string_view kProxyPrefix = "OFPROXY,";
    if (line.compare(0, kProxyPrefix.size(), kProxyPrefix) != 0)
    {
        return false;
    }

    const std::size_t source_start = kProxyPrefix.size();
    const std::size_t source_end = line.find(',', source_start);
    if (source_end == std::string::npos)
    {
        return false;
    }

    const std::string source_ip = TrimWhitespace(line.substr(source_start, source_end - source_start));
    const std::string inner_line = TrimWhitespace(line.substr(source_end + 1));
    if (source_ip.empty() || inner_line.empty())
    {
        return false;
    }

    if (out_source_endpoint != nullptr)
    {
        *out_source_endpoint = source_ip + ":0";
    }

    if (out_inner_line != nullptr)
    {
        *out_inner_line = inner_line;
    }

    return true;
}

} // namespace

AdcReceiver::AdcReceiver() = default;

AdcReceiver::~AdcReceiver()
{
    Stop();
}

bool AdcReceiver::Start(std::uint16_t port, std::string* out_error)
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
        std::lock_guard<std::mutex> lock(stats_mutex_);
        stats_ = {};
        stats_.port = port;
        last_error_.clear();
    }

    port_ = port;
    socket_ = static_cast<std::uintptr_t>(udp_socket);
    running_ = true;
    thread_ = std::thread(&AdcReceiver::Run, this);
    return true;
}

void AdcReceiver::Stop()
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

bool AdcReceiver::IsRunning() const
{
    return running_.load();
}

std::size_t AdcReceiver::DrainPackets(std::vector<ReceivedAdcPacket>* out_packets)
{
    if (out_packets == nullptr)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(queue_mutex_);
    const std::size_t count = queue_.size();
    out_packets->insert(out_packets->end(), queue_.begin(), queue_.end());
    queue_.clear();
    return count;
}

ReceiverStats AdcReceiver::GetStats() const
{
    std::lock_guard<std::mutex> lock(stats_mutex_);
    return stats_;
}

std::string AdcReceiver::GetLastError() const
{
    std::lock_guard<std::mutex> lock(stats_mutex_);
    return last_error_;
}

void AdcReceiver::Run()
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

        std::string source_endpoint = MakeEndpointLabel(from_addr);
        std::string parse_line = raw_line;
        TryExtractProxyPacket(raw_line, &source_endpoint, &parse_line);

        AdcPacket packet;
        std::string parse_error;
        if (!ParseAdcPacket(parse_line, &packet, &parse_error))
        {
            std::lock_guard<std::mutex> lock(stats_mutex_);
            ++stats_.parse_errors;
            last_error_ = "packet parse failed: " + parse_error + " | line=" + parse_line;
            continue;
        }

        ReceivedAdcPacket received_packet;
        received_packet.packet = packet;
        received_packet.received_at = std::chrono::steady_clock::now();
        received_packet.raw_line = std::move(parse_line);
        received_packet.source_endpoint = std::move(source_endpoint);

        {
            std::lock_guard<std::mutex> lock(stats_mutex_);
            ++stats_.received_packets;
        }

        PushPacket(std::move(received_packet));
    }
}

void AdcReceiver::PushPacket(ReceivedAdcPacket packet)
{
    std::lock_guard<std::mutex> lock(queue_mutex_);
    if (queue_.size() >= kMaxQueueDepth)
    {
        queue_.pop_front();
    }
    queue_.push_back(std::move(packet));
}

} // namespace openfinger
