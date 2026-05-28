# 版本

OpenFinger 使用两个版本号。

```text
VERSION
PROTOCOL_VERSION
```

`VERSION` 是产品版本。`PROTOCOL_VERSION` 是设备和本机运行时协议版本。

## 生成文件

运行：

```powershell
python x.py version
```

会更新：

```text
src/OpenFinger.Control/Generated/OpenFingerVersion.g.cs
src/firmware/common/openfinger_version.h
build/generated/openfinger/OpenFingerVersion.h
```

如果存在 SteamVR manifest，也会同步写入版本字段：

```text
src/drivers/openfinger/driver.vrdrivermanifest
src/OpenFinger.ControllerBridge/resources/openfinger_bridge.vrmanifest
```

这些文件由脚本生成或维护，不要手工改一半。

## 产品版本

`VERSION` 使用三段式版本：

```text
MAJOR.MINOR.PATCH
```

规则：

| 变更 | 版本 |
|---|---|
| 破坏协议、driver ABI、配置迁移 | `MAJOR` |
| 新功能，保持兼容 | `MINOR` |
| 修复、样式调整、打包修正、文档 | `PATCH` |

项目还在早期，`0.x` 阶段可以更频繁地调整接口。即使如此，发布包内部组件也应保持同一版本。

## 协议版本

`PROTOCOL_VERSION` 使用整数。

需要增加协议版本的情况：

- `OFADC` 字段顺序或含义改变
- `OFRUNTIME` 字段顺序或含义改变
- 必需的 `OFSTATUS` 字段改变
- Service 和 Driver 的运行时假设不兼容
- GUI 的设备兼容性判断需要新规则

不一定需要增加协议版本的情况：

- 末尾追加可选字段
- 新增可选串口命令
- 新增 GUI 本地配置项
- 只修复解析 bug，输入输出保持兼容

## 固件包

GUI 可能携带预构建固件包：

```text
src/OpenFinger.Control/FirmwarePackages/
```

manifest 里的版本必须和实际 `.bin` 里编译出来的版本一致。

别只改 manifest。这样 GUI 看起来版本对了，设备实际跑的还是旧固件，后面排查会很麻烦。

推荐流程：

```powershell
python x.py version
python x.py firmware --board all
```

然后重新生成或替换固件包。

## 发布包

发布包应该包含：

```text
OpenFinger.Control/
openfinger_service.exe
openfinger_controller_bridge.exe
openfinger_adc_monitor.exe
openfinger_firmware_tool.exe
drivers/openfinger/
VERSION
PROTOCOL_VERSION
README.txt
```

Service、Bridge、Driver、GUI 不要混用不同发布包里的文件。协议内部兼容不是给这种用法准备的。
