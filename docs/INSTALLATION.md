# SimFlow 安装、升级与卸载

> 当前没有官方公开二进制。先前的个人身份自签版本已经撤回。以下内容仅用于后续重新签名版本的安装说明，不要继续使用旧的 MSIX/CER。

## 系统要求

- Windows 10 2004（Build 19041）或更高版本
- x64
- 有权为当前用户安装侧载应用

## 校验发布包

下载同一 Release 中的 ZIP、MSIX、CER 和 `SHA256SUMS.txt`，先核对 SHA-256。任何文件不一致都不要安装。

```powershell
Get-FileHash .\SimFlow_1.0.5.0_x64.msix -Algorithm SHA256
Get-AuthenticodeSignature .\SimFlow_1.0.5.0_x64.msix
```

签名状态应为 `Valid`。正式恢复发布后，以对应 Release 的签名与校验说明为准。

## 安装

正式恢复发布后，推荐按对应 Release 的说明安装完整分发包。

不要从源码目录、未知网盘或第三方重新打包文件安装。

## 升级

安装更高版本号的同一应用包即可升级。升级前建议备份：

- `%LOCALAPPDATA%\SimFlow`
- 设置中配置的本机 Work 根
- 尚未清理的迁移源副本

升级不得删除或移动上述数据。

## 卸载

从 Windows“已安装的应用”卸载 SimFlow。数据库、配置、日志和项目数据位于安装包外，不应随卸载删除。

卸载后如不再使用，请在确认备份后自行处理 `%LOCALAPPDATA%\SimFlow` 和 Work 目录；安装程序不会自动删除它们。

## 回滚

如新版本无法使用：

1. 备份 `%LOCALAPPDATA%\SimFlow` 和项目 Work 目录；
2. 卸载当前版本；
3. 安装上一版已验证的 MSIX；
4. 启动后先检查项目数量和存储路径，不要立即执行迁移或删除。

数据库结构发生升级时，优先使用应用生成的升级前备份，禁止直接覆盖正在使用的数据库。
