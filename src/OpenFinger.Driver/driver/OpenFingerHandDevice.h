#pragma once

#include "common/Config.h"
#include "common/ControllerBridgeReceiver.h"
#include "common/RuntimeFrameReceiver.h"
#include "driver/SkeletonPoseBuilder.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <string>
#include <thread>

#include "openvr_driver.h"

namespace openfinger
{

class OpenFingerHandDevice : public vr::ITrackedDeviceServerDriver
{
public:
    OpenFingerHandDevice(
        HandSide side,
        const AppConfig& config,
        RuntimeFrameReceiver* runtime_receiver,
        ControllerBridgeReceiver* controller_receiver);
    ~OpenFingerHandDevice();

    vr::EVRInitError Activate(uint32_t object_id) override;
    void Deactivate() override;
    void EnterStandby() override;
    void* GetComponent(const char* component_name_and_version) override;
    void DebugRequest(const char* request, char* response_buffer, uint32_t response_buffer_size) override;
    vr::DriverPose_t GetPose() override;

    const std::string& serial_number() const;
    HandSide side() const;

private:
    enum InputHandleId
    {
        kInput_SystemClick = 0,
        kInput_SystemTouch,
        kInput_AClick,
        kInput_ATouch,
        kInput_BClick,
        kInput_BTouch,
        kInput_TriggerValue,
        kInput_TriggerClick,
        kInput_TriggerTouch,
        kInput_GripValue,
        kInput_GripForce,
        kInput_GripClick,
        kInput_GripTouch,
        kInput_JoystickX,
        kInput_JoystickY,
        kInput_JoystickClick,
        kInput_JoystickTouch,
        kInput_IndexCurl,
        kInput_MiddleCurl,
        kInput_RingCurl,
        kInput_PinkyCurl,
        kInput_PoseRaw,
        kInput_PoseBase,
        kInput_PoseHandGrip,
        kInput_PoseGrip,
        kInput_PoseTip,
        kInput_PoseOpenXrAim,
        kInput_PoseOpenXrGrip,
        kInput_Haptic,
        kInput_Skeleton,
        kInput_Count
    };

    vr::EVRInputError CreateInputs(vr::PropertyContainerHandle_t container);
    bool TryBuildPoseFromTrackedSource(vr::DriverPose_t* out_pose, const ControllerPoseOffset& pose_offset);
    void FillFallbackPose(vr::DriverPose_t* out_pose, const ControllerPoseOffset& pose_offset);
    bool IsEligiblePoseSource(
        vr::TrackedDeviceIndex_t device_index,
        const std::array<vr::TrackedDevicePose_t, vr::k_unMaxTrackedDeviceCount>& poses,
        std::string* out_serial = nullptr,
        std::string* out_controller_type = nullptr,
        std::string* out_manufacturer = nullptr) const;
    vr::TrackedDeviceIndex_t ResolvePoseSourceIndex(
        const std::array<vr::TrackedDevicePose_t, vr::k_unMaxTrackedDeviceCount>& poses);
    void UpdateLoop();
    void SubmitCurrentState();
    bool RefreshControllerForwardingSetting();
    void UpdateControllerComponents(
        const ForwardedControllerState& forwarded_state,
        bool forwarding_enabled,
        std::chrono::steady_clock::time_point now);
    void SubmitBooleanComponent(InputHandleId handle, bool value);
    void SubmitScalarComponent(InputHandleId handle, float value);
    void SubmitPoseComponent(InputHandleId handle);
    void LogInputError(const char* label, vr::EVRInputError error);

    std::array<vr::VRInputComponentHandle_t, kInput_Count> input_handles_ {};
    std::atomic<bool> active_ = false;
    std::thread update_thread_;

    HandSide side_ = HandSide::Right;
    AppConfig config_;
    RuntimeFrameReceiver* runtime_receiver_ = nullptr;
    ControllerBridgeReceiver* controller_receiver_ = nullptr;
    SkeletonPoseBuilder skeleton_builder_;

    vr::TrackedDeviceIndex_t device_index_ = vr::k_unTrackedDeviceIndexInvalid;
    std::string serial_number_;
    std::string model_number_;
    std::atomic<std::uint32_t> pose_source_index_ { vr::k_unTrackedDeviceIndexInvalid };
    std::atomic<bool> using_fallback_pose_ { false };
    std::chrono::steady_clock::time_point last_settings_poll_ {};
    bool controller_forwarding_enabled_ = true;
    vr::EVRSettingsError last_settings_error_ = vr::VRSettingsError_None;
    std::atomic<std::uint64_t> last_runtime_present_ms_ { 0 };
    std::uint64_t last_logged_runtime_seq_ = 0;
    bool last_logged_runtime_present_ = false;
    std::array<float, kFingerCount> last_logged_runtime_bends_ {};
    bool last_thumb_to_index_fallback_ = false;
};

} // namespace openfinger
