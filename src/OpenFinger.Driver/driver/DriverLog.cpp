#include "driver/DriverLog.h"

#include <cstdarg>
#include <cstdio>

#include "openvr_driver.h"

namespace
{

void DriverLogVarArgs(const char* format, va_list args)
{
    char buffer[1024];
#if defined(_WIN32)
    vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, format, args);
#else
    vsnprintf(buffer, sizeof(buffer), format, args);
#endif

    if (vr::VRDriverLog() != nullptr)
    {
        vr::VRDriverLog()->Log(buffer);
    }
}

} // namespace

void DriverLog(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    DriverLogVarArgs(format, args);
    va_end(args);
}

void DebugDriverLog(const char* format, ...)
{
#ifdef _DEBUG
    va_list args;
    va_start(args, format);
    DriverLogVarArgs(format, args);
    va_end(args);
#else
    (void)format;
#endif
}
