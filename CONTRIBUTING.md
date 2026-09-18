# 参与 HaloPixelToolBox

感谢你愿意为 **花再工具箱** 提供反馈 🙏
无论是 Bug 报告、功能建议，还是使用体验的改进想法，都非常欢迎。

为了让问题能被更快理解和处理，请在提交 Issue 前阅读以下说明。

---

## 🐞 提交 Bug 报告

如果你遇到了程序异常、崩溃或行为不符合预期的问题，请选择 **Bug 报告** 模板，并尽量提供：

- 清晰的问题描述
- 可复现的操作步骤
- 使用的软件版本
- 操作系统环境
- 相关日志或截图（如有）

📌 **请尽量避免：**
- 只描述“不能用 / 有问题”
- 缺少复现步骤或环境信息

---

## ✨ 提交功能需求

如果你有新的功能想法或改进建议，请选择 **功能需求** 模板，并说明：

- 你当前遇到的使用场景或不便
- 你期望的解决方式
- 这个功能能解决什么问题

功能需求不一定都会立刻实现，但每一条都会被认真考虑和记录。

---

## 🌍 关于语言

- Issue **支持使用中文提交**
- 仓库已配置自动翻译为英文，方便更多人参与讨论
- 无需自行翻译

---

## 🚫 不适合使用 Issue 的情况

以下情况请不要提交 Issue：

- 使用咨询或简单提问（可查看 README 或 Discussions）
- 与项目无关的内容
- 已在现有 Issue 中被明确提及的问题（请先搜索）

---

## 开发与仓库约定

仓库根目录的 `global.json` 固定 .NET SDK 版本，`Directory.Build.props` 统一产品版本和编译器配置，`.gitattributes` 负责 Git 文本与二进制文件规则。这三个文件依赖根目录作用域，请保留在当前位置。

- `HaloPixelToolBox/`：主程序和核心库。
- `packaging/`：卸载器、WPF 安装器与最终单文件封装器，共同组成安装版发布链路。
- `eng/`：版本、构建、品牌资源和工作区清理脚本。
- `docs/images/`：README 使用的软件界面截图。

### 提交前检查

1. 执行 `git status -sb`、`git diff --check` 和与修改相关的构建。
2. 只暂存当前任务涉及的路径；遇到来源不明的修改时先查明归属。
3. 保留本机的模型缓存、应用数据和个人配置。
4. 常规开发从默认分支创建 `codex/<简短主题>` 分支，提交标题使用简短祈使句。
5. 推送前检查 `git remote -v` 和 `git branch --show-current`，推送后确认提交位于预期远端分支。

### 构建与发布

统一版本定义在 `Directory.Build.props`。设置版本、构建主程序和生成发行文件的顺序如下：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Set-Version.ps1 -Version <版本号>
dotnet build HaloPixelToolBox.sln -c Release -p:Platform=x64
powershell -ExecutionPolicy Bypass -File eng/Build-Release.ps1 -Platform x64 -Runtime win-x64
```

发布脚本按顺序生成主程序、便携包、卸载器、安装器和单文件封装器，最终文件写入 `artifacts/release/v<版本号>/`。上传同目录中的安装器 EXE、便携 ZIP 和 `SHA256SUMS.txt` 到同名 GitHub Release；Release 说明应包含用户可见功能、兼容范围、验证场景和已知限制。

### 本地归档与清理

`artifacts/` 不进入 Git。其子目录用途如下：

- `release/`：可发布的最终文件。
- `captures/`：设备抓包及证据清单。
- `archive/`：研究文档、一次性工具源码和旧实现。
- `cache/`：可再生的构建缓存与展开目录。

`captures/` 和 `archive/` 可能包含不可再生的本机资料，长期保存时请另做备份。清理前可先预览，再删除构建输出和 `cache/`：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Clean-Workspace.ps1 -WhatIf
powershell -ExecutionPolicy Bypass -File eng/Clean-Workspace.ps1
```

## ❤️ 最后

感谢你花时间参与花再工具箱的改进！
你的每一次反馈，都会让这个项目变得更好。
