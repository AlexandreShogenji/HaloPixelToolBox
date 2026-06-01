namespace HaloPixelToolBox.Views;

public sealed partial class PersonalSceneToolPage : Page
{
    public PersonalSceneToolPageViewModel ViewModel { get; } = new();

    public PersonalSceneToolPage()
    {
        InitializeComponent();
    }

    private async void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Core.Models.Scenes.PersonalSceneDefinition scene })
            await ViewModel.SendSceneAsync(scene);
    }

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PersonalSceneCategoryGroup category })
            ViewModel.SelectCategory(category);
    }
}
