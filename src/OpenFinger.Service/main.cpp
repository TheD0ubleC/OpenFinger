#include "service/OpenFingerService.h"
#include "openfinger/OpenFingerVersion.h"

#include <atomic>
#include <csignal>
#include <iostream>

#include <windows.h>

namespace
{

std::atomic<bool> g_stop_requested = false;
openfinger::OpenFingerService* g_service = nullptr;

BOOL WINAPI ConsoleHandler(DWORD control_type)
{
    if (control_type == CTRL_C_EVENT || control_type == CTRL_CLOSE_EVENT || control_type == CTRL_BREAK_EVENT
        || control_type == CTRL_SHUTDOWN_EVENT)
    {
        g_stop_requested = true;
        if (g_service != nullptr)
        {
            g_service->Stop();
        }
        return TRUE;
    }

    return FALSE;
}

} // namespace

int main()
{
    std::cout.setf(std::ios::unitbuf);
    std::cerr.setf(std::ios::unitbuf);

    openfinger::OpenFingerService service;
    g_service = &service;
    SetConsoleCtrlHandler(ConsoleHandler, TRUE);

    std::string error;
    if (!service.Start(&error))
    {
        std::cerr << "OpenFinger service failed to start: " << error << "\n";
        return 1;
    }

    std::cout << "OpenFinger service " << OPENFINGER_VERSION << " running (protocol v" << OPENFINGER_PROTOCOL_VERSION << ")\n";
    service.WaitForExit();
    std::cout << "OpenFinger service stopped\n";
    return 0;
}
