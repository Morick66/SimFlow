# SimFlow 1.0.11.0 发布说明

本版修复“所有项目”筛选区中，项目状态按钮选中时顶部和底部轮廓被裁切的问题。按钮排列格和上下留白已调整，筛选逻辑未改变。

## 下载和升级

- `SimFlow-1.0.11.0-win-x64-portable.zip`：Windows 10 2004+ / Windows 11 x64 免安装便携版；
- `SHA256SUMS.txt`：下载包的 SHA-256 校验值。

请从本仓库的 GitHub Releases 下载两个文件并核对哈希。关闭旧版后，将 ZIP 完整解压到新目录运行 `SimFlow.exe`；不要覆盖正在运行的旧目录。升级前建议备份 `%LOCALAPPDATA%\SimFlow` 和项目 Work 目录。回滚与卸载方式见 [安装、升级与卸载](INSTALLATION.md)。

## 验收状态

Windows CI 检查自动化测试、依赖漏洞与便携版构建。由于开发环境为 Mac，按钮在 Windows 不同缩放比例下的实际显示仍需实机确认。

当前便携版没有商业代码签名，首次运行可能显示“未知发布者”或 Microsoft Defender SmartScreen 提示。请先确认下载来源和 SHA-256；本版不需要安装证书。
