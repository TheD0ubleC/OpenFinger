# Release 打包与发布

OpenFinger 的 GitHub 分发包由 `x.py package` 生成，目标是让用户解压后能直接找到入口，而不是面对构建目录。

## 本地打包

```powershell
python x.py package --config Release --archive-format zip
python x.py verify-package --config Release
```

默认产物：

```text
dist/
  OpenFinger-<version>-win-x64/
    bin/
      OpenFinger.Control.exe
      openfinger_service.exe
      openfinger_controller_bridge.exe
      openfinger_adc_monitor.exe
      openfinger_firmware_tool.exe
      FirmwarePackages/
      FirmwareTools/
    drivers/openfinger/
    docs/
    README.txt
    VERSION
    PROTOCOL_VERSION
    package-manifest.json
    checksums.sha256
  OpenFinger-<version>-win-x64.zip
  release-notes.md
```

## 手动发布到 GitHub Draft Release

进入 GitHub Actions，运行 **Draft Release** workflow。

可选输入：

- `tag_name`：留空时使用 `v<VERSION>`。
- `prerelease`：是否标记为预发布。

workflow 会完成以下工作：

1. 在 Windows runner 上构建 Release。
2. 生成 `OpenFinger-<version>-win-x64.zip`。
3. 校验 package manifest 和 SHA-256。
4. 创建或复用 tag。
5. 创建 GitHub Release 草稿。

发布结果一定是 draft，不会自动公开。

## CI 验证

`CI` workflow 会在 push、pull request 和手动触发时运行：

1. 构建 C++ 组件。
2. 构建 WPF 控制端。
3. 生成 zip 分发包。
4. 校验分发包布局和 checksum。
5. 上传 CI artifact。

这能避免 release 前才发现包里缺 exe、driver manifest 或固件资源。
