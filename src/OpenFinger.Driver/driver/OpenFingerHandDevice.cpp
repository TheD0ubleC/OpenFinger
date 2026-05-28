#include "driver/OpenFingerHandDevice.h"

#include "driver/DriverLog.h"
#include "driver/VrMath.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <thread>
#include <windows.h>
#include <shellapi.h>

namespace openfinger
{

namespace
{

constexpr const char* kInputProfilePath = "{openfinger}/input/openfinger_profile.json";
constexpr const char* kControllerType = "knuckles";
constexpr const char* kSettingsSection = "driver_openfinger";
constexpr const char* kSettingsForwardControllerInputs = "forward_controller_inputs";
constexpr const char* kSettingsOpenFingerTestWindow = "open_finger_test_window";
constexpr int kOpenFingerHandSelectionPriority = 10000;
constexpr auto kSettingsPollInterval = std::chrono::milliseconds(500);
constexpr auto kForwardedControllerFreshFor = std::chrono::milliseconds(250);
constexpr std::uint64_t kRuntimePresenceHoldMs = 180;
constexpr ULONGLONG kFingerTestLaunchCooldownMs = 2000;

std::atomic<ULONGLONG> g_last_finger_test_launch_ms { 0 };

vr::HmdMatrix34_t IdentityPoseOffset()
{
    vr::HmdMatrix34_t identity {};
    identity.m[0][0] = 1.0f;
    identity.m[1][1] = 1.0f;
    identity.m[2][2] = 1.0f;
    return identity;
}

const char* SkeletonInputPath(HandSide side)
{
    return side == HandSide::Left ? "/input/skeleton/left" : "/input/skeleton/right";
}

const char* SkeletonBodyPath(HandSide side)
{
    return side == HandSide::Left ? "/skeleton/hand/left" : "/skeleton/hand/right";
}

const char* PrimaryButtonPath(HandSide side)
{
    return side == HandSide::Left ? "/input/x" : "/input/a";
}

const char* SecondaryButtonPath(HandSide side)
{
    return side == HandSide::Left ? "/input/y" : "/input/b";
}

const char* RenderModelName(HandSide side)
{
    (void)side;
    return "";
}

vr::ETrackedControllerRole ControllerRole(HandSide side)
{
    return side == HandSide::Left ? vr::TrackedControllerRole_LeftHand : vr::TrackedControllerRole_RightHand;
}

bool ShouldMirrorThumbToIndex(const std::array<float, kFingerCount>& bends)
{
    if (!std::isfinite(bends[0]) || bends[0] < 0.05f)
    {
        return false;
    }

    for (std::size_t index = 1; index < bends.size(); ++index)
    {
        if (std::isfinite(bends[index]) && bends[index] >= 0.05f)
        {
            return false;
        }
    }

    return true;
}

std::filesystem::path GetCurrentModulePath()
{
    HMODULE module_handle = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&GetCurrentModulePath),
            &module_handle))
    {
        return {};
    }

    wchar_t buffer[MAX_PATH];
    const DWORD length = GetModuleFileNameW(module_handle, buffer, MAX_PATH);
    if (length == 0 || length >= MAX_PATH)
    {
        return {};
    }

    return std::filesystem::path(std::wstring(buffer, length));
}

std::filesystem::path FindFingerTestExecutable()
{
    const auto module_path = GetCurrentModulePath();
    if (module_path.empty())
    {
        return {};
    }

    auto current = module_path.parent_path();
    for (int i = 0; i < 8 && !current.empty(); ++i)
    {
        const std::array<std::filesystem::path, 2> candidates =
        {
            current / "src" / "OpenFinger.Control" / "bin",
            current / "OpenFinger.Control"
        };

        for (const auto& bin_root : candidates)
        {
            if (!std::filesystem::exists(bin_root))
            {
                continue;
            }

            for (const auto& entry : std::filesystem::recursive_directory_iterator(bin_root))
            {
                if (!entry.is_regular_file())
                {
                    continue;
                }

                if (entry.path().filename() == L"OpenFinger.Control.exe")
                {
                    return entry.path();
                }
            }
        }

        if (current == current.root_path())
        {
            break;
        }
        current = current.parent_path();
    }

    return {};
}

bool AcquireFingerTestLaunchSlot()
{
    const ULONGLONG now = GetTickCount64();
    ULONGLONG previous = g_last_finger_test_launch_ms.load(std::memory_order_relaxed);
    while (true)
    {
        if (previous != 0 && (now - previous) < kFingerTestLaunchCooldownMs)
        {
            return false;
        }

        if (g_last_finger_test_launch_ms.compare_exchange_weak(previous, now, std::memory_order_relaxed))
        {
            return true;
        }
    }
}

void MaybeLaunchFingerTestWindow()
{
    vr::EVRSettingsError read_error = vr::VRSettingsError_None;
    const bool requested = vr::VRSettings()->GetBool(kSettingsSection, kSettingsOpenFingerTestWindow, &read_error);
    if (read_error != vr::VRSettingsError_None && read_error != vr::VRSettingsError_UnsetSettingHasNoDefault)
    {
        DriverLog(
            "OpenFinger settings read failed for %s/%s: %s",
            kSettingsSection,
            kSettingsOpenFingerTestWindow,
            vr::VRSettings()->GetSettingsErrorNameFromEnum(read_error));
        return;
    }

    if (!requested)
    {
        return;
    }

    vr::EVRSettingsError write_error = vr::VRSettingsError_None;
    vr::VRSettings()->SetBool(kSettingsSection, kSettingsOpenFingerTestWindow, false, &write_error);
    if (write_error != vr::VRSettingsError_None)
    {
        DriverLog(
            "OpenFinger settings write failed for %s/%s: %s",
            kSettingsSection,
            kSettingsOpenFingerTestWindow,
            vr::VRSettings()->GetSettingsErrorNameFromEnum(write_error));
    }

    if (!AcquireFingerTestLaunchSlot())
    {
        return;
    }

    const auto executable = FindFingerTestExecutable();
    if (executable.empty())
    {
        DriverLog("OpenFinger could not locate OpenFinger.Control.exe for finger test window");
        return;
    }

    const auto result = reinterpret_cast<INT_PTR>(
        ShellExecuteW(nullptr, L"open", executable.c_str(), L"--finger-test", executable.parent_path().c_str(), SW_SHOWNORMAL));
    if (result <= 32)
    {
        DriverLog("OpenFinger finger test window launch failed with ShellExecute code %Id", result);
        return;
    }

    DriverLog("OpenFinger launched finger test window: %ls", executable.c_str());
}


void ApplyPoseOffsetToDriverPose(vr::DriverPose_t* pose, const ControllerPoseOffset& offset)
{
    if (pose == nullptr)
    {
        return;
    }

    const vr::HmdQuaternion_t base_rotation = pose->qRotation;
    const vr::HmdQuaternion_t offset_rotation = HmdQuaternionFromEulerAngles(
        DegToRad(offset.rotation_roll),
        DegToRad(offset.rotation_pitch),
        DegToRad(offset.rotation_yaw));
    const vr::HmdVector3_t local_position_offset = { offset.position_x, offset.position_y, offset.position_z };
    const vr::HmdVector3_t world_position_offset = local_position_offset * base_rotation;

    pose->vecPosition[0] += world_position_offset.v[0];
    pose->vecPosition[1] += world_position_offset.v[1];
    pose->vecPosition[2] += world_position_offset.v[2];
    pose->qRotation = base_rotation * offset_rotation;
}

} // namespace

OpenFingerHandDevice::OpenFingerHandDevice(
    HandSide side,
    const AppConfig& config,
    RuntimeFrameReceiver* runtime_receiver,
    ControllerBridgeReceiver* controller_receiver)
    : side_(side),
      config_(config),
      runtime_receiver_(runtime_receiver),
      controller_receiver_(controller_receiver)
{
    serial_number_ = side_ == HandSide::Left ? "OpenFinger-Left-001" : "OpenFinger-Right-001";
    model_number_ = side_ == HandSide::Left ? "OpenFinger Left Hand" : "OpenFinger Right Hand";
}

OpenFingerHandDevice::~OpenFingerHandDevice()
{
    Deactivate();
}

vr::EVRInitError OpenFingerHandDevice::Activate(uint32_t object_id)
{
    device_index_ = object_id;

    const vr::PropertyContainerHandle_t container = vr::VRProperties()->TrackedDeviceToPropertyContainer(device_index_);
    vr::VRProperties()->SetStringProperty(container, vr::Prop_ModelNumber_String, model_number_.c_str());
    vr::VRProperties()->SetStringProperty(container, vr::Prop_ManufacturerName_String, "OpenFinger");
    vr::VRProperties()->SetStringProperty(container, vr::Prop_SerialNumber_String, serial_number_.c_str());
    vr::VRProperties()->SetStringProperty(container, vr::Prop_InputProfilePath_String, kInputProfilePath);
    vr::VRProperties()->SetStringProperty(container, vr::Prop_ControllerType_String, kControllerType);
    vr::VRProperties()->SetStringProperty(container, vr::Prop_RenderModelName_String, RenderModelName(side_));
    vr::VRProperties()->SetStringProperty(
        container,
        vr::Prop_RegisteredDeviceType_String,
        side_ == HandSide::Left ? "openfinger/left_hand" : "openfinger/right_hand");
    vr::VRProperties()->SetInt32Property(container, vr::Prop_ControllerRoleHint_Int32, static_cast<int32_t>(ControllerRole(side_)));
    vr::VRProperties()->SetInt32Property(container, vr::Prop_ControllerHandSelectionPriority_Int32, kOpenFingerHandSelectionPriority);
    vr::VRProperties()->SetBoolProperty(container, vr::Prop_DeviceCanPowerOff_Bool, false);
    vr::VRProperties()->SetBoolProperty(container, vr::Prop_DeviceIsWireless_Bool, true);

    const vr::EVRInputError input_error = CreateInputs(container);
    if (input_error != vr::VRInputError_None)
    {
        LogInputError("CreateInputs", input_error);
        return vr::VRInitError_Driver_Failed;
    }

    active_ = true;
    RefreshControllerForwardingSetting();
    SubmitCurrentState();
    update_thread_ = std::thread(&OpenFingerHandDevice::UpdateLoop, this);

    DriverLog("OpenFinger %s activated", side_ == HandSide::Left ? "left hand" : "right hand");
    return vr::VRInitError_None;
}

void OpenFingerHandDevice::Deactivate()
{
    if (active_.exchange(false))
    {
        if (update_thread_.joinable())
        {
            update_thread_.join();
        }
    }

    device_index_ = vr::k_unTrackedDeviceIndexInvalid;
}

void OpenFingerHandDevice::EnterStandby()
{
}

void* OpenFingerHandDevice::GetComponent(const char* component_name_and_version)
{
    (void)component_name_and_version;
    return nullptr;
}

void OpenFingerHandDevice::DebugRequest(const char* request, char* response_buffer, uint32_t response_buffer_size)
{
    (void)request;
    if (response_buffer_size >= 1)
    {
        response_buffer[0] = '\0';
    }
}

vr::DriverPose_t OpenFingerHandDevice::GetPose()
{
    vr::DriverPose_t pose {};
    ControllerPoseOffset pose_offset;

    if (runtime_receiver_ != nullptr)
    {
        RuntimeFrame frame;
        if (runtime_receiver_->CopyLatestFrame(&frame))
        {
            const HandRuntimeState& hand_state = side_ == HandSide::Left ? frame.left : frame.right;
            pose_offset = hand_state.pose_offset;
            if (!hand_state.present)
            {
                const std::uint64_t now = GetTickCount64();
                const std::uint64_t last_present = last_runtime_present_ms_.load(std::memory_order_relaxed);
                if (last_present == 0 || (now - last_present) >= kRuntimePresenceHoldMs)
                {
                    pose.poseTimeOffset = 0.0;
                    pose.qWorldFromDriverRotation = { 1.0, 0.0, 0.0, 0.0 };
                    pose.qDriverFromHeadRotation = { 1.0, 0.0, 0.0, 0.0 };
                    pose.qRotation = { 1.0, 0.0, 0.0, 0.0 };
                    pose.result = vr::TrackingResult_Uninitialized;
                    pose.poseIsValid = false;
                    pose.deviceIsConnected = false;
                    pose.willDriftInYaw = false;
                    pose.shouldApplyHeadModel = false;
                    return pose;
                }
            }
        }
    }

    if (!TryBuildPoseFromTrackedSource(&pose, pose_offset))
    {
        FillFallbackPose(&pose, pose_offset);
    }

    return pose;
}

const std::string& OpenFingerHandDevice::serial_number() const
{
    return serial_number_;
}

HandSide OpenFingerHandDevice::side() const
{
    return side_;
}

vr::EVRInputError OpenFingerHandDevice::CreateInputs(vr::PropertyContainerHandle_t container)
{
    vr::EVRInputError error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/system/click", &input_handles_[kInput_SystemClick]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/system/touch", &input_handles_[kInput_SystemTouch]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, side_ == HandSide::Left ? "/input/x/click" : "/input/a/click", &input_handles_[kInput_AClick]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, side_ == HandSide::Left ? "/input/x/touch" : "/input/a/touch", &input_handles_[kInput_ATouch]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, side_ == HandSide::Left ? "/input/y/click" : "/input/b/click", &input_handles_[kInput_BClick]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, side_ == HandSide::Left ? "/input/y/touch" : "/input/b/touch", &input_handles_[kInput_BTouch]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/trigger/value", &input_handles_[kInput_TriggerValue], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/trigger/click", &input_handles_[kInput_TriggerClick]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/trigger/touch", &input_handles_[kInput_TriggerTouch]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/grip/value", &input_handles_[kInput_GripValue], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/grip/force", &input_handles_[kInput_GripForce], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/grip/click", &input_handles_[kInput_GripClick]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/grip/touch", &input_handles_[kInput_GripTouch]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/joystick/x", &input_handles_[kInput_JoystickX], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedTwoSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/joystick/y", &input_handles_[kInput_JoystickY], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedTwoSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/joystick/click", &input_handles_[kInput_JoystickClick]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateBooleanComponent(
        container, "/input/joystick/touch", &input_handles_[kInput_JoystickTouch]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/finger/index", &input_handles_[kInput_IndexCurl], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/finger/middle", &input_handles_[kInput_MiddleCurl], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/finger/ring", &input_handles_[kInput_RingCurl], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateScalarComponent(
        container, "/input/finger/pinky", &input_handles_[kInput_PinkyCurl], vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreatePoseComponent(container, "/pose/raw", &input_handles_[kInput_PoseRaw]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreatePoseComponent(container, "/pose/base", &input_handles_[kInput_PoseBase]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreatePoseComponent(container, "/pose/handgrip", &input_handles_[kInput_PoseHandGrip]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreatePoseComponent(container, "/pose/grip", &input_handles_[kInput_PoseGrip]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreatePoseComponent(container, "/pose/tip", &input_handles_[kInput_PoseTip]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreatePoseComponent(container, "/pose/openxr_aim", &input_handles_[kInput_PoseOpenXrAim]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreatePoseComponent(container, "/pose/openxr_grip", &input_handles_[kInput_PoseOpenXrGrip]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateHapticComponent(container, "/output/haptic", &input_handles_[kInput_Haptic]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    error = vr::VRDriverInput()->CreateSkeletonComponent(
        container,
        SkeletonInputPath(side_),
        SkeletonBodyPath(side_),
        "/pose/raw",
        vr::VRSkeletalTracking_Partial,
        nullptr,
        0,
        &input_handles_[kInput_Skeleton]);
    if (error != vr::VRInputError_None)
    {
        return error;
    }

    SubmitPoseComponent(kInput_PoseRaw);
    SubmitPoseComponent(kInput_PoseBase);
    SubmitPoseComponent(kInput_PoseHandGrip);
    SubmitPoseComponent(kInput_PoseGrip);
    SubmitPoseComponent(kInput_PoseTip);
    SubmitPoseComponent(kInput_PoseOpenXrAim);
    SubmitPoseComponent(kInput_PoseOpenXrGrip);
    return vr::VRInputError_None;
}

bool OpenFingerHandDevice::TryBuildPoseFromTrackedSource(vr::DriverPose_t* out_pose, const ControllerPoseOffset& pose_offset)
{
    if (out_pose == nullptr)
    {
        return false;
    }

    std::array<vr::TrackedDevicePose_t, vr::k_unMaxTrackedDeviceCount> poses {};
    vr::VRServerDriverHost()->GetRawTrackedDevicePoses(0.0f, poses.data(), static_cast<uint32_t>(poses.size()));

    const vr::TrackedDeviceIndex_t source_index = ResolvePoseSourceIndex(poses);
    if (source_index == vr::k_unTrackedDeviceIndexInvalid)
    {
        return false;
    }

    const vr::TrackedDevicePose_t& source_pose = poses[source_index];
    if (!source_pose.bDeviceIsConnected || !source_pose.bPoseIsValid)
    {
        return false;
    }

    out_pose->poseTimeOffset = 0.0;
    out_pose->qWorldFromDriverRotation = { 1.0, 0.0, 0.0, 0.0 };
    out_pose->qDriverFromHeadRotation = { 1.0, 0.0, 0.0, 0.0 };

    const vr::HmdVector3_t position = HmdVector3From34Matrix(source_pose.mDeviceToAbsoluteTracking);
    const vr::HmdQuaternion_t orientation = HmdQuaternionFromMatrix(source_pose.mDeviceToAbsoluteTracking);

    out_pose->vecPosition[0] = position.v[0];
    out_pose->vecPosition[1] = position.v[1];
    out_pose->vecPosition[2] = position.v[2];

    out_pose->vecVelocity[0] = source_pose.vVelocity.v[0];
    out_pose->vecVelocity[1] = source_pose.vVelocity.v[1];
    out_pose->vecVelocity[2] = source_pose.vVelocity.v[2];

    out_pose->vecAngularVelocity[0] = source_pose.vAngularVelocity.v[0];
    out_pose->vecAngularVelocity[1] = source_pose.vAngularVelocity.v[1];
    out_pose->vecAngularVelocity[2] = source_pose.vAngularVelocity.v[2];

    out_pose->qRotation = orientation;
    out_pose->result = source_pose.eTrackingResult;
    out_pose->poseIsValid = true;
    out_pose->deviceIsConnected = true;
    out_pose->willDriftInYaw = false;
    out_pose->shouldApplyHeadModel = false;
    ApplyPoseOffsetToDriverPose(out_pose, pose_offset);

    if (using_fallback_pose_.exchange(false))
    {
        DriverLog("OpenFinger %s resumed tracked pose following from device index %u", ToString(side_).data(), source_index);
    }

    return true;
}

void OpenFingerHandDevice::FillFallbackPose(vr::DriverPose_t* out_pose, const ControllerPoseOffset& pose_offset)
{
    if (out_pose == nullptr)
    {
        return;
    }

    out_pose->qWorldFromDriverRotation = { 1.0, 0.0, 0.0, 0.0 };
    out_pose->qDriverFromHeadRotation = { 1.0, 0.0, 0.0, 0.0 };
    out_pose->qRotation = { 1.0, 0.0, 0.0, 0.0 };

    vr::TrackedDevicePose_t hmd_pose {};
    vr::VRServerDriverHost()->GetRawTrackedDevicePoses(0.0f, &hmd_pose, 1);

    const double hand_sign = side_ == HandSide::Left ? -1.0 : 1.0;
    if (hmd_pose.bPoseIsValid)
    {
        const vr::HmdVector3_t hmd_position = HmdVector3From34Matrix(hmd_pose.mDeviceToAbsoluteTracking);
        const vr::HmdQuaternion_t hmd_orientation = HmdQuaternionFromMatrix(hmd_pose.mDeviceToAbsoluteTracking);
        const vr::HmdQuaternion_t offset_orientation = HmdQuaternionFromEulerAngles(DegToRad(90.0), DegToRad(90.0 * hand_sign), 0.0);
        out_pose->qRotation = hmd_orientation * offset_orientation;

        const vr::HmdVector3_t offset_position = { static_cast<float>(0.18f * hand_sign), 0.08f, -0.45f };
        const vr::HmdVector3_t world_position = hmd_position + (offset_position * hmd_orientation);

        out_pose->vecPosition[0] = world_position.v[0];
        out_pose->vecPosition[1] = world_position.v[1];
        out_pose->vecPosition[2] = world_position.v[2];
    }
    else
    {
        out_pose->vecPosition[0] = 0.18 * hand_sign;
        out_pose->vecPosition[1] = 1.2;
        out_pose->vecPosition[2] = -0.45;
    }

    out_pose->poseIsValid = true;
    out_pose->deviceIsConnected = true;
    out_pose->result = vr::TrackingResult_Running_OK;
    out_pose->willDriftInYaw = false;
    out_pose->shouldApplyHeadModel = false;
    ApplyPoseOffsetToDriverPose(out_pose, pose_offset);

    if (!using_fallback_pose_.exchange(true))
    {
        DriverLog("OpenFinger %s could not find tracked pose source; falling back to synthetic pose", ToString(side_).data());
    }
}

bool OpenFingerHandDevice::IsEligiblePoseSource(
    vr::TrackedDeviceIndex_t tracked_device_index,
    const std::array<vr::TrackedDevicePose_t, vr::k_unMaxTrackedDeviceCount>& poses,
    std::string* out_serial,
    std::string* out_controller_type,
    std::string* out_manufacturer) const
{
    if (tracked_device_index == vr::k_unTrackedDeviceIndexInvalid || tracked_device_index == device_index_)
    {
        return false;
    }

    if (tracked_device_index >= poses.size() || !poses[tracked_device_index].bDeviceIsConnected)
    {
        return false;
    }

    vr::CVRPropertyHelpers* properties = vr::VRProperties();
    const vr::PropertyContainerHandle_t container = properties->TrackedDeviceToPropertyContainer(tracked_device_index);

    vr::ETrackedPropertyError error = vr::TrackedProp_Success;
    const int device_class = properties->GetInt32Property(container, vr::Prop_DeviceClass_Int32, &error);
    if (error != vr::TrackedProp_Success || device_class != vr::TrackedDeviceClass_Controller)
    {
        return false;
    }

    error = vr::TrackedProp_Success;
    const int role_hint = properties->GetInt32Property(container, vr::Prop_ControllerRoleHint_Int32, &error);
    if (error != vr::TrackedProp_Success || role_hint != ControllerRole(side_))
    {
        return false;
    }

    error = vr::TrackedProp_Success;
    const std::string serial = properties->GetStringProperty(container, vr::Prop_SerialNumber_String, &error);
    if (error != vr::TrackedProp_Success || serial.empty() || serial == serial_number_)
    {
        return false;
    }

    if (out_serial != nullptr)
    {
        *out_serial = serial;
    }

    if (out_controller_type != nullptr)
    {
        error = vr::TrackedProp_Success;
        *out_controller_type = properties->GetStringProperty(container, vr::Prop_ControllerType_String, &error);
    }

    if (out_manufacturer != nullptr)
    {
        error = vr::TrackedProp_Success;
        *out_manufacturer = properties->GetStringProperty(container, vr::Prop_ManufacturerName_String, &error);
    }

    return true;
}

vr::TrackedDeviceIndex_t OpenFingerHandDevice::ResolvePoseSourceIndex(
    const std::array<vr::TrackedDevicePose_t, vr::k_unMaxTrackedDeviceCount>& poses)
{
    const auto cached_index = static_cast<vr::TrackedDeviceIndex_t>(pose_source_index_.load());
    if (IsEligiblePoseSource(cached_index, poses))
    {
        return cached_index;
    }

    vr::TrackedDeviceIndex_t resolved_index = vr::k_unTrackedDeviceIndexInvalid;
    std::string serial;
    std::string controller_type;
    std::string manufacturer;

    for (vr::TrackedDeviceIndex_t index = 0; index < poses.size(); ++index)
    {
        if (IsEligiblePoseSource(index, poses, &serial, &controller_type, &manufacturer))
        {
            resolved_index = index;
            break;
        }
    }

    const auto previous_index = static_cast<vr::TrackedDeviceIndex_t>(pose_source_index_.exchange(resolved_index));
    if (resolved_index != previous_index)
    {
        if (resolved_index == vr::k_unTrackedDeviceIndexInvalid)
        {
            DriverLog("OpenFinger %s lost tracked pose source", ToString(side_).data());
        }
        else
        {
            DriverLog(
                "OpenFinger %s pose source locked to device index %u serial '%s' manufacturer '%s' controller_type '%s'",
                ToString(side_).data(),
                resolved_index,
                serial.c_str(),
                manufacturer.c_str(),
                controller_type.c_str());
        }
    }

    return resolved_index;
}

void OpenFingerHandDevice::UpdateLoop()
{
    using clock = std::chrono::steady_clock;
    const int hz = std::max(10, config_.steamvr.update_hz);
    const auto frame_interval = std::chrono::duration_cast<clock::duration>(std::chrono::duration<double>(1.0 / static_cast<double>(hz)));

    while (active_)
    {
        const auto frame_start = clock::now();
        SubmitCurrentState();
        std::this_thread::sleep_until(frame_start + frame_interval);
    }
}

void OpenFingerHandDevice::SubmitCurrentState()
{
    if (device_index_ == vr::k_unTrackedDeviceIndexInvalid)
    {
        return;
    }

    RuntimeFrame frame;
    const bool has_frame = runtime_receiver_ != nullptr && runtime_receiver_->CopyLatestFrame(&frame);
    RuntimeFrame empty_frame;
    empty_frame.left.side = HandSide::Left;
    empty_frame.right.side = HandSide::Right;
    const HandRuntimeState& hand_state = !has_frame
        ? (side_ == HandSide::Left ? empty_frame.left : empty_frame.right)
        : (side_ == HandSide::Left ? frame.left : frame.right);

    std::array<float, kFingerCount> bends {};
    for (std::size_t index = 0; index < kFingerCount; ++index)
    {
        bends[index] = static_cast<float>(hand_state.fingers[index].bend_smoothed);
    }

    std::array<float, kFingerCount> effective_bends = bends;
    const bool thumb_to_index_fallback = ShouldMirrorThumbToIndex(effective_bends);
    if (thumb_to_index_fallback)
    {
        effective_bends[1] = std::max(effective_bends[1], effective_bends[0]);
    }

    bool should_log_runtime = false;
    if (has_frame && frame.seq != last_logged_runtime_seq_)
    {
        if (hand_state.present != last_logged_runtime_present_)
        {
            should_log_runtime = true;
        }
        else
        {
            for (std::size_t index = 0; index < bends.size(); ++index)
            {
                if (std::abs(bends[index] - last_logged_runtime_bends_[index]) >= 0.10f)
                {
                    should_log_runtime = true;
                    break;
                }
            }
        }
    }

    if (should_log_runtime)
    {
        DriverLog(
            "OpenFinger %s runtime seq=%llu present=%d stale=%d bends=(%.3f, %.3f, %.3f, %.3f, %.3f)",
            ToString(side_).data(),
            static_cast<unsigned long long>(frame.seq),
            hand_state.present ? 1 : 0,
            hand_state.stale ? 1 : 0,
            bends[0],
            bends[1],
            bends[2],
            bends[3],
            bends[4]);
    }

    if (has_frame)
    {
        if (hand_state.present)
        {
            last_runtime_present_ms_.store(static_cast<std::uint64_t>(GetTickCount64()), std::memory_order_relaxed);
        }
        last_logged_runtime_seq_ = frame.seq;
        last_logged_runtime_present_ = hand_state.present;
        last_logged_runtime_bends_ = bends;
    }

    if (thumb_to_index_fallback != last_thumb_to_index_fallback_)
    {
        DriverLog(
            "OpenFinger %s thumb->index compatibility fallback %s (thumb=%.3f index=%.3f middle=%.3f ring=%.3f pinky=%.3f)",
            ToString(side_).data(),
            thumb_to_index_fallback ? "enabled" : "disabled",
            bends[0],
            bends[1],
            bends[2],
            bends[3],
            bends[4]);
        last_thumb_to_index_fallback_ = thumb_to_index_fallback;
    }

    SubmitScalarComponent(kInput_IndexCurl, effective_bends[1]);
    SubmitScalarComponent(kInput_MiddleCurl, effective_bends[2]);
    SubmitScalarComponent(kInput_RingCurl, effective_bends[3]);
    SubmitScalarComponent(kInput_PinkyCurl, effective_bends[4]);

    const bool joystick_usable = hand_state.present && !hand_state.stale && hand_state.joystick_available;
    const bool joystick_axis_enabled = joystick_usable && hand_state.joystick_axis_mode == 1;
    const float joystick_x = joystick_axis_enabled ? std::clamp(hand_state.joystick_x, -1.0f, 1.0f) : 0.0f;
    const float joystick_y = joystick_axis_enabled ? std::clamp(hand_state.joystick_y, -1.0f, 1.0f) : 0.0f;
    const bool joystick_click = joystick_usable && hand_state.joystick_click && hand_state.joystick_click_action == 1;
    const bool joystick_touch = joystick_axis_enabled
        && (hand_state.joystick_touch || std::abs(joystick_x) >= 0.15f || std::abs(joystick_y) >= 0.15f);
    const bool a_click = joystick_usable && hand_state.joystick_click && hand_state.joystick_click_action == 2;
    const bool b_click = joystick_usable && hand_state.joystick_click && hand_state.joystick_click_action == 3;
    const bool grip_click = joystick_usable && hand_state.joystick_click && hand_state.joystick_click_action == 4;
    const bool system_click = joystick_usable && hand_state.joystick_click && hand_state.joystick_click_action == 5;
    const float grip_value = grip_click ? 1.0f : 0.0f;

    SubmitBooleanComponent(kInput_SystemClick, system_click);
    SubmitBooleanComponent(kInput_SystemTouch, system_click);
    SubmitBooleanComponent(kInput_AClick, a_click);
    SubmitBooleanComponent(kInput_ATouch, a_click);
    SubmitBooleanComponent(kInput_BClick, b_click);
    SubmitBooleanComponent(kInput_BTouch, b_click);
    SubmitScalarComponent(kInput_TriggerValue, 0.0f);
    SubmitBooleanComponent(kInput_TriggerClick, false);
    SubmitBooleanComponent(kInput_TriggerTouch, false);
    SubmitScalarComponent(kInput_GripValue, grip_value);
    SubmitScalarComponent(kInput_GripForce, grip_value);
    SubmitBooleanComponent(kInput_GripClick, grip_click);
    SubmitBooleanComponent(kInput_GripTouch, grip_click);
    SubmitScalarComponent(kInput_JoystickX, joystick_x);
    SubmitScalarComponent(kInput_JoystickY, joystick_y);
    SubmitBooleanComponent(kInput_JoystickClick, joystick_click);
    SubmitBooleanComponent(kInput_JoystickTouch, joystick_touch);

    vr::VRBoneTransform_t without_controller[SkeletonPoseBuilder::kBoneCount];
    vr::VRBoneTransform_t with_controller[SkeletonPoseBuilder::kBoneCount];
    skeleton_builder_.BuildHand(side_, effective_bends, without_controller, with_controller);

    vr::EVRInputError error = vr::VRDriverInput()->UpdateSkeletonComponent(
        input_handles_[kInput_Skeleton],
        vr::VRSkeletalMotionRange_WithoutController,
        without_controller,
        SkeletonPoseBuilder::kBoneCount);
    if (error != vr::VRInputError_None)
    {
        LogInputError("UpdateSkeletonComponent WithoutController", error);
    }

    error = vr::VRDriverInput()->UpdateSkeletonComponent(
        input_handles_[kInput_Skeleton],
        vr::VRSkeletalMotionRange_WithController,
        with_controller,
        SkeletonPoseBuilder::kBoneCount);
    if (error != vr::VRInputError_None)
    {
        LogInputError("UpdateSkeletonComponent WithController", error);
    }

    vr::VRServerDriverHost()->TrackedDevicePoseUpdated(device_index_, GetPose(), sizeof(vr::DriverPose_t));
}

bool OpenFingerHandDevice::RefreshControllerForwardingSetting()
{
    const auto now = std::chrono::steady_clock::now();
    if (last_settings_poll_.time_since_epoch().count() != 0 && (now - last_settings_poll_) < kSettingsPollInterval)
    {
        return controller_forwarding_enabled_;
    }

    last_settings_poll_ = now;

    vr::EVRSettingsError error = vr::VRSettingsError_None;
    const bool enabled = vr::VRSettings()->GetBool(kSettingsSection, kSettingsForwardControllerInputs, &error);
    if (error == vr::VRSettingsError_None)
    {
        controller_forwarding_enabled_ = enabled;
    }
    else if (error != vr::VRSettingsError_UnsetSettingHasNoDefault)
    {
        if (error != last_settings_error_)
        {
            DriverLog(
                "OpenFinger settings read failed for %s/%s: %s",
                kSettingsSection,
                kSettingsForwardControllerInputs,
                vr::VRSettings()->GetSettingsErrorNameFromEnum(error));
        }
    }

    last_settings_error_ = error;
    MaybeLaunchFingerTestWindow();
    return controller_forwarding_enabled_;
}

void OpenFingerHandDevice::UpdateControllerComponents(
    const ForwardedControllerState& forwarded_state,
    bool forwarding_enabled,
    std::chrono::steady_clock::time_point now)
{
    const bool usable = forwarding_enabled
        && forwarded_state.connected
        && IsForwardedControllerStateFresh(forwarded_state, now, kForwardedControllerFreshFor);

    SubmitBooleanComponent(kInput_SystemClick, usable && forwarded_state.system_click);
    SubmitBooleanComponent(kInput_SystemTouch, usable && forwarded_state.system_touch);
    SubmitBooleanComponent(kInput_AClick, usable && forwarded_state.a_click);
    SubmitBooleanComponent(kInput_ATouch, usable && forwarded_state.a_touch);
    SubmitBooleanComponent(kInput_BClick, usable && forwarded_state.b_click);
    SubmitBooleanComponent(kInput_BTouch, usable && forwarded_state.b_touch);
    SubmitScalarComponent(kInput_TriggerValue, usable ? std::clamp(forwarded_state.trigger_value, 0.0f, 1.0f) : 0.0f);
    SubmitBooleanComponent(kInput_TriggerClick, usable && (forwarded_state.trigger_click || forwarded_state.trigger_value >= 0.75f));
    SubmitBooleanComponent(kInput_TriggerTouch, usable && (forwarded_state.trigger_touch || forwarded_state.trigger_value >= 0.05f));
    SubmitScalarComponent(kInput_GripValue, usable ? std::clamp(forwarded_state.grip_value, 0.0f, 1.0f) : 0.0f);
    SubmitScalarComponent(kInput_GripForce, usable ? std::clamp(forwarded_state.grip_value, 0.0f, 1.0f) : 0.0f);
    SubmitBooleanComponent(kInput_GripClick, usable && (forwarded_state.grip_click || forwarded_state.grip_value >= 0.75f));
    SubmitBooleanComponent(kInput_GripTouch, usable && (forwarded_state.grip_touch || forwarded_state.grip_value >= 0.05f));
    SubmitScalarComponent(kInput_JoystickX, usable ? std::clamp(forwarded_state.joystick_x, -1.0f, 1.0f) : 0.0f);
    SubmitScalarComponent(kInput_JoystickY, usable ? std::clamp(forwarded_state.joystick_y, -1.0f, 1.0f) : 0.0f);
    SubmitBooleanComponent(kInput_JoystickClick, usable && forwarded_state.joystick_click);
    SubmitBooleanComponent(
        kInput_JoystickTouch,
        usable
            && (forwarded_state.joystick_touch
                || std::abs(forwarded_state.joystick_x) >= 0.15f
                || std::abs(forwarded_state.joystick_y) >= 0.15f));
}

void OpenFingerHandDevice::SubmitBooleanComponent(InputHandleId handle, bool value)
{
    const auto error = vr::VRDriverInput()->UpdateBooleanComponent(input_handles_[handle], value, 0.0);
    if (error != vr::VRInputError_None)
    {
        LogInputError("UpdateBooleanComponent", error);
    }
}

void OpenFingerHandDevice::SubmitScalarComponent(InputHandleId handle, float value)
{
    const auto error = vr::VRDriverInput()->UpdateScalarComponent(input_handles_[handle], value, 0.0);
    if (error != vr::VRInputError_None)
    {
        LogInputError("UpdateScalarComponent", error);
    }
}

void OpenFingerHandDevice::SubmitPoseComponent(InputHandleId handle)
{
    static const vr::HmdMatrix34_t identity = IdentityPoseOffset();
    const auto error = vr::VRDriverInput()->UpdatePoseComponent(input_handles_[handle], &identity, 0.0);
    if (error != vr::VRInputError_None)
    {
        LogInputError("UpdatePoseComponent", error);
    }
}

void OpenFingerHandDevice::LogInputError(const char* label, vr::EVRInputError error)
{
    DriverLog("%s %s failed with EVRInputError=%d", ToString(side_).data(), label, static_cast<int>(error));
}

} // namespace openfinger
