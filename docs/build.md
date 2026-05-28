# 构建

OpenFinger 当前按 Windows x64 开发。C++ 部分使用 CMake，GUI 使用 .NET/WPF，固件使用 PlatformIO。

## 环境

建议环境：

- Windows 11 x64
- Visual Studio 2022，带 MSVC 和 Windows SDK
- CMake 3.21 或更新
- Python 3.10 或更新
- .NET SDK，能构建 `net10.0-windows10.0.26100.0`
- PlatformIO，用于固件
- SteamVR，用于 driver 运行测试

OpenVR 头文件和库需要位于：

```text
third_party/openvr/headers
third_party/openvr/lib/win64/openvr_api.lib
```

缺这个时 CMake 会直接报错。不要在 driver 代码里绕过这个检查。

## 常用命令

完整构建：

```powershell
python x.py build --config Debug
```

启动 GUI：

```powershell
python x.py run --config Debug
```

`run` 会构建 C++、GUI、两个固件目标，然后启动 `OpenFinger.Control.exe`。固件只构建，不会自动刷写。

发布包：

```powershell
python x.py package --config Release
```

输出在：

```text
dist/OpenFinger-<version>-win-x64/
dist/OpenFinger-<version>-win-x64.tar.gz
```

## 单组件构建

```powershell
python x.py gui --config Debug
python x.py service --config Debug
python x.py bridge --config Debug
python x.py monitor --config Debug
python x.py driver --config Release
```

Driver 改动后建议加上：

```powershell
python x.py driver --config Release --stop-steamvr
```

SteamVR 可能锁住 driver DLL。不停进程时，构建成功不代表新 DLL 已被加载。

## 固件

构建默认板子：

```powershell
python x.py firmware --board esp32c3
```

构建全部板子：

```powershell
python x.py firmware --board all
```

刷写：

```powershell
python x.py firmware --board esp32c3 --flash --port COM5
```

板子名称由 `x.py` 维护。当前目标：

| 参数 | PlatformIO 环境 |
|---|---|
| `esp32c3` | `esp32-c3-dev-module` |
| `esp32s3` | `esp32-s3-devkitc-1` |

## 版本生成

```powershell
python x.py version
```

会生成或更新：

```text
src/OpenFinger.Control/Generated/OpenFingerVersion.g.cs
src/firmware/common/openfinger_version.h
build/generated/openfinger/OpenFingerVersion.h
```

构建命令会自动调用这个步骤。手动改 `VERSION` 或 `PROTOCOL_VERSION` 后，单独跑一次也可以。

## 清理

```powershell
python x.py clean
```

会删除：

```text
build/
dist/
src/OpenFinger.Control/bin/
src/OpenFinger.Control/obj/
src/firmware/*/.pio/
```

它不会删除 SteamVR 已注册的 driver。driver 注册状态由 GUI 的 SteamVR 页面处理。

## 常见问题

### CMake 找不到 OpenVR

检查：

```text
third_party/openvr/headers/openvr_driver.h
third_party/openvr/lib/win64/openvr_api.lib
```

路径不对就补依赖。不要把本机 SteamVR 安装目录里的文件随便复制进源码树，版本会变得不可控。

### WPF 编译时出现 `System.Windows.Forms` 和 WPF 类型冲突

项目启用了 Windows Forms 兼容时，`TextBox`、`KeyEventArgs`、`Color` 这类名字容易撞。

在 GUI 代码里写全限定名或使用别名：

```csharp
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
```

别靠 `using` 顺序赌编译器解析。

### Driver 编译成功但 SteamVR 没变化

常见原因：

- SteamVR 还在运行，旧 DLL 被占用
- driver 包没有重新注册
- SteamVR 缓存了旧 manifest

处理方式：关闭 SteamVR，重新构建 driver，在 GUI 里修复/重新安装 driver，再启动 SteamVR。

### 固件刷写失败

检查顺序：

- 端口是不是正确的 `COMx`
- 板子是否进入下载模式
- PlatformIO 环境是否能单独构建
- USB 线是否支持数据

ESP32-C3 板子有时需要按住 `BOOT`，轻按一次 `RESET`，再松开 `BOOT`。

## 不推荐

- 直接双击中间产物测试 driver
- 把 `build/` 下的 DLL 手动复制到多个地方
- 提交 `.pio/`、`.vs/`、`bin/`、`obj/`
- 修改 CMake 输出路径但不改打包逻辑

构建脚本已经承担了路径约定。绕过它可以，但别把绕过后的状态提交进仓库。
