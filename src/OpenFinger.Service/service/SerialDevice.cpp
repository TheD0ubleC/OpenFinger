#include "service/SerialDevice.h"

#include <algorithm>
#include <chrono>
#include <cctype>
#include <cstring>
#include <string_view>
#include <vector>

#include <windows.h>

namespace openfinger
{

namespace
{

class SerialHandle
{
public:
    SerialHandle() = default;
    explicit SerialHandle(HANDLE handle)
        : handle_(handle)
    {
    }

    ~SerialHandle()
    {
        Reset();
    }

    SerialHandle(const SerialHandle&) = delete;
    SerialHandle& operator=(const SerialHandle&) = delete;

    SerialHandle(SerialHandle&& other) noexcept
        : handle_(other.handle_)
    {
        other.handle_ = INVALID_HANDLE_VALUE;
    }

    SerialHandle& operator=(SerialHandle&& other) noexcept
    {
        if (this != &other)
        {
            Reset();
            handle_ = other.handle_;
            other.handle_ = INVALID_HANDLE_VALUE;
        }
        return *this;
    }

    HANDLE get() const
    {
        return handle_;
    }

    explicit operator bool() const
    {
        return handle_ != INVALID_HANDLE_VALUE;
    }

private:
    void Reset()
    {
        if (handle_ != INVALID_HANDLE_VALUE)
        {
            CloseHandle(handle_);
            handle_ = INVALID_HANDLE_VALUE;
        }
    }

    HANDLE handle_ = INVALID_HANDLE_VALUE;
};

std::string ToUpperCopy(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::toupper(ch));
    });
    return value;
}

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

bool StartsWith(std::string_view value, std::string_view prefix)
{
    return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

std::string ReadRegistryString(HKEY key, DWORD index)
{
    char value_name[256] = {};
    char value_data[256] = {};
    DWORD value_name_size = sizeof(value_name);
    DWORD value_data_size = sizeof(value_data);
    DWORD value_type = 0;

    const LSTATUS status = RegEnumValueA(
        key,
        index,
        value_name,
        &value_name_size,
        nullptr,
        &value_type,
        reinterpret_cast<LPBYTE>(value_data),
        &value_data_size);
    if (status != ERROR_SUCCESS || value_type != REG_SZ)
    {
        return {};
    }

    return std::string(value_data, value_data + std::strlen(value_data));
}

std::string PortPath(std::string_view port_name)
{
    return "\\\\.\\" + std::string(port_name);
}

bool ConfigureSerialPort(HANDLE handle, std::string* out_error)
{
    DCB dcb {};
    dcb.DCBlength = sizeof(dcb);
    if (!GetCommState(handle, &dcb))
    {
        if (out_error != nullptr)
        {
            *out_error = "GetCommState failed";
        }
        return false;
    }

    dcb.BaudRate = 115200;
    dcb.ByteSize = 8;
    dcb.Parity = NOPARITY;
    dcb.StopBits = ONESTOPBIT;
    dcb.fBinary = TRUE;
    dcb.fOutxCtsFlow = FALSE;
    dcb.fOutxDsrFlow = FALSE;
    dcb.fDtrControl = DTR_CONTROL_DISABLE;
    dcb.fDsrSensitivity = FALSE;
    dcb.fTXContinueOnXoff = TRUE;
    dcb.fOutX = FALSE;
    dcb.fInX = FALSE;
    dcb.fErrorChar = FALSE;
    dcb.fNull = FALSE;
    dcb.fRtsControl = RTS_CONTROL_DISABLE;
    dcb.fAbortOnError = FALSE;

    if (!SetCommState(handle, &dcb))
    {
        if (out_error != nullptr)
        {
            *out_error = "SetCommState failed";
        }
        return false;
    }

    COMMTIMEOUTS timeouts {};
    timeouts.ReadIntervalTimeout = 50;
    timeouts.ReadTotalTimeoutConstant = 100;
    timeouts.ReadTotalTimeoutMultiplier = 10;
    timeouts.WriteTotalTimeoutConstant = 500;
    timeouts.WriteTotalTimeoutMultiplier = 20;
    if (!SetCommTimeouts(handle, &timeouts))
    {
        if (out_error != nullptr)
        {
            *out_error = "SetCommTimeouts failed";
        }
        return false;
    }

    EscapeCommFunction(handle, CLRDTR);
    EscapeCommFunction(handle, CLRRTS);
    PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
    return true;
}

SerialHandle OpenSerialPort(std::string_view port_name, std::string* out_error)
{
    const std::string path = PortPath(port_name);
    HANDLE handle = CreateFileA(
        path.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        0,
        nullptr);
    if (handle == INVALID_HANDLE_VALUE)
    {
        if (out_error != nullptr)
        {
            *out_error = "CreateFile failed for " + std::string(port_name);
        }
        return {};
    }

    if (!ConfigureSerialPort(handle, out_error))
    {
        CloseHandle(handle);
        return {};
    }

    return SerialHandle(handle);
}

bool WriteSerialLine(HANDLE handle, std::string_view line, std::string* out_error)
{
    const std::string payload(line);
    DWORD written = 0;
    if (!WriteFile(handle, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr) || written != payload.size())
    {
        if (out_error != nullptr)
        {
            *out_error = "WriteFile failed";
        }
        return false;
    }
    FlushFileBuffers(handle);
    return true;
}

bool ReadLineWithDeadline(HANDLE handle, std::chrono::steady_clock::time_point deadline, std::string* out_line)
{
    std::string line;
    char ch = '\0';
    DWORD read = 0;
    while (std::chrono::steady_clock::now() < deadline)
    {
        if (!ReadFile(handle, &ch, 1, &read, nullptr))
        {
            return false;
        }

        if (read == 0)
        {
            continue;
        }

        if (ch == '\n')
        {
            *out_line = TrimWhitespace(line);
            return true;
        }

        if (ch != '\r')
        {
            line.push_back(ch);
        }
    }

    if (!line.empty())
    {
        *out_line = TrimWhitespace(line);
        return true;
    }

    return false;
}

bool AwaitStatusJson(HANDLE handle, std::chrono::milliseconds timeout, DeviceStatusMessage* out_status)
{
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline)
    {
        std::string line;
        if (!ReadLineWithDeadline(handle, deadline, &line))
        {
            continue;
        }

        if (StartsWith(line, "OFSTATUS "))
        {
            return ParseDeviceStatusJson(line.substr(std::strlen("OFSTATUS ")), out_status);
        }
    }

    return false;
}

bool RequestStatusJson(HANDLE handle, std::string_view command, std::chrono::milliseconds timeout, DeviceStatusMessage* out_status, bool* out_received, std::string* out_error)
{
    PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
    if (!WriteSerialLine(handle, command, out_error))
    {
        return false;
    }

    if (out_received != nullptr)
    {
        *out_received = AwaitStatusJson(handle, timeout, out_status);
    }
    return true;
}

bool SendAdcConfigCommand(HANDLE handle, const ProvisionRequest& request, bool include_network_fields, std::string* out_error)
{
    const std::string query = BuildAdcConfigQuery(request, include_network_fields);
    const std::string line = "OFADC_CFG " + query + "\n";
    return WriteSerialLine(handle, line, out_error);
}

} // namespace

std::vector<std::string> EnumerateSerialPorts()
{
    std::vector<std::string> ports;
    HKEY key = nullptr;
    if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, "HARDWARE\\DEVICEMAP\\SERIALCOMM", 0, KEY_READ, &key) != ERROR_SUCCESS)
    {
        return ports;
    }

    DWORD index = 0;
    while (true)
    {
        const std::string value = ReadRegistryString(key, index++);
        if (value.empty())
        {
            break;
        }

        if (StartsWith(ToUpperCopy(value), "COM"))
        {
            ports.push_back(value);
        }
    }

    RegCloseKey(key);
    std::sort(ports.begin(), ports.end());
    ports.erase(std::unique(ports.begin(), ports.end()), ports.end());
    return ports;
}

bool ReadDeviceStatusFromSerial(const std::string& port_name, DeviceStatusMessage* out_status, std::string* out_error)
{
    SerialHandle handle = OpenSerialPort(port_name, out_error);
    if (!handle)
    {
        return false;
    }

    Sleep(180);
    std::string ignored;
    AwaitStatusJson(handle.get(), std::chrono::milliseconds(300), out_status);
    if (out_status != nullptr && !out_status->device_name.empty())
    {
        return true;
    }

    static constexpr std::string_view kCommands[] =
    {
        "OFINFO\n",
        "OFSTATUS\n",
        "OFHELLO\n",
    };

    for (int attempt = 0; attempt < 3; ++attempt)
    {
        for (const auto command : kCommands)
        {
            bool received = false;
            if (!RequestStatusJson(handle.get(), command, std::chrono::milliseconds(1800), out_status, &received, out_error))
            {
                return false;
            }

            if (received && out_status != nullptr && !out_status->device_name.empty())
            {
                return true;
            }
        }

        Sleep(250);
    }

    if (out_error != nullptr)
    {
        *out_error = "device did not answer OFSTATUS";
    }

    return false;
}

bool SendIdentifyOverSerial(const std::string& port_name, std::string* out_error)
{
    SerialHandle handle = OpenSerialPort(port_name, out_error);
    if (!handle)
    {
        return false;
    }

    return WriteSerialLine(handle.get(), "OFIDENT\n", out_error);
}

bool SendProvisionOverSerial(const std::string& port_name, const ProvisionRequest& request, std::string* out_error)
{
    SerialHandle handle = OpenSerialPort(port_name, out_error);
    if (!handle)
    {
        return false;
    }

    const std::string query = BuildProvisionQuery(request);
    const std::string line = "OFPROV " + query + "\n";
    return WriteSerialLine(handle.get(), line, out_error);
}

bool SendRoleOverSerial(const std::string& port_name, HandRole role, std::string* out_error)
{
    ProvisionRequest request;
    request.role = role;

    SerialHandle handle = OpenSerialPort(port_name, out_error);
    if (!handle)
    {
        return false;
    }

    return SendAdcConfigCommand(handle.get(), request, false, out_error);
}

bool ResetDeviceOverSerial(const std::string& port_name, std::string* out_error)
{
    SerialHandle handle = OpenSerialPort(port_name, out_error);
    if (!handle)
    {
        return false;
    }

    return WriteSerialLine(handle.get(), "OFRESET\n", out_error);
}

} // namespace openfinger
