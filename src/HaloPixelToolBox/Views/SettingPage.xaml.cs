using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace HaloPixelToolBox.Views
{
    /// <summary>
    /// ����ҳ��
    /// </summary>
    public sealed partial class SettingPage : Page
    {
        public static SettingPage? Current { get; set; }
        public SettingPageViewModel ViewModel { get; set; } = new();
        public SettingPage()
        {
            Current = this;
            this.InitializeComponent();
            Loaded += SettingPage_Loaded;
            Unloaded += SettingPage_Unloaded;
            ViewModel.DialogService.RegisterDialog(cleanCacheContentDialog);
            ViewModel.SettingService.AddComboBox(appThemeComboBox, ProfileHelper.GetEnumProfileSaveFunc<ElementTheme>(), ProfileHelper.GetEnumProfileLoadFuncForComboBox());
            ViewModel.SettingService.Initialize();
            ViewModel.SettingService.RegisterEvents();
        }

        private void SettingPage_Loaded(object sender, RoutedEventArgs e)
            => ViewModel.AttachVoiceAgentEvents();

        private void SettingPage_Unloaded(object sender, RoutedEventArgs e)
            => ViewModel.DetachVoiceAgentEvents();

        private void AgentFields_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Grid layout || layout.ColumnDefinitions.Count != 2)
                return;

            var stacked = e.NewSize.Width < 580;
            layout.ColumnDefinitions[1].Width = stacked
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            for (var index = 0; index < layout.Children.Count; index++)
            {
                if (layout.Children[index] is FrameworkElement field)
                {
                    Grid.SetColumn(field, stacked ? 0 : index);
                    Grid.SetRow(field, stacked ? index : 0);
                }
            }
        }

        private void AgentActions_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Grid { Children.Count: 1 } layout && layout.Children[0] is StackPanel actions)
                actions.Orientation = e.NewSize.Width < 380 ? Orientation.Vertical : Orientation.Horizontal;
        }

        private void AgentToggle_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Grid { Children.Count: 2 } layout || layout.Children[1] is not ToggleSwitch toggle)
                return;

            var stacked = e.NewSize.Width < 360;
            layout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumn(toggle, stacked ? 0 : 1);
            Grid.SetRow(toggle, stacked ? 1 : 0);
            toggle.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
    }
}
