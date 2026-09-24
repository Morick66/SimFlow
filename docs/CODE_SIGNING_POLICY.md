# Code signing policy

SimFlow 不向公众分发自签证书，也不要求用户手动信任开发证书。首个公开版本采用未签名便携 ZIP，并通过官方 GitHub Actions 构建、版本标签和 SHA-256 校验保证来源可追溯。下载页必须明确披露 Windows 会显示“未知发布者”。

获得 Microsoft Store 或公共可信代码签名服务后，项目会为后续版本补充可信签名；在此之前不发布 MSIX 安装包，也不把便携版描述为“已签名”。

## 计划采用的开源签名

项目将优先申请 SignPath Foundation 的免费开源代码签名：

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

- Repository: <https://github.com/Morick66/SimFlow>
- Committers and reviewers: repository owner and explicitly listed GitHub collaborators
- Approvers: repository owner `Morick66`

正式签名只接受来自公开仓库受保护发布分支或版本标签的可追溯自动化构建。构建脚本、依赖锁文件和签名配置均需纳入代码审查。签名私钥不得由项目维护者下载、导出或存入 GitHub Secrets。

是否申请 SignPath Foundation 不阻塞便携版发布。
