# HaloPixelToolBox 安装器打包说明

安装器项目负责把主程序发布目录中的文件安装到用户选择的位置。它依赖一个嵌入资源：`Resources\Resource\Source.zip`。该压缩包内部应直接包含 `HaloPixelToolBox.exe` 和运行所需文件，而不是再套一层目录。

推荐使用仓库根目录的发布脚本完成完整打包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 -Version 2.0.0 -Platform x64 -Runtime win-x64
```

脚本会自动完成：

1. 发布主程序。
2. 生成并嵌入安装器所需的 `Resources\Resource\Source.zip`。
3. 发布安装器。
4. 生成并嵌入 `HaloPixelToolBox.Installer.Package` 所需的 `Source.zip`。
5. 输出最终安装器 EXE、便携 ZIP 和 SHA256 校验文件到 `release/v2.0.0/`。

如果必须手动打包，请按以下顺序操作：

1. 发布 `HaloPixelToolBox/HaloPixelToolBox/HaloPixelToolBox.csproj`。
2. 将发布目录中的全部内容压缩为 `Resources\Resource\Source.zip`。
3. 发布 `HaloPixelToolBox.Installer/HaloPixelToolBox.Installer.csproj`。
4. 将安装器发布目录中的全部内容压缩为 `HaloPixelToolBox.Installer.Package/Source.zip`。
5. 发布 `HaloPixelToolBox.Installer.Package/HaloPixelToolBox.Installer.Package.csproj`，得到最终自解压安装器。
