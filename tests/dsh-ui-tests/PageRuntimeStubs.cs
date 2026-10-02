using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;

namespace Windows.Foundation
{
    public readonly record struct Point(double X, double Y);
    public readonly record struct Size(double Width, double Height);
}
namespace Windows.System { public enum VirtualKey { Up, Down, PageUp, PageDown, Home, End, Enter, Shift, A } }
namespace Windows.UI.Core { [Flags] public enum CoreVirtualKeyStates { None = 0, Down = 1 } }
namespace Microsoft.UI.Input
{
    public static class InputKeyboardSource
    {
        public static Windows.UI.Core.CoreVirtualKeyStates ShiftState;
        public static Windows.UI.Core.CoreVirtualKeyStates GetKeyStateForCurrentThread(Windows.System.VirtualKey key) => ShiftState;
    }
}
namespace Microsoft.UI.Xaml.Navigation { public enum NavigationCacheMode { Required } }
namespace Microsoft.UI.Xaml
{
    public class DependencyObject
    {
        public DependencyObject? Parent;
        public List<DependencyObject> Children { get; } = [];
        public void AddChild(DependencyObject child) { child.Parent = this; Children.Add(child); }
    }
    public class RoutedEventArgs : EventArgs { public object? OriginalSource { get; set; } }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public class UIElement : DependencyObject
    {
        public static readonly object PointerWheelChangedEvent = new(), PointerPressedEvent = new(), PointerReleasedEvent = new(), PointerCanceledEvent = new(), KeyDownEvent = new();
        private readonly Dictionary<object, List<Delegate>> handlers = [];
        public int HandlerCount => handlers.Values.Sum(list => list.Count);
        public void AddHandler(object ev, object handler, bool handledToo)
        {
            if (!handlers.TryGetValue(ev, out var list)) handlers.Add(ev, list = []);
            list.Add((Delegate)handler);
        }
        public void RemoveHandler(object ev, object handler)
        {
            if (handlers.TryGetValue(ev, out var list)) list.RemoveAll(value => ReferenceEquals(value, handler));
        }
        public void Send(object ev, RoutedEventArgs args)
        {
            if (handlers.TryGetValue(ev, out var list)) foreach (var handler in list.ToArray()) handler.DynamicInvoke(this, args);
        }
    }
    public class FrameworkElement : UIElement
    {
        public bool IsLoaded { get; set; } = true;
        public double ActualHeight { get; set; } = 100;
        public double ActualWidth { get; set; } = 700;
        public double TestTop { get; set; }
        public double MinHeight { get; set; }
        public double MaxHeight { get; set; }
        public Visibility Visibility { get; set; } = Visibility.Visible;
        public XamlRoot? XamlRoot { get; set; } = new();
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public DispatcherQueue DispatcherQueue { get; } = new();
        public event RoutedEventHandler? Loaded;
        public event RoutedEventHandler? Unloaded;
        public void RaiseLoaded() { IsLoaded = true; Loaded?.Invoke(this, new()); }
        public void RaiseUnloaded() { IsLoaded = false; Unloaded?.Invoke(this, new()); }
        public Microsoft.UI.Xaml.Media.GeneralTransform TransformToVisual(FrameworkElement target) => new(TestTop);
    }
    public enum GridUnitType { Pixel, Star, Auto }
    public class XamlRoot { public Windows.Foundation.Size Size { get; set; } = new(800,640); }
    public enum FocusState { Programmatic }
    public enum TextWrapping { Wrap }
    public readonly record struct GridLength(double Value, GridUnitType Type = GridUnitType.Pixel);
    public sealed class SizeChangedEventArgs(Windows.Foundation.Size size) : EventArgs { public Windows.Foundation.Size NewSize => size; }
}
namespace Microsoft.UI.Xaml.Input
{
    public delegate void PointerEventHandler(object sender, PointerRoutedEventArgs e);
    public delegate void KeyEventHandler(object sender, KeyRoutedEventArgs e);
    public sealed class Pointer(uint id) { public uint PointerId => id; }
    public sealed class PointerRoutedEventArgs : RoutedEventArgs { public Pointer Pointer { get; set; } = new(1); }
    public sealed class KeyRoutedEventArgs : RoutedEventArgs { public Windows.System.VirtualKey Key { get; set; } public bool Handled { get; set; } }
}
namespace Microsoft.UI.Xaml.Media
{
    public sealed class GeneralTransform(double top) { public Windows.Foundation.Point TransformPoint(Windows.Foundation.Point point) => new(point.X, point.Y + top); }
    public static class VisualTreeHelper
    {
        public static int GetChildrenCount(DependencyObject parent) => parent.Children.Count;
        public static DependencyObject GetChild(DependencyObject parent, int index) => parent.Children[index];
        public static DependencyObject? GetParent(DependencyObject child) => child.Parent;
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class TextCompositionStartedEventArgs : EventArgs { }
    public sealed class TextCompositionEndedEventArgs : EventArgs { }
    public sealed class TextChangedEventArgs : EventArgs { }
    public class TextBox : FrameworkElement
    {
        private string text = string.Empty;
        public string Text { get => text; set { text = value; TextChanged?.Invoke(this, new()); } }
        public int MaxLength { get; set; }
        public string? PlaceholderText { get; set; }
        public bool AcceptsReturn { get; set; }
        public object? Header { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public event EventHandler<TextChangedEventArgs>? TextChanged;
        public bool Focus(FocusState state) => true;
        public void SelectAll() { }
    }
    public sealed class TextBlock : FrameworkElement
    {
        public string? Text { get; set; }
        public double FontSize { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public bool IsTextSelectionEnabled { get; set; }
        public object? FontWeight { get; set; }
    }
    public sealed class StackPanel : FrameworkElement { public double Spacing { get; set; } }
    public enum ContentDialogButton { None, Primary }
    public enum ContentDialogResult { None, Primary, Secondary }
    public sealed class ContentDialogButtonClickEventArgs : EventArgs { public bool Cancel { get; set; } }
    public sealed class ContentDialog : FrameworkElement
    {
        private TaskCompletionSource<ContentDialogResult>? pending;
        public static Func<ContentDialog, Task<ContentDialogResult>>? NextShow;
        public static ContentDialog? Last;
        public object? Title { get; set; }
        public string? PrimaryButtonText { get; set; }
        public string? SecondaryButtonText { get; set; }
        public string? CloseButtonText { get; set; }
        public ContentDialogButton DefaultButton { get; set; }
        public object? Content { get; set; }
        public bool IsPrimaryButtonEnabled { get; set; }
        public bool IsSecondaryButtonEnabled { get; set; }
        public event EventHandler<ContentDialogButtonClickEventArgs>? PrimaryButtonClick;
        public event EventHandler<ContentDialogButtonClickEventArgs>? SecondaryButtonClick;
        public event EventHandler<object>? Opened;
        public Task<ContentDialogResult> ShowAsync()
        {
            Last = this; pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Opened?.Invoke(this, EventArgs.Empty);
            return NextShow?.Invoke(this) ?? pending.Task;
        }
        public bool ValidatePrimary()
        {
            var args = new ContentDialogButtonClickEventArgs(); PrimaryButtonClick?.Invoke(this, args);
            return IsPrimaryButtonEnabled && !args.Cancel;
        }
        public void Hide() => pending?.TrySetResult(ContentDialogResult.None);
    }
    public class Page : FrameworkElement { public Microsoft.UI.Xaml.Navigation.NavigationCacheMode NavigationCacheMode { get; set; } }
    public class Grid : FrameworkElement
    {
        private static readonly Dictionary<DependencyObject, int> rows = [], columns = [], spans = [];
        public double ColumnSpacing { get; set; }
        public static void SetRow(DependencyObject target, int row) => rows[target] = row;
        public static void SetColumn(DependencyObject target, int column) => columns[target] = column;
        public static void SetColumnSpan(DependencyObject target, int span) => spans[target] = span;
        public static int TestRow(DependencyObject target) => rows.GetValueOrDefault(target);
        public static int TestSpan(DependencyObject target) => spans.GetValueOrDefault(target);
    }
    public sealed class ColumnDefinition { public GridLength Width { get; set; } }
    public enum ScrollIntoViewAlignment { Default, Leading }
    public sealed class ListView : FrameworkElement
    {
        public int ScrollCalls { get; private set; }
        public object? LastScrolledItem { get; private set; }
        public FrameworkElement? LastContainer { get; set; }
        public object? ContainerFromItem(object item) => LastContainer;
        public void ScrollIntoView(object item, ScrollIntoViewAlignment alignment)
        {
            ScrollCalls++; LastScrolledItem = item;
            if (Children.OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
            { viewer.VerticalOffset = viewer.ScrollableHeight; viewer.RaiseViewChanged(false); }
        }
    }
    public sealed class ScrollViewerViewChangedEventArgs(bool isIntermediate) : EventArgs { public bool IsIntermediate => isIntermediate; }
    public sealed class ScrollViewer : FrameworkElement
    {
        public object? Content { get; set; }
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
        public double ScrollableHeight { get; set; } = 1000;
        public double VerticalOffset { get; set; }
        public double ViewportHeight { get; set; } = 300;
        public event EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged;
        public event EventHandler<object>? DirectManipulationStarted;
        public event EventHandler<object>? DirectManipulationCompleted;
        public int ViewSubscriptions => ViewChanged?.GetInvocationList().Length ?? 0;
        public void RaiseViewChanged(bool intermediate) => ViewChanged?.Invoke(this, new(intermediate));
        public void BeginManipulation() => DirectManipulationStarted?.Invoke(this, EventArgs.Empty);
        public void EndManipulation() => DirectManipulationCompleted?.Invoke(this, EventArgs.Empty);
    }
    public sealed class SelectionChangedEventArgs : EventArgs { public List<object> AddedItems { get; } = []; public List<object> RemovedItems { get; } = []; }
    public sealed class ItemClickEventArgs(object item) : EventArgs { public object ClickedItem => item; }
}
namespace Microsoft.UI.Text { public static class FontWeights { public static object SemiBold { get; } = new(); } }
namespace Microsoft.UI.Xaml.Controls { public enum ScrollBarVisibility { Disabled, Auto } }
namespace Microsoft.UI.Xaml.Controls.Primitives { public sealed class ScrollBar : FrameworkElement { } }
namespace HaloPixelToolBox.Views
{
    public sealed partial class DshSessionsPage
    {
        private ColumnDefinition SessionDetailsColumn = new(), SessionListColumn = new();
        private Grid SessionsWorkspace = new(), ConnectionLayout = new(), ConnectionActions = new();
        private ColumnDefinition ConnectionActionColumn0 = new(), ConnectionActionColumn1 = new(), ConnectionActionColumn2 = new(), ConnectionActionColumn3 = new();
        private FrameworkElement NewTaskButton = new(), VoiceActionButton = new(), ConnectDshButton = new(), RefreshSessionsButton = new();
        private ListView HistoryListView = new();
        private TextBox ComposerTextBox = new();
        private TextBlock ComposerHintText = new();
        public ListView TestHistory => HistoryListView;
        public TextBox TestComposer => ComposerTextBox;
        public ScrollViewer TestViewer => HistoryListView.Children.OfType<ScrollViewer>().Single();
        private void InitializeComponent() => HistoryListView.AddChild(new ScrollViewer());
    }
}
