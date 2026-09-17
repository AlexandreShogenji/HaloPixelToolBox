# 在 VS Code 中微调 UI 并编译 Debug 版本

本文面向 HaloPixelToolBox 当前仓库，说明如何使用 VS Code 修改 XAML/C# 参数、编译 x64 Debug 版本、运行程序以及附加断点调试。

## 1. 开发环境

建议使用以下环境：

- Windows 10/11 x64。
- .NET 8 SDK。仓库根目录的 `global.json` 当前指定 `8.0.421`。
- Visual Studio Build Tools 或 Visual Studio，并安装 Windows SDK/桌面开发相关组件。
- VS Code。
- VS Code 扩展：
  - C# Dev Kit：`ms-dotnettools.csdevkit`
  - C#：`ms-dotnettools.csharp`
  - PowerShell：`ms-vscode.powershell`，可选但推荐

在 PowerShell 中确认 SDK：

```powershell
dotnet --version
```

正常情况下应输出 `8.0.421`，或由 `global.json` 允许的兼容 .NET 8 SDK。

## 2. 用 VS Code 打开正确目录

请打开仓库根目录，而不是只打开某个子项目：

```text
<仓库根目录>
```

这个目录内应当能看到：

```text
global.json
HaloPixelToolBox.slnx
HaloPixelToolBox/
HaloPixelToolBox.Installer/
docs/
```

在 VS Code 中选择“文件 → 打开文件夹”，打开以上目录。后续命令都假定终端当前目录是仓库根目录。

## 3. 第一次还原和编译

打开 VS Code 集成终端：

```text
终端 → 新建终端
```

第一次先还原依赖：

```powershell
dotnet restore .\HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj -p:Platform=x64
```

编译 x64 Debug 主程序：

```powershell
dotnet build .\HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj -c Debug -p:Platform=x64
```

依赖已经还原后，可以用下面的命令加快重复编译：

```powershell
dotnet build .\HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj -c Debug -p:Platform=x64 --no-restore
```

成功时终端末尾应显示：

```text
已成功生成。
0 个错误
```

当前依赖可能输出 `CS9057` 分析器版本警告；只要最终是 `0 个错误`，不会阻止 Debug 版本生成。

> UI 调整时建议只编译主程序 `.csproj`。直接编译 `HaloPixelToolBox.slnx` 还会处理测试、安装器和安装包项目，速度更慢，也不是普通 UI 微调所必需的。

## 4. Debug 输出位置和运行方式

编译后的程序位于：

```text
HaloPixelToolBox\HaloPixelToolBox\bin\x64\Debug\net8.0-windows10.0.22621.0\HaloPixelToolBox.exe
```

可以在资源管理器中双击，也可以从 VS Code 终端启动：

```powershell
$appOutput = Resolve-Path .\HaloPixelToolBox\HaloPixelToolBox\bin\x64\Debug\net8.0-windows10.0.22621.0
$appExe = Join-Path $appOutput 'HaloPixelToolBox.exe'
Start-Process -FilePath $appExe -WorkingDirectory $appOutput
```

修改参数后的推荐循环：

1. 从系统托盘完全退出正在运行的 HaloPixelToolBox。
2. 在 VS Code 中修改并保存文件。
3. 执行 Debug 编译，确认 `0 个错误`。
4. 启动上述 Debug EXE。
5. 调整窗口宽度，检查窄屏、普通宽度和宽屏布局。

HaloPixelToolBox 是单实例应用。如果旧进程仍在托盘中，新启动的程序可能立即退出或把启动请求转发给旧实例，导致你看到的仍是旧界面。因此每轮测试前应先从托盘菜单退出旧实例。

连接真实设备时，灯光、亮度、字幕和场景操作会调用真实接口并发送到设备。只检查静态排版时，不要随意操作会发送设备命令的控件。

## 5. 预览六边形参数

### 5.1 外轮廓路径

文件：

```text
HaloPixelToolBox/HaloPixelToolBox/Views/MainPage.xaml.cs
```

修改 `PreviewOuterFrameGeometry`：

```csharp
private static readonly Point[] PreviewOuterFrameGeometry =
[
    new(9.464, 38.659),
    new(73.238, 5),
    new(926.762, 5),
    new(990.536, 38.659),
    new(912.386, 95),
    new(87.614, 95)
];
```

这里使用 `1000 × 100` 的归一化坐标，左上角是 `(0, 0)`。对应常量是 `PreviewGeometryWidth` 和 `PreviewGeometryHeight`，`PreviewGeometryPadding` 控制最外框与预览容器边缘的留白：

- X 越大，点越靠右。
- Y 越大，点越靠下。
- 四条内框会根据外框自动生成，不需要分别维护四套坐标。

如果修改 `PreviewGeometryWidth` 或 `PreviewGeometryHeight`，必须按相同比例重新计算六个点；只想改变预览框在容器中的整体留白时，应优先调整 `PreviewGeometryPadding`。

六个点必须保持以下顺序：

| 序号 | 位置 | 常见调节方式 |
| --- | --- | --- |
| 1 | 左侧尖点 | 减小 Y 会让左尖点上移 |
| 2 | 左上角 | 改 X 控制左上斜边长度，改 Y 控制顶边高度 |
| 3 | 右上角 | 通常与第 2 点左右对称 |
| 4 | 右侧尖点 | 减小 Y 会让右尖点上移 |
| 5 | 右下角 | 控制右下斜边和底边 |
| 6 | 左下角 | 通常与第 5 点左右对称 |

想保持左右对称时，可让成对点的 X 坐标之和保持为 `1000`：

```text
左尖点 X + 右尖点 X = 1000
左上角 X + 右上角 X = 1000
左下角 X + 右下角 X = 1000
```

不要改变点的顺序，也不要让相邻点重合，否则平行内缩计算会失去正确的多边形方向。

### 5.2 四层间距

同一文件中的 `PreviewFrameInsets` 控制四层中心线向内缩进的距离，单位是 DIP：

```csharp
private static readonly double[] PreviewFrameInsets = [0, 12, 22, 28];
```

- 第一个值必须是 `0`，代表外框。
- 后面的值越大，对应框越靠内。
- 数组顺序必须严格由小到大。

当前四层线宽为 `7 / 6 / 4 / 2`。相邻两框的可见空隙可按下面的公式估算：

```text
可见空隙 = 两条中心线的内缩差 - (外侧线宽 + 内侧线宽) / 2
```

按当前配置估算，三段可见空隙约为 `5.5 / 5 / 3` DIP：

```text
12 - (7 + 6) / 2 = 5.5
10 - (6 + 4) / 2 = 5
 6 - (4 + 2) / 2 = 3
```

如果修改线宽，也建议同时重新计算 `PreviewFrameInsets`，否则斜边可能看起来粘连或间距不一致。

### 5.3 线宽、预览高度和字幕字体

文件：

```text
HaloPixelToolBox/HaloPixelToolBox/Views/MainPage.xaml
```

常用参数：

| 参数 | 当前值 | 作用 |
| --- | --- | --- |
| 预览容器 `Border Height` | `136` | 控制整块预览区域高度 |
| 四个 `Polygon StrokeThickness` | `7 / 6 / 4 / 2` | 控制外到内四层线宽 |
| 字幕 `TextBlock Margin` | `80,30,80,22` | 避免文字碰到斜边和上下边框 |
| 字幕 `FontSize` | `16` | 模拟 16 像素高字幕 |
| 字幕 `LineHeight` | `18` | 控制单行文字高度 |

像素字体资源也定义在 `MainPage.xaml` 顶部：

```xaml
<FontFamily x:Key="PixelPreviewFontFamily">ms-appx:///Assets/Fonts/fusion-pixel-12px-proportional-zh_hans.ttf#Fusion Pixel 12px Prop zh_hans</FontFamily>
```

更换字体文件时，需要同时确保：

1. 字体文件位于 `Assets/Fonts/`。
2. `HaloPixelToolBox.csproj` 仍将 `Assets/Fonts/**/*.*` 作为内容复制到输出目录。
3. `#` 后的字体族名称是字体文件内部名称，而不只是文件名。

## 6. 灯光控制页面参数

文件：

```text
HaloPixelToolBox/HaloPixelToolBox/Views/LightingToolPage.xaml
```

主要位置：

| 区域 | 搜索名称或内容 | 用途 |
| --- | --- | --- |
| 两个灯光模块排布 | `LightingCardsGrid` | 控制桌面环境光与主题颜色左右/上下排布 |
| 环境光色板 | `AmbientPickerLayout` | 环境光色板和 RGB 输入框 |
| 主题色板 | `PixelPickerLayout` | 字幕主题色板和 RGB 输入框 |
| RGB 编辑器 | `AmbientRgbEditor`、`PixelRgbEditor` | 控制 RGB 输入框横排或竖排 |
| 灯效选项 | 文本 `灯效模式` | 当前放在环境光色板下方 |
| 滚动条安全距离 | `Margin="0,0,24,24"` | 避免右侧控件被覆盖式滚动条遮挡 |

当前响应式断点：

| 最小窗口宽度 | 布局行为 |
| --- | --- |
| `0` | 两个灯光模块上下排布，RGB 输入框放到色板下方 |
| `700` | 单列灯光模块中，RGB 输入框移到色板右侧 |
| `1180` | 两个灯光模块左右排布，RGB 输入框放到各自色板下方 |
| `1500` | 两个灯光模块左右排布，RGB 输入框移到各自色板右侧 |

断点由 `AdaptiveTrigger MinWindowWidth` 控制。修改断点后，应至少拖动窗口测试以下宽度附近：

```text
小于 700
700 左右
1000 左右
1180 左右
1500 以上
```

## 7. 其他 XAML 文案和排布

页面文件位于：

```text
HaloPixelToolBox/HaloPixelToolBox/Views/
```

删除或修改说明文字时，通常修改对应页面中的 `TextBlock Text="..."`。如果删掉整个说明 `TextBlock`，同时检查父级 `StackPanel` 的 `Spacing`，避免留下过大的空白。

常见页面对应关系：

| 页面 | 文件 |
| --- | --- |
| 设备控制台 | `MainPage.xaml` |
| 灯光控制 | `LightingToolPage.xaml` |
| 歌词字幕 | `LyricsSubtitleToolPage.xaml` |
| 视频字幕 | `VideoSubtitleToolPage.xaml` |
| 浏览器翻译字幕 | `BrowserTranslationSubtitleToolPage.xaml` |
| 自定义字幕 | `CustomSubtitleToolPage.xaml` |
| 个性场景 | `PersonalSceneToolPage.xaml` |
| 设置 | `SettingPage.xaml` |

VS Code 对 WinUI 3 没有与完整 Visual Studio 相同的 XAML 设计器和可靠的 XAML Hot Reload。XAML 修改后的稳妥验证方式仍是停止旧程序、重新编译并启动 Debug EXE。

## 8. 配置 VS Code 一键编译和运行

下面的配置是可选项。若要使用，在仓库根目录创建 `.vscode/tasks.json`：

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "HaloPixel: Build Debug x64",
      "type": "process",
      "command": "dotnet",
      "args": [
        "build",
        "${workspaceFolder}/HaloPixelToolBox/HaloPixelToolBox/HaloPixelToolBox.csproj",
        "-c",
        "Debug",
        "-p:Platform=x64"
      ],
      "problemMatcher": "$msCompile",
      "group": {
        "kind": "build",
        "isDefault": true
      }
    },
    {
      "label": "HaloPixel: Run Debug x64",
      "type": "process",
      "command": "${workspaceFolder}/HaloPixelToolBox/HaloPixelToolBox/bin/x64/Debug/net8.0-windows10.0.22621.0/HaloPixelToolBox.exe",
      "options": {
        "cwd": "${workspaceFolder}/HaloPixelToolBox/HaloPixelToolBox/bin/x64/Debug/net8.0-windows10.0.22621.0"
      },
      "dependsOrder": "sequence",
      "dependsOn": "HaloPixel: Build Debug x64",
      "problemMatcher": []
    }
  ]
}
```

使用方式：

- `Ctrl+Shift+B`：执行默认的 Debug x64 编译任务。
- `Ctrl+Shift+P` → `Tasks: Run Task` → `HaloPixel: Run Debug x64`：先编译再运行。

## 9. 在 VS Code 中断点调试

在仓库根目录创建 `.vscode/launch.json`：

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "HaloPixel: Launch Debug x64",
      "type": "coreclr",
      "request": "launch",
      "program": "${workspaceFolder}/HaloPixelToolBox/HaloPixelToolBox/bin/x64/Debug/net8.0-windows10.0.22621.0/HaloPixelToolBox.exe",
      "cwd": "${workspaceFolder}/HaloPixelToolBox/HaloPixelToolBox/bin/x64/Debug/net8.0-windows10.0.22621.0",
      "preLaunchTask": "HaloPixel: Build Debug x64",
      "stopAtEntry": false,
      "justMyCode": true
    },
    {
      "name": "HaloPixel: Attach to running process",
      "type": "coreclr",
      "request": "attach",
      "processId": "${command:pickProcess}",
      "justMyCode": true
    }
  ]
}
```

使用方式：

1. 在 `.xaml.cs`、ViewModel 或服务代码行左侧单击设置断点。
2. 打开 VS Code 的“运行和调试”面板。
3. 优先尝试 `HaloPixel: Launch Debug x64`。
4. 如果 WinUI 启动调试在本机不稳定，则先运行 `HaloPixel: Run Debug x64` 任务，再选择 `HaloPixel: Attach to running process`，并选中 `HaloPixelToolBox.exe`。

XAML 标记本身不能像 C# 一样设置执行断点。要观察 XAML 交互，应在对应事件处理器、命令或绑定属性的 setter 中设置断点。

## 10. 常见问题

### 编译提示文件正在使用

原因通常是 HaloPixelToolBox 仍在运行或留在系统托盘。先从托盘菜单完全退出，再重新编译。只有应用无响应时，才通过任务管理器结束对应的 `HaloPixelToolBox.exe` 进程。

### 修改后界面没有变化

依次确认：

1. 文件已经保存。
2. 编译命令最终显示 `0 个错误`。
3. 启动的是 `bin/x64/Debug/...` 下的 EXE，而不是旧的安装版或 Release 版。
4. 系统托盘里没有旧实例。

### XAML 编译报错但定位不直观

先看终端中第一个 `error`，后续错误往往只是连锁结果。重点检查：

- 标签是否成对闭合。
- 属性引号是否完整。
- `x:Name` 是否重复。
- `x:Bind` 的属性名是否仍存在。
- 删除 `Grid` 行列后，子控件是否仍引用不存在的行列。

### XAML 缓存导致异常

先执行普通 clean，不要直接删除整个仓库：

```powershell
dotnet clean .\HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj -c Debug -p:Platform=x64
dotnet build .\HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj -c Debug -p:Platform=x64
```

### 如何确认只改了预期文件

```powershell
git status --short
git diff --check
git diff -- .\HaloPixelToolBox\HaloPixelToolBox\Views\MainPage.xaml
git diff -- .\HaloPixelToolBox\HaloPixelToolBox\Views\MainPage.xaml.cs
```

当前工作区可能还包含其他未提交修改。不要使用 `git reset --hard` 或 `git checkout -- .` 清理工作区，否则会丢失尚未提交的 UI 和文案调整。

## 11. 最短操作流程

日常只需记住下面几步：

```powershell
# 1. 在系统托盘退出旧实例

# 2. 修改并保存 XAML/C#

# 3. 编译
dotnet build .\HaloPixelToolBox\HaloPixelToolBox\HaloPixelToolBox.csproj -c Debug -p:Platform=x64 --no-restore

# 4. 运行
$appOutput = Resolve-Path .\HaloPixelToolBox\HaloPixelToolBox\bin\x64\Debug\net8.0-windows10.0.22621.0
$appExe = Join-Path $appOutput 'HaloPixelToolBox.exe'
Start-Process -FilePath $appExe -WorkingDirectory $appOutput
```
