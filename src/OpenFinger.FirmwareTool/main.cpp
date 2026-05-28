#include "service/SerialDevice.h"
#include "openfinger/OpenFingerVersion.h"

#include <windows.h>

#include <algorithm>
#include <cctype>
#include <cstring>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

namespace openfinger
{
namespace
{

constexpr const char* kDefaultEnv = "esp32-c3-dev-module";

struct ProcessResult
{
    int exit_code = -1;
    std::string output;
};

struct ToolInvocation
{
    std::string executable;
    std::vector<std::string> prefix_arguments;
    std::string display_name;
    std::vector<std::pair<std::string, std::string>> environment_overrides;
};

struct FirmwareBundleFile
{
    std::string file;
    std::string offset;
};

struct FirmwareBundleManifest
{
    std::string id;
    std::string display_name;
    std::string target;
    std::string version;
    int report_rate_hz = 30;
    std::string boot_hint;
    std::filesystem::path manifest_path;
    FirmwareBundleFile bootloader;
    FirmwareBundleFile partitions;
    FirmwareBundleFile firmware;
};

std::string EscapeJson(std::string_view value)
{
    std::string escaped;
    escaped.reserve(value.size() + 8);
    for (const char ch : value)
    {
        switch (ch)
        {
        case '\\':
            escaped += "\\\\";
            break;
        case '"':
            escaped += "\\\"";
            break;
        case '\r':
            escaped += "\\r";
            break;
        case '\n':
            escaped += "\\n";
            break;
        case '\t':
            escaped += "\\t";
            break;
        default:
            escaped.push_back(ch);
            break;
        }
    }
    return escaped;
}

std::string QuoteCommandArgument(std::string_view value)
{
    if (value.find_first_of(" \t\"") == std::string_view::npos)
    {
        return std::string(value);
    }

    std::string quoted = "\"";
    for (const char ch : value)
    {
        if (ch == '"')
        {
            quoted += "\\\"";
        }
        else
        {
            quoted.push_back(ch);
        }
    }
    quoted += "\"";
    return quoted;
}

std::string ToLowerCopy(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool EqualsIgnoreCase(std::string_view left, std::string_view right)
{
    return left.size() == right.size()
        && std::equal(left.begin(), left.end(), right.begin(), right.end(), [](char a, char b) {
            return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b));
        });
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

std::string ReadTextFile(const std::filesystem::path& path, std::string* out_error)
{
    std::ifstream stream(path, std::ios::binary);
    if (!stream)
    {
        if (out_error != nullptr)
        {
            *out_error = "failed to open " + path.string();
        }
        return {};
    }

    std::ostringstream buffer;
    buffer << stream.rdbuf();
    return buffer.str();
}

bool ExtractString(const std::string& text, const char* key, std::string* out_value)
{
    const std::string needle = std::string("\"") + key + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return false;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return false;
    }

    const std::size_t quote_start = text.find('"', colon + 1);
    if (quote_start == std::string::npos)
    {
        return false;
    }

    std::string result;
    bool escaping = false;
    for (std::size_t index = quote_start + 1; index < text.size(); ++index)
    {
        const char ch = text[index];
        if (escaping)
        {
            result.push_back(ch);
            escaping = false;
            continue;
        }

        if (ch == '\\')
        {
            escaping = true;
            continue;
        }

        if (ch == '"')
        {
            *out_value = result;
            return true;
        }

        result.push_back(ch);
    }

    return false;
}

bool ExtractInt(const std::string& text, const char* key, int* out_value)
{
    const std::string needle = std::string("\"") + key + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return false;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return false;
    }

    std::size_t start = colon + 1;
    while (start < text.size() && std::isspace(static_cast<unsigned char>(text[start])))
    {
        ++start;
    }

    std::size_t end = start;
    if (end < text.size() && text[end] == '-')
    {
        ++end;
    }
    while (end < text.size() && std::isdigit(static_cast<unsigned char>(text[end])))
    {
        ++end;
    }

    if (start == end)
    {
        return false;
    }

    *out_value = std::stoi(text.substr(start, end - start));
    return true;
}

bool ExtractObjectBlock(const std::string& text, const char* key, std::string* out_block)
{
    const std::string needle = std::string("\"") + key + "\"";
    const std::size_t key_pos = text.find(needle);
    if (key_pos == std::string::npos)
    {
        return false;
    }

    const std::size_t colon = text.find(':', key_pos + needle.size());
    if (colon == std::string::npos)
    {
        return false;
    }

    const std::size_t brace_start = text.find('{', colon + 1);
    if (brace_start == std::string::npos)
    {
        return false;
    }

    int depth = 0;
    for (std::size_t index = brace_start; index < text.size(); ++index)
    {
        if (text[index] == '{')
        {
            ++depth;
        }
        else if (text[index] == '}')
        {
            --depth;
            if (depth == 0)
            {
                *out_block = text.substr(brace_start, index - brace_start + 1);
                return true;
            }
        }
    }

    return false;
}

bool ParseBundleFile(const std::string& text, const char* key, FirmwareBundleFile* out_file)
{
    if (out_file == nullptr)
    {
        return false;
    }

    std::string object_block;
    if (!ExtractObjectBlock(text, key, &object_block))
    {
        return false;
    }

    return ExtractString(object_block, "file", &out_file->file)
        && ExtractString(object_block, "offset", &out_file->offset);
}

bool LoadFirmwareBundleManifest(const std::filesystem::path& manifest_path, FirmwareBundleManifest* out_manifest, std::string* out_error)
{
    if (out_manifest == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "manifest output pointer was null";
        }
        return false;
    }

    const std::string text = ReadTextFile(manifest_path, out_error);
    if (text.empty())
    {
        return false;
    }

    FirmwareBundleManifest manifest;
    if (!ExtractString(text, "id", &manifest.id)
        || !ExtractString(text, "display_name", &manifest.display_name)
        || !ExtractString(text, "target", &manifest.target)
        || !ExtractString(text, "version", &manifest.version)
        || !ExtractInt(text, "report_rate_hz", &manifest.report_rate_hz)
        || !ParseBundleFile(text, "bootloader", &manifest.bootloader)
        || !ParseBundleFile(text, "partitions", &manifest.partitions)
        || !ParseBundleFile(text, "firmware", &manifest.firmware))
    {
        if (out_error != nullptr)
        {
            *out_error = "manifest.json missing required fields";
        }
        return false;
    }

    ExtractString(text, "boot_hint", &manifest.boot_hint);
    manifest.manifest_path = manifest_path;

    const auto root = manifest_path.parent_path();
    const auto bootloader = root / manifest.bootloader.file;
    const auto partitions = root / manifest.partitions.file;
    const auto firmware = root / manifest.firmware.file;
    if (!std::filesystem::exists(bootloader) || !std::filesystem::exists(partitions) || !std::filesystem::exists(firmware))
    {
        if (out_error != nullptr)
        {
            *out_error = "manifest referenced missing firmware files";
        }
        return false;
    }

    *out_manifest = manifest;
    return true;
}

std::filesystem::path ModuleDirectory()
{
    std::vector<wchar_t> buffer(MAX_PATH);
    DWORD size = 0;
    while (true)
    {
        size = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (size == 0)
        {
            return std::filesystem::current_path();
        }

        if (size < buffer.size())
        {
            return std::filesystem::path(std::wstring(buffer.data(), size)).parent_path();
        }

        buffer.resize(buffer.size() * 2);
    }
}

std::filesystem::path ResolveRepositoryRoot()
{
    std::vector<std::filesystem::path> roots;
    roots.push_back(std::filesystem::current_path());
    roots.push_back(ModuleDirectory());

    for (const auto& start : roots)
    {
        for (auto current = start; !current.empty(); current = current.parent_path())
        {
            if (std::filesystem::exists(current / "src" / "firmware" / "esp32c3" / "platformio.ini")
                && std::filesystem::exists(current / "CMakeLists.txt"))
            {
                return current;
            }

            if (current == current.root_path())
            {
                break;
            }
        }
    }

    return std::filesystem::current_path();
}

std::filesystem::path FirmwareProjectDirectory(const std::filesystem::path& repo_root)
{
    return repo_root / "src" / "firmware" / "esp32c3";
}

std::filesystem::path FirmwareArtifactPath(const std::filesystem::path& project_dir, std::string_view env_name)
{
    return project_dir / ".pio" / "build" / std::string(env_name) / "firmware.bin";
}

std::string ResolvePioExecutable()
{
    char buffer[MAX_PATH] = {};
    const DWORD length = SearchPathA(nullptr, "pio.exe", nullptr, MAX_PATH, buffer, nullptr);
    if (length > 0 && length < MAX_PATH)
    {
        return std::string(buffer, buffer + length);
    }

    return "pio";
}

bool ResolveBundledEspflashInvocation(ToolInvocation* out_invocation)
{
    if (out_invocation == nullptr)
    {
        return false;
    }

    const auto module_dir = ModuleDirectory();
    const auto repo_root = ResolveRepositoryRoot();
    const std::vector<std::filesystem::path> candidates =
    {
        module_dir / "FirmwareTools" / "espflash.exe",
        module_dir / "FirmwareTools" / "espflash" / "espflash.exe",
        module_dir / "espflash.exe",
        repo_root / "src" / "OpenFinger.Control" / "FirmwareTools" / "espflash.exe",
        repo_root / ".codex_temp" / "cargo-root" / "bin" / "espflash.exe"
    };

    for (const auto& candidate : candidates)
    {
        if (!std::filesystem::exists(candidate))
        {
            continue;
        }

        out_invocation->executable = candidate.string();
        out_invocation->display_name = "bundled espflash.exe";
        out_invocation->prefix_arguments.clear();
        out_invocation->environment_overrides.clear();
        return true;
    }

    return false;
}

bool ResolveCargoEspflashInvocation(ToolInvocation* out_invocation)
{
    if (out_invocation == nullptr)
    {
        return false;
    }

    if (const char* user_profile = std::getenv("USERPROFILE");
        user_profile != nullptr && *user_profile != '\0')
    {
        const auto candidate = std::filesystem::path(user_profile) / ".cargo" / "bin" / "espflash.exe";
        if (std::filesystem::exists(candidate))
        {
            out_invocation->executable = candidate.string();
            out_invocation->display_name = "cargo espflash.exe";
            out_invocation->prefix_arguments.clear();
            out_invocation->environment_overrides.clear();
            return true;
        }
    }

    return false;
}

bool ResolvePlatformIoEsptoolInvocation(ToolInvocation* out_invocation)
{
    if (out_invocation == nullptr)
    {
        return false;
    }

    if (const char* user_profile = std::getenv("USERPROFILE");
        user_profile != nullptr && *user_profile != '\0')
    {
        const auto root = std::filesystem::path(user_profile) / ".platformio";
        const auto python = root / "penv" / "Scripts" / "python.exe";
        const auto esptool = root / "packages" / "tool-esptoolpy" / "esptool.py";
        if (std::filesystem::exists(python) && std::filesystem::exists(esptool))
        {
            out_invocation->executable = python.string();
            out_invocation->display_name = "platformio esptool.py";
            out_invocation->prefix_arguments = { esptool.string() };
            out_invocation->environment_overrides.clear();
            return true;
        }
    }

    return false;
}

bool ResolveEspflashInvocation(ToolInvocation* out_invocation, std::string* out_error)
{
    if (out_invocation == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "tool invocation pointer was null";
        }
        return false;
    }

    if (ResolveBundledEspflashInvocation(out_invocation))
    {
        return true;
    }

    if (ResolveCargoEspflashInvocation(out_invocation))
    {
        return true;
    }

    char buffer[MAX_PATH] = {};
    DWORD length = SearchPathA(nullptr, "espflash.exe", nullptr, MAX_PATH, buffer, nullptr);
    if (length > 0 && length < MAX_PATH)
    {
        out_invocation->executable = std::string(buffer, buffer + length);
        out_invocation->display_name = "espflash.exe";
        out_invocation->prefix_arguments.clear();
        out_invocation->environment_overrides.clear();
        return true;
    }

    if (out_error != nullptr)
    {
        *out_error = "could not find espflash.exe";
    }
    return false;
}

bool ResolveEsptoolInvocation(ToolInvocation* out_invocation, std::string* out_error)
{
    if (out_invocation == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "tool invocation pointer was null";
        }
        return false;
    }

    if (ResolvePlatformIoEsptoolInvocation(out_invocation))
    {
        return true;
    }

    if (out_error != nullptr)
    {
        *out_error = "could not find esptool.py";
    }
    return false;
}

bool RunProcess(
    const std::string& executable,
    const std::vector<std::string>& arguments,
    const std::filesystem::path& working_directory,
    ProcessResult* out_result,
    std::string* out_error,
    const std::vector<std::pair<std::string, std::string>>* environment_overrides = nullptr)
{
    if (out_result == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "output result pointer was null";
        }
        return false;
    }

    SECURITY_ATTRIBUTES security {};
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;

    HANDLE read_pipe = nullptr;
    HANDLE write_pipe = nullptr;
    if (!CreatePipe(&read_pipe, &write_pipe, &security, 0))
    {
        if (out_error != nullptr)
        {
            *out_error = "CreatePipe failed";
        }
        return false;
    }

    SetHandleInformation(read_pipe, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOA startup {};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = write_pipe;
    startup.hStdError = write_pipe;

    PROCESS_INFORMATION process {};

    std::ostringstream command_line;
    command_line << QuoteCommandArgument(executable);
    for (const auto& argument : arguments)
    {
        command_line << " " << QuoteCommandArgument(argument);
    }
    std::string command = command_line.str();
    std::vector<char> mutable_command(command.begin(), command.end());
    mutable_command.push_back('\0');

    std::vector<char> environment_block;
    LPVOID environment_ptr = nullptr;
    if (environment_overrides != nullptr && !environment_overrides->empty())
    {
        auto should_override = [&](std::string_view key) {
            return std::find_if(environment_overrides->begin(), environment_overrides->end(), [&](const auto& item) {
                return EqualsIgnoreCase(item.first, key);
            }) != environment_overrides->end();
        };

        if (LPCH inherited = GetEnvironmentStringsA())
        {
            for (LPCCH cursor = inherited; *cursor != '\0'; cursor += std::strlen(cursor) + 1)
            {
                std::string entry(cursor);
                const auto separator = entry.find('=');
                const bool is_special = !entry.empty() && entry.front() == '=';
                if (!is_special && separator != std::string::npos && should_override(entry.substr(0, separator)))
                {
                    continue;
                }

                environment_block.insert(environment_block.end(), entry.begin(), entry.end());
                environment_block.push_back('\0');
            }

            FreeEnvironmentStringsA(inherited);
        }

        for (const auto& [key, value] : *environment_overrides)
        {
            const std::string entry = key + "=" + value;
            environment_block.insert(environment_block.end(), entry.begin(), entry.end());
            environment_block.push_back('\0');
        }

        environment_block.push_back('\0');
        environment_ptr = environment_block.data();
    }

    const BOOL created = CreateProcessA(
        nullptr,
        mutable_command.data(),
        nullptr,
        nullptr,
        TRUE,
        CREATE_NO_WINDOW,
        environment_ptr,
        working_directory.string().c_str(),
        &startup,
        &process);

    CloseHandle(write_pipe);

    if (!created)
    {
        CloseHandle(read_pipe);
        if (out_error != nullptr)
        {
            *out_error = "CreateProcess failed";
        }
        return false;
    }

    std::string output;
    char buffer[4096] = {};
    DWORD bytes_read = 0;
    while (ReadFile(read_pipe, buffer, sizeof(buffer), &bytes_read, nullptr) && bytes_read > 0)
    {
        output.append(buffer, buffer + bytes_read);
    }

    WaitForSingleObject(process.hProcess, INFINITE);

    DWORD exit_code = 0;
    GetExitCodeProcess(process.hProcess, &exit_code);

    CloseHandle(read_pipe);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);

    out_result->exit_code = static_cast<int>(exit_code);
    out_result->output = std::move(output);
    return true;
}

std::string JsonBool(bool value)
{
    return value ? "true" : "false";
}

std::string ExtractOption(const std::vector<std::string>& arguments, std::string_view key)
{
    for (std::size_t index = 0; index + 1 < arguments.size(); ++index)
    {
        if (arguments[index] == key)
        {
            return arguments[index + 1];
        }
    }
    return {};
}

bool ContainsUploadPortFailure(std::string_view output)
{
    const std::string lowered = ToLowerCopy(std::string(output));
    return lowered.find("could not open port") != std::string::npos
        || lowered.find("failed to open serial port") != std::string::npos
        || lowered.find("serial port not found") != std::string::npos
        || lowered.find("could not connect to device") != std::string::npos
        || lowered.find("could not open serial device") != std::string::npos
        || lowered.find("the system cannot find the file specified") != std::string::npos
        || lowered.find("serial exception") != std::string::npos
        || lowered.find("permissionerror") != std::string::npos;
}

bool ContainsRetryableFlashFailure(std::string_view output)
{
    const std::string lowered = ToLowerCopy(std::string(output));
    return lowered.find("espflash::timeout") != std::string::npos
        || lowered.find("error while connecting to device") != std::string::npos
        || lowered.find("timeout while running command") != std::string::npos;
}

std::string PickReplacementPort(const std::vector<std::string>& before, const std::vector<std::string>& after, std::string_view original)
{
    for (const auto& port : after)
    {
        if (!std::equal(port.begin(), port.end(), original.begin(), original.end(),
                [](char left, char right) { return std::tolower(left) == std::tolower(right); })
            && std::find_if(before.begin(), before.end(), [&](const std::string& item) {
                return ToLowerCopy(item) == ToLowerCopy(port);
            }) == before.end())
        {
            return port;
        }
    }

    if (after.size() == 1)
    {
        return after.front();
    }

    return {};
}

bool RunEspflashStage(
    const ToolInvocation& invocation,
    const std::filesystem::path& working_directory,
    const std::string& port,
    const std::string& chip,
    std::string_view baud,
    std::string_view before,
    std::string_view after,
    const FirmwareBundleFile& file,
    ProcessResult* out_result,
    std::string* out_error)
{
    if (out_result == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "stage result pointer was null";
        }
        return false;
    }

    std::vector<std::string> args = invocation.prefix_arguments;
    args.push_back("--skip-update-check");
    args.push_back("write-bin");
    args.push_back("-p");
    args.push_back(port);
    args.push_back("-c");
    args.push_back(chip);
    args.push_back("-B");
    args.push_back(std::string(baud));
    args.push_back("--before");
    args.push_back(std::string(before));
    args.push_back("--after");
    args.push_back(std::string(after));
    args.push_back(file.offset);
    args.push_back((working_directory / file.file).string());

    return RunProcess(invocation.executable, args, working_directory, out_result, out_error, &invocation.environment_overrides);
}

std::vector<std::string> GetEspflashBaudCandidates(std::string_view chip)
{
    const std::string lowered = ToLowerCopy(std::string(chip));
    if (lowered.find("esp32s3") != std::string::npos)
    {
        return { "921600", "460800", "230400" };
    }

    return { "921600", "460800" };
}

bool RunEspflashStageAcrossBauds(
    const ToolInvocation& invocation,
    const std::filesystem::path& working_directory,
    const std::string& port,
    const std::string& chip,
    std::string_view before,
    std::string_view after,
    const FirmwareBundleFile& file,
    ProcessResult* out_result,
    std::string* out_error)
{
    if (out_result == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "stage result pointer was null";
        }
        return false;
    }

    const auto baud_candidates = GetEspflashBaudCandidates(chip);
    ProcessResult attempt_result;
    if (!RunEspflashStage(invocation, working_directory, port, chip, baud_candidates.front(), before, after, file, &attempt_result, out_error))
    {
        return false;
    }

    *out_result = attempt_result;
    if (attempt_result.exit_code == 0)
    {
        return true;
    }

    for (std::size_t index = 1; index < baud_candidates.size(); ++index)
    {
        ProcessResult retry_result;
        if (!RunEspflashStage(invocation, working_directory, port, chip, baud_candidates[index], before, after, file, &retry_result, out_error))
        {
            return false;
        }

        out_result->output += "\n[retry] switched baud to " + baud_candidates[index] + "\n" + retry_result.output;
        out_result->exit_code = retry_result.exit_code;
        if (retry_result.exit_code == 0)
        {
            return true;
        }
    }

    return true;
}

bool RunEspflashStageWithRetry(
    const ToolInvocation& invocation,
    const std::filesystem::path& working_directory,
    const std::string& port,
    const std::string& chip,
    std::string_view before,
    std::string_view after,
    const FirmwareBundleFile& file,
    ProcessResult* out_result,
    std::string* out_error,
    std::string* out_used_port)
{
    if (out_result == nullptr || out_used_port == nullptr)
    {
        if (out_error != nullptr)
        {
            *out_error = "stage output pointer was null";
        }
        return false;
    }

    const auto before_ports = EnumerateSerialPorts();
    if (!RunEspflashStageAcrossBauds(invocation, working_directory, port, chip, before, after, file, out_result, out_error))
    {
        return false;
    }

    *out_used_port = port;
    if (out_result->exit_code != 0 && ContainsRetryableFlashFailure(out_result->output))
    {
        Sleep(250);
        ProcessResult retry_same_port;
        if (!RunEspflashStageAcrossBauds(invocation, working_directory, port, chip, before, after, file, &retry_same_port, out_error))
        {
            return false;
        }

        out_result->output += "\n[retry] retried upload port " + port + " after timeout\n" + retry_same_port.output;
        out_result->exit_code = retry_same_port.exit_code;
        if (retry_same_port.exit_code == 0)
        {
            return true;
        }
    }

    if (out_result->exit_code == 0 || !ContainsUploadPortFailure(out_result->output))
    {
        return true;
    }

    const auto after_ports = EnumerateSerialPorts();
    const std::string replacement = PickReplacementPort(before_ports, after_ports, port);
    if (replacement.empty())
    {
        return true;
    }

    ProcessResult retry_result;
    if (!RunEspflashStageAcrossBauds(invocation, working_directory, replacement, chip, before, after, file, &retry_result, out_error))
    {
        return false;
    }

    out_result->output += "\n[retry] switched upload port to " + replacement + "\n" + retry_result.output;
    out_result->exit_code = retry_result.exit_code;
    *out_used_port = replacement;
    return true;
}

bool RunEsptoolWatchdogReset(
    const std::string& port,
    const std::string& chip,
    ProcessResult* out_result,
    std::string* out_error);

bool RunEspflashFlash(
    const std::string& port,
    const FirmwareBundleManifest& manifest,
    ProcessResult* out_result,
    std::string* out_error,
    std::string* out_used_port)
{
    ToolInvocation invocation;
    if (!ResolveEspflashInvocation(&invocation, out_error))
    {
        return false;
    }

    const auto working_directory = manifest.manifest_path.parent_path();
    const std::string chip = ToLowerCopy(manifest.target);
    if (chip.empty())
    {
        if (out_error != nullptr)
        {
            *out_error = "manifest target was empty";
        }
        return false;
    }

    struct FlashStage
    {
        const char* title;
        const FirmwareBundleFile* file;
        const char* before;
        const char* after;
    };

    const FlashStage stages[] =
    {
        { "bootloader", &manifest.bootloader, "default-reset", "no-reset" },
        { "partitions", &manifest.partitions, "no-reset", "no-reset" },
        { "firmware", &manifest.firmware, "no-reset", "hard-reset" }
    };

    std::string current_port = port;
    ProcessResult combined;
    combined.exit_code = 0;

    for (const auto& stage : stages)
    {
        ProcessResult stage_result;
        std::string stage_port;
        if (!RunEspflashStageWithRetry(
                invocation,
                working_directory,
                current_port,
                chip,
                stage.before,
                stage.after,
                *stage.file,
                &stage_result,
                out_error,
                &stage_port))
        {
            return false;
        }

        if (!combined.output.empty())
        {
            combined.output += "\n";
        }

        combined.output += "[tool] " + invocation.display_name + "\n";
        combined.output += "[stage] ";
        combined.output += stage.title;
        combined.output += " ";
        combined.output += stage.file->offset;
        combined.output += " ";
        combined.output += stage.file->file;
        combined.output += "\n";
        combined.output += stage_result.output;

        current_port = stage_port;
        combined.exit_code = stage_result.exit_code;
        if (stage_result.exit_code != 0)
        {
            *out_result = std::move(combined);
            *out_used_port = current_port;
            return true;
        }
    }

    *out_result = std::move(combined);
    *out_used_port = current_port;

    if (combined.exit_code == 0 && EqualsIgnoreCase(chip, "esp32s3"))
    {
        ProcessResult reset_result;
        if (!RunEsptoolWatchdogReset(current_port, chip, &reset_result, out_error))
        {
            return false;
        }

        if (!out_result->output.empty())
        {
            out_result->output += "\n";
        }

        out_result->output += "[tool] platformio esptool.py\n";
        out_result->output += "[stage] watchdog-reset\n";
        out_result->output += reset_result.output;
        out_result->exit_code = reset_result.exit_code;
        return true;
    }

    return true;
}

bool RunEspflashBoardInfo(
    const std::string& port,
    ProcessResult* out_result,
    std::string* out_error)
{
    ToolInvocation invocation;
    if (!ResolveEspflashInvocation(&invocation, out_error))
    {
        return false;
    }

    std::vector<std::string> args = invocation.prefix_arguments;
    args.push_back("--skip-update-check");
    args.push_back("board-info");
    args.push_back("-p");
    args.push_back(port);
    args.push_back("-b");
    args.push_back("no-reset");
    args.push_back("-a");
    args.push_back("no-reset");

    return RunProcess(
        invocation.executable,
        args,
        ResolveRepositoryRoot(),
        out_result,
        out_error,
        &invocation.environment_overrides);
}

bool RunEsptoolWatchdogReset(
    const std::string& port,
    const std::string& chip,
    ProcessResult* out_result,
    std::string* out_error)
{
    ToolInvocation invocation;
    if (!ResolveEsptoolInvocation(&invocation, out_error))
    {
        return false;
    }

    std::vector<std::string> args = invocation.prefix_arguments;
    args.push_back("--chip");
    args.push_back(chip);
    args.push_back("--port");
    args.push_back(port);
    args.push_back("--before");
    args.push_back("default_reset");
    args.push_back("--after");
    args.push_back("watchdog_reset");
    args.push_back("chip_id");

    return RunProcess(
        invocation.executable,
        args,
        ResolveRepositoryRoot(),
        out_result,
        out_error,
        &invocation.environment_overrides);
}

std::string BuildPortsResponse(const std::filesystem::path& repo_root, std::string_view pio_path, std::string_view message)
{
    const std::vector<std::string> ports = EnumerateSerialPorts();
    std::ostringstream json;
    json << "{";
    json << "\"ok\":true,";
    json << "\"command\":\"ports\",";
    json << "\"message\":\"" << EscapeJson(message) << "\",";
    json << "\"repo_root\":\"" << EscapeJson(repo_root.string()) << "\",";
    json << "\"project_dir\":\"" << EscapeJson(FirmwareProjectDirectory(repo_root).string()) << "\",";
    json << "\"env\":\"" << kDefaultEnv << "\",";
    json << "\"pio_path\":\"" << EscapeJson(pio_path) << "\",";
    json << "\"ports\":[";
    for (std::size_t index = 0; index < ports.size(); ++index)
    {
        json << "{"
             << "\"port\":\"" << EscapeJson(ports[index]) << "\","
             << "\"display_name\":\"" << EscapeJson(ports[index]) << "\""
             << "}";
        if (index + 1 != ports.size())
        {
            json << ",";
        }
    }
    json << "]}";
    return json.str();
}

std::string BuildCommandResponse(
    bool ok,
    std::string_view command,
    std::string_view message,
    std::string_view output,
    const std::filesystem::path& repo_root,
    std::string_view pio_path,
    std::string_view used_port = {})
{
    const auto project_dir = FirmwareProjectDirectory(repo_root);
    const auto artifact = FirmwareArtifactPath(project_dir, kDefaultEnv);

    std::ostringstream json;
    json << "{";
    json << "\"ok\":" << JsonBool(ok) << ",";
    json << "\"command\":\"" << EscapeJson(command) << "\",";
    json << "\"message\":\"" << EscapeJson(message) << "\",";
    json << "\"repo_root\":\"" << EscapeJson(repo_root.string()) << "\",";
    json << "\"project_dir\":\"" << EscapeJson(project_dir.string()) << "\",";
    json << "\"env\":\"" << kDefaultEnv << "\",";
    json << "\"pio_path\":\"" << EscapeJson(pio_path) << "\",";
    json << "\"artifact_path\":\"" << EscapeJson(artifact.string()) << "\",";
    json << "\"used_port\":\"" << EscapeJson(used_port) << "\",";
    json << "\"output\":\"" << EscapeJson(output) << "\"";
    json << "}";
    return json.str();
}

std::string BuildProbeResponse(bool ok, std::string_view command, std::string_view port, const DeviceStatusMessage& status, std::string_view message)
{
    std::ostringstream json;
    json << "{";
    json << "\"ok\":" << JsonBool(ok) << ",";
    json << "\"command\":\"" << EscapeJson(command) << "\",";
    json << "\"message\":\"" << EscapeJson(message) << "\",";
    json << "\"port\":\"" << EscapeJson(port) << "\",";
    json << "\"device\":\"" << EscapeJson(status.device_name) << "\",";
    json << "\"state\":\"" << EscapeJson(status.state) << "\",";
    json << "\"detail\":\"" << EscapeJson(status.message) << "\",";
    json << "\"mac\":\"" << EscapeJson(status.mac) << "\",";
    json << "\"sta_ip\":\"" << EscapeJson(status.sta_ip) << "\",";
    json << "\"role\":\"" << EscapeJson(ToString(status.role)) << "\",";
    json << "\"board_target\":\"" << EscapeJson(status.board_target) << "\",";
    json << "\"firmware_version\":\"" << EscapeJson(status.firmware_version) << "\",";
    json << "\"report_hz\":" << status.report_hz << ",";
    json << "\"tracking_enabled\":" << JsonBool(status.tracking_enabled);
    json << "}";
    return json.str();
}

} // namespace

int FirmwareToolMain(const std::vector<std::string>& arguments)
{
    const std::filesystem::path repo_root = ResolveRepositoryRoot();
    const std::filesystem::path project_dir = FirmwareProjectDirectory(repo_root);
    const std::string pio_path = ResolvePioExecutable();

    if (arguments.empty())
    {
        std::cout << BuildCommandResponse(
            false,
            "help",
            "usage: openfinger_firmware_tool.exe <ports|probe|verify|bootloader-info|flash-package|build|flash|build-flash> [--port COMx] [--manifest path]",
            {},
            repo_root,
            pio_path);
        return 1;
    }

    const std::string& command = arguments[0];
    if (command == "ports")
    {
        std::cout << BuildPortsResponse(repo_root, pio_path, "ports listed");
        return 0;
    }

    if (command == "bootloader-info")
    {
        const std::string port = ExtractOption(arguments, "--port");
        if (port.empty())
        {
            std::cout << BuildCommandResponse(false, command, "missing --port COMx", {}, repo_root, pio_path);
            return 1;
        }

        ProcessResult result;
        std::string error;
        if (!RunEspflashBoardInfo(port, &result, &error))
        {
            std::cout << BuildCommandResponse(false, command, error, {}, repo_root, pio_path, port);
            return 1;
        }

        const bool ok = result.exit_code == 0;
        const std::string message = ok ? "bootloader detected" : "bootloader not detected";
        std::cout << BuildCommandResponse(ok, command, message, result.output, repo_root, pio_path, port);
        return ok ? 0 : result.exit_code;
    }

    if (command == "probe" || command == "verify")
    {
        const std::string port = ExtractOption(arguments, "--port");
        if (port.empty())
        {
            std::cout << BuildCommandResponse(false, command, "missing --port COMx", {}, repo_root, pio_path);
            return 1;
        }

        DeviceStatusMessage status;
        std::string error;
        if (!ReadDeviceStatusFromSerial(port, &status, &error))
        {
            std::cout << BuildCommandResponse(false, command, error, {}, repo_root, pio_path, port);
            return 1;
        }

        std::cout << BuildProbeResponse(true, command, port, status, command == "probe" ? "device probed" : "device verified");
        return 0;
    }

    if (command == "flash-package")
    {
        const std::string port = ExtractOption(arguments, "--port");
        const std::string manifest_path = ExtractOption(arguments, "--manifest");
        if (port.empty())
        {
            std::cout << BuildCommandResponse(false, command, "missing --port COMx", {}, repo_root, pio_path);
            return 1;
        }
        if (manifest_path.empty())
        {
            std::cout << BuildCommandResponse(false, command, "missing --manifest path", {}, repo_root, pio_path, port);
            return 1;
        }

        FirmwareBundleManifest manifest;
        std::string error;
        if (!LoadFirmwareBundleManifest(manifest_path, &manifest, &error))
        {
            std::cout << BuildCommandResponse(false, command, error, {}, repo_root, pio_path, port);
            return 1;
        }

        ProcessResult result;
        std::string used_port;
        if (!RunEspflashFlash(port, manifest, &result, &error, &used_port))
        {
            std::cout << BuildCommandResponse(false, command, error, {}, repo_root, pio_path, port);
            return 1;
        }

        const bool ok = result.exit_code == 0;
        const std::string message = ok ? "firmware package flashed" : "firmware package flash failed";
        std::cout << BuildCommandResponse(ok, command, message, result.output, repo_root, pio_path, used_port.empty() ? port : used_port);
        return ok ? 0 : result.exit_code;
    }

    if (!std::filesystem::exists(project_dir / "platformio.ini"))
    {
        std::cout << BuildCommandResponse(
            false,
            command,
            "missing src/firmware/esp32c3/platformio.ini",
            {},
            repo_root,
            pio_path);
        return 1;
    }

    std::vector<std::string> process_arguments = {
        "run",
        "-d",
        project_dir.string(),
        "-e",
        kDefaultEnv,
    };

    if (command == "flash" || command == "build-flash")
    {
        const std::string port = ExtractOption(arguments, "--port");
        if (port.empty())
        {
            std::cout << BuildCommandResponse(false, command, "missing --port COMx", {}, repo_root, pio_path);
            return 1;
        }

        process_arguments.push_back("-t");
        process_arguments.push_back("upload");
        process_arguments.push_back("--upload-port");
        process_arguments.push_back(port);
    }
    else if (command != "build")
    {
        std::cout << BuildCommandResponse(false, command, "unknown command", {}, repo_root, pio_path);
        return 1;
    }

    ProcessResult result;
    std::string error;
    if (!RunProcess(pio_path, process_arguments, repo_root, &result, &error))
    {
        std::cout << BuildCommandResponse(false, command, error, {}, repo_root, pio_path);
        return 1;
    }

    const bool ok = result.exit_code == 0;
    std::string message;
    if (command == "build")
    {
        message = ok ? "firmware built" : "firmware build failed";
    }
    else if (command == "flash")
    {
        message = ok ? "firmware flashed" : "firmware flash failed";
    }
    else
    {
        message = ok ? "firmware built and flashed" : "firmware build+flash failed";
    }

    std::cout << BuildCommandResponse(ok, command, message, result.output, repo_root, pio_path);
    return ok ? 0 : result.exit_code;
}

} // namespace openfinger

int main(int argc, char** argv)
{
    std::vector<std::string> arguments;
    for (int index = 1; index < argc; ++index)
    {
        arguments.emplace_back(argv[index]);
    }
    return openfinger::FirmwareToolMain(arguments);
}
