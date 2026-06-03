# 排查

这份文档只写常见故障。更细的内部状态看 GUI 的状态页和日志。

## 设备连不上

检查：

```text
USB 线是否支持数据
设备是否出现在串口列表
固件是否能响应 OFSTATUS
GUI 里左右手角色是否正确
Wi-Fi 配置是否写入
```

如果串口能看到设备，但 UDP 没数据，通常是 Wi-Fi、主机 IP 或端口配置问题。

## 只能识别一只手

允许只连接一只手。先确认这不是正常半工作状态。

需要检查：

- 两块板子的 `role` 是否重复
- 两块板子的 IP 是否都在同一网络
- Service 是否收到两个不同 MAC 的 `OFADC`
- GUI 是否把旧设备缓存当成当前设备

角色重复时，后到的数据可能覆盖前一个状态。

## SteamVR 没有手

按这个顺序查：

```text
Service 是否运行
Driver 是否安装
SteamVR 是否加载 openfinger driver
OpenFinger.Control 是否正在运行，并向 127.0.0.1:39003 输出 OFRUNTIME
ControllerBridge 是否运行
```

Driver 问题不要先改固件。固件只负责输入，SteamVR 里有没有设备由 driver 决定。

## 按钮或摇杆没反应

这通常是 ControllerBridge 问题，不是手套问题。

检查：

- SteamVR 是否能看到真实控制器
- Bridge 是否启动
- Bridge manifest 是否注册
- Driver 是否收到 controller UDP

OpenFinger 的固件不提供真实控制器按钮。

## 手指数据很抖

常见原因：

- 传感器固定不牢
- 线材拉扯
- ADC 范围太窄
- Wi-Fi 丢包
- 滤波参数太轻

先看原始 ADC。原始数据抖，后面只能缓解，不能根治。

## 固件刷写后版本不对

可能是刷错板子、刷错端口，或固件包 manifest 和 `.bin` 不一致。

处理：

```powershell
python x.py version
python x.py firmware --board esp32c3 --flash --port COM5
```

刷完重新读 `OFSTATUS`，不要只看 GUI 里的包名。

## 深色主题颜色异常

如果某个页面还是浅色，通常是 XAML 里还有硬编码颜色。

应该改成主题资源，不要在页面里直接写：

```xml
Background="#FFFFFF"
Foreground="#111827"
```

优先使用项目已有的 `Brush*` 资源。
