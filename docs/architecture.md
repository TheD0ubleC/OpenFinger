# 架构

OpenFinger 的核心链路是采集、聚合、桥接、提交。每段之间用简单协议连接，方便单独替换和调试。

## 问题

手套侧只能给出原始输入：ADC、摇杆、电池、电量状态、角色配置。SteamVR 需要的是本机 driver 可以消费的 skeleton pose。

中间不能直接把固件数据塞给 driver。原因很现实：

- driver 运行在 SteamVR 进程内，调试不方便
- 串口设备发现、Wi-Fi 配网、固件版本检查不应该进 driver
- GUI 需要状态和控制接口，但不应该直接控制 driver 内部状态
- 真实控制器的按钮和轴来自 OpenVR，不来自手套固件

所以 OpenFinger 把这些职责拆开。

## 组件

| 组件 | 路径 | 职责 |
|---|---|---|
| 固件 | `src/firmware/esp32c3`, `src/firmware/esp32s3` | 采集 ADC、摇杆、电池状态；提供串口配置；通过 UDP 上报实时数据 |
| Core | `src/OpenFinger.Core` | 共享协议、数据结构、滤波和运行时帧处理 |
| Service | `src/OpenFinger.Service` | 设备发现、串口读写、接收 `OFADC`、聚合左右手状态、提供 GUI RPC |
| Driver | `src/OpenFinger.Driver`, `src/drivers/openfinger` | SteamVR driver，创建左右手 skeleton，消费本机运行时帧 |
| ControllerBridge | `src/OpenFinger.ControllerBridge` | 读取真实控制器按钮、摇杆、扳机，转发给 driver |
| Control | `src/OpenFinger.Control` | 配置、刷写、校准、状态页、SteamVR driver 管理 |
| FirmwareTool | `src/OpenFinger.FirmwareTool` | 命令行固件工具，给 GUI 之外的维护场景使用 |
| AdcMonitor | `src/OpenFinger.AdcMonitor` | 开发调试用 ADC 观察工具 |

## 数据流

```text
ESP32
  USB Serial
    OFHELLO / OFSTATUS / OFINFO / OFVERSION / OFIDENT / OFRESET
    OFPROV / OFADC_CFG

  UDP 39001
    OFADC

openfinger_service.exe
  UDP in
    OFADC from ESP32

  Named Pipe
    GUI RPC

  UDP out 127.0.0.1:39003
    OFRUNTIME

openfinger_controller_bridge.exe
  OpenVR input
  UDP out 127.0.0.1:39002
    controller state

driver_openfinger.dll
  OFRUNTIME + controller state
  SteamVR skeleton
```

## 设计

### Service 不做 SteamVR 逻辑

Service 只关心设备和运行时状态。它不直接调用 OpenVR，也不提交骨骼。这样可以在不启动 SteamVR 的情况下测试设备连接、串口配置和 UDP 数据。

### Driver 不碰设备发现

Driver 只消费本机运行时流。串口、Wi-Fi 配置、固件版本检查都留给 Service 和 GUI。

这会多一个进程，但能减少 driver 内部状态。SteamVR driver 一旦崩，定位问题比普通进程麻烦得多。

### ControllerBridge 独立出来

手套只提供手指弯曲，不提供真实控制器按钮。ControllerBridge 从 OpenVR 读取现有控制器输入，再交给 driver 合成。

这样用户仍然可以用原控制器移动、点击、抓取。OpenFinger 负责手指 skeleton。

### GUI 不当核心依赖

GUI 可以退出，Service 和 driver 仍应继续工作。GUI 是控制台，不是运行时必需组件。

关闭窗口时最小化到托盘，符合这个模型。直接退出 GUI 不应该停止正在运行的本地会话，除非用户明确选择停止。

## 取舍

- 进程数量比单体程序多
- 本机 UDP 包需要版本兼容
- 打包时要带齐 GUI、service、bridge、driver 和资源文件
- 端口冲突会导致表现很怪，需要诊断页暴露状态

换来的东西是边界清楚。串口问题、Wi-Fi 问题、driver 问题、控制器桥接问题可以分开查。

## 所有权规则

- `src/OpenFinger.Core` 只能放跨组件共享代码
- `src/OpenFinger.Service` 不依赖 WPF
- `src/OpenFinger.Driver` 不依赖 GUI，也不直接读设备串口
- `src/OpenFinger.Control` 不直接写 driver 内部文件，安装/修复走明确路径
- `src/firmware/common` 放固件共享头，例如版本和协议号
- `src/drivers/openfinger` 保持 SteamVR driver 包结构

别把“顺手能调用”的代码放进 Core。Core 变胖之后，依赖会很快倒过来。

## 边界条件

- 两只手可以只连接一只。状态页必须允许半工作状态。
- 固件上报的角色可能和 GUI 选择不一致。写配置后要重新读状态确认。
- Driver 可能在 SteamVR 缓存旧包。修复 driver 后通常需要重启 SteamVR。
- 6DoF 偏移属于运行时校准，不应该写进固件。
- `OFRUNTIME` 是本机内部协议。Service 和 driver 应当来自同一个发布包。

## 备注

当前架构偏开发友好，不是最省资源的结构。等协议稳定后，可以考虑减少进程或改 IPC，但现在不急。
