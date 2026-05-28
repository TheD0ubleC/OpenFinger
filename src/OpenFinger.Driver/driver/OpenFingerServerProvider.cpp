#include "driver/OpenFingerServerProvider.h"

#include "driver/DriverLog.h"

namespace openfinger
{

vr::EVRInitError OpenFingerServerProvider::Init(vr::IVRDriverContext* driver_context)
{
    VR_INIT_SERVER_DRIVER_CONTEXT(driver_context);
    DriverLog("OpenFingerServerProvider::Init");

    std::string error;
    if (!config_store_.LoadOrCreate(&error))
    {
        DriverLog("OpenFinger config load failed: %s", error.c_str());
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        return vr::VRInitError_Driver_Failed;
    }

    if (!runtime_receiver_.Start(config_store_.config(), &error))
    {
        DriverLog("OpenFinger runtime receiver start failed: %s", error.c_str());
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        return vr::VRInitError_Driver_Failed;
    }

    if (!controller_receiver_.Start(static_cast<std::uint16_t>(config_store_.config().controller_bridge.udp_port), &error))
    {
        DriverLog("OpenFinger controller bridge receiver start failed: %s", error.c_str());
        runtime_receiver_.Stop();
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        return vr::VRInitError_Driver_Failed;
    }

    left_hand_ = std::make_unique<OpenFingerHandDevice>(
        HandSide::Left,
        config_store_.config(),
        &runtime_receiver_,
        &controller_receiver_);
    right_hand_ = std::make_unique<OpenFingerHandDevice>(
        HandSide::Right,
        config_store_.config(),
        &runtime_receiver_,
        &controller_receiver_);

    const bool added_left = vr::VRServerDriverHost()->TrackedDeviceAdded(
        left_hand_->serial_number().c_str(),
        vr::TrackedDeviceClass_Controller,
        left_hand_.get());
    const bool added_right = vr::VRServerDriverHost()->TrackedDeviceAdded(
        right_hand_->serial_number().c_str(),
        vr::TrackedDeviceClass_Controller,
        right_hand_.get());

    if (!added_left || !added_right)
    {
        DriverLog("TrackedDeviceAdded failed for OpenFinger hands");
        left_hand_.reset();
        right_hand_.reset();
        controller_receiver_.Stop();
        runtime_receiver_.Stop();
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        return vr::VRInitError_Driver_Failed;
    }

    DriverLog(
        "OpenFinger provider listening for runtime on UDP %d and controller bridge on UDP %d",
        config_store_.config().runtime.local_runtime_udp_port,
        config_store_.config().controller_bridge.udp_port);
    return vr::VRInitError_None;
}

void OpenFingerServerProvider::Cleanup()
{
    DriverLog("OpenFingerServerProvider::Cleanup");
    left_hand_.reset();
    right_hand_.reset();
    controller_receiver_.Stop();
    runtime_receiver_.Stop();
    VR_CLEANUP_SERVER_DRIVER_CONTEXT();
}

const char* const* OpenFingerServerProvider::GetInterfaceVersions()
{
    return vr::k_InterfaceVersions;
}

void OpenFingerServerProvider::RunFrame()
{
}

bool OpenFingerServerProvider::ShouldBlockStandbyMode()
{
    return false;
}

void OpenFingerServerProvider::EnterStandby()
{
}

void OpenFingerServerProvider::LeaveStandby()
{
}

} // namespace openfinger
