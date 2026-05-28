#include "driver/OpenFingerServerProvider.h"

#include <cstring>

#include "openvr_driver.h"

#if defined(_WIN32)
#define HMD_DLL_EXPORT extern "C" __declspec(dllexport)
#else
#define HMD_DLL_EXPORT extern "C"
#endif

namespace
{

openfinger::OpenFingerServerProvider g_server_provider;

} // namespace

HMD_DLL_EXPORT void* HmdDriverFactory(const char* interface_name, int* return_code)
{
    if (std::strcmp(vr::IServerTrackedDeviceProvider_Version, interface_name) == 0)
    {
        return &g_server_provider;
    }

    if (return_code != nullptr)
    {
        *return_code = vr::VRInitError_Init_InterfaceNotFound;
    }

    return nullptr;
}
