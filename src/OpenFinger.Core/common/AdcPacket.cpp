#include "common/AdcPacket.h"

#include <array>
#include <charconv>

namespace openfinger
{

namespace
{

bool ParseInt(std::string_view token, int* out_value)
{
    const char* begin = token.data();
    const char* end = token.data() + token.size();

    int value = 0;
    const auto result = std::from_chars(begin, end, value);
    if (result.ec != std::errc{} || result.ptr != end)
    {
        return false;
    }

    *out_value = value;
    return true;
}

bool ParseUint64(std::string_view token, std::uint64_t* out_value)
{
    const char* begin = token.data();
    const char* end = token.data() + token.size();

    std::uint64_t value = 0;
    const auto result = std::from_chars(begin, end, value);
    if (result.ec != std::errc{} || result.ptr != end)
    {
        return false;
    }

    *out_value = value;
    return true;
}

} // namespace

bool ParseAdcPacket(std::string_view line, AdcPacket* out_packet, std::string* out_error)
{
    if (out_packet == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "output packet pointer was null";
        }
        return false;
    }

    std::array<std::string_view, 10> parts {};
    std::size_t part_index = 0;
    std::size_t start = 0;

    while (start <= line.size() && part_index < parts.size())
    {
        const std::size_t comma = line.find(',', start);
        if (comma == std::string_view::npos)
        {
            parts[part_index++] = line.substr(start);
            start = line.size() + 1;
            break;
        }

        parts[part_index++] = line.substr(start, comma - start);
        start = comma + 1;
    }

    if ((part_index != 9 && part_index != 10) || start <= line.size())
    {
        if (out_error != nullptr)
        {
            *out_error = "expected 9 or 10 CSV fields";
        }
        return false;
    }

    if (parts[0] != "OFADC")
    {
        if (out_error != nullptr)
        {
            *out_error = "packet prefix was not OFADC";
        }
        return false;
    }

    AdcPacket packet;
    if (!ParseInt(parts[1], &packet.seq))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid seq field";
        }
        return false;
    }

    if (!ParseUint64(parts[2], &packet.device_ms))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid ms field";
        }
        return false;
    }

    if (!ParseInt(parts[3], &packet.mask))
    {
        if (out_error != nullptr)
        {
            *out_error = "invalid mask field";
        }
        return false;
    }

    for (std::size_t i = 0; i < packet.raw.size(); ++i)
    {
        if (!ParseInt(parts[4 + i], &packet.raw[i]))
        {
            if (out_error != nullptr)
            {
                *out_error = "invalid raw field at index " + std::to_string(i);
            }
            return false;
        }
    }

    if (part_index >= 10)
    {
        int tracking_flag = 0;
        if (!ParseInt(parts[9], &tracking_flag))
        {
            if (out_error != nullptr)
            {
                *out_error = "invalid tracking field";
            }
            return false;
        }

        packet.tracking_enabled = tracking_flag != 0;
    }

    *out_packet = packet;
    return true;
}

} // namespace openfinger
