# Code signing policy

SimFlow 的正式 Windows 二进制必须由 Microsoft Store 或公共可信代码签名服务签名。项目不向公众分发自签证书，也不要求用户手动信任开发证书。

## 计划采用的开源签名

项目将优先申请 SignPath Foundation 的免费开源代码签名：

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

- Repository: <https://github.com/Morick66/SimFlow>
- Committers and reviewers: repository owner and explicitly listed GitHub collaborators
- Approvers: repository owner `Morick66`

正式签名只接受来自公开仓库受保护发布分支或版本标签的可追溯自动化构建。构建脚本、依赖锁文件和签名配置均需纳入代码审查。签名私钥不得由项目维护者下载、导出或存入 GitHub Secrets。

在 SignPath Foundation 接受项目之前，GitHub Releases 不发布被描述为正式版的 Windows 二进制。
