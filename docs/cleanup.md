# 清理记录

这个仓库经历过原型阶段。清理的目标不是把历史抹掉，而是把当前维护边界固定下来。

## 已移除或替换

- 移除 `legacy/temp_python`
- 移除根目录 `MainWindow.xaml` 占位文件
- 移除 `src/OpenFinger.Control/.vs` IDE 缓存
- 移除 `src/drivers/openfinger/bin` 下误提交的 driver 二进制
- 移除旧 Python 原型 GUI 使用的 `requirements.txt`
- 移除旧 `OpenFingerGUI/Core.cs` 项目引用
- 将 `OpenFingerGUI` 命名空间迁移到 `OpenFinger.Control`
- 重写 `x.py`，匹配当前目录结构
- 增加统一的 `VERSION` 和 `PROTOCOL_VERSION`

## 当前规则

生成产物不进源码树：

```text
build/
dist/
.pio/
.vs/
bin/
obj/
```

SteamVR driver 的包结构可以保留，但编译出的 DLL 和 PDB 不应该作为普通源码提交。发布时走 `python x.py package --config Release`。

## 目录边界

```text
src/OpenFinger.*     PC 端组件
src/firmware         ESP32 固件
src/drivers          SteamVR driver 包结构
docs                 维护文档
```

不要重新引入 `apps/`、`service/`、`driver/` 这类根目录源码入口。源码入口越多，脚本和文档越容易漂。

## 可能还需要做的事

- 确认固件包 manifest 和 `.bin` 版本一致
- 给 driver 安装/修复流程补自动化测试或检查脚本
- 把协议字段解析测试补到 Core 层
- 把 GUI 配置迁移逻辑写成可测试代码
- 清理仅靠 UI 手测才能发现的问题

这些不是一次清理能完成的。后面改到相关模块时顺手补。
