using CommunityToolkit.Mvvm.Input;
using HaloPixelToolBox.Installer.Profiles;
using HaloPixelToolBox.Installer.ViewModel.Windows;
using HaloPixelToolBox.Installer.Views.Pages;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HaloPixelToolBox.Installer.ViewModel.Pages
{
    public partial class InstallProgressPageViewModel(InstallProgressPage viewPage) : ViewModelBase
    {
        public InstallProgressPage ViewPage { get; set; } = viewPage;

        [RelayCommand]
        void OpenApplication()
        {
            try
            {
                var startInfo = new ProcessStartInfo(Path.Combine(SystemProfile.InstallPath, "HaloPixelToolBox.exe"))
                {
                    UseShellExecute = true,
                    WorkingDirectory = SystemProfile.InstallPath
                };
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("系统没有返回已启动的应用进程。");
                MainWindowViewModel.CloseWindow();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开 HaloPixelToolBox：\n{ex.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        void CloseInstaller()
        {
            MainWindowViewModel.CloseWindow();
        }
    }
}
