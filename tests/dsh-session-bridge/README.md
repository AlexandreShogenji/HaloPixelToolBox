# DSH 会话桥接测试

在仓库根目录运行：

```powershell
node --test tests/dsh-session-bridge/bridge.test.mjs
pwsh -File eng/Test.ps1 -Suite Dsh
```

也可在运行时包目录执行 `npm run check`，会检查桥接源码语法并运行本目录中的测试。测试直接导入 `integrations/deepseek-harness/halo-session-bridge/bridge-core.js`，使用假 Host、临时发现文件和隔离的本地服务，不启动真实 DSH 模型任务或读取个人会话。
