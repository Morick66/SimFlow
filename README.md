# SimFlow

SimFlow 是面向个人 CAE 仿真工程师的 Windows 原生项目管理工具，用于管理项目状态、版本、文件位置、迁移、归档和历史记录。

## 当前版本

- 源码里程碑：`1.0.5.0`
- 平台：Windows 10 2004+ / Windows 11，x64
- 技术栈：.NET 10、WinUI 3、Windows App SDK 2.4、SQLite
- 数据库结构：Schema 3
- 状态：源码和数据保留验收已完成；公开二进制正在更换发布身份与签名方案

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
- 首发版不会自动永久删除迁移后的源副本；请在停止求解器并人工核对后自行备份或清理。
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
- [产品需求与技术设计](docs/SimFlow%20个人版产品需求与技术设计文档.md)
- [项目目录结构规范](docs/SimFlow%20项目目录结构规范.md)
- [项目规划与进度](docs/SimFlow%20项目规划与进度.md)
- [安全策略](SECURITY.md)
- [变更记录](CHANGELOG.md)

本公开仓库从 `1.0.5.0` 的审查后快照开始，不包含发布前的内部开发历史和内部审查材料。

## 分发说明

当前没有官方公开二进制。先前使用个人身份自签证书的 `1.0.5.0` 安装包已撤回，不应继续分发或安装。新的公开安装包将在使用中性发布身份和合适的签名渠道后重新发布。

项目许可证将在公开仓库发布前确定。在许可证文件加入前，代码公开可见不代表授予复制、修改或再分发权利。
