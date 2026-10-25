using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MultiFunPlayer.OutputTarget.Tracking;
using MultiFunPlayer.UI.Controls.ViewModels;

namespace MultiFunPlayer.UI.Controls;
internal partial class TestRecordingPanel : UserControl
{
    private const string Root = @"D:\0SR-plus-work\实机测试";
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private AnnotationKeyboard keyboard;
    private sealed record KeyContext(bool OwnAllowed, string Folder, double Seconds, bool Playing, string Settings);
    private KeyContext keyContext;
    private string currentVideo;
    private double currentDuration;
    private AnnotationStore.Entry selectedTimeline;
    private string previousSettings;
    private string annotationFolder;
    private readonly System.Collections.ObjectModel.ObservableCollection<AnnotationStore.Entry> marks = new();
    public TestRecordingPanel()
    {
        InitializeComponent();
        AnnotationList.ItemsSource = marks;
        marks.CollectionChanged += (_, _) => RenderTimeline();
        timer.Tick += (_, _) => Capture();
        ShortcutEnabled.Unchecked += (_, _) => Volatile.Write(ref keyContext, null);
        System.Windows.Input.Keyboard.AddGotKeyboardFocusHandler(this, (_, _) => UpdateKeyFocus());
        System.Windows.Input.Keyboard.AddLostKeyboardFocusHandler(this, (_, _) => UpdateKeyFocus());
        Loaded += (_, _) => {
            timer.Start();
            try { keyboard ??= new AnnotationKeyboard(CanAnnotateKey, ShortcutMark, ex => Dispatcher.BeginInvoke(new Action(() => LastNote.Text = "快捷键异常：" + ex.Message))); }
            catch (Exception ex) { LastNote.Text = "数字快捷键启用失败：" + ex.Message; }
        };
        Unloaded += async (_, _) => { timer.Stop(); Volatile.Write(ref keyContext, null); keyboard?.Dispose(); keyboard = null; await TestRecording.StopAsync(); };
    }
    private void Capture()
    {
        StatusText.Text = TestRecording.Status + (TestRecording.DirectoryPath == null ? "" : " · " + TestRecording.DirectoryPath);
        StartButton.IsEnabled = !TestRecording.Active; StopButton.IsEnabled = TestRecording.Active;
        if (!TestRecording.Active || DataContext is not ScriptViewModel vm)
        {
            Volatile.Write(ref keyContext, null);
            ShortcutStatus.Text = "快捷键未启用：请先开始记录";
            return;
        }
        var settings = vm.TestRecordingSettings();
        using (var json = System.Text.Json.JsonDocument.Parse(settings))
        {
            var video = json.RootElement.GetProperty("mediaPath").GetString();
            if (video != currentVideo || currentDuration != vm.MediaDuration)
            {
                currentVideo = video; currentDuration = vm.MediaDuration;
                selectedTimeline = null; SelectedMarkPanel.Visibility = Visibility.Collapsed;
                RenderTimeline();
            }
        }
        var ownAllowed = OwnFocusAllowed();
        Volatile.Write(ref keyContext, ShortcutEnabled.IsChecked == true
            ? new KeyContext(ownAllowed, TestRecording.DirectoryPath, vm.MediaPosition, vm.IsPlaying, settings) : null);
        ShortcutStatus.Text = ShortcutEnabled.IsChecked != true ? "数字快捷键已关闭" :
            !ownAllowed ? "MFP输入框内不标注；切到MPV可用" : keyboard?.Status ?? "快捷键尚未启动";        if (settings != previousSettings)
        {
            TestRecording.Record("ui_settings_observed", new { json = settings, resolutionMs = 100 });
            previousSettings = settings;
        }
        TestRecording.Record("playback_cursor", new { vm.MediaPosition, vm.PlaybackSpeed, vm.IsPlaying });
    }
    private void StartClick(object sender, RoutedEventArgs e)
    {
        try { TestRecording.Start(Root); previousSettings = null; annotationFolder = TestRecording.DirectoryPath; marks.Clear(); AnnotationFolder.Text = annotationFolder; Capture(); }
        catch (Exception ex) { StatusText.Text = "无法开始记录：" + ex.Message; }
    }
    private async void StopClick(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        try
        {
            // This project weaves awaits to ConfigureAwait(false); WPF updates must resume on the UI thread.
            await TestRecording.StopAsync().ConfigureAwait(true);
            Capture();
        }
        catch (Exception ex)
        {
            StatusText.Text = "停止记录失败：" + ex.Message;
            StartButton.IsEnabled = !TestRecording.Active;
            StopButton.IsEnabled = TestRecording.Active;
        }
    }
    private static bool OwnFocusAllowed()
    {
        var focus = System.Windows.Input.Keyboard.FocusedElement;
        return focus is not System.Windows.Controls.Primitives.TextBoxBase && focus is not PasswordBox
            && !System.Windows.Interop.ComponentDispatcher.IsThreadModal;
    }
    private void UpdateKeyFocus()
    {
        var context = Volatile.Read(ref keyContext);
        if (context != null) Volatile.Write(ref keyContext, context with { OwnAllowed = OwnFocusAllowed() });
    }
    private bool CanAnnotateKey(bool ownWindow)
    {
        var context = Volatile.Read(ref keyContext);
        return TestRecording.Active && context != null && (!ownWindow || context.OwnAllowed);
    }
    private void ShortcutMark(string label)
    {
        // Hook thread never accesses WPF or waits for disk. Snapshot refreshes with playback cursor.
        var context = Volatile.Read(ref keyContext);
        if (context == null || !TestRecording.Active || context.Folder != TestRecording.DirectoryPath) return;
        var id = TestRecording.Annotate(label, "", context.Seconds, context.Playing, context.Settings);
        Dispatcher.BeginInvoke(new Action(() => {
            try {
                if (annotationFolder != context.Folder && !LoadMarks(context.Folder)) return;
                var entry = AnnotationStore.Create(id, context.Seconds, label, "", context.Settings);
                if (!marks.Any(m => m.Id == id)) marks.Add(entry);


                LastNote.Text = "✓ 快捷键已标记 " + entry.Display;
                if (MarkSound.IsChecked == true) System.Media.SystemSounds.Asterisk.Play();
                MarkCount.Text = $"当前列表：{marks.Count} 条标注";
            } catch (Exception ex) { LastNote.Text = "标注列表更新失败：" + ex.Message; }
        }));
    }    private void MarkClick(object sender, RoutedEventArgs e)
    {
        if (!TestRecording.Active) { LastNote.Text = "请先开始记录"; return; }
        if (DataContext is not ScriptViewModel vm) return;
        var label = (sender as Button)?.Content?.ToString();
        if (annotationFolder != TestRecording.DirectoryPath && !LoadMarks(TestRecording.DirectoryPath)) return;
        var settings = vm.TestRecordingSettings();
        var id = TestRecording.Annotate(label, NoteBox.Text, vm.MediaPosition, vm.IsPlaying, settings);
        var entry = AnnotationStore.Create(id, vm.MediaPosition, label, NoteBox.Text, settings);
        marks.Add(entry);
        LastNote.Text = "已标记 " + entry.Display;
        MarkCount.Text = $"{marks.Count} 条标注";
        NoteBox.Clear();
    }
    private bool LoadMarks(string folder)
    {
        if (folder == null) return false;
        try
        {
            var loaded = AnnotationStore.Load(folder);
            marks.Clear(); foreach (var mark in loaded) marks.Add(mark);
            annotationFolder = folder; AnnotationFolder.Text = folder; MarkCount.Text = $"当前列表：{marks.Count} 条标注"; return true;
        }
        catch (Exception ex) { LastNote.Text = "读取标注失败：" + ex.Message; return false; }
    }
    private void RefreshMarksClick(object sender, RoutedEventArgs e) => LoadMarks(TestRecording.DirectoryPath);
    private void OpenMarksClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "测试记录 (events.jsonl)|events.jsonl", InitialDirectory = Root };
        if (dialog.ShowDialog() == true) LoadMarks(System.IO.Path.GetDirectoryName(dialog.FileName));
    }
    private void AnnotationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteMarkButton.IsEnabled = AnnotationList.SelectedItem is AnnotationStore.Entry;
        if (AnnotationList.SelectedItem is AnnotationStore.Entry entry) SelectTimeline(entry);
    }
    private void DeleteMarkClick(object sender, RoutedEventArgs e)
    {
        var entry = (sender as FrameworkElement)?.Tag as AnnotationStore.Entry ?? AnnotationList.SelectedItem as AnnotationStore.Entry;
        if (entry == null || annotationFolder == null) { LastNote.Text = "请先选择标注，或打开历史记录"; return; }
        try
        {
            AnnotationStore.Delete(annotationFolder, entry.Id);
            if (selectedTimeline?.Id == entry.Id) { selectedTimeline = null; SelectedMarkPanel.Visibility = Visibility.Collapsed; }
            marks.Remove(entry); MarkCount.Text = $"当前列表：{marks.Count} 条标注";
            LastNote.Text = "已删除标注：" + entry.Display;
        }
        catch (Exception ex) { LastNote.Text = "删除失败，标注已保留：" + ex.Message; }
    }
    private void TimelineSeek(object sender, SeekRequestEventArgs e)
    { if (DataContext is ScriptViewModel vm) vm.OnSeekRequest(sender, e); }
    private void TimelineSizeChanged(object sender, SizeChangedEventArgs e)
    { if (e.WidthChanged) RenderTimeline(); }
    private void SelectTimeline(AnnotationStore.Entry entry)
    {
        selectedTimeline = entry;
        SelectedMarkText.Text = entry.Display;
        SelectedMarkPanel.Visibility = Visibility.Visible;
        RenderTimeline();
    }
    private void DeleteSelectedTimeline(object sender, RoutedEventArgs e)
    { if (selectedTimeline != null) DeleteMarkClick(new Button { Tag = selectedTimeline }, e); }
    private void RenderTimeline()
    {
        if (AnnotationTimeline == null) return;
        AnnotationTimeline.Children.Clear();
        var video = currentVideo;
        if (string.IsNullOrEmpty(video) && !TestRecording.Active) video = marks.FirstOrDefault()?.Video;
        var visible = marks.Where(m => string.Equals(m.Video, video, StringComparison.OrdinalIgnoreCase) && double.IsFinite(m.Seconds) && m.Seconds >= 0).OrderBy(m => m.Seconds).ToList();
        var width = Math.Max(100, AnnotationTimeline.ActualWidth);
        var duration = double.IsFinite(currentDuration) && currentDuration > 0 ? currentDuration : Math.Max(1, visible.Select(m => m.Seconds).DefaultIfEmpty(1).Max());
        var rowEnds = new List<double>();
        foreach (var entry in visible)
        {
            var x = Math.Clamp(entry.Seconds / duration * width, 0, width);
            var left = Math.Min(x, Math.Max(0, width - 110));
            var row = rowEnds.FindIndex(end => end + 4 <= left);
            if (row < 0) { row = rowEnds.Count; rowEnds.Add(0); }
            rowEnds[row] = left + 110;
            var line = new System.Windows.Shapes.Line {X1=x,X2=x,Y1=0,Y2=5+row*25,Stroke=System.Windows.Media.Brushes.Teal,StrokeThickness=1,IsHitTestVisible=false};
            AnnotationTimeline.Children.Add(line);
            var button = new Button {Content=$"{TimeSpan.FromSeconds(entry.Seconds):mm\\:ss} {entry.Label}", Width=110, Height=23, Padding=new Thickness(2), FontSize=10, ToolTip=entry.Display,
                Background=selectedTimeline?.Id==entry.Id ? System.Windows.Media.Brushes.Gold : System.Windows.Media.Brushes.LightBlue,
                Foreground=System.Windows.Media.Brushes.Black, Tag=entry};
            button.Click += (_, _) => SelectTimeline(entry);
            Canvas.SetLeft(button,left); Canvas.SetTop(button,5+row*25);
            AnnotationTimeline.Children.Add(button);
        }
        AnnotationTimeline.Height = Math.Max(30, rowEnds.Count*25+6);
        if (visible.Count == 0)
        {
            var hint=new TextBlock {Text="当前视频暂无人工标注；开始记录后按 1～6", FontSize=11,Foreground=System.Windows.Media.Brushes.Gray};
            Canvas.SetTop(hint,6); AnnotationTimeline.Children.Add(hint);
        }
        MarkCount.Text=$"当前视频 {visible.Count} 条 / 本记录 {marks.Count} 条";
    }    private void OpenClick(object sender, RoutedEventArgs e)
    {
        try { if (System.IO.Directory.Exists(TestRecording.DirectoryPath)) Process.Start(new ProcessStartInfo(TestRecording.DirectoryPath) { UseShellExecute = true }); }
        catch (Exception ex) { LastNote.Text = ex.Message; }
    }
}






