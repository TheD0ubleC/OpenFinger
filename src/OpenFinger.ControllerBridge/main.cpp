#include "common/Config.h"
#include "common/ControllerInputState.h"
#include "openfinger/OpenFingerVersion.h"

#include <array>
#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>
#include <tlhelp32.h>
#include <winsock2.h>
#include <ws2tcpip.h>

#include "openvr.h"

namespace
{

constexpr auto kRetryInterval = std::chrono::seconds(2);
constexpr auto kLoopInterval = std::chrono::milliseconds(11);
constexpr const char* kOpenFingerBridgeAppKey = "openfinger.controller_bridge";

struct ActionContext
{
    openfinger::HandSide side = openfinger::HandSide::Right;
    vr::VRActionSetHandle_t action_set = vr::k_ulInvalidActionSetHandle;
    vr::VRInputValueHandle_t generic_source = vr::k_ulInvalidInputValueHandle;
    vr::VRInputValueHandle_t device_source = vr::k_ulInvalidInputValueHandle;
    std::string device_source_path;
    vr::VRActionHandle_t trigger_value = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t trigger_click = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t trigger_touch = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t grip_value = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t grip_click = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t grip_touch = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t joystick = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t joystick_click = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t joystick_touch = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t primary_click = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t primary_touch = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t secondary_click = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t secondary_touch = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t system_click = vr::k_ulInvalidActionHandle;
    vr::VRActionHandle_t system_touch = vr::k_ulInvalidActionHandle;
};

std::filesystem::path ExecutableDirectory()
{
    wchar_t buffer[MAX_PATH] = {};
    const DWORD length = GetModuleFileNameW(nullptr, buffer, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
    {
        return std::filesystem::current_path();
    }

    return std::filesystem::path(buffer).parent_path();
}

std::filesystem::path BridgeLogPath()
{
    const char* local_app_data = std::getenv("LOCALAPPDATA");
    if (local_app_data != nullptr && *local_app_data != '\0')
    {
        return std::filesystem::path(local_app_data) / "OpenFinger" / "openfinger_bridge.log";
    }

    return std::filesystem::current_path() / "openfinger_bridge.log";
}

void BridgeLog(const std::string& message)
{
    const std::filesystem::path path = BridgeLogPath();
    std::error_code ec;
    std::filesystem::create_directories(path.parent_path(), ec);

    std::ofstream stream(path, std::ios::app);
    if (!stream.is_open())
    {
        return;
    }

    stream << message << "\n";
}

bool IsProcessRunning(std::wstring_view executable_name)
{
    if (executable_name.empty())
    {
        return false;
    }

    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    PROCESSENTRY32W entry {};
    entry.dwSize = sizeof(entry);
    bool found = false;
    if (Process32FirstW(snapshot, &entry))
    {
        do
        {
            if (_wcsicmp(entry.szExeFile, executable_name.data()) == 0)
            {
                found = true;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
    return found;
}

struct OpenVrApi
{
    using InitInternal2Fn = std::uint32_t(VR_CALLTYPE*)(vr::EVRInitError*, vr::EVRApplicationType, const char*);
    using ShutdownInternalFn = void(VR_CALLTYPE*)();
    using GetGenericInterfaceFn = void*(VR_CALLTYPE*)(const char*, vr::EVRInitError*);
    using GetInitErrorDescriptionFn = const char*(VR_CALLTYPE*)(vr::EVRInitError);

    HMODULE module = nullptr;
    InitInternal2Fn init_internal2 = nullptr;
    ShutdownInternalFn shutdown_internal = nullptr;
    GetGenericInterfaceFn get_generic_interface = nullptr;
    GetInitErrorDescriptionFn get_error_description = nullptr;

    bool Load(std::string* out_error)
    {
        if (module != nullptr)
        {
            return true;
        }

        const std::wstring dll_path = FindOpenVrDll();
        module = LoadLibraryW(dll_path.c_str());
        if (module == nullptr)
        {
            if (out_error != nullptr)
            {
                *out_error = "LoadLibraryW failed for openvr_api.dll";
            }
            return false;
        }

        init_internal2 = reinterpret_cast<InitInternal2Fn>(GetProcAddress(module, "VR_InitInternal2"));
        shutdown_internal = reinterpret_cast<ShutdownInternalFn>(GetProcAddress(module, "VR_ShutdownInternal"));
        get_generic_interface = reinterpret_cast<GetGenericInterfaceFn>(GetProcAddress(module, "VR_GetGenericInterface"));
        get_error_description = reinterpret_cast<GetInitErrorDescriptionFn>(GetProcAddress(module, "VR_GetVRInitErrorAsEnglishDescription"));

        if (init_internal2 == nullptr || shutdown_internal == nullptr || get_generic_interface == nullptr || get_error_description == nullptr)
        {
            if (out_error != nullptr)
            {
                *out_error = "openvr_api.dll exports were incomplete";
            }
            Unload();
            return false;
        }

        return true;
    }

    void Unload()
    {
        if (module != nullptr)
        {
            FreeLibrary(module);
            module = nullptr;
        }

        init_internal2 = nullptr;
        shutdown_internal = nullptr;
        get_generic_interface = nullptr;
        get_error_description = nullptr;
    }

    vr::IVRSystem* InitBackground(const char* startup_info, vr::EVRInitError* out_error) const
    {
        if (out_error != nullptr)
        {
            *out_error = vr::VRInitError_Init_InterfaceNotFound;
        }

        if (init_internal2 == nullptr || get_generic_interface == nullptr)
        {
            return nullptr;
        }

        vr::EVRInitError init_error = vr::VRInitError_None;
        init_internal2(&init_error, vr::VRApplication_Background, startup_info);
        if (out_error != nullptr)
        {
            *out_error = init_error;
        }

        if (init_error != vr::VRInitError_None)
        {
            return nullptr;
        }

        vr::EVRInitError interface_error = vr::VRInitError_None;
        void* iface = get_generic_interface(vr::IVRSystem_Version, &interface_error);
        if (interface_error != vr::VRInitError_None || iface == nullptr)
        {
            if (shutdown_internal != nullptr)
            {
                shutdown_internal();
            }

            if (out_error != nullptr)
            {
                *out_error = interface_error;
            }
            return nullptr;
        }

        return reinterpret_cast<vr::IVRSystem*>(iface);
    }

    void Shutdown() const
    {
        if (shutdown_internal != nullptr)
        {
            shutdown_internal();
        }
    }

    const char* Describe(vr::EVRInitError error) const
    {
        if (get_error_description == nullptr)
        {
            return "unknown";
        }

        return get_error_description(error);
    }

    static std::wstring FindOpenVrDll()
    {
        std::vector<std::filesystem::path> candidates;
        candidates.emplace_back(std::filesystem::current_path() / "openvr_api.dll");

        for (char drive = 'A'; drive <= 'Z'; ++drive)
        {
            const std::filesystem::path root(std::string(1, drive) + ":\\" );
            candidates.emplace_back(root / "Steam" / "steamapps" / "common" / "SteamVR" / "bin" / "win64" / "openvr_api.dll");
            candidates.emplace_back(root / "Program Files (x86)" / "Steam" / "steamapps" / "common" / "SteamVR" / "bin" / "win64" / "openvr_api.dll");
            candidates.emplace_back(root / "Program Files" / "Steam" / "steamapps" / "common" / "SteamVR" / "bin" / "win64" / "openvr_api.dll");
        }

        for (const auto& candidate : candidates)
        {
            std::error_code ec;
            if (std::filesystem::exists(candidate, ec))
            {
                return candidate.wstring();
            }
        }

        return L"openvr_api.dll";
    }
};

std::filesystem::path ResolveActionManifestPath()
{
    const auto candidate = ExecutableDirectory() / "actions.json";
    if (std::filesystem::exists(candidate))
    {
        return candidate;
    }

    return std::filesystem::current_path() / "actions.json";
}

std::filesystem::path ResolveApplicationManifestPath()
{
    const auto candidate = ExecutableDirectory() / "openfinger_bridge.vrmanifest";
    if (std::filesystem::exists(candidate))
    {
        return candidate;
    }

    return std::filesystem::current_path() / "openfinger_bridge.vrmanifest";
}

std::string EscapeJsonString(std::string_view value)
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
        case '\b':
            escaped += "\\b";
            break;
        case '\f':
            escaped += "\\f";
            break;
        case '\n':
            escaped += "\\n";
            break;
        case '\r':
            escaped += "\\r";
            break;
        case '\t':
            escaped += "\\t";
            break;
        default:
            escaped += ch;
            break;
        }
    }

    return escaped;
}

std::string BuildOpenVrStartupInfo()
{
    const auto action_manifest_path = ResolveActionManifestPath().string();
    return std::string("{\"app_key\":\"")
        + EscapeJsonString(kOpenFingerBridgeAppKey)
        + "\",\"app_name\":\"OpenFinger Controller Bridge\",\"action_manifest_path\":\""
        + EscapeJsonString(action_manifest_path)
        + "\"}";
}

void IdentifyBridgeApplication(const OpenVrApi& openvr_api)
{
    if (openvr_api.get_generic_interface == nullptr)
    {
        return;
    }

    vr::EVRInitError interface_error = vr::VRInitError_None;
    auto* applications = reinterpret_cast<vr::IVRApplications*>(
        openvr_api.get_generic_interface(vr::IVRApplications_Version, &interface_error));
    if (interface_error != vr::VRInitError_None || applications == nullptr)
    {
        BridgeLog("bridge application manifest skipped: IVRApplications unavailable");
        return;
    }

    const auto manifest_path = ResolveApplicationManifestPath();
    if (!std::filesystem::exists(manifest_path))
    {
        BridgeLog("bridge application manifest missing path=" + manifest_path.string());
        return;
    }

    const auto add_result = applications->AddApplicationManifest(manifest_path.string().c_str(), false);
    if (add_result != vr::VRApplicationError_None && add_result != vr::VRApplicationError_AppKeyAlreadyExists)
    {
        BridgeLog("bridge application manifest add failed code=" + std::to_string(static_cast<int>(add_result))
            + " path=" + manifest_path.string());
        return;
    }

    const auto identify_result = applications->IdentifyApplication(GetCurrentProcessId(), kOpenFingerBridgeAppKey);
    if (identify_result != vr::VRApplicationError_None)
    {
        BridgeLog("bridge application identify failed code=" + std::to_string(static_cast<int>(identify_result)));
        return;
    }

    BridgeLog("bridge application identified key=" + std::string(kOpenFingerBridgeAppKey)
        + " manifest=" + manifest_path.string());
}

bool ButtonPressed(const vr::VRControllerState_t& state, vr::EVRButtonId button)
{
    return (state.ulButtonPressed & vr::ButtonMaskFromId(button)) != 0;
}

bool ButtonTouched(const vr::VRControllerState_t& state, vr::EVRButtonId button)
{
    return (state.ulButtonTouched & vr::ButtonMaskFromId(button)) != 0;
}

std::string GetTrackedDeviceString(vr::IVRSystem* vr_system, vr::TrackedDeviceIndex_t device_index, vr::ETrackedDeviceProperty property)
{
    if (vr_system == nullptr || device_index == vr::k_unTrackedDeviceIndexInvalid)
    {
        return {};
    }

    vr::ETrackedPropertyError error = vr::TrackedProp_Success;
    char buffer[256] = {};
    const uint32_t required = vr_system->GetStringTrackedDeviceProperty(device_index, property, buffer, sizeof(buffer), &error);
    if (error != vr::TrackedProp_Success)
    {
        return {};
    }

    if (required <= sizeof(buffer))
    {
        return buffer;
    }

    std::string dynamic_buffer(required, '\0');
    vr_system->GetStringTrackedDeviceProperty(device_index, property, dynamic_buffer.data(), required, &error);
    if (error != vr::TrackedProp_Success || dynamic_buffer.empty())
    {
        return {};
    }

    if (!dynamic_buffer.empty() && dynamic_buffer.back() == '\0')
    {
        dynamic_buffer.pop_back();
    }

    return dynamic_buffer;
}

std::string ToLowerAscii(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool GetActionHandle(vr::IVRInput* vr_input, const std::string& path, vr::VRActionHandle_t* out_handle)
{
    return vr_input != nullptr
        && out_handle != nullptr
        && vr_input->GetActionHandle(path.c_str(), out_handle) == vr::VRInputError_None
        && *out_handle != vr::k_ulInvalidActionHandle;
}

bool GetActionSetHandle(vr::IVRInput* vr_input, const std::string& path, vr::VRActionSetHandle_t* out_handle)
{
    return vr_input != nullptr
        && out_handle != nullptr
        && vr_input->GetActionSetHandle(path.c_str(), out_handle) == vr::VRInputError_None
        && *out_handle != vr::k_ulInvalidActionSetHandle;
}

bool GetInputSourceHandle(vr::IVRInput* vr_input, const std::string& path, vr::VRInputValueHandle_t* out_handle)
{
    return vr_input != nullptr
        && out_handle != nullptr
        && vr_input->GetInputSourceHandle(path.c_str(), out_handle) == vr::VRInputError_None
        && *out_handle != vr::k_ulInvalidInputValueHandle;
}

bool InitializeActionContext(vr::IVRInput* vr_input, const char* side_name, ActionContext* out_context)
{
    if (vr_input == nullptr || side_name == nullptr || out_context == nullptr)
    {
        return false;
    }

    ActionContext context;
    context.side = std::string_view(side_name) == "left" ? openfinger::HandSide::Left : openfinger::HandSide::Right;

    const bool is_left = std::string_view(side_name) == "left";
    const std::string legacy_prefix = std::string("/actions/legacy/in/") + (is_left ? "Left_" : "Right_");
    if (!GetActionSetHandle(vr_input, "/actions/legacy", &context.action_set)
        || !GetInputSourceHandle(vr_input, std::string("/user/hand/") + side_name, &context.generic_source)
        || !GetActionHandle(vr_input, legacy_prefix + "Axis1_Value", &context.trigger_value)
        || !GetActionHandle(vr_input, legacy_prefix + "Axis1_Press", &context.trigger_click)
        || !GetActionHandle(vr_input, legacy_prefix + "Axis1_Touch", &context.trigger_touch)
        || !GetActionHandle(vr_input, legacy_prefix + "Axis2_Value1", &context.grip_value)
        || !GetActionHandle(vr_input, legacy_prefix + "Grip_Press", &context.grip_click)
        || !GetActionHandle(vr_input, legacy_prefix + "Grip_Touch", &context.grip_touch)
        || !GetActionHandle(vr_input, legacy_prefix + "Axis0_Value", &context.joystick)
        || !GetActionHandle(vr_input, legacy_prefix + "Axis0_Press", &context.joystick_click)
        || !GetActionHandle(vr_input, legacy_prefix + "Axis0_Touch", &context.joystick_touch)
        || !GetActionHandle(vr_input, legacy_prefix + "A_Press", &context.primary_click)
        || !GetActionHandle(vr_input, legacy_prefix + "A_Touch", &context.primary_touch)
        || !GetActionHandle(vr_input, legacy_prefix + "ApplicationMenu_Press", &context.secondary_click)
        || !GetActionHandle(vr_input, legacy_prefix + "ApplicationMenu_Touch", &context.secondary_touch))
    {
        return false;
    }

    context.system_click = vr::k_ulInvalidActionHandle;
    context.system_touch = vr::k_ulInvalidActionHandle;

    *out_context = context;
    return true;
}

std::string BuildDeviceInputSourcePath(vr::IVRSystem* vr_system, vr::TrackedDeviceIndex_t device_index, std::string_view serial)
{
    std::string tracking_system = GetTrackedDeviceString(vr_system, device_index, vr::Prop_TrackingSystemName_String);
    if (tracking_system.empty())
    {
        tracking_system = "oculus";
    }

    return "/devices/" + ToLowerAscii(tracking_system) + "/" + std::string(serial);
}

vr::TrackedDeviceIndex_t ResolveTrackedSource(
    vr::IVRSystem* vr_system,
    openfinger::HandSide side,
    std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount>* out_axis_types,
    std::string* out_serial);

bool ReadTextFile(const std::filesystem::path& path, std::string* out_text)
{
    if (out_text == nullptr)
    {
        return false;
    }

    std::ifstream stream(path, std::ios::binary);
    if (!stream.is_open())
    {
        return false;
    }

    *out_text = std::string(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
    return true;
}

bool WriteTextFile(const std::filesystem::path& path, std::string_view text)
{
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream.is_open())
    {
        return false;
    }

    stream.write(text.data(), static_cast<std::streamsize>(text.size()));
    return stream.good();
}

void ReplaceAll(std::string* text, std::string_view from, std::string_view to)
{
    if (text == nullptr || from.empty())
    {
        return;
    }

    std::size_t position = 0;
    while ((position = text->find(from, position)) != std::string::npos)
    {
        text->replace(position, from.size(), to);
        position += to.size();
    }
}

bool RewriteBindingForDevicePath(
    std::string* binding_text,
    std::string_view side_name,
    std::string_view device_path)
{
    if (binding_text == nullptr || side_name.empty() || device_path.empty())
    {
        return false;
    }

    const std::string hand_root = "/user/hand/" + std::string(side_name);
    const std::string before = *binding_text;
    ReplaceAll(binding_text, hand_root + "/input/", std::string(device_path) + "/input/");
    ReplaceAll(binding_text, hand_root + "/output/", std::string(device_path) + "/output/");
    ReplaceAll(binding_text, hand_root + "/pose/", std::string(device_path) + "/pose/");
    return *binding_text != before;
}

bool PrepareDeviceSpecificBindings(vr::IVRSystem* vr_system)
{
    if (vr_system == nullptr)
    {
        return false;
    }

    const auto binding_path = ExecutableDirectory() / "bindings_oculus_touch.json";
    std::string binding_text;
    if (!ReadTextFile(binding_path, &binding_text))
    {
        BridgeLog("bridge device binding skipped: cannot read " + binding_path.string());
        return false;
    }

    bool changed = false;
    for (const openfinger::HandSide side : { openfinger::HandSide::Left, openfinger::HandSide::Right })
    {
        std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount> axis_types {};
        std::string serial;
        const auto source_index = ResolveTrackedSource(vr_system, side, &axis_types, &serial);
        if (source_index == vr::k_unTrackedDeviceIndexInvalid || serial.empty())
        {
            continue;
        }

        const std::string device_path = BuildDeviceInputSourcePath(vr_system, source_index, serial);
        const bool side_changed = RewriteBindingForDevicePath(
            &binding_text,
            side == openfinger::HandSide::Left ? "left" : "right",
            device_path);
        changed = changed || side_changed;
        BridgeLog(
            std::string("bridge device binding side=") + std::string(openfinger::ToString(side))
            + " index=" + std::to_string(source_index)
            + " path=" + device_path);
    }

    if (!changed)
    {
        BridgeLog("bridge device binding skipped: no physical sources");
        return false;
    }

    if (!WriteTextFile(binding_path, binding_text))
    {
        BridgeLog("bridge device binding write failed path=" + binding_path.string());
        return false;
    }

    BridgeLog("bridge device binding written path=" + binding_path.string());
    return true;
}

bool RefreshDeviceSourceHandle(
    vr::IVRInput* vr_input,
    vr::IVRSystem* vr_system,
    vr::TrackedDeviceIndex_t device_index,
    std::string_view serial,
    ActionContext* context)
{
    if (vr_input == nullptr || vr_system == nullptr || context == nullptr || serial.empty())
    {
        return false;
    }

    const std::string desired_path = BuildDeviceInputSourcePath(vr_system, device_index, serial);
    if (context->device_source != vr::k_ulInvalidInputValueHandle
        && context->device_source_path == desired_path)
    {
        return true;
    }

    vr::VRInputValueHandle_t handle = vr::k_ulInvalidInputValueHandle;
    if (!GetInputSourceHandle(vr_input, desired_path, &handle))
    {
        context->device_source = vr::k_ulInvalidInputValueHandle;
        context->device_source_path = desired_path;
        return false;
    }

    context->device_source = handle;
    context->device_source_path = desired_path;
    BridgeLog(
        std::string("bridge action device source fixed side=") + std::string(openfinger::ToString(context->side))
        + " tracked_index=" + std::to_string(device_index)
        + " path=" + desired_path);
    return true;
}

bool TryReadDigitalAction(
    vr::IVRInput* vr_input,
    vr::VRActionHandle_t action_handle,
    vr::VRInputValueHandle_t restrict_to_device,
    bool* out_state)
{
    if (vr_input == nullptr || out_state == nullptr || action_handle == vr::k_ulInvalidActionHandle)
    {
        return false;
    }

    vr::InputDigitalActionData_t data {};
    if (vr_input->GetDigitalActionData(action_handle, &data, sizeof(data), restrict_to_device) != vr::VRInputError_None)
    {
        return false;
    }

    *out_state = data.bState;
    return true;
}

bool TryReadAnalogAction(
    vr::IVRInput* vr_input,
    vr::VRActionHandle_t action_handle,
    vr::VRInputValueHandle_t restrict_to_device,
    vr::InputAnalogActionData_t* out_data)
{
    if (vr_input == nullptr || out_data == nullptr || action_handle == vr::k_ulInvalidActionHandle)
    {
        return false;
    }

    vr::InputAnalogActionData_t data {};
    if (vr_input->GetAnalogActionData(action_handle, &data, sizeof(data), restrict_to_device) != vr::VRInputError_None)
    {
        return false;
    }

    *out_data = data;
    return true;
}

bool OriginMatchesTrackedIndex(vr::IVRInput* vr_input, vr::VRInputValueHandle_t origin, vr::TrackedDeviceIndex_t expected_index)
{
    if (vr_input == nullptr || origin == vr::k_ulInvalidInputValueHandle || expected_index == vr::k_unTrackedDeviceIndexInvalid)
    {
        return false;
    }

    vr::InputOriginInfo_t origin_info {};
    if (vr_input->GetOriginTrackedDeviceInfo(origin, &origin_info, sizeof(origin_info)) != vr::VRInputError_None)
    {
        return false;
    }

    return origin_info.trackedDeviceIndex == expected_index;
}

bool AnyOriginMatchesTrackedIndex(
    vr::IVRInput* vr_input,
    vr::TrackedDeviceIndex_t expected_index,
    std::initializer_list<vr::VRInputValueHandle_t> origins)
{
    for (const auto origin : origins)
    {
        if (OriginMatchesTrackedIndex(vr_input, origin, expected_index))
        {
            return true;
        }
    }

    return false;
}

std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount> ReadAxisTypes(
    vr::IVRSystem* vr_system,
    vr::TrackedDeviceIndex_t device_index)
{
    std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount> axis_types {};
    for (std::size_t axis_index = 0; axis_index < axis_types.size(); ++axis_index)
    {
        vr::ETrackedPropertyError error = vr::TrackedProp_Success;
        const auto property = static_cast<vr::ETrackedDeviceProperty>(vr::Prop_Axis0Type_Int32 + static_cast<int>(axis_index));
        const int axis_type = vr_system->GetInt32TrackedDeviceProperty(device_index, property, &error);
        axis_types[axis_index] = (error == vr::TrackedProp_Success)
            ? static_cast<vr::EVRControllerAxisType>(axis_type)
            : vr::k_eControllerAxis_None;
    }

    return axis_types;
}

int FindAxisIndex(
    const std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount>& axis_types,
    vr::EVRControllerAxisType desired_type,
    int occurrence = 0)
{
    for (int index = 0; index < static_cast<int>(axis_types.size()); ++index)
    {
        if (axis_types[index] == desired_type)
        {
            if (occurrence == 0)
            {
                return index;
            }

            --occurrence;
        }
    }

    return -1;
}

bool HasPhysicalControllerAxes(const std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount>& axis_types)
{
    const bool has_primary_axis = FindAxisIndex(axis_types, vr::k_eControllerAxis_Joystick) >= 0
        || FindAxisIndex(axis_types, vr::k_eControllerAxis_TrackPad) >= 0;
    const bool has_trigger_axis = FindAxisIndex(axis_types, vr::k_eControllerAxis_Trigger) >= 0;
    return has_primary_axis && has_trigger_axis;
}

bool IsSyntheticHandSource(
    std::string_view serial,
    std::string_view manufacturer,
    std::string_view controller_type,
    std::string_view model_number)
{
    const std::string serial_lower = ToLowerAscii(std::string(serial));
    const std::string manufacturer_lower = ToLowerAscii(std::string(manufacturer));
    const std::string controller_type_lower = ToLowerAscii(std::string(controller_type));
    const std::string model_lower = ToLowerAscii(std::string(model_number));

    if (manufacturer_lower == "openfinger")
    {
        return true;
    }

    if (serial_lower.rfind("hand", 0) == 0)
    {
        return true;
    }

    if (controller_type_lower.find("hand") != std::string::npos
        || controller_type_lower.find("finger") != std::string::npos)
    {
        return true;
    }

    if (manufacturer_lower.find("virtualdesktop") != std::string::npos
        && (model_lower.find("hand") != std::string::npos || serial_lower.find("hand") != std::string::npos))
    {
        return true;
    }

    return false;
}

int ScoreTrackedSourceCandidate(
    std::string_view manufacturer,
    std::string_view controller_type,
    const std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount>& axis_types)
{
    const std::string manufacturer_lower = ToLowerAscii(std::string(manufacturer));
    const std::string controller_type_lower = ToLowerAscii(std::string(controller_type));

    int score = 0;
    if (HasPhysicalControllerAxes(axis_types))
    {
        score += 100;
    }

    if (controller_type_lower == "oculus_touch"
        || controller_type_lower == "knuckles"
        || controller_type_lower == "vive_controller"
        || controller_type_lower == "pico_controller")
    {
        score += 50;
    }

    if (manufacturer_lower == "oculus"
        || manufacturer_lower == "meta"
        || manufacturer_lower == "valve"
        || manufacturer_lower == "htc"
        || manufacturer_lower == "pico")
    {
        score += 20;
    }

    return score;
}

bool HasActiveForwardedInput(const openfinger::ForwardedControllerState& state)
{
    return state.trigger_value >= 0.05f
        || state.grip_value >= 0.05f
        || state.joystick_x <= -0.2f
        || state.joystick_x >= 0.2f
        || state.joystick_y <= -0.2f
        || state.joystick_y >= 0.2f
        || state.joystick_click
        || state.joystick_touch
        || state.trigger_click
        || state.trigger_touch
        || state.grip_click
        || state.grip_touch
        || state.a_click
        || state.a_touch
        || state.b_click
        || state.b_touch
        || state.system_click
        || state.system_touch;
}

bool IsEligibleTrackedSource(
    vr::IVRSystem* vr_system,
    vr::TrackedDeviceIndex_t device_index,
    std::string* out_serial)
{
    if (vr_system == nullptr || device_index == vr::k_unTrackedDeviceIndexInvalid)
    {
        return false;
    }

    if (vr_system->GetTrackedDeviceClass(device_index) != vr::TrackedDeviceClass_Controller)
    {
        return false;
    }

    const std::string serial = GetTrackedDeviceString(vr_system, device_index, vr::Prop_SerialNumber_String);
    if (serial.empty())
    {
        return false;
    }

    const std::string manufacturer = GetTrackedDeviceString(vr_system, device_index, vr::Prop_ManufacturerName_String);
    if (manufacturer == "OpenFinger")
    {
        return false;
    }

    if (out_serial != nullptr)
    {
        *out_serial = serial;
    }

    return true;
}

int ReadRoleHint(vr::IVRSystem* vr_system, vr::TrackedDeviceIndex_t device_index)
{
    vr::ETrackedPropertyError error = vr::TrackedProp_Success;
    return vr_system->GetInt32TrackedDeviceProperty(device_index, vr::Prop_ControllerRoleHint_Int32, &error);
}

bool TextSuggestsSide(std::string_view text, openfinger::HandSide side)
{
    const std::string lowered = ToLowerAscii(std::string(text));
    if (lowered.empty())
    {
        return false;
    }

    if (side == openfinger::HandSide::Left)
    {
        return lowered.find("left") != std::string::npos || lowered.find("_l") != std::string::npos;
    }

    return lowered.find("right") != std::string::npos || lowered.find("_r") != std::string::npos;
}

bool CandidateMatchesSide(
    vr::IVRSystem* vr_system,
    vr::TrackedDeviceIndex_t device_index,
    openfinger::HandSide side,
    std::string_view serial,
    std::string_view model_number,
    std::string_view registered_device_type)
{
    const auto expected_role = side == openfinger::HandSide::Left ? vr::TrackedControllerRole_LeftHand : vr::TrackedControllerRole_RightHand;
    if (vr_system->GetControllerRoleForTrackedDeviceIndex(device_index) == expected_role)
    {
        return true;
    }

    if (ReadRoleHint(vr_system, device_index) == static_cast<int>(expected_role))
    {
        return true;
    }

    return TextSuggestsSide(serial, side)
        || TextSuggestsSide(model_number, side)
        || TextSuggestsSide(registered_device_type, side);
}

vr::TrackedDeviceIndex_t ResolveTrackedSource(
    vr::IVRSystem* vr_system,
    openfinger::HandSide side,
    std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount>* out_axis_types,
    std::string* out_serial)
{
    if (vr_system == nullptr)
    {
        return vr::k_unTrackedDeviceIndexInvalid;
    }

    vr::TrackedDeviceIndex_t best_index = vr::k_unTrackedDeviceIndexInvalid;
    std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount> best_axis_types {};
    std::string best_serial;
    int best_score = std::numeric_limits<int>::min();

    for (vr::TrackedDeviceIndex_t index = 0; index < vr::k_unMaxTrackedDeviceCount; ++index)
    {
        std::string serial;
        if (!IsEligibleTrackedSource(vr_system, index, &serial))
        {
            continue;
        }

        const std::string manufacturer = GetTrackedDeviceString(vr_system, index, vr::Prop_ManufacturerName_String);
        const std::string controller_type = GetTrackedDeviceString(vr_system, index, vr::Prop_ControllerType_String);
        const std::string model_number = GetTrackedDeviceString(vr_system, index, vr::Prop_ModelNumber_String);
        const std::string registered_device_type = GetTrackedDeviceString(vr_system, index, vr::Prop_RegisteredDeviceType_String);
        const auto axis_types = ReadAxisTypes(vr_system, index);

        if (IsSyntheticHandSource(serial, manufacturer, controller_type, model_number))
        {
            continue;
        }

        if (!CandidateMatchesSide(vr_system, index, side, serial, model_number, registered_device_type))
        {
            continue;
        }

        const int score = ScoreTrackedSourceCandidate(manufacturer, controller_type, axis_types);
        if (score <= best_score)
        {
            continue;
        }

        best_index = index;
        best_axis_types = axis_types;
        best_serial = serial;
        best_score = score;
    }

    if (best_index == vr::k_unTrackedDeviceIndexInvalid)
    {
        return vr::k_unTrackedDeviceIndexInvalid;
    }

    if (out_axis_types != nullptr)
    {
        *out_axis_types = best_axis_types;
    }

    if (out_serial != nullptr)
    {
        *out_serial = best_serial;
    }

    return best_index;
}

openfinger::ForwardedControllerState BuildForwardedState(
    openfinger::HandSide side,
    const vr::VRControllerState_t& source_state,
    const std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount>& axis_types,
    std::uint64_t sequence)
{
    openfinger::ForwardedControllerState forwarded;
    forwarded.side = side;
    forwarded.seq = sequence;
    forwarded.connected = true;

    const int joystick_axis = FindAxisIndex(axis_types, vr::k_eControllerAxis_Joystick);
    const int fallback_trackpad_axis = FindAxisIndex(axis_types, vr::k_eControllerAxis_TrackPad);
    const int primary_axis = joystick_axis >= 0 ? joystick_axis : fallback_trackpad_axis;
    const int trigger_axis = FindAxisIndex(axis_types, vr::k_eControllerAxis_Trigger, 0);
    const int grip_axis = FindAxisIndex(axis_types, vr::k_eControllerAxis_Trigger, 1);

    if (primary_axis >= 0)
    {
        forwarded.joystick_x = source_state.rAxis[primary_axis].x;
        forwarded.joystick_y = source_state.rAxis[primary_axis].y;
        const auto axis_button = static_cast<vr::EVRButtonId>(vr::k_EButton_Axis0 + primary_axis);
        forwarded.joystick_click = ButtonPressed(source_state, axis_button);
        forwarded.joystick_touch = ButtonTouched(source_state, axis_button);
    }

    if (trigger_axis >= 0)
    {
        forwarded.trigger_value = source_state.rAxis[trigger_axis].x;
        const auto axis_button = static_cast<vr::EVRButtonId>(vr::k_EButton_Axis0 + trigger_axis);
        forwarded.trigger_click = ButtonPressed(source_state, axis_button) || forwarded.trigger_value >= 0.55f;
        forwarded.trigger_touch = ButtonTouched(source_state, axis_button) || forwarded.trigger_value >= 0.05f;
    }

    if (grip_axis >= 0)
    {
        forwarded.grip_value = source_state.rAxis[grip_axis].x;
    }

    forwarded.grip_click = ButtonPressed(source_state, vr::k_EButton_Grip) || forwarded.grip_value >= 0.65f;
    forwarded.grip_touch = ButtonTouched(source_state, vr::k_EButton_Grip) || forwarded.grip_value >= 0.05f;
    forwarded.a_click = ButtonPressed(source_state, vr::k_EButton_A);
    forwarded.a_touch = ButtonTouched(source_state, vr::k_EButton_A);
    forwarded.b_click = ButtonPressed(source_state, vr::k_EButton_ApplicationMenu);
    forwarded.b_touch = ButtonTouched(source_state, vr::k_EButton_ApplicationMenu);
    forwarded.system_click = ButtonPressed(source_state, vr::k_EButton_System);
    forwarded.system_touch = ButtonTouched(source_state, vr::k_EButton_System);
    return forwarded;
}

bool BuildForwardedStateFromActions(
    vr::IVRInput* vr_input,
    ActionContext* action_context,
    vr::TrackedDeviceIndex_t expected_index,
    std::uint64_t sequence,
    openfinger::ForwardedControllerState* out_forwarded)
{
    if (vr_input == nullptr
        || action_context == nullptr
        || out_forwarded == nullptr
        || action_context->action_set == vr::k_ulInvalidActionSetHandle)
    {
        return false;
    }

    const vr::VRInputValueHandle_t restrict_to_device =
        action_context->device_source != vr::k_ulInvalidInputValueHandle
            ? action_context->device_source
            : action_context->generic_source;

    vr::VRActiveActionSet_t active_set {};
    active_set.ulActionSet = action_context->action_set;
    active_set.ulRestrictedToDevice = restrict_to_device;
    active_set.ulSecondaryActionSet = vr::k_ulInvalidActionSetHandle;
    active_set.nPriority = 0;

    if (vr_input->UpdateActionState(&active_set, sizeof(active_set), 1) != vr::VRInputError_None)
    {
        return false;
    }

    openfinger::ForwardedControllerState forwarded;
    forwarded.side = action_context->side;
    forwarded.seq = sequence;
    forwarded.connected = true;

    vr::InputAnalogActionData_t analog {};
    vr::InputAnalogActionData_t trigger_analog {};
    vr::InputAnalogActionData_t grip_analog {};
    vr::InputAnalogActionData_t joystick_analog {};
    vr::InputDigitalActionData_t trigger_click {};
    vr::InputDigitalActionData_t trigger_touch {};
    vr::InputDigitalActionData_t grip_click {};
    vr::InputDigitalActionData_t grip_touch {};
    vr::InputDigitalActionData_t joystick_click {};
    vr::InputDigitalActionData_t joystick_touch {};
    vr::InputDigitalActionData_t primary_click {};
    vr::InputDigitalActionData_t primary_touch {};
    vr::InputDigitalActionData_t secondary_click {};
    vr::InputDigitalActionData_t secondary_touch {};
    vr::InputDigitalActionData_t system_click {};
    vr::InputDigitalActionData_t system_touch {};

    if (TryReadAnalogAction(vr_input, action_context->trigger_value, restrict_to_device, &analog))
    {
        trigger_analog = analog;
        forwarded.trigger_value = analog.x;
    }

    if (TryReadAnalogAction(vr_input, action_context->grip_value, restrict_to_device, &analog))
    {
        grip_analog = analog;
        forwarded.grip_value = analog.x;
    }

    if (TryReadAnalogAction(vr_input, action_context->joystick, restrict_to_device, &analog))
    {
        joystick_analog = analog;
        forwarded.joystick_x = analog.x;
        forwarded.joystick_y = analog.y;
    }

    if (action_context->trigger_click != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->trigger_click, &trigger_click, sizeof(trigger_click), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.trigger_click = trigger_click.bState;
    }

    if (action_context->trigger_touch != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->trigger_touch, &trigger_touch, sizeof(trigger_touch), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.trigger_touch = trigger_touch.bState;
    }

    if (action_context->grip_click != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->grip_click, &grip_click, sizeof(grip_click), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.grip_click = grip_click.bState;
    }

    if (action_context->grip_touch != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->grip_touch, &grip_touch, sizeof(grip_touch), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.grip_touch = grip_touch.bState;
    }

    if (action_context->joystick_click != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->joystick_click, &joystick_click, sizeof(joystick_click), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.joystick_click = joystick_click.bState;
    }

    if (action_context->joystick_touch != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->joystick_touch, &joystick_touch, sizeof(joystick_touch), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.joystick_touch = joystick_touch.bState;
    }

    if (action_context->primary_click != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->primary_click, &primary_click, sizeof(primary_click), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.a_click = primary_click.bState;
    }

    if (action_context->primary_touch != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->primary_touch, &primary_touch, sizeof(primary_touch), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.a_touch = primary_touch.bState;
    }

    if (action_context->secondary_click != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->secondary_click, &secondary_click, sizeof(secondary_click), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.b_click = secondary_click.bState;
    }

    if (action_context->secondary_touch != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->secondary_touch, &secondary_touch, sizeof(secondary_touch), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.b_touch = secondary_touch.bState;
    }

    if (action_context->system_click != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->system_click, &system_click, sizeof(system_click), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.system_click = system_click.bState;
    }

    if (action_context->system_touch != vr::k_ulInvalidActionHandle
        && vr_input->GetDigitalActionData(action_context->system_touch, &system_touch, sizeof(system_touch), restrict_to_device) == vr::VRInputError_None)
    {
        forwarded.system_touch = system_touch.bState;
    }

    const bool action_origin_matches = AnyOriginMatchesTrackedIndex(
        vr_input,
        expected_index,
        {
            trigger_analog.activeOrigin,
            grip_analog.activeOrigin,
            joystick_analog.activeOrigin,
            trigger_click.activeOrigin,
            trigger_touch.activeOrigin,
            grip_click.activeOrigin,
            grip_touch.activeOrigin,
            joystick_click.activeOrigin,
            joystick_touch.activeOrigin,
            primary_click.activeOrigin,
            primary_touch.activeOrigin,
            secondary_click.activeOrigin,
            secondary_touch.activeOrigin,
            system_click.activeOrigin,
            system_touch.activeOrigin,
        });
    const bool restricted_to_physical_device =
        action_context->device_source != vr::k_ulInvalidInputValueHandle
        && restrict_to_device == action_context->device_source;
    if (!action_origin_matches && !restricted_to_physical_device)
    {
        return false;
    }

    if (!forwarded.trigger_click && forwarded.trigger_value >= 0.55f)
    {
        forwarded.trigger_click = true;
    }

    if (!forwarded.trigger_touch && forwarded.trigger_value >= 0.05f)
    {
        forwarded.trigger_touch = true;
    }

    if (!forwarded.grip_click && forwarded.grip_value >= 0.65f)
    {
        forwarded.grip_click = true;
    }

    if (!forwarded.grip_touch && forwarded.grip_value >= 0.05f)
    {
        forwarded.grip_touch = true;
    }

    *out_forwarded = forwarded;
    return true;
}

bool SendPacket(SOCKET socket_handle, const sockaddr_in& destination, const openfinger::ForwardedControllerState& state)
{
    const std::string payload = openfinger::SerializeForwardedControllerPacket(state);
    const int result = sendto(
        socket_handle,
        payload.c_str(),
        static_cast<int>(payload.size()),
        0,
        reinterpret_cast<const sockaddr*>(&destination),
        sizeof(destination));
    return result != SOCKET_ERROR;
}

} // namespace

int main()
{
    using clock = std::chrono::steady_clock;

    std::cout.setf(std::ios::unitbuf);
    std::cerr.setf(std::ios::unitbuf);

    openfinger::ConfigStore config_store;
    std::string error;
    if (!config_store.LoadOrCreate(&error))
    {
        std::cerr << "Failed to load config: " << error << "\n";
        return 1;
    }

    WSADATA wsa_data;
    const int startup_result = WSAStartup(MAKEWORD(2, 2), &wsa_data);
    if (startup_result != 0)
    {
        std::cerr << "WSAStartup failed with code " << startup_result << "\n";
        return 1;
    }

    SOCKET socket_handle = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (socket_handle == INVALID_SOCKET)
    {
        std::cerr << "socket() failed with code " << WSAGetLastError() << "\n";
        WSACleanup();
        return 1;
    }

    sockaddr_in destination = {};
    destination.sin_family = AF_INET;
    destination.sin_port = htons(static_cast<u_short>(config_store.config().controller_bridge.udp_port));
    inet_pton(AF_INET, "127.0.0.1", &destination.sin_addr);

    std::cout << "OpenFinger controller bridge " << OPENFINGER_VERSION << " / protocol v" << OPENFINGER_PROTOCOL_VERSION << "\n"
              << "Forwarding to UDP 127.0.0.1:" << config_store.config().controller_bridge.udp_port << "\n"
              << "Config file: " << config_store.path().string() << "\n";
    BridgeLog(std::string("bridge startup version=") + OPENFINGER_VERSION
        + " protocol=" + std::to_string(OPENFINGER_PROTOCOL_VERSION)
        + " port=" + std::to_string(config_store.config().controller_bridge.udp_port)
        + " config=" + config_store.path().string());

    OpenVrApi openvr_api;
    std::string load_error;
    if (!openvr_api.Load(&load_error))
    {
        std::cerr << "Failed to load openvr_api.dll: " << load_error << "\n";
        closesocket(socket_handle);
        WSACleanup();
        return 1;
    }

    vr::IVRSystem* vr_system = nullptr;
    vr::IVRInput* vr_input = nullptr;
    ActionContext left_action_context;
    ActionContext right_action_context;
    bool action_input_ready = false;
    std::array<vr::TrackedDeviceIndex_t, 2> source_indices = {
        vr::k_unTrackedDeviceIndexInvalid,
        vr::k_unTrackedDeviceIndexInvalid,
    };
    std::array<vr::TrackedDeviceIndex_t, 2> logged_source_indices = {
        vr::k_unTrackedDeviceIndexInvalid,
        vr::k_unTrackedDeviceIndexInvalid,
    };
    std::array<std::array<vr::EVRControllerAxisType, vr::k_unControllerStateAxisCount>, 2> axis_types {};
    std::array<std::string, 2> source_serials;
    std::array<bool, 2> active_input_logged = { false, false };
    std::uint64_t sequence = 0;
    auto last_init_attempt = clock::now() - kRetryInterval;

    while (true)
    {
        if (vr_system == nullptr && (clock::now() - last_init_attempt) >= kRetryInterval)
        {
            last_init_attempt = clock::now();
            if (!IsProcessRunning(L"vrserver.exe"))
            {
                std::cout << "Waiting for SteamVR vrserver.exe\n";
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                continue;
            }

            vr::EVRInitError init_error = vr::VRInitError_None;
            const std::string startup_info = BuildOpenVrStartupInfo();
            vr_system = openvr_api.InitBackground(startup_info.c_str(), &init_error);
            if (init_error != vr::VRInitError_None)
            {
                vr_system = nullptr;
                std::cout << "Waiting for SteamVR, VR_Init error=" << openvr_api.Describe(init_error) << "\n";
            }
            else
            {
                std::cout << "Connected to SteamVR runtime\n";
                IdentifyBridgeApplication(openvr_api);
                action_input_ready = false;
            }
        }

        if (vr_system == nullptr)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            continue;
        }

        for (const openfinger::HandSide side : { openfinger::HandSide::Left, openfinger::HandSide::Right })
        {
            const std::size_t slot = side == openfinger::HandSide::Left ? 0 : 1;
            if (!IsEligibleTrackedSource(vr_system, source_indices[slot], nullptr))
            {
                source_serials[slot].clear();
                source_indices[slot] = ResolveTrackedSource(vr_system, side, &axis_types[slot], &source_serials[slot]);
            }

            if (source_indices[slot] != logged_source_indices[slot])
            {
                logged_source_indices[slot] = source_indices[slot];
                if (source_indices[slot] == vr::k_unTrackedDeviceIndexInvalid)
                {
                    BridgeLog(std::string("bridge source lost side=") + std::string(openfinger::ToString(side)));
                }
                else
                {
                    BridgeLog(
                        std::string("bridge source selected side=") + std::string(openfinger::ToString(side))
                        + " index=" + std::to_string(source_indices[slot])
                        + " serial=" + source_serials[slot]
                        + " manufacturer=" + GetTrackedDeviceString(vr_system, source_indices[slot], vr::Prop_ManufacturerName_String)
                        + " controller_type=" + GetTrackedDeviceString(vr_system, source_indices[slot], vr::Prop_ControllerType_String));
                }
            }

            if (!action_input_ready)
            {
                const bool has_physical_source =
                    source_indices[0] != vr::k_unTrackedDeviceIndexInvalid
                    || source_indices[1] != vr::k_unTrackedDeviceIndexInvalid;
                if (has_physical_source)
                {
                    if (vr_input == nullptr)
                    {
                        vr::EVRInitError input_error = vr::VRInitError_None;
                        vr_input = reinterpret_cast<vr::IVRInput*>(
                            openvr_api.get_generic_interface(vr::IVRInput_Version, &input_error));
                        if (vr_input == nullptr || input_error != vr::VRInitError_None)
                        {
                            BridgeLog("bridge action interface unavailable");
                        }
                    }

                    if (vr_input != nullptr)
                    {
                        const auto manifest_path = ResolveActionManifestPath();
                        if (vr_input->SetActionManifestPath(manifest_path.string().c_str()) == vr::VRInputError_None
                            && InitializeActionContext(vr_input, "left", &left_action_context)
                            && InitializeActionContext(vr_input, "right", &right_action_context))
                        {
                            action_input_ready = true;
                            BridgeLog("bridge action manifest ready path=" + manifest_path.string());
                        }
                        else
                        {
                            BridgeLog("bridge action manifest init failed path=" + manifest_path.string());
                        }
                    }
                }
            }

            if (source_indices[slot] == vr::k_unTrackedDeviceIndexInvalid)
            {
                openfinger::ForwardedControllerState disconnected;
                disconnected.side = side;
                disconnected.seq = ++sequence;
                SendPacket(socket_handle, destination, disconnected);
                continue;
            }

            openfinger::ForwardedControllerState forwarded;
            bool have_forwarded_state = false;
            if (action_input_ready && vr_input != nullptr)
            {
                auto* action_context = side == openfinger::HandSide::Left ? &left_action_context : &right_action_context;
                RefreshDeviceSourceHandle(vr_input, vr_system, source_indices[slot], source_serials[slot], action_context);
                have_forwarded_state = BuildForwardedStateFromActions(vr_input, action_context, source_indices[slot], ++sequence, &forwarded);
            }

            if (!have_forwarded_state)
            {
                vr::VRControllerState_t controller_state {};
                const bool got_state = vr_system->GetControllerState(
                    source_indices[slot],
                    &controller_state,
                    sizeof(controller_state));
                const bool is_connected = vr_system->IsTrackedDeviceConnected(source_indices[slot]);

                if (!got_state || !is_connected)
                {
                    openfinger::ForwardedControllerState disconnected;
                    disconnected.side = side;
                    disconnected.seq = ++sequence;
                    SendPacket(socket_handle, destination, disconnected);
                    source_indices[slot] = vr::k_unTrackedDeviceIndexInvalid;
                    continue;
                }

                forwarded = BuildForwardedState(side, controller_state, axis_types[slot], ++sequence);
                BridgeLog(
                    std::string("bridge raw side=") + std::string(openfinger::ToString(side))
                    + " mode=legacy pressed=" + std::to_string(controller_state.ulButtonPressed)
                    + " touched=" + std::to_string(controller_state.ulButtonTouched)
                    + " trigger=" + std::to_string(forwarded.trigger_value)
                    + " grip=" + std::to_string(forwarded.grip_value)
                    + " joy_x=" + std::to_string(forwarded.joystick_x)
                    + " joy_y=" + std::to_string(forwarded.joystick_y));
            }
            else
            {
                BridgeLog(
                    std::string("bridge raw side=") + std::string(openfinger::ToString(side))
                    + " mode=actions pressed=0 touched=0"
                    + " trigger=" + std::to_string(forwarded.trigger_value)
                    + " grip=" + std::to_string(forwarded.grip_value)
                    + " joy_x=" + std::to_string(forwarded.joystick_x)
                    + " joy_y=" + std::to_string(forwarded.joystick_y));
            }

            const bool has_active_input = HasActiveForwardedInput(forwarded);
            if (has_active_input != active_input_logged[slot])
            {
                active_input_logged[slot] = has_active_input;
                BridgeLog(
                    std::string("bridge input side=") + std::string(openfinger::ToString(side))
                    + " active=" + (has_active_input ? "1" : "0")
                    + " trigger=" + std::to_string(forwarded.trigger_value)
                    + " grip=" + std::to_string(forwarded.grip_value)
                    + " joy_x=" + std::to_string(forwarded.joystick_x)
                    + " joy_y=" + std::to_string(forwarded.joystick_y)
                    + " a=" + std::to_string(forwarded.a_click ? 1 : 0)
                    + " b=" + std::to_string(forwarded.b_click ? 1 : 0)
                    + " system=" + std::to_string(forwarded.system_click ? 1 : 0));
            }

            if (!SendPacket(socket_handle, destination, forwarded))
            {
                std::cerr << "sendto() failed with code " << WSAGetLastError() << "\n";
                BridgeLog("bridge send failed side=" + std::string(openfinger::ToString(side))
                    + " wsa=" + std::to_string(WSAGetLastError()));
            }
        }

        std::this_thread::sleep_for(kLoopInterval);
    }
}
