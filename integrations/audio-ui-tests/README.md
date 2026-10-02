# 音频控制界面状态测试

独立的 .NET 8 控制台测试，直接链接项目中的真实 `AudioControlPageViewModel`、`ViewModelBase` 和音频模型源码。所有链接使用仓库相对路径。

`Stubs.cs` 只替代 WinUI 类型、调度器和音频服务；测试不会加载窗口、连接音箱、修改系统默认输出或写入 Equalizer APO 配置。这套测试验证 ViewModel 的状态与交互逻辑，不验证 XAML 渲染、响应式布局或实际音效。

在仓库根目录运行：

```powershell
dotnet restore integrations/audio-ui-tests/AudioUiTests.csproj --ignore-failed-sources
dotnet run --project integrations/audio-ui-tests/AudioUiTests.csproj --no-restore
```

依赖为 `CommunityToolkit.Mvvm` 8.4.0 和仓库统一配置的编译器包。已有 NuGet 缓存可直接还原；缓存缺失时需要正常可用的软件包源。

当前 38 项检查覆盖：初始读取不重复写配置、250 ms 连续调节合并、最新增益值、外部状态不覆盖未保存草稿、离开页面保存与返回恢复、保存预设前提交草稿、提交失败不保存旧曲线、内置曲线保护、非法数值处理、快照版本保护、自动增益余量的实际值和属性通知，以及媒体/通话输出角色、下拉不隐式切换系统输出、显式按钮切换、组件接入与音效启用/参数写入状态的区分。
