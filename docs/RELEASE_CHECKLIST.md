# SimFlow 发布检查清单

## 1. 冻结源码

- [ ] 工作区干净
- [ ] 版本号在源 manifest、包内 manifest、文件名中一致
- [ ] 发布提交已合并到 `main`
- [ ] 已创建不可变版本 tag
- [ ] 发布记录包含完整 Git SHA

## 2. 依赖和安全

```powershell
dotnet --version
dotnet restore SimFlow.slnx --locked-mode
dotnet list SimFlow.slnx package --vulnerable --include-transitive --no-restore
```

- [ ] SDK 与 `global.json` 一致
- [ ] 锁文件没有意外变化
- [ ] 未发现已知漏洞
- [ ] 未提交私钥、Token、数据库、日志或真实项目数据

## 3. 构建和测试

```powershell
dotnet test Tests\SimFlow.Tests\SimFlow.Tests.csproj -c Release --no-restore
dotnet build SimFlow.csproj -c Release -p:Platform=x64 --no-restore
```

- [ ] 0 错误、0 警告
- [ ] 全部自动化测试通过
- [ ] 新增数据操作具有失败路径测试

## 4. 数据安全冒烟

- [ ] 项目删除移动到 `.simflow-recovery`
- [ ] 版本删除移动到恢复区，数据库失败能恢复目录
- [ ] 迁移期间修改源文件会停止切换
- [ ] 伪造或不完整目标目录不会被重试流程接管
- [ ] 冲突项目不会污染正式项目版本表
- [ ] 离线项目不能修改状态
- [ ] 归档仅在目标切换成功并再次完整校验后删除源目录
- [ ] 关闭自动删除设置时保留源目录
- [ ] 删除或复核失败时保持目标为主副本并进入待清理状态

## 5. 便携版打包与发布

- [ ] GitHub 便携包来自同一公开 tag 和可追溯自动化构建
- [ ] ZIP 写入 `SHA256SUMS.txt`，发布页文件名与版本号一致
- [ ] 发布说明明确披露当前没有商业代码签名及 SmartScreen 提示
- [ ] 发布目录包含安装、升级、卸载和回滚说明
- [ ] 未上传开发 CER、PFX 或自签二进制

## 6. 安装验收

- [ ] 在全新目录解压并启动
- [ ] 从上一便携版切换到新版本
- [ ] 配置、数据库和项目数据升级后保持
- [ ] 卸载后包外数据保持
- [ ] 重新安装后能继续读取原数据
- [ ] 日志中没有新增 Fatal/Unhandled 错误

## 7. UI 冒烟

- [ ] 新建、编辑、状态修改、版本、报告、扫描、迁移入口可用
- [ ] 键盘可以触发统计卡和状态修改
- [ ] 100%、125%、150% 缩放无遮挡
- [ ] 浅色和深色主题可读
- [ ] 当前截图与发布包一致
