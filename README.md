<p align="center">
  <img src="OpenFingerBanner.png" alt="OpenFinger" width="360">
</p>

---

<h3 align="center">全程零3D打印，零额外定位器，轻量手指追踪手套</h3>

<p align="center">
<img alt="版本" src="https://img.shields.io/github/v/release/TheD0ubleC/OpenFinger?display_name=tag&style=flat-square&label=%E7%89%88%E6%9C%AC">
  <img alt="OpenFingerKit协议" src="https://img.shields.io/badge/%E5%8D%8F%E8%AE%AE-v2-6b8cff?style=flat-square">
  <img alt="平台" src="https://img.shields.io/badge/%E5%B9%B3%E5%8F%B0-Windows%20x64-555555?style=flat-square">
  <img alt="SteamVR" src="https://img.shields.io/badge/SteamVR-driver-2f2f2f?style=flat-square">
  <img alt="固件" src="https://img.shields.io/badge/%E5%9B%BA%E4%BB%B6-ESP32--C3%20%2F%20ESP32--S3-4f8a8b?style=flat-square">
  <img alt="许可证" src="https://img.shields.io/badge/license-MIT-22c55e?style=flat-square">

</p>

## OpenFinger

OpenFinger 是一套 SteamVR 手指追踪手套实现。

目标很简单：

- 低成本 (最低 40 人民币每只手)
- 不依赖额外定位器
- 不依赖摄像头
- 不需要 3D 打印
- 能直接进 SteamVR skeleton

## 状态

| 项目      | 当前值                                                                                                                                           |
| --------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| 产品版本  | <img alt="版本" src="https://img.shields.io/github/v/release/TheD0ubleC/OpenFinger?display_name=tag&style=flat-square&label=%E7%89%88%E6%9C%AC"> |
| 协议版本  | `v2`                                                                                                                                             |
| 主要平台  | Windows x64                                                                                                                                      |
| GUI       | WPF / .NET 10                                                                                                                                    |
| 固件目标  | ESP32-C3、ESP32-S3                                                                                                                               |
| VR 运行时 | SteamVR                                                                                                                                          |

## 它解决什么

普通 VR 控制器没有连续的五指弯曲数据。而拥有五指数据的控制器通常价格不菲，且需要额外的定位器或摄像头。包括很多 DIY 方案的价格与复杂度都不太友好。

OpenFinger 只处理这条链路：

```text
手套传感器 -> ESP32 固件 -> 本地服务 -> SteamVR driver -> skeleton hand
```

范围故意收窄。

| 做                             | 不做                 |
| ------------------------------ | -------------------- |
| 采集手指 ADC / 摇杆 / 电池状态 | 摄像头手势识别       |
| 写入设备配置和刷写固件         | 独立 6DoF 定位       |
| 生成 SteamVR skeleton 输入     | 全身追踪             |
| 桥接真实控制器输入             | 通用手势 SDK         |
| 提供 GUI 校准和状态检查        | 商用即插即用外设体验 |

## 工作方式

```text
ESP32 firmware
  OFADC UDP :39001
        |
        v
openfinger_service.exe
  设备发现、数据过滤、运行时状态
        |
        | OFRUNTIME UDP 127.0.0.1:39003
        v
SteamVR driver_openfinger.dll
  提交左右手 skeleton

真实控制器
        |
        v
openfinger_controller_bridge.exe
  按钮、摇杆、扳机桥接
        |
        | controller UDP :39002
        v
SteamVR driver_openfinger.dll
```

GUI 不在实时链路里。它负责配置、刷写、安装 driver、校准和排错。

## 目录

```text
src/
  OpenFinger.Core/              共享协议、配置、滤波、运行时帧
  OpenFinger.Service/           本地服务
  OpenFinger.Driver/            SteamVR driver 源码
  OpenFinger.ControllerBridge/  控制器输入桥接
  OpenFinger.Control/           WPF 控制台
  OpenFinger.FirmwareTool/      固件刷写命令行工具
  OpenFinger.AdcMonitor/        ADC 调试工具
  firmware/                     ESP32 固件
  drivers/                      SteamVR driver 包结构

docs/                           架构、构建、协议、校准说明
VERSION                         产品版本
PROTOCOL_VERSION                协议版本
x.py                            构建入口
```

`src/OpenFinger.*` 是 PC 端组件。`src/firmware` 是板子上的代码。`src/drivers` 是 SteamVR 读取的 driver 包结构，不是普通源码分层。

## 构建

Windows x64 是主开发环境。需要 CMake、Ninja、.NET SDK、可用的 C++ 编译工具链。固件需要 PlatformIO。

```powershell
python x.py build --config Debug
python x.py run --config Debug
python x.py package --config Release
```

单独构建组件：

```powershell
python x.py gui --config Debug
python x.py service --config Debug
python x.py driver --config Release
python x.py firmware --board esp32c3
```

刷写固件：

```powershell
python x.py firmware --board esp32c3 --flash --port COM5
```

构建细节放在 [`docs/build.md`](docs/build.md)。

## 配置和校准

GUI 里有三类配置：

- 设备连接参数：Wi-Fi、主机 IP、UDP 端口、默认手型
- 手指输入校准：ADC 范围、方向、死区、滤波
- 虚拟控制器 6DoF 偏移：左右手各自的位置和旋转补偿

6DoF 偏移只修装配误差。不要拿它修追踪漂移，也不要用很大的旋转值硬掰手型。偏移看起来对，不代表抓握交互会对。

校准细节见 [`docs/calibration.md`](docs/calibration.md)。

## 协议和版本

仓库根目录有两个版本文件：

```text
VERSION
PROTOCOL_VERSION
```

`python x.py version` 会同步生成 C++、C#、固件侧需要的版本文件。

协议变动要同时改：

```text
PROTOCOL_VERSION
docs/protocol.md
src/OpenFinger.Core/ 中对应的解析/序列化代码
src/firmware/common/ 中对应的包格式
```

别只改 GUI。GUI 显示成功不代表 driver 和固件能互相读包。

## 文档

| 文件                                                 | 内容                             |
| ---------------------------------------------------- | -------------------------------- |
| [`docs/architecture.md`](docs/architecture.md)       | 组件边界、数据流、取舍           |
| [`docs/build.md`](docs/build.md)                     | 构建、打包、固件刷写             |
| [`docs/protocol.md`](docs/protocol.md)               | 串口命令、UDP 包、版本规则       |
| [`docs/calibration.md`](docs/calibration.md)         | 手指校准、6DoF 偏移              |
| [`docs/troubleshooting.md`](docs/troubleshooting.md) | SteamVR、串口、固件、driver 排查 |
| [`docs/versioning.md`](docs/versioning.md)           | 产品版本、协议版本、固件包版本   |
| [`docs/cleanup.md`](docs/cleanup.md)                 | 目录清理记录和边界               |

## 不适合的用法

- 没有真实 VR 控制器，只靠手套做完整输入
- 需要稳定量产外设
- 希望零校准、零调参
- 把 UDP 端口暴露到公网
- 在 SteamVR 正在运行时反复覆盖 driver 文件
- 硬件固定方式还没定，就开始调大量软件参数

硬件装配会直接影响软件效果。线材、传感器固定、手套松紧、Wi-Fi 状态都会进结果里。

## 常见坑

- SteamVR 没读到 driver：检查 `src/drivers/openfinger` 是否被注册，SteamVR 是否缓存了旧路径。
- GUI 能连接，VR 里没有手：检查 `openfinger_service.exe`、`openfinger_controller_bridge.exe`、UDP 端口和 driver 日志。
- 刷写后版本不对：重新生成固件包，不要只改 manifest。
- 手指方向反了：先看校准方向，不要在 driver 里写死反向。
- 深色模式颜色不对：检查是否还有硬编码 `#FFF...`、`White`、`Black`。

更多排查见 [`docs/troubleshooting.md`](docs/troubleshooting.md)。

## 提交前检查

```powershell
python x.py version
python x.py build --config Debug
python x.py package --config Release
```

不要提交这些目录：

```text
build/
dist/
.pio/
.vs/
src/drivers/openfinger/bin/
```

涉及协议、目录、固件包的改动，需要同步文档。小项目也需要这个习惯，不然后面排查会很烦。
