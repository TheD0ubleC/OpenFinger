# 协议

OpenFinger 协议分两层：设备协议和本机运行时协议。

设备协议给固件和 Service 使用。本机运行时协议给 Service、ControllerBridge 和 Driver 使用。两者不要混在一起。

## 版本

协议版本存放在：

```text
PROTOCOL_VERSION
```

当前版本：

```text
2
```

协议号只在不兼容变更时增加。新增可选字段、追加字段、兼容旧解析时，不一定需要增加协议号。改字段顺序或字段含义时必须增加。

## USB Serial

串口用于低频控制，不用于实时手指数据。

Host -> Device：

```text
OFHELLO
OFSTATUS
OFINFO
OFVERSION
OFIDENT
OFRESET
OFPROV ssid=...&password=...&save=1&host_ip=192.168.1.2&udp_port=39001&adc_mask=31&role=left
OFADC_CFG host_ip=192.168.1.2&udp_port=39001&adc_mask=31&role=left&report_hz=30&thumb_pin=0&index_pin=1&middle_pin=2&ring_pin=3&pinky_pin=4
```

Device -> Host：

```text
OFSTATUS {json}
```

常用字段：

```json
{
  "device": "OpenFinger",
  "mac": "AA:BB:CC:DD:EE:FF",
  "sta_ip": "192.168.1.50",
  "role": "left",
  "board_target": "esp32c3",
  "firmware_version": "0.1.0",
  "protocol_version": "2",
  "report_hz": 30,
  "thumb_pin": 0,
  "index_pin": 1,
  "middle_pin": 2,
  "ring_pin": 3,
  "pinky_pin": 4
}
```

`OFPROV` 和 `OFADC_CFG` 使用 query-string 风格参数。不要在 SSID 或密码里依赖复杂转义。需要复杂字符时，先在固件和 Service 两端确认解析逻辑。

## 设备 UDP：OFADC

默认端口：

```text
39001
```

格式：

```text
OFADC,seq,device_ms,mask,thumb,index,middle,ring,pinky,tracking_enabled,joystick_raw_x,joystick_raw_y,joystick_sw
```

字段含义：

| 字段 | 说明 |
|---|---|
| `seq` | 固件侧递增序号 |
| `device_ms` | 固件侧毫秒时间 |
| `mask` | 有效 ADC 通道位图 |
| `thumb`..`pinky` | 五指原始 ADC 值 |
| `tracking_enabled` | 固件侧是否允许上报追踪 |
| `joystick_raw_x` / `joystick_raw_y` | 摇杆原始值 |
| `joystick_sw` | 摇杆按键 |

UDP 包不保证到达，也不保证顺序。Service 需要按序号和时间戳处理丢包，不要假设每一帧都存在。

## 本机运行时 UDP：OFRUNTIME

默认地址：

```text
127.0.0.1:39003
```

方向：

```text
openfinger_service.exe -> driver_openfinger.dll
```

`OFRUNTIME` 是内部协议。它面向同一发布包内的 Control、Driver 和 Bridge，不承诺跨版本兼容。

当前实现需要兼容旧字段数量和新字段数量。新增虚拟控制器 6DoF 偏移时，只允许在末尾追加字段。不要在中间插入字段。

6DoF 偏移属于运行时数据，不写入固件：

```text
left_offset.position  x/y/z，单位 m
left_offset.rotation  pitch/yaw/roll，单位 degree
right_offset.position x/y/z，单位 m
right_offset.rotation pitch/yaw/roll，单位 degree
```

Driver 侧应用顺序：

```text
真实控制器位姿
  + 本地空间位置偏移
  + 旋转偏移
  -> 虚拟手位姿
```

偏移应当很小。它用于修正装配误差，不用于重建追踪。

## ControllerBridge UDP

默认端口：

```text
39002
```

方向：

```text
openfinger_controller_bridge.exe -> driver_openfinger.dll
```

这条流转发真实控制器按钮、摇杆、扳机等输入。OpenFinger 的手套数据只负责手指弯曲，不能替代这些输入。

## 取舍

协议使用文本格式，原因是调试方便。串口日志、UDP 抓包、手写测试都简单。

代价：

- 字段越多，解析越脆
- 数字格式要统一
- 性能不如二进制协议
- 追加字段需要保持旧解析兼容

现在数据量很小，文本格式足够。等协议稳定后再考虑二进制，不要提前加复杂度。

## 变更规则

允许：

- 末尾追加字段
- 添加可选 JSON 字段
- 添加新命令
- 旧字段保持原含义

不允许直接做：

- 改已有字段顺序
- 改已有字段单位
- 删除旧字段
- 让旧 Service 无法识别基础状态

需要破坏兼容时：

- 增加 `PROTOCOL_VERSION`
- 更新 `docs/protocol.md`
- 更新 GUI 的兼容性检查
- 更新固件包 manifest
