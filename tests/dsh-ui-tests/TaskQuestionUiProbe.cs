using System.Reflection;
using HaloPixelToolBox;
using HaloPixelToolBox.Models;
using HaloPixelToolBox.Profiles.CrossVersionProfiles;
using HaloPixelToolBox.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

public static class TaskQuestionUiProbe
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var tasks = App.DshTasks;
        var service = App.DshSessions;
        tasks.Reset();
        DisplayFeatureProfile.DshProfileName = "default";
        var session = new DshSessionSummary("QUESTIONS", "待答任务", @"C:\Tasks\questions", DateTimeOffset.Now, "running");
        service.Publish(new(true, "host", "connected", [session], null)
        {
            Home = @"C:\QuestionHome",
            Capabilities = new(true, true, true, true, true)
            {
                CanMonitorTasks = true, CanRespondToTasks = true, CanAdoptTasks = true, CanPromptTasks = true
            }
        });
        service.HistoryReader = (_, _, _) => Task.FromResult(new DshHistoryPage([], null, false));
        var first = new DshTaskInteraction("FIRST", "question", "ask", "选择内容",
        [
            new("single", "方向", "选择一个方向", [new("灯效", "灯光动画"), new("场景", "像素屏场景")]),
            new("multi", "范围", "选择多个范围", [new("文档", "说明文件"), new("代码", "项目源码")], true)
        ]);
        var second = new DshTaskInteraction("SECOND", "question", "ask", "补充信息",
            [new("text", "补充", "还需要什么？", [])]);
        DshTaskSnapshot Snapshot(params DshTaskInteraction[] requests) => new(session.Id, session.Title, session.WorkingDirectory,
            "waitingInput", "任务等待回答", "", "", requests, true, false);
        tasks.Publish(Snapshot(first, second));
        var page = new DshSessionsPage();
        page.RaiseLoaded();
        void Open() => typeof(DshSessionsPage).GetMethod("TaskDetails_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [page, new RoutedEventArgs()]);
        async Task WaitClosed()
        {
            var field = typeof(DshSessionsPage).GetField("taskDialog", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var i = 0; i < 150 && field.GetValue(page) is not null; i++) await Task.Delay(10);
            if (field.GetValue(page) is not null) throw new Exception("Question dialog did not finish");
        }
        IEnumerable<DependencyObject> Elements(DependencyObject parent)
        {
            foreach (var child in parent.Children)
            {
                yield return child;
                foreach (var descendant in Elements(child)) yield return descendant;
            }
        }
        StackPanel Body(ContentDialog dialog) => (StackPanel)((ScrollViewer)dialog.Content!).Content!;
        T[] Controls<T>(ContentDialog dialog) where T : DependencyObject => Elements(Body(dialog)).OfType<T>().ToArray();

        ContentDialog.NextShow = dialog =>
        {
            check(Controls<ComboBox>(dialog).Length == 1 && !dialog.IsPrimaryButtonEnabled,
                "multiple pending requests expose a selector and no default answer");
            var radios = Controls<RadioButton>(dialog);
            var boxes = Controls<CheckBox>(dialog).Where(box => box is not RadioButton).ToArray();
            check(radios.Length == 2 && boxes.Length == 2 && radios.All(button => button.IsChecked != true),
                "question dialog renders actual single and multi choice controls without preselection");
            radios[0].IsChecked = true;
            check(!dialog.IsPrimaryButtonEnabled, "one completed question does not submit another unanswered question");
            radios[1].IsChecked = true;
            check(radios[0].IsChecked == false && radios[1].IsChecked == true,
                "single question choices remain mutually exclusive");
            boxes[0].IsChecked = true;
            boxes[1].IsChecked = true;
            check(dialog.IsPrimaryButtonEnabled, "explicit multi choices complete the question form");
            var inputs = Controls<TextBox>(dialog);
            inputs[0].Text = "自定义方向";
            check(radios.All(button => button.IsChecked == false) && dialog.IsPrimaryButtonEnabled,
                "custom answer clears single choices without affecting another question");
            radios[1].IsChecked = true;
            check(inputs[0].Text == "", "choosing an option replaces custom answer visibly");
            Controls<ComboBox>(dialog).Single().SelectedIndex = 1;
            check(Controls<TextBox>(dialog).Length == 1 && !dialog.IsPrimaryButtonEnabled,
                "switching pending request opens its own empty answer form");
            Controls<TextBox>(dialog).Single().Text = "第二个请求草稿";
            Controls<ComboBox>(dialog).Single().SelectedIndex = 0;
            check(Controls<RadioButton>(dialog)[1].IsChecked == true && Controls<CheckBox>(dialog).Where(box => box is not RadioButton).All(box => box.IsChecked == true),
                "returning to first request restores its independent selected answers");
            return Task.FromResult(ContentDialogResult.None);
        };
        Open(); await WaitClosed();
        check(tasks.QuestionCalls == 0, "closing an answered form never submits implicitly");
        ContentDialog.NextShow = dialog =>
        {
            check(Controls<RadioButton>(dialog)[1].IsChecked == true && dialog.IsPrimaryButtonEnabled,
                "closing and reopening task details preserves selected answer draft");
            Controls<ComboBox>(dialog).Single().SelectedIndex = 1;
            check(Controls<TextBox>(dialog).Single().Text == "第二个请求草稿", "other request draft also survives dialog close");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        Open(); await WaitClosed();
        check(tasks.QuestionCalls == 1 && tasks.LastQuestion!.Value.Id == second.Id
            && tasks.LastQuestion.Value.Answers["text"] == "第二个请求草稿", "selected later request is submitted under its own exact identity");

        ContentDialog.NextShow = dialog =>
        {
            check(Controls<ComboBox>(dialog).Length == 0 && dialog.IsPrimaryButtonEnabled,
                "remaining request retains draft after another request resolves");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        tasks.QuestionResponder = (_, _, _) => Task.FromException(new IOException("临时连接失败"));
        Open(); await WaitClosed();
        check(tasks.QuestionCalls == 1, "failed answer transmission is not retried or reported submitted");
        tasks.QuestionResponder = null;
        ContentDialog.NextShow = dialog =>
        {
            check(Controls<RadioButton>(dialog)[1].IsChecked == true && dialog.IsPrimaryButtonEnabled,
                "unconfirmed transmission retains original choice draft for review");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        Open(); await WaitClosed();
        check(tasks.LastQuestion!.Value.Id == first.Id && tasks.LastQuestion.Value.Answers["single"] == "场景"
            && tasks.LastQuestion.Value.Answers["multi"] == "文档 | 代码", "multi choice form submits all selected labels in protocol format");

        tasks.Publish(Snapshot(first));
        ContentDialog.NextShow = dialog =>
        {
            check(Controls<RadioButton>(dialog).All(button => button.IsChecked == false) && !dialog.IsPrimaryButtonEnabled,
                "resolved request cannot resurrect old answer if its id later reappears");
            Controls<TextBox>(dialog)[0].Text = "草稿";
            tasks.Publish(Snapshot(first with { Questions = [first.Questions[0] with { Question = "已更换的问题" }, first.Questions[1]] }));
            check(!dialog.IsPrimaryButtonEnabled, "same request id with changed content disables stale answer form");
            return Task.FromResult(ContentDialogResult.Primary);
        };
        var beforeStale = tasks.QuestionCalls;
        Open(); await WaitClosed();
        check(tasks.QuestionCalls == beforeStale, "same-id question replacement never receives a stale draft");

        tasks.Publish(Snapshot(first));
        ContentDialog.NextShow = dialog =>
        {
            Controls<TextBox>(dialog)[0].Text = "断线前的草稿";
            tasks.Publish(Snapshot() with { State = "disconnected", StatusText = "状态暂不可用" });
            check(!dialog.IsPrimaryButtonEnabled, "disconnect immediately removes authority to submit an open form");
            return Task.FromResult(ContentDialogResult.None);
        };
        Open(); await WaitClosed();
        tasks.Publish(Snapshot(first));
        ContentDialog.NextShow = dialog =>
        {
            check(Controls<TextBox>(dialog)[0].Text == "断线前的草稿", "same request after reconnect restores in-memory draft without submitting it");
            return Task.FromResult(ContentDialogResult.None);
        };
        Open(); await WaitClosed();
        page.RaiseUnloaded();
        tasks.Publish(Snapshot());
        tasks.Publish(Snapshot(first));
        page.RaiseLoaded();
        ContentDialog.NextShow = dialog =>
        {
            check(Controls<TextBox>(dialog)[0].Text == "", "cached page reattachment cannot revive drafts whose request changes were unobserved");
            Controls<TextBox>(dialog)[0].Text = "弹窗关闭后的草稿";
            return Task.FromResult(ContentDialogResult.None);
        };
        Open(); await WaitClosed();
        tasks.Publish(Snapshot());
        tasks.Publish(Snapshot(first));
        ContentDialog.NextShow = dialog =>
        {
            check(Controls<TextBox>(dialog)[0].Text == "", "requests resolved while dialog is closed still invalidate old drafts");
            return Task.FromResult(ContentDialogResult.None);
        };
        Open(); await WaitClosed();
        page.RaiseUnloaded();
        ContentDialog.NextShow = null;
        tasks.Reset();
    }
}
