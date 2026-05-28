cmake_minimum_required(VERSION 3.21)

project(OpenFinger LANGUAGES CXX)

file(READ "${CMAKE_CURRENT_SOURCE_DIR}/VERSION" OPENFINGER_VERSION)
string(STRIP "${OPENFINGER_VERSION}" OPENFINGER_VERSION)
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/PROTOCOL_VERSION" OPENFINGER_PROTOCOL_VERSION)
string(STRIP "${OPENFINGER_PROTOCOL_VERSION}" OPENFINGER_PROTOCOL_VERSION)
set(OPENFINGER_DRIVER_ABI_VERSION 1)

configure_file(
    "${CMAKE_CURRENT_SOURCE_DIR}/cmake/OpenFingerVersion.h.in"
    "${CMAKE_BINARY_DIR}/generated/openfinger/OpenFingerVersion.h"
    @ONLY
)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(OPENFINGER_OPENVR_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/third_party/openvr")
set(OPENFINGER_OPENVR_INCLUDE_DIR "${OPENFINGER_OPENVR_ROOT}/headers")
set(OPENFINGER_OPENVR_LIB "${OPENFINGER_OPENVR_ROOT}/lib/win64/openvr_api.lib")
get_filename_component(OPENFINGER_WINDOWS_KITS_ROOT "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Kits\\Installed Roots;KitsRoot10]" ABSOLUTE)
set(OPENFINGER_CPPWINRT_INCLUDE_DIR "${OPENFINGER_WINDOWS_KITS_ROOT}/Include/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/cppwinrt")
set(OPENFINGER_WINDOWSAPP_LIB "${OPENFINGER_WINDOWS_KITS_ROOT}/Lib/${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}/um/x64/WindowsApp.lib")

if(NOT EXISTS "${OPENFINGER_OPENVR_INCLUDE_DIR}/openvr_driver.h")
    message(FATAL_ERROR "Missing OpenVR headers under third_party/openvr/headers.")
endif()

add_library(openfinger_core STATIC
    src/OpenFinger.Core/common/AdcPacket.cpp
    src/OpenFinger.Core/common/AdcReceiver.cpp
    src/OpenFinger.Core/common/Config.cpp
    src/OpenFinger.Core/common/ControllerBridgeReceiver.cpp
    src/OpenFinger.Core/common/ControllerInputState.cpp
    src/OpenFinger.Core/common/FingerFilter.cpp
    src/OpenFinger.Core/common/RuntimeFrameReceiver.cpp
    src/OpenFinger.Core/common/RuntimeState.cpp
)

target_include_directories(openfinger_core
    PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/src/OpenFinger.Core"
        "${CMAKE_BINARY_DIR}/generated"
        "${OPENFINGER_OPENVR_INCLUDE_DIR}"
)

target_compile_definitions(openfinger_core
    PUBLIC
        _CRT_SECURE_NO_WARNINGS
        WIN32_LEAN_AND_MEAN
        NOMINMAX
)

target_link_libraries(openfinger_core
    PUBLIC
        ws2_32
)

add_executable(openfinger_adc_monitor
    src/OpenFinger.AdcMonitor/main.cpp
)

target_link_libraries(openfinger_adc_monitor
    PRIVATE
        openfinger_core
)

add_executable(openfinger_controller_bridge
    src/OpenFinger.ControllerBridge/main.cpp
)

target_link_libraries(openfinger_controller_bridge
    PRIVATE
        openfinger_core
)

add_custom_command(TARGET openfinger_controller_bridge POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/src/OpenFinger.ControllerBridge/resources/actions.json"
        "$<TARGET_FILE_DIR:openfinger_controller_bridge>/actions.json"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/src/OpenFinger.ControllerBridge/resources/bindings_oculus_touch.json"
        "$<TARGET_FILE_DIR:openfinger_controller_bridge>/bindings_oculus_touch.json"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/src/OpenFinger.ControllerBridge/resources/openfinger_bridge.vrmanifest"
        "$<TARGET_FILE_DIR:openfinger_controller_bridge>/openfinger_bridge.vrmanifest"
)

add_executable(openfinger_service
    src/OpenFinger.Service/main.cpp
    src/OpenFinger.Service/service/OpenFingerService.cpp
    src/OpenFinger.Service/service/SerialDevice.cpp
    src/OpenFinger.Service/service/ServiceProtocol.cpp
)

add_executable(openfinger_firmware_tool
    src/OpenFinger.FirmwareTool/main.cpp
    src/OpenFinger.Service/service/SerialDevice.cpp
    src/OpenFinger.Service/service/ServiceProtocol.cpp
)

target_include_directories(openfinger_service
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src/OpenFinger.Service"
)

target_include_directories(openfinger_firmware_tool
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src/OpenFinger.Service"
)

if(EXISTS "${OPENFINGER_CPPWINRT_INCLUDE_DIR}/winrt/base.h")
    target_include_directories(openfinger_service PRIVATE "${OPENFINGER_CPPWINRT_INCLUDE_DIR}")
endif()

target_link_libraries(openfinger_service
    PRIVATE
        openfinger_core
        advapi32
)

target_link_libraries(openfinger_firmware_tool
    PRIVATE
        openfinger_core
        advapi32
)

if(EXISTS "${OPENFINGER_WINDOWSAPP_LIB}")
    target_link_libraries(openfinger_service PRIVATE "${OPENFINGER_WINDOWSAPP_LIB}")
endif()

add_library(driver_openfinger SHARED
    src/OpenFinger.Driver/driver/DriverFactory.cpp
    src/OpenFinger.Driver/driver/DriverLog.cpp
    src/OpenFinger.Driver/driver/OpenFingerHandDevice.cpp
    src/OpenFinger.Driver/driver/OpenFingerServerProvider.cpp
    src/OpenFinger.Driver/driver/SkeletonPoseBuilder.cpp
)

target_include_directories(driver_openfinger
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src/OpenFinger.Driver"
)

target_link_libraries(driver_openfinger
    PRIVATE
        openfinger_core
)

if(EXISTS "${OPENFINGER_OPENVR_LIB}")
    target_link_libraries(driver_openfinger PRIVATE "${OPENFINGER_OPENVR_LIB}")
    target_link_libraries(openfinger_controller_bridge PRIVATE "${OPENFINGER_OPENVR_LIB}")
endif()

set(OPENFINGER_DRIVER_OUTPUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/src/drivers/openfinger/bin/win64")

set_target_properties(driver_openfinger PROPERTIES
    OUTPUT_NAME "driver_openfinger"
    RUNTIME_OUTPUT_DIRECTORY "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELWITHDEBINFO "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_MINSIZEREL "${OPENFINGER_DRIVER_OUTPUT_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)

if(MSVC)
    target_compile_options(openfinger_core PRIVATE /W4 /permissive- /FS /utf-8)
    target_compile_options(openfinger_adc_monitor PRIVATE /W4 /permissive- /FS /utf-8)
    target_compile_options(openfinger_controller_bridge PRIVATE /W4 /permissive- /FS /utf-8)
    target_compile_options(openfinger_service PRIVATE /W4 /permissive- /FS /utf-8)
    target_compile_options(openfinger_firmware_tool PRIVATE /W4 /permissive- /FS /utf-8)
    target_compile_options(driver_openfinger PRIVATE /W4 /permissive- /FS /utf-8)
else()
    target_compile_options(openfinger_core PRIVATE -Wall -Wextra -Wpedantic)
    target_compile_options(openfinger_adc_monitor PRIVATE -Wall -Wextra -Wpedantic)
    target_compile_options(openfinger_controller_bridge PRIVATE -Wall -Wextra -Wpedantic)
    target_compile_options(openfinger_service PRIVATE -Wall -Wextra -Wpedantic)
    target_compile_options(openfinger_firmware_tool PRIVATE -Wall -Wextra -Wpedantic)
    target_compile_options(driver_openfinger PRIVATE -Wall -Wextra -Wpedantic)
endif()
