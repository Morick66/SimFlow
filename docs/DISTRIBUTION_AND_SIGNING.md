# 发布与签名策略

SimFlow 采用双通道发布，不把安装能力完全依赖于 Microsoft Store。

## 通道一：Microsoft Store

- 提供最省心的安装、更新和卸载体验；
- MSIX 由 Microsoft Store 在认证后重新签名；
- 用户不需要安装开发证书；
- 适合能够正常访问 Microsoft Store 的环境。

提交前需在 Partner Center 创建 MSIX 产品、预留 `SimFlow` 名称，并从产品 Identity 页面取得 Store 分配的 Name、Publisher、PublisherId 和 Package Family Name。源码中的 `CN=SimFlow Development` 仅为占位值，不能用于正式提交。

在 Visual Studio 中使用 **Publish → Associate App with the Store** 关联产品，再使用 **Publish → Create App Packages** 生成 `.msixupload`。首版只构建 x64，并运行 Windows App Certification Kit。

## 通道二：GitHub Releases

- 提供不依赖 Store 的直接下载；
- 计划同时提供签名 MSIX 和便携 ZIP；
- 便携版不要求导入证书或安装，但仍必须对其中的可执行文件进行可信代码签名；
- ZIP、MSIX 和签名摘要必须对应同一公开提交和自动化构建。

GitHub 二进制优先申请 SignPath Foundation 的开源项目免费签名。若项目尚未通过其审核，则不把自签或无签名构建标为正式版；备用方案是购买公共 CA 的 OV 代码签名服务。

便携版构建命令：

```powershell
dotnet publish SimFlow.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64-portable
```

该配置生成自包含、非 MSIX 的 x64 目录，不依赖 Microsoft Store。正式发布前仍需对可执行文件进行可信签名，并在干净 Windows 环境完成启动和数据目录验证。

2026-09-23 已在 Windows x64 验证机完成构建和真实进程启动：生成 359 个文件、约 270 MiB；应用启动成功且日志无新增启动错误。该结果只证明便携形态可运行，不代表未签名产物可以作为正式版发布。

## SignPath Foundation 准备

- 使用 OSI 批准的 MIT License；
- 仓库公开且项目持续维护；
- 所有参与者启用多因素认证；
- 发布代码签名政策与隐私政策；
- 构建和签名来源可追溯到 GitHub 仓库与具体提交；
- 正式签名需要独立审批，私钥不得进入仓库或构建日志。

SignPath Foundation 的证书发布者显示为 `SignPath Foundation`，不会使用维护者的个人姓名。

## 发布前共同检查

- 锁定依赖还原成功；
- 自动化测试全部通过；
- NuGet 已知漏洞扫描无发现；
- x64 Release 构建无错误；
- Store 包通过 Windows App Certification Kit；
- GitHub 包通过签名验证、哈希验证和全新环境启动测试；
- 升级、卸载和包外数据保留测试通过；
- 仓库和产物中不存在 PFX、私钥、Token、数据库、日志和个人路径。

## 禁止事项

- 不再公开自签 `.msix`、开发 `.cer` 或 PFX；
- 不要求普通用户把开发证书加入受信任证书库；
- 不手工猜测 Store Publisher；
- 未签名或签名链不可信的构建只能标为开发测试产物，不能称为正式版。
