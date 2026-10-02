# DSH 会话界面回归测试

从仓库根目录使用 .NET 8 SDK 运行：

```powershell
dotnet run --project tests/dsh-ui-tests/DshUiTests.csproj
dotnet run --project tests/dsh-ui-tests/DshUiTests.csproj --no-build -- --page
dotnet run --project tests/dsh-ui-tests/DshUiTests.csproj --no-build -- --tasks
dotnet run --project tests/dsh-ui-tests/DshUiTests.csproj --no-build -- --voice
```

项目以相对路径编译真实的会话 ViewModel、页面交互代码和状态模型，使用假 WinUI 控件、会话客户端及语音服务。默认分支验证搜索、历史分页、缓存、草稿、手动发送与会话管理；`--page` 验证选择事件、输入法、聊天滚动和 XAML 绑定；`--tasks` 验证任务入口、精确授权、问题回答和任务会话跟随；`--voice` 验证监听快捷入口、页面生命周期与状态同步。

搜索回归验证隐藏当前列表行时仍保留聊天历史和草稿，清空过滤后重新绑定实际可见行，恢复选择通知；真实移除会话或切换数据目录时清除失效详情。页面回归同时检查过滤导致的移除事件不会关闭聊天，而鼠标、键盘的新增选择仍可切换会话。

测试不启动真实 DSH 模型任务，不读取本机凭据或个人设置，不接入麦克风、音箱或网络。每项输出 `PASS` 或 `FAIL`；失败返回非零退出码。假控件测试不能替代真实 WinUI 的视觉验收，主工程仍须编译并在实际页面检查布局、输入和高亮。
