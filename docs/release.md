# Release 打包与发布

OpenFinger 的 GitHub 分发包由 `x.py package` 生成，目标是让用户解压后能直接找到入口，而不是面对构建目录。

## 本地打包

```powershell
python x.py package --config Release --archive-format zip --distribution all --runtime-mode all
python x.py verify-package --config Release --archive-format zip --distribution all --runtime-mode all
```

默认会同时生成 4 种 Windows x64 分发形式：

- 便携版（依赖 .NET 运行库）
- 便携版（自包含）
- 安装器（依赖 .NET 运行库）
- 安装器（自包含）

产物布局：

```text
dist/
  OpenFinger-<version>-win-x64-dotnet/
  OpenFinger-<version>-win-x64-dotnet.zip
  OpenFinger-<version>-win-x64-self-contained/
  OpenFinger-<version>-win-x64-self-contained.zip
  OpenFinger-<version>-win-x64-dotnet.zip.sha256
  OpenFinger-<version>-win-x64-self-contained.zip.sha256
  OpenFingerSetup-<version>-win-x64-dotnet.exe
  OpenFingerSetup-<version>-win-x64-self-contained.exe
  OpenFingerSetup-<version>-win-x64-dotnet.exe.sha256
  OpenFingerSetup-<version>-win-x64-self-contained.exe.sha256
  OpenFinger-<version>-checksums.sha256
  release-notes.md
```

## 手动发布到 GitHub Draft Release

进入 GitHub Actions，运行 **Draft Release** workflow。

可选输入：

- `tag_name`：留空时使用 `v<VERSION>`。
- `prerelease`：是否标记为预发布。

workflow 会完成以下工作：

1. 在 Windows runner 上构建 Release。
2. 生成四种发行产物（两种便携版 + 两种安装器）。
3. 生成发布级总校验清单，以及每个发行文件各自的 `.sha256` 校验文件。
4. 创建或复用 tag。
5. 创建 GitHub Release 草稿。

发布结果一定是 draft，不会自动公开。

## CI 验证

`CI` workflow 会在 push、pull request 和手动触发时运行：

1. 构建 C++ 组件。
2. 构建 WPF 控制端。
3. 生成四种 Windows x64 分发产物。
4. 校验分发包布局和 release checksum。
5. 上传 CI artifact。

这能避免 release 前才发现包里缺 exe、driver manifest 或固件资源。
