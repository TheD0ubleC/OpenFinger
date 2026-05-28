#pragma once

#include <array>
#include <cstdint>
#include <string>
#include <string_view>

namespace openfinger
{

struct AdcPacket
{
    int seq = 0;
    std::uint64_t device_ms = 0;
    int mask = 0;
    std::array<int, 5> raw = { 0, 0, 0, 0, 0 };
    bool tracking_enabled = true;
};

bool ParseAdcPacket(std::string_view line, AdcPacket* out_packet, std::string* out_error = nullptr);

} // namespace openfinger
