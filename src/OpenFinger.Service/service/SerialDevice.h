#pragma once

#include "service/ServiceProtocol.h"

#include <string>
#include <vector>

namespace openfinger
{

std::vector<std::string> EnumerateSerialPorts();

bool ReadDeviceStatusFromSerial(const std::string& port_name, DeviceStatusMessage* out_status, std::string* out_error);
bool SendIdentifyOverSerial(const std::string& port_name, std::string* out_error);
bool SendProvisionOverSerial(const std::string& port_name, const ProvisionRequest& request, std::string* out_error);
bool SendRoleOverSerial(const std::string& port_name, HandRole role, std::string* out_error);
bool ResetDeviceOverSerial(const std::string& port_name, std::string* out_error);

} // namespace openfinger
