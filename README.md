# SimFlow

SimFlow 是面向个人 CAE 仿真工程师的 Windows 原生项目管理工具，用于管理项目状态、版本、文件位置、迁移、归档和历史记录。

## 当前版本

- 当前版本：`1.0.8.0`
- 平台：Windows 10 2004+ / Windows 11，x64
- 技术栈：.NET 10、WinUI 3、Windows App SDK 2.4、SQLite
- 数据库结构：Schema 3
- 状态：源码和数据保留验收已完成；GitHub Releases 提供免安装便携版

## 主要能力

- 项目创建、编辑、搜索、筛选、收藏和看板管理
- 未开始、进行中、等待中、已完成状态及历史追踪
- 项目版本、标签、软件标签、封面和仿真报告模板
- 本机 Work、工作站 Work、工作站 Archive 三类存储位置
- SHA-256 校验的安全迁移、失败恢复和冲突处理
- SQLite 全局索引与项目内 `project.json` 双重档案
- 统计分析、等待原因和软件使用统计

## 数据安全约束

- 项目和版本的“删除文件夹”会移动到 `.simflow-recovery`，不会立即永久删除。
- 迁移提升目标前会重新比对当前源目录与目标副本。
- 归档默认在目标完成校验并成为主副本后删除原 Work 目录；可在设置中改为保留源副本。
- 删除前会再次核验项目身份、主路径及源/目标内容；删除失败时保留源目录并进入待清理状态。
- 存储位置离线时禁止修改项目状态，避免数据库与 `project.json` 不一致。
- 数据库、配置和日志位于 `%LOCALAPPDATA%\SimFlow`；项目文件位于用户配置的 Work 目录，均不放在安装目录内。

## 从源码构建

需要 Windows、Visual Studio/Build Tools 的 Windows 应用开发组件，以及 `global.json` 指定的 .NET SDK。

```powershell
dotnet restore SimFlow.slnx --locked-mode
dotnet test Tests\SimFlow.Tests\SimFlow.Tests.csproj -c Release --no-restore
dotnet build SimFlow.csproj -c Release -p:Platform=x64 --no-restore
```

## 文档

- [安装、升级与卸载](docs/INSTALLATION.md)
- [已知问题](docs/KNOWN_ISSUES.md)
- [发布检查清单](docs/RELEASE_CHECKLIST.md)
- [1.0.5.0 发布说明与验证记录](docs/RELEASE_NOTES_1.0.5.0.md)
- [1.0.6.0 发布说明](docs/RELEASE_NOTES_1.0.6.0.md)
- [1.0.7.0 发布说明](docs/RELEASE_NOTES_1.0.7.0.md)
- [1.0.8.0 发布说明](docs/RELEASE_NOTES_1.0.8.0.md)
- [发布与签名策略](docs/DISTRIBUTION_AND_SIGNING.md)
- [代码签名政策](docs/CODE_SIGNING_POLICY.md)
- [隐私政策](PRIVACY.md)
- [产品需求与技术设计](docs/SimFlow%20个人版产品需求与技术设计文档.md)
- [项目目录结构规范](docs/SimFlow%20项目目录结构规范.md)
- [项目规划与进度](docs/SimFlow%20项目规划与进度.md)
- [安全策略](SECURITY.md)
- [变更记录](CHANGELOG.md)

本公开仓库从 `1.0.5.0` 的审查后快照开始，不包含发布前的内部开发历史和内部审查材料。

## 分发说明

官方二进制通过 [GitHub Releases](https://github.com/Morick66/SimFlow/releases) 发布。当前提供免安装的 Windows x64 便携版 ZIP，不依赖 Microsoft Store，也不要求导入证书；下载后请先核对同一 Release 中的 `SHA256SUMS.txt`。

当前便携版尚未使用商业代码签名证书，因此 Windows 首次运行时可能显示“未知发布者”或 Microsoft Defender SmartScreen 提示。请只从本仓库的 Releases 页面下载。先前使用个人身份自签证书的 `1.0.5.0` MSIX 已撤回，不应继续分发或安装。

项目采用 [MIT License](LICENSE) 开源。
