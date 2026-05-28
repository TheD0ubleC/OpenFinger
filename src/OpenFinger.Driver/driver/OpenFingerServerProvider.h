#pragma once

#include "common/Config.h"
#include "common/ControllerBridgeReceiver.h"
#include "common/RuntimeFrameReceiver.h"
#include "driver/OpenFingerHandDevice.h"

#include <memory>

#include "openvr_driver.h"

namespace openfinger
{

class OpenFingerServerProvider : public vr::IServerTrackedDeviceProvider
{
public:
    vr::EVRInitError Init(vr::IVRDriverContext* driver_context) override;
    void Cleanup() override;
    const char* const* GetInterfaceVersions() override;
    void RunFrame() override;
    bool ShouldBlockStandbyMode() override;
    void EnterStandby() override;
    void LeaveStandby() override;

private:
    ConfigStore config_store_;
    RuntimeFrameReceiver runtime_receiver_;
    ControllerBridgeReceiver controller_receiver_;
    std::unique_ptr<OpenFingerHandDevice> left_hand_;
    std::unique_ptr<OpenFingerHandDevice> right_hand_;
};

} // namespace openfinger
