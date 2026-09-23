# Contributing

感谢参与 SimFlow。提交修改前请遵循以下约定：

1. 不提交真实项目数据、数据库、配置、日志、安装证书或内部共享路径。
2. 数据操作必须失败关闭：验证失败时保留源文件，不覆盖既有目标。
3. 删除、迁移、扫描和数据库升级必须包含故障路径测试。
4. UI 改动应保留键盘操作、AutomationProperties 和浅色/深色主题资源。
5. 提交前运行：

```powershell
dotnet restore SimFlow.slnx --locked-mode
dotnet test Tests\SimFlow.Tests\SimFlow.Tests.csproj -c Release --no-restore
dotnet build SimFlow.csproj -c Release -p:Platform=x64 --no-restore
```

Pull Request 应说明改动范围、验证结果、数据风险和尚未验证的真实环境条件。
