using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.Views;

public sealed partial class DshSessionsPage
{
    private ContentDialog? taskDialog;
    private Action? taskDialogContextChanged;
    private readonly DshTaskAnswerDrafts taskAnswerDrafts = new();

    private string TaskDialogScope => App.DshSessions.Current.Home.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant()
        + "\n" + DisplayFeatureProfile.DshProfileName;

    private static bool IsValidTaskDirectory(string text)
    {
        try { return !string.IsNullOrWhiteSpace(text) && !text.Trim().StartsWith(@"\\", StringComparison.Ordinal)
            && Path.IsPathFullyQualified(text.Trim()) && !text.Any(char.IsControl) && Path.GetFullPath(text.Trim()).Length > 0; }
        catch (Exception) { return false; }
    }

    private async void NewSpeakerTask_Click(object sender, RoutedEventArgs e)
    {
        if (!pageIsLoaded || XamlRoot is null || taskDialog is not null || sessionTitleDialog is not null || !ViewModel.CanStartTask) return;
        var generation = scrollGeneration;
        var scope = TaskDialogScope;
        var title = new TextBox { Header = "任务名称", PlaceholderText = "例如：整理项目文档", MaxLength = 200 };
        var root = new TextBox
        {
            Header = "任务根目录",
            Text = string.IsNullOrWhiteSpace(DisplayFeatureProfile.DshTaskRootDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSH任务") : DisplayFeatureProfile.DshTaskRootDirectory,
            PlaceholderText = "选择任务文件夹的上级目录"
        };
        var prompt = new TextBox { Header = "任务指令（可稍后补充）", PlaceholderText = "填写后立即执行；留空后在会话或通过音箱发送…", MaxLength = 4096,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 180 };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(title); body.Children.Add(root); body.Children.Add(prompt);
        body.Children.Add(new TextBlock { Text = "会在根目录下新建独立任务文件夹和会话，并设为音箱任务目标；留空指令时等待后续内容。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "创建音箱任务", PrimaryButtonText = "创建任务", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None, Content = TaskDialogBody(body), IsPrimaryButtonEnabled = false
        };
        bool Current() => pageIsLoaded && generation == scrollGeneration && scope == TaskDialogScope && ViewModel.CanStartTask;
        bool Valid() => Current() && IsValidSessionTitle(title.Text) && IsValidTaskDirectory(root.Text)
            && (prompt.Text?.Trim().Length ?? 0) <= 4096;
        void Update() => dialog.IsPrimaryButtonEnabled = Valid();
        title.TextChanged += (_, _) => Update(); root.TextChanged += (_, _) => Update(); prompt.TextChanged += (_, _) => Update();
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Valid();
        dialog.Opened += (_, _) => title.Focus(FocusState.Programmatic);
        taskDialog = dialog;
        taskDialogContextChanged = () => { if (!Current()) dialog.Hide(); else Update(); };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !Valid()) return;
            await ViewModel.StartTaskAsync(new(root.Text.Trim(), title.Text.Trim(), prompt.Text.Trim()));
        }
        catch (Exception exception) { if (Current()) ViewModel.TaskOperationStatus = $"无法创建音箱任务：{exception.Message}"; }
        finally { ClearTaskDialog(dialog); }
    }

    private ScrollViewer TaskDialogBody(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        MaxHeight = Math.Max(120, Math.Min(420, (XamlRoot?.Size.Height ?? 640) - 220))
    };

    private async void TaskDetails_Click(object sender, RoutedEventArgs e)
    {
        if (!pageIsLoaded || XamlRoot is null || taskDialog is not null || sessionTitleDialog is not null) return;
        var generation = scrollGeneration;
        var scope = TaskDialogScope;
        var snapshot = ViewModel.TaskSnapshot;
        taskAnswerDrafts.Synchronize(scope, snapshot);
        var requests = snapshot.PendingInteractions.Select(DshTaskInteractionIdentity.Capture).ToArray();
        DshTaskInteraction? interaction = requests.FirstOrDefault();
        IReadOnlyList<DshTaskQuestionAnswer> answers = [];
        var unsubscribeAnswers = new List<Action>();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = snapshot.StatusText, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = snapshot.WorkingDirectory, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var operationStatus = TaskDetailText(ViewModel.TaskOperationStatus);
        body.Children.Add(operationStatus);
        var requestBody = new StackPanel { Spacing = 12 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "音箱任务" : snapshot.Title,
            CloseButtonText = "关闭", DefaultButton = ContentDialogButton.None, Content = TaskDialogBody(body)
        };
        bool Current() => pageIsLoaded && generation == scrollGeneration && scope == TaskDialogScope
            && ViewModel.TaskSnapshot.SessionId == snapshot.SessionId;
        bool Pending() => Current() && interaction is not null
            && ViewModel.IsTaskInteractionCurrent(snapshot.SessionId, interaction);
        bool SameRequest() => interaction is not null
            && ViewModel.TaskSnapshot.PendingInteractions.Any(item => DshTaskInteractionIdentity.Matches(item, interaction))
            && App.DshTasks.Current.SessionId == snapshot.SessionId
            && App.DshTasks.Current.PendingInteractions.Any(item => DshTaskInteractionIdentity.Matches(item, interaction));
        void Update()
        {
            var approval = interaction?.Type == "approval";
            var question = interaction?.Type == "question" && interaction.Questions.Count > 0;
            dialog.PrimaryButtonText = approval ? "仅批准本次" : question ? "提交答案" : string.Empty;
            dialog.SecondaryButtonText = approval ? "拒绝本次" : string.Empty;
            dialog.IsPrimaryButtonEnabled = Pending() && (approval || question && answers.Count > 0 && answers.All(answer => answer.HasAnswer));
            dialog.IsSecondaryButtonEnabled = Pending() && approval;
            operationStatus.Text = ViewModel.TaskOperationStatus;
            operationStatus.Visibility = string.IsNullOrWhiteSpace(operationStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        void ClearAnswerHandlers()
        {
            foreach (var unsubscribe in unsubscribeAnswers) unsubscribe();
            unsubscribeAnswers.Clear();
        }
        void ShowRequest()
        {
            ClearAnswerHandlers();
            requestBody.Children.Clear();
            answers = [];
            if (interaction is null)
            {
                if (!string.IsNullOrWhiteSpace(snapshot.Detail)) requestBody.Children.Add(TaskDetailText(snapshot.Detail));
                if (!string.IsNullOrWhiteSpace(snapshot.FinalText)) requestBody.Children.Add(TaskDetailText(snapshot.FinalText));
            }
            else if (SameRequest())
            {
                if (!string.IsNullOrWhiteSpace(interaction.ToolName)) requestBody.Children.Add(TaskDetailText($"工具：{interaction.ToolName}"));
                if (!string.IsNullOrWhiteSpace(interaction.Reason)) requestBody.Children.Add(TaskDetailText(interaction.Reason));
                if (interaction.Type == "question" && interaction.Questions.Count > 0)
                {
                    answers = taskAnswerDrafts.GetOrCreate(scope, snapshot.SessionId, interaction);
                    foreach (var answer in answers)
                        requestBody.Children.Add(CreateTaskQuestionAnswer(answer, Update, unsubscribeAnswers));
                }
                else if (interaction.Type != "approval")
                    requestBody.Children.Add(TaskDetailText("此请求类型暂不支持从这里答复，请打开原会话处理。"));
            }
            Update();
        }
        if (requests.Length > 1)
        {
            var selector = new ComboBox
            {
                Header = $"待处理请求（{requests.Length} 项）", HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = requests.Select((item, index) => $"{index + 1}. " + (item.Type == "approval"
                    ? "授权 · " + item.ToolName : "问题 · " + (item.Questions.FirstOrDefault()?.Header is { Length: > 0 } header ? header : "等待回答"))).ToArray()
            };
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedIndex < 0 || selector.SelectedIndex >= requests.Length) return;
                interaction = requests[selector.SelectedIndex];
                ShowRequest();
            };
            body.Children.Add(selector);
            selector.SelectedIndex = 0;
        }
        body.Children.Add(requestBody);
        ShowRequest();
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !dialog.IsPrimaryButtonEnabled || !Pending();
        dialog.SecondaryButtonClick += (_, args) => args.Cancel = !dialog.IsSecondaryButtonEnabled || !Pending();
        taskDialog = dialog;
        taskDialogContextChanged = () =>
        {
            taskAnswerDrafts.Synchronize(TaskDialogScope, ViewModel.TaskSnapshot);
            Update();
            if (!Current() || interaction is not null && !SameRequest()) dialog.Hide();
        };
        Update();
        try
        {
            var result = await dialog.ShowAsync();
            if (!Pending() || interaction is null) return;
            if (interaction.Type == "approval" && result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
                await ViewModel.RespondTaskApprovalAsync(snapshot.SessionId, interaction, result == ContentDialogResult.Primary);
            else if (interaction.Type == "question" && result == ContentDialogResult.Primary && answers.Count > 0 && answers.All(answer => answer.HasAnswer))
                await ViewModel.RespondTaskQuestionAsync(snapshot.SessionId, interaction,
                    answers.ToDictionary(answer => answer.Question.Id, answer => answer.Answer, StringComparer.Ordinal));
        }
        catch (Exception exception) { if (Current()) ViewModel.TaskOperationStatus = $"无法完成本次请求：{exception.Message}"; }
        finally
        {
            ClearAnswerHandlers();
            taskAnswerDrafts.Synchronize(TaskDialogScope, ViewModel.TaskSnapshot);
            ClearTaskDialog(dialog);
        }
    }

    private StackPanel CreateTaskQuestionAnswer(DshTaskQuestionAnswer answer, Action changed, List<Action> unsubscribe)
    {
        var question = answer.Question;
        var panel = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(question.Header))
            panel.Children.Add(new TextBlock { Text = question.Header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(TaskDetailText(question.Question));
        var optionSetters = new List<Action<bool>>();
        var synchronizing = false;
        var group = "TaskQuestion-" + Guid.NewGuid().ToString("N");
        for (var index = 0; index < question.Options.Count; index++)
        {
            var optionIndex = index;
            var option = question.Options[index];
            var label = new StackPanel { Spacing = 2, MaxWidth = Math.Max(160, Math.Min(380, (XamlRoot?.Size.Width ?? 600) - 160)) };
            label.Children.Add(TaskDetailText(option.Label));
            if (!string.IsNullOrWhiteSpace(option.Description))
                label.Children.Add(new TextBlock { Text = option.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (question.MultiSelect)
            {
                var choice = new CheckBox { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
                choice.Checked += (_, _) => { if (!synchronizing) answer.SetOptionSelected(optionIndex, true); };
                choice.Unchecked += (_, _) => { if (!synchronizing) answer.SetOptionSelected(optionIndex, false); };
                optionSetters.Add(value => choice.IsChecked = value);
                panel.Children.Add(choice);
            }
            else
            {
                var choice = new RadioButton { Content = label, GroupName = group, HorizontalAlignment = HorizontalAlignment.Stretch };
                choice.Checked += (_, _) => { if (!synchronizing) answer.SetOptionSelected(optionIndex, true); };
                optionSetters.Add(value => choice.IsChecked = value);
                panel.Children.Add(choice);
            }
        }
        var freeText = new TextBox
        {
            Header = question.Options.Count > 0 ? "或自行填写答案" : "答案",
            PlaceholderText = "输入本题答案…", MaxLength = 4096, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 52, MaxHeight = 120
        };
        freeText.TextChanged += (_, _) => { if (!synchronizing) answer.FreeText = freeText.Text; };
        panel.Children.Add(freeText);
        void Synchronize()
        {
            synchronizing = true;
            try
            {
                for (var index = 0; index < optionSetters.Count; index++) optionSetters[index](answer.IsOptionSelected(index));
                if (freeText.Text != answer.FreeText) freeText.Text = answer.FreeText;
            }
            finally { synchronizing = false; }
            changed();
        }
        EventHandler handler = (_, _) => Synchronize();
        answer.Changed += handler;
        unsubscribe.Add(() => answer.Changed -= handler);
        Synchronize();
        return panel;
    }

    private static TextBlock TaskDetailText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private void ClearTaskDialog(ContentDialog dialog)
    {
        if (ReferenceEquals(taskDialog, dialog)) { taskDialog = null; taskDialogContextChanged = null; }
    }
}
