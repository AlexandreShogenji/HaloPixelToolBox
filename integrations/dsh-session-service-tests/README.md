# DSH 会话服务恢复回归测试

使用 .NET 8 SDK 从仓库根目录运行：

```powershell
dotnet run --project integrations/dsh-session-service-tests/SessionServiceTests.csproj
```

项目编译真实的 `DshSessionsService` 及其依赖，Profile 使用最小假实现。每项测试在临时目录写入虚构的发现描述文件，在本机随机端口启动隔离 HTTP 假服务；进程身份只使用测试自身，不启动真实 DSH、模型或硬件，不读取本机凭据或个人会话。

覆盖同一进程更换桥接端口、同端口更换令牌、列表暂态故障与进行中的设备命令、换连接后旧回复拒绝、其他 Profile 的描述文件，以及没有可用服务时只读刷新保持断线。恢复只调用状态与列表 GET；虚构命令用于核对并发回复，记录断言保证不重发。

临时描述文件与假服务在测试结束时清理。每项输出 `PASS` 或 `FAIL`，失败返回非零退出码。此组验证真实 HTTP 客户端与发现逻辑，任务接管及语音路由另见 `integrations/dsh-task-routing-tests`。
