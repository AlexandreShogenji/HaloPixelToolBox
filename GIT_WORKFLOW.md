# Git 工作约定

本文件是本仓库日常 Git 与发布操作的速查表；以当前仓库配置和既有发布脚本为准。

## 提交前

1. 先执行 `git status -sb`、`git diff --check` 和相关构建/测试，确认每一项修改都属于当前任务。
2. 有未归属或不理解的改动时，不使用 `git add -A`；仅按文件路径暂存本次工作。
3. 不使用 `git reset --hard`、`git checkout --` 等会丢弃本地工作的命令，除非仓库维护者明确要求。
4. 不删除用户的模型缓存、应用数据或个人配置。`bin/`、`obj/`、`artifacts/`、`release/` 和安装器的 `Source.zip` 属于可再生构建产物，可在确认不需要后清理。

## 分支、提交与推送

- 常规开发从默认分支创建 `codex/<简短主题>` 分支；发布任务在维护者明确授权后可以直接更新 `master`。
- 提交标题使用简短祈使句，例如：`Add QQ Music lyric synchronization`。
- 推送前再次确认远端和分支：`git remote -v`、`git branch --show-current`；以 `origin` 为默认远端。
- 推送后检查提交已位于预期远端分支；需要评审的常规改动创建 Draft PR。

## 版本与 Release

1. 统一版本定义在 `Directory.Build.props`；MSIX 版本同时更新 `HaloPixelToolBox/Package.appxmanifest`。
2. 从已推送的发布提交构建：

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 -Version <版本号> -Platform x64 -Runtime win-x64
   ```

3. 上传 `release/v<版本号>/` 中的安装器 EXE、便携 ZIP 和 `SHA256SUMS.txt` 到同名 GitHub Release。
4. Release 说明须列出用户可见功能、支持范围/兼容性、已验证场景和已知限制。

## 本次 v2.1.0

- QQ 音乐歌词同步采用 QQMusicLyricNew 本地 QRC 缓存。
- QRC 通过 QQ 的非标准 DES 位序解密，并按媒体会话播放进度输出当前歌词行。
- 已在 QQ 音乐桌面歌词场景中验证加载、拖动进度和切歌后的自动重试。
