# SimFlow 1.0.8.0 发布说明

SimFlow 1.0.8.0 修复了 Windows 只读属性导致归档源目录无法彻底删除的问题。

## 下载

- `SimFlow-1.0.8.0-win-x64-portable.zip`：Windows 10 2004+ / Windows 11 x64 免安装便携版；
- `SHA256SUMS.txt`：下载包的 SHA-256 校验值。

请只从本仓库的 GitHub Releases 页面下载，并在运行前核对 SHA-256。完整解压 ZIP 后运行 `SimFlow.exe`。

## 修复内容

- 归档副本完成校验并切换为主副本后，删除流程会先清除源目录树的 Windows 只读属性；
- 修复项目内容已删除、只读根目录却残留并提示 `Access to the path ... is denied` 的问题；
- 清理过程中仍会拒绝目录连接点和符号链接，不会越过项目目录边界；
- 文件正在被 Adams、求解器或其他程序占用时仍会安全保留源目录，并进入待清理状态。

## 界面修复

- 修复便携版发布时遗漏侧边栏 Logo PNG，导致左上角只显示“SimFlow”文字的问题；
- 发布流水线现在会检查窗口图标和侧边栏 Logo 是否都进入最终便携版。

## 关于 Windows 安全提示

当前便携版没有商业代码签名。Windows 首次运行时可能显示“未知发布者”或 Microsoft Defender SmartScreen 提示。确认下载来源和 SHA-256 后，可以选择“更多信息”→“仍要运行”。本版本不需要安装任何证书。
