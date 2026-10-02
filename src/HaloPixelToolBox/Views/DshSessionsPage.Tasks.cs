using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;

namespace HaloPixelToolBox.Views;

public sealed partial class DshSessionsPage
{
    private ContentDialog? taskDialog;
    private Action? taskDialogContextChanged;

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
        var interaction = snapshot.PendingInteractions.FirstOrDefault();
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = snapshot.StatusText, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = snapshot.WorkingDirectory, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (!string.IsNullOrWhiteSpace(snapshot.Detail)) body.Children.Add(TaskDetailText(snapshot.Detail));
        if (!string.IsNullOrWhiteSpace(ViewModel.TaskOperationStatus)) body.Children.Add(TaskDetailText(ViewModel.TaskOperationStatus));
        var answers = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        var knownApproval = interaction?.Type == "approval";
        var knownQuestion = interaction?.Type == "question" && interaction.Questions.Count > 0;
        if (interaction is not null)
        {
            body.Children.Add(new TextBlock { Text = $"待处理 {snapshot.PendingInteractions.Count} 项 · 当前请求 {interaction.Id}", FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (!string.IsNullOrWhiteSpace(interaction.ToolName)) body.Children.Add(TaskDetailText($"工具：{interaction.ToolName}"));
            if (!string.IsNullOrWhiteSpace(interaction.Reason)) body.Children.Add(TaskDetailText(interaction.Reason));
            foreach (var question in interaction.Questions)
            {
                if (!string.IsNullOrWhiteSpace(question.Header)) body.Children.Add(new TextBlock { Text = question.Header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(TaskDetailText(question.Question));
                if (question.Options.Count > 0)
                    body.Children.Add(new TextBlock { Text = string.Join("\n", question.Options.Select(option => string.IsNullOrWhiteSpace(option.Description) ? option.Label : $"{option.Label}：{option.Description}")), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                if (!answers.ContainsKey(question.Id))
                {
                    var answer = new TextBox { PlaceholderText = question.MultiSelect ? "填写选择，可列出多项；也可以自由回答。" : "填写本题答案，也可以自由回答。",
                        MaxLength = 4096, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, MaxHeight = 120 };
                    answers.Add(question.Id, answer);
                    body.Children.Add(answer);
                }
            }
            if (!knownApproval && !knownQuestion) body.Children.Add(TaskDetailText("此请求类型暂不支持从这里答复，请打开原会话处理。"));
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.FinalText)) body.Children.Add(TaskDetailText(snapshot.FinalText));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = string.IsNullOrWhiteSpace(snapshot.Title) ? "音箱任务" : snapshot.Title,
            PrimaryButtonText = knownApproval ? "仅批准本次" : knownQuestion ? "提交答案" : string.Empty,
            SecondaryButtonText = knownApproval ? "拒绝本次" : string.Empty,
            CloseButtonText = "关闭", DefaultButton = ContentDialogButton.None, Content = TaskDialogBody(body)
        };
        bool Current() => pageIsLoaded && generation == scrollGeneration && scope == TaskDialogScope
            && ViewModel.TaskSnapshot.SessionId == snapshot.SessionId;
        bool Pending() => Current() && interaction is not null
            && ViewModel.IsTaskInteractionCurrent(snapshot.SessionId, interaction.Id, interaction.Type);
        void Update()
        {
            dialog.IsPrimaryButtonEnabled = Pending() && (knownApproval || knownQuestion && answers.Values.All(answer => !string.IsNullOrWhiteSpace(answer.Text)));
            dialog.IsSecondaryButtonEnabled = Pending() && knownApproval;
        }
        foreach (var answer in answers.Values) answer.TextChanged += (_, _) => Update();
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !dialog.IsPrimaryButtonEnabled || !Pending();
        dialog.SecondaryButtonClick += (_, args) => args.Cancel = !dialog.IsSecondaryButtonEnabled || !Pending();
        taskDialog = dialog;
        taskDialogContextChanged = () => { if (!Current()) dialog.Hide(); else Update(); };
        Update();
        try
        {
            var result = await dialog.ShowAsync();
            if (!Pending() || interaction is null) return;
            if (knownApproval && result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
                await ViewModel.RespondTaskApprovalAsync(snapshot.SessionId, interaction.Id, result == ContentDialogResult.Primary);
            else if (knownQuestion && result == ContentDialogResult.Primary && answers.Values.All(answer => !string.IsNullOrWhiteSpace(answer.Text)))
                await ViewModel.RespondTaskQuestionAsync(snapshot.SessionId, interaction.Id,
                    answers.ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim(), StringComparer.Ordinal));
        }
        catch (Exception exception) { if (Current()) ViewModel.TaskOperationStatus = $"无法完成本次请求：{exception.Message}"; }
        finally { ClearTaskDialog(dialog); }
    }

    private static TextBlock TaskDetailText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private void ClearTaskDialog(ContentDialog dialog)
    {
        if (ReferenceEquals(taskDialog, dialog)) { taskDialog = null; taskDialogContextChanged = null; }
    }
}
