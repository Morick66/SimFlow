# Security Policy

## Supported versions

首个正式发布后，仅维护最新的 `1.0.x` 版本。旧版本发现数据安全问题时应优先升级。

## Reporting a vulnerability

请通过 GitHub Security Advisory 的私密报告入口提交安全问题。不要在公开 Issue 中包含真实项目名称、目录结构、仿真数据、数据库、配置、日志、共享路径、证书私钥、密码、Token 或 API Key。

报告应包含受影响版本、复现条件、预期行为和最小化测试数据。涉及文件损坏或数据丢失的问题按最高优先级处理。

## Data safety

在问题确认前，请保留项目原目录和 `%LOCALAPPDATA%\SimFlow` 的只读备份。不要使用生产项目复现迁移、删除或扫描冲突问题。
