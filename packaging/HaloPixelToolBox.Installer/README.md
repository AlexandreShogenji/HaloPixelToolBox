# HaloPixelToolBox 安装器打包说明

安装器项目负责把主程序发布目录中的文件安装到用户选择的位置。它依赖一个嵌入资源：`Resources\Resource\Source.zip`。该压缩包内部应直接包含 `HaloPixelToolBox.exe` 和运行所需文件，而不是再套一层目录。

推荐使用仓库根目录的发布脚本完成完整打包：

```powershell
powershell -ExecutionPolicy Bypass -File eng/Build-Release.ps1 -Platform x64 -Runtime win-x64
```

脚本会自动完成：

1. 发布主程序。
2. 在加入卸载程序前生成便携 ZIP。
3. 发布卸载程序，并将它加入安装版载荷的 `Uninstaller` 目录。
4. 生成并嵌入安装器所需的 `Resources\Resource\Source.zip`。
5. 发布安装器。
6. 生成并嵌入 `HaloPixelToolBox.Installer.Package` 所需的 `Source.zip`。
7. 按 `Directory.Build.props` 中的当前版本，把最终安装器 EXE、便携 ZIP 和 SHA256 校验文件输出到 `artifacts/release/v<版本号>/`，并清理展开目录和临时嵌入包。

如果必须手动打包，请按以下顺序操作：

1. 发布 `HaloPixelToolBox/HaloPixelToolBox/HaloPixelToolBox.csproj`。
2. 将此时的主程序发布目录压缩为便携 ZIP。
3. 发布 `packaging/HaloPixelToolBox.Uninstaller/HaloPixelToolBox.Uninstaller.csproj`，并把输出复制到主程序发布目录的 `Uninstaller` 子目录。
4. 将安装版主程序发布目录中的全部内容压缩为 `Resources\Resource\Source.zip`。
5. 发布 `packaging/HaloPixelToolBox.Installer/HaloPixelToolBox.Installer.csproj`。
6. 将安装器发布目录中的全部内容压缩为 `packaging/HaloPixelToolBox.Installer.Package/Source.zip`。
7. 发布 `packaging/HaloPixelToolBox.Installer.Package/HaloPixelToolBox.Installer.Package.csproj`，得到最终自解压安装器。
