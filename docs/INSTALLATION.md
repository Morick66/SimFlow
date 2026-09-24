# SimFlow 下载、运行与卸载

SimFlow 通过 GitHub Releases 提供免安装的 Windows x64 便携版。它不依赖 Microsoft Store，不需要安装证书。

## 系统要求

- Windows 10 2004（Build 19041）或更高版本
- x64
- 建议至少保留 500 MB 可用磁盘空间

## 校验发布包

从官方 [GitHub Releases](https://github.com/Morick66/SimFlow/releases) 下载最新的 `SimFlow-版本号-win-x64-portable.zip` 和同一 Release 中的 `SHA256SUMS.txt`。不要从第三方网盘或重新打包页面下载。

```powershell
Get-FileHash .\SimFlow-1.0.8.0-win-x64-portable.zip -Algorithm SHA256
```

输出的哈希应与 `SHA256SUMS.txt` 完全一致。任何不一致都不要运行。

## 安装

1. 把 ZIP 完整解压到一个普通目录，例如 `D:\Apps\SimFlow-1.0.8.0`；
2. 运行其中的 `SimFlow.exe`；
3. 如果 Windows 显示 Microsoft Defender SmartScreen，请确认文件来自本仓库且哈希一致，再选择“更多信息”→“仍要运行”。

当前便携版没有商业代码签名，Windows 可能显示“未知发布者”。它不会要求用户导入 `.cer` 证书；官方 Release 也不会提供 PFX、开发证书或自签 MSIX。

## 升级

下载新版本 ZIP，解压到新的版本目录后运行即可。不要直接覆盖正在运行的旧目录。升级前建议备份：

- `%LOCALAPPDATA%\SimFlow`
- 设置中配置的本机 Work 根
- 尚未清理的迁移源副本

升级不得删除或移动上述数据。

## 卸载

关闭 SimFlow 后删除便携版程序目录即可。数据库、配置、日志和项目数据位于程序目录外，不会随程序目录删除。

卸载后如不再使用，请在确认备份后自行处理 `%LOCALAPPDATA%\SimFlow` 和 Work 目录；安装程序不会自动删除它们。

## 回滚

如新版本无法使用：

1. 备份 `%LOCALAPPDATA%\SimFlow` 和项目 Work 目录；
2. 关闭当前版本；
3. 重新运行上一版便携目录中的 `SimFlow.exe`；
4. 启动后先检查项目数量和存储路径，不要立即执行迁移或删除。

数据库结构发生升级时，优先使用应用生成的升级前备份，禁止直接覆盖正在使用的数据库。
