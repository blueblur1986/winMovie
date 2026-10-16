using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace WinMovie;

public sealed partial class MainWindow : Window
{
    private MovieProject _project = new();
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly string _thumbDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinMovieThumbs");
    private readonly string _previewDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinMoviePreviews");
    private MediaAsset? _selectedAsset;
    private int _selectedClip = -1;
    private string? _projectPath;
    private string? _previewPath;
    private double _rangeStart, _rangeEnd;
    private bool _draggingRange, _loadingControls, _seeking, _previewPlayback, _rangeExplicit, _exporting, _pendingTimelinePlay;
    private bool _boundarySelected;
    private CancellationTokenSource? _previewRenderCts, _exportCts;
    private string? _timelinePreviewPath;
    private int _previewRenderVersion;
    private readonly List<string> _stalePreviews = [];
    private double? _pendingPosition;
    private DateTime _lastSkim = DateTime.MinValue;
    private readonly HashSet<string> _loadingFilmstrips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Canvas> _mediaStrips = new(StringComparer.OrdinalIgnoreCase);
    private Canvas? _dragStrip;
    private int _rangeDragMode;
    private int _selectedBoundary = -1;
    private const double StripWidth = 480;
    private const double StripHeight = 74;
    private sealed record DragPayload(string Kind, string? Path = null, double In = 0, double Out = 0, int Index = -1, TransitionKind Transition = TransitionKind.Cut);

    public MainWindow()
    {
        InitializeComponent();
        Title = "WinMovie · 1.0 Beta 3";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "WinMovie.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        Preview.SetMediaPlayer(_player);
        _player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_pendingPosition is { } position)
            {
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
                _pendingPosition = null;
            }
        });
        _clock.Tick += Clock_Tick;
        _clock.Start();
        TitleBox.SelectedIndex = 0;
        VolumeSlider.Value = 1;
        EditorSurface.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Editor_KeyDown), true);
        RefreshAll();
    }

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".mp4"); picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".mkv"); picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".m4v"); picker.FileTypeFilter.Add(".webm");
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        var imported = 0;
        foreach (var file in files)
        {
            if (_project.Assets.Any(x => string.Equals(x.Path, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                Status.Text = $"Reading {file.Name}…";
                var asset = await MediaEngine.ProbeAsync(file.Path);
                _project.Assets.Add(asset);
                imported++;
            }
            catch (Exception ex) { await ErrorAsync($"Could not import {file.Name}", ex); }
        }
        RefreshAssets();
        if (_selectedAsset is null && _project.Assets.Count > 0) await SelectAssetAsync(_project.Assets[0]);
        Status.Text = $"Imported {imported} video{(imported == 1 ? "" : "s")}.";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _player.Pause();
        _project = new MovieProject();
        _projectPath = null;
        _selectedAsset = null;
        _selectedClip = -1;
        _previewPath = null;
        _player.Source = null;
        CancelPreviewRender();
        _previewPlayback = false;
        _rangeExplicit = false;
        _boundarySelected = false;
        _selectedBoundary = -1;
        _mediaStrips.Clear();
        RefreshAll();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".winmovie");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var project = JsonSerializer.Deserialize<MovieProject>(await File.ReadAllTextAsync(file.Path));
            _project = project ?? throw new InvalidOperationException("The project file is empty.");
            _projectPath = file.Path;
            _selectedAsset = null;
            _selectedClip = -1;
            _previewPath = null;
            _player.Source = null;
            CancelPreviewRender();
            _previewPlayback = false;
            _rangeExplicit = false;
            _boundarySelected = false;
            _selectedBoundary = -1;
            RefreshAll();
            if (_project.Assets.Count > 0) await SelectAssetAsync(_project.Assets[0]);
            InvalidateTimelinePreview();
            Status.Text = $"Opened {file.Name}";
        }
        catch (Exception ex) { await ErrorAsync("Could not open project", ex); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_projectPath is null)
            {
                var picker = new FileSavePicker { SuggestedFileName = "My Movie" };
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
                picker.FileTypeChoices.Add("WinMovie project", new List<string> { ".winmovie" });
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                _projectPath = file.Path;
            }
            await File.WriteAllTextAsync(_projectPath, JsonSerializer.Serialize(_project, new JsonSerializerOptions { WriteIndented = true }));
            Status.Text = $"Saved {System.IO.Path.GetFileName(_projectPath)}";
        }
        catch (Exception ex) { await ErrorAsync("Could not save project", ex); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Clips.Count == 0) { await MessageAsync("Nothing to export", "Add a selected range to the timeline first."); return; }
        int maxHeight;
        string encoder;
        try
        {
            Status.Text = "Checking source resolution and graphics hardware…";
            maxHeight = await MediaEngine.MaxSourceHeightAsync(_project);
            encoder = await MediaEngine.DetectBestEncoderAsync();
        }
        catch (Exception ex) { await ErrorAsync("Could not inspect export options", ex); return; }
        var resolutions = MediaEngine.ResolutionOptions(maxHeight);
        if (resolutions.Count == 0) { await MessageAsync("No video size available", "Import a valid video first."); return; }
        var box = new ComboBox { MinWidth = 230 };
        foreach (var resolution in resolutions) box.Items.Add(new ComboBoxItem { Content = $"{resolution.Label}  ·  {resolution.Width} × {resolution.Height}", Tag = resolution });
        box.SelectedIndex = Math.Max(0, resolutions.ToList().FindLastIndex(x => x.Height <= 1080));
        var dialog = new ContentDialog
        {
            Title = "Export MP4",
            Content = new StackPanel { Spacing = 10, Children =
                { new TextBlock { Text = $"Resolution · source limit {maxHeight}p" }, box,
                  new TextBlock { Text = $"Encoder: {encoder} (selected automatically; software fallback available)", TextWrapping = TextWrapping.Wrap } } },
            PrimaryButtonText = "Export", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var selected = (VideoResolution)((ComboBoxItem)box.SelectedItem).Tag;
        var picker = new FileSavePicker { SuggestedFileName = "My Movie" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var stagedOutput = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file.Path)!,
            $".WinMovie-{Guid.NewGuid():N}.mp4");
        var resumePreview = _timelinePreviewPath is null && _project.Clips.Count > 0;
        if (resumePreview) CancelPreviewRender();
        _player.Pause();
        _exporting = true;
        _exportCts = new CancellationTokenSource();
        EditorHost.IsEnabled = false;
        ExportOverlay.Visibility = Visibility.Visible;
        ExportCancelButton.Focus(FocusState.Programmatic);
        ExportOverlayProgress.IsIndeterminate = false;
        ExportOverlayProgress.Value = 0;
        ExportOverlayMessage.Text = "Preparing clips…";
        Exception? failure = null;
        var cancelled = false;
        try
        {
            IProgress<(double Value, string Message)> progress = new Progress<(double Value, string Message)>(p =>
            {
                ExportOverlayProgress.IsIndeterminate = p.Message == "Encoding final video";
                if (!ExportOverlayProgress.IsIndeterminate) ExportOverlayProgress.Value = p.Value;
                ExportOverlayMessage.Text = p.Message;
                Status.Text = p.Message;
            });
            await MediaEngine.ExportAsync(_project, stagedOutput,
                new ExportSettings(selected.Width, selected.Height, encoder),
                (value, message) => progress.Report((value, message)), _exportCts.Token);
            _exportCts.Token.ThrowIfCancellationRequested();
            File.Move(stagedOutput, file.Path, true);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { File.Delete(stagedOutput); } catch { }
            _exportCts.Dispose(); _exportCts = null;
            _exporting = false;
            EditorHost.IsEnabled = true;
            ExportOverlay.Visibility = Visibility.Collapsed;
            if (resumePreview) InvalidateTimelinePreview();
        }
        if (failure is not null) await ErrorAsync("Could not export MP4", failure);
        else if (cancelled) Status.Text = "Export cancelled.";
        else await MessageAsync("Export complete", $"Saved {file.Path}");
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        ExportOverlayMessage.Text = "Cancelling export…";
        _exportCts?.Cancel();
    }

    private void RefreshAll()
    {
        RefreshAssets();
        RefreshTimeline();
        RefreshInspector();
        RangeLabel.Text = _selectedAsset is null ? "Import a video to begin" : FormatRange();
    }

    private void RefreshAssets()
    {
        MediaList.Children.Clear();
        _mediaStrips.Clear();
        MediaCount.Text = $"{_project.Assets.Count} clips";
        foreach (var asset in _project.Assets)
        {
            var card = new StackPanel { Spacing = 5 };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = asset.Name, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var duration = new TextBlock { Text = Time(asset.Duration), Opacity = .65, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(duration, 1);
            header.Children.Add(name); header.Children.Add(duration);
            card.Children.Add(header);
            var strip = new Canvas { Width = StripWidth, Height = StripHeight, Tag = asset, Background = Brush("#252B31") };
            for (var i = 0; i < 6; i++)
            {
                var placeholder = new Border { Width = 80, Height = StripHeight, Background = Brush(i % 2 == 0 ? "#303942" : "#39434D"), IsHitTestVisible = false };
                Canvas.SetLeft(placeholder, i * 80);
                strip.Children.Add(placeholder);
                if (i < asset.FilmstripFrames.Count && File.Exists(asset.FilmstripFrames[i]))
                {
                    var frame = new Image { Source = new BitmapImage(new Uri(asset.FilmstripFrames[i])), Width = 80, Height = StripHeight,
                        Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
                    Canvas.SetLeft(frame, i * 80);
                    strip.Children.Add(frame);
                }
            }
            strip.PointerPressed += Filmstrip_PointerPressed;
            strip.PointerMoved += Filmstrip_PointerMoved;
            strip.PointerReleased += Filmstrip_PointerReleased;
            strip.PointerCaptureLost += Filmstrip_PointerCaptureLost;
            _mediaStrips[asset.Path] = strip;
            UpdateRangeOverlay(strip, asset);
            var scroller = new ScrollViewer { Content = strip, HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled };
            card.Children.Add(scroller);
            var drag = new Border { Background = Brush("#334E78"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3),
                CanDrag = true, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = "↗ Drag selected range to timeline", FontSize = 11 } };
            drag.DragStarting += (_, args) => StartSourceDrag(asset, args);
            card.Children.Add(drag);
            var outer = new Border { Background = asset == _selectedAsset ? Brush("#253A54") : Brush("#1D2329"),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = card };
            MediaList.Children.Add(outer);
            if (asset.FilmstripFrames.Count == 0 && asset.ThumbnailPath != "failed" && !_loadingFilmstrips.Contains(asset.Path) && File.Exists(asset.Path))
                _ = LoadAssetFilmstripAsync(asset);
        }
    }

    private async Task LoadAssetFilmstripAsync(MediaAsset asset)
    {
        _loadingFilmstrips.Add(asset.Path);
        try
        {
            var frames = new List<string>();
            for (var i = 0; i < 6; i++)
            {
                try { frames.Add(await MediaEngine.ThumbnailAsync(asset.Path, asset.Duration * (i + .5) / 6, _thumbDirectory)); }
                catch { break; }
            }
            asset.FilmstripFrames = frames;
            asset.ThumbnailPath = frames.FirstOrDefault() ?? "failed";
        }
        finally
        {
            _loadingFilmstrips.Remove(asset.Path);
            if (!_draggingRange && _project.Assets.Contains(asset)) { RefreshAssets(); RefreshTimeline(); }
        }
    }

    private async Task SelectAssetAsync(MediaAsset asset)
    {
        _selectedAsset = asset;
        _selectedClip = -1;
        _boundarySelected = false;
        _rangeStart = 0;
        _rangeEnd = asset.Duration;
        _rangeExplicit = false;
        _previewPlayback = false;
        _player.Pause();
        _player.PlaybackSession.PlaybackRate = 1;
        _player.Volume = 1;
        LoadPreview(asset.Path, 0);
        RefreshAssets();
        RefreshTimeline();
        RefreshInspector();
        RangeLabel.Text = FormatRange();
        PreviewMode.Text = asset.Name;
        await Task.CompletedTask;
    }

    private static double FilmTime(MediaAsset asset, double x) => Math.Clamp(x / StripWidth * asset.Duration, 0, asset.Duration);

    private void Filmstrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas strip || strip.Tag is not MediaAsset asset) return;
        if (e.OriginalSource is FrameworkElement { Tag: "selection", CanDrag: true }) return;
        if (_selectedAsset != asset)
        {
            _selectedAsset = asset;
            _selectedClip = -1;
            _boundarySelected = false;
            _rangeStart = 0;
            _rangeEnd = asset.Duration;
            _rangeExplicit = false;
            _previewPlayback = false;
            _player.Pause();
            _player.PlaybackSession.PlaybackRate = 1;
            _player.Volume = 1;
            LoadPreview(asset.Path, 0);
            PreviewMode.Text = asset.Name;
            RefreshTimeline();
            RefreshInspector();
        }
        _dragStrip = strip;
        _draggingRange = true;
        var x = e.GetCurrentPoint(strip).Position.X;
        var left = Math.Min(_rangeStart, _rangeEnd) / asset.Duration * StripWidth;
        var right = Math.Max(_rangeStart, _rangeEnd) / asset.Duration * StripWidth;
        _rangeDragMode = _rangeExplicit && Math.Abs(x - left) <= 9 ? 1 :
            _rangeExplicit && Math.Abs(x - right) <= 9 ? 2 : 0;
        if (_rangeDragMode == 0) _rangeStart = _rangeEnd = FilmTime(asset, x);
        strip.CapturePointer(e.Pointer);
        UpdateRangeOverlay(strip, asset);
    }

    private void Filmstrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas strip || strip.Tag is not MediaAsset asset) return;
        var time = FilmTime(asset, e.GetCurrentPoint(strip).Position.X);
        if (_draggingRange && _dragStrip == strip)
        {
            if (_rangeDragMode == 1) _rangeStart = Math.Min(time, _rangeEnd - .08);
            else if (_rangeDragMode == 2) _rangeEnd = Math.Max(time, _rangeStart + .08);
            else _rangeEnd = time;
            UpdateRangeOverlay(strip, asset);
        }
        if ((DateTime.UtcNow - _lastSkim).TotalMilliseconds < 65) return;
        _lastSkim = DateTime.UtcNow;
        _player.Pause();
        LoadPreview(asset.Path, time);
        PreviewTime.Text = Time(time);
    }

    private void Filmstrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingRange || sender is not Canvas strip || strip.Tag is not MediaAsset asset || _dragStrip != strip) return;
        var time = FilmTime(asset, e.GetCurrentPoint(strip).Position.X);
        if (_rangeDragMode == 1) _rangeStart = Math.Min(time, _rangeEnd - .08);
        else if (_rangeDragMode == 2) _rangeEnd = Math.Max(time, _rangeStart + .08);
        else _rangeEnd = time;
        _draggingRange = false;
        _dragStrip = null;
        strip.ReleasePointerCapture(e.Pointer);
        _rangeExplicit = true;
        (_rangeStart, _rangeEnd) = (Math.Min(_rangeStart, _rangeEnd), Math.Max(_rangeStart, _rangeEnd));
        if (Math.Abs(_rangeEnd - _rangeStart) < .1)
        {
            _rangeStart = Math.Max(0, _rangeStart - 1.5);
            _rangeEnd = Math.Min(asset.Duration, _rangeStart + 3);
        }
        UpdateRangeOverlay(strip, asset);
        if (asset.FilmstripFrames.Count > 0) RefreshAssets();
    }

    private void Filmstrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) { _draggingRange = false; _dragStrip = null; }

    private void UpdateRangeOverlay(Canvas strip, MediaAsset asset)
    {
        for (var i = strip.Children.Count - 1; i >= 0; i--)
            if (strip.Children[i] is FrameworkElement element && element.Tag?.ToString() == "selection")
                strip.Children.RemoveAt(i);
        if (_selectedAsset != asset || asset.Duration <= 0 || (!_rangeExplicit && !_draggingRange)) return;
        var left = Math.Min(_rangeStart, _rangeEnd) / asset.Duration * StripWidth;
        var width = Math.Abs(_rangeEnd - _rangeStart) / asset.Duration * StripWidth;
        var selection = new Rectangle { Tag = "selection", Width = Math.Max(2, width), Height = StripHeight,
            Fill = Brush("#44FFCE40"), Stroke = Brush("#FFFFD34E"), StrokeThickness = 3, IsHitTestVisible = false };
        Canvas.SetLeft(selection, left);
        strip.Children.Add(selection);
        if (_rangeExplicit && !_draggingRange && width > 24)
        {
            var dragArea = new Border { Tag = "selection", Width = width - 20, Height = StripHeight - 8,
                Background = Brush("#01FFFFFF"), CanDrag = true,
                Child = new TextBlock { Text = width > 115 ? "↗ Drag range" : "↗", FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brush("#FFFFE6A0"), IsHitTestVisible = false } };
            Canvas.SetLeft(dragArea, left + 10);
            Canvas.SetTop(dragArea, 4);
            dragArea.DragStarting += (_, args) => StartSourceDrag(asset, args);
            strip.Children.Add(dragArea);
        }
        foreach (var edge in new[] { left, left + width })
        {
            var handle = new Rectangle { Tag = "selection", Width = 6, Height = StripHeight, Fill = Brush("#FFFFD34E"), IsHitTestVisible = false };
            Canvas.SetLeft(handle, Math.Clamp(edge - 3, 0, StripWidth - 6));
            strip.Children.Add(handle);
        }
        RangeLabel.Text = FormatRange();
    }

    private string FormatRange() => !_rangeExplicit && _selectedAsset is not null ?
        $"Full clip {Time(_selectedAsset.Duration)} · drag to select a range" :
        $"Selected {Time(Math.Min(_rangeStart, _rangeEnd))}–{Time(Math.Max(_rangeStart, _rangeEnd))}  ({Time(Math.Abs(_rangeEnd - _rangeStart))})";

    private void AddRange_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAsset is null) return;
        var start = Math.Min(_rangeStart, _rangeEnd);
        var end = Math.Max(_rangeStart, _rangeEnd);
        if (end - start < .08) { Status.Text = "Select a longer range."; return; }
        InsertSource(_selectedAsset.Path, start, end, _project.Clips.Count);
        Status.Text = "Range added to timeline.";
    }

    private void InsertSource(string path, double start, double end, int index)
    {
        if (end - start < .08 || !File.Exists(path)) return;
        index = Math.Clamp(index, 0, _project.Clips.Count);
        _project.Clips.Insert(index, new TimelineClip { SourcePath = path, In = start, Out = end });
        SelectClip(index);
        _selectedBoundary = index > 0 ? index : 0;
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private void RefreshTimeline()
    {
        TimelineList.Children.Clear();
        var duration = 0d;
        for (var i = 0; i < _project.Clips.Count; i++)
        {
            AddBoundarySlot(i);
            var clip = _project.Clips[i];
            var index = i;
            var name = System.IO.Path.GetFileName(clip.SourcePath);
            var asset = _project.Assets.FirstOrDefault(x => x.Path == clip.SourcePath);
            var content = new StackPanel { Spacing = 4 };
            var frames = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Height = 57 };
            for (var frameIndex = 0; frameIndex < 3; frameIndex++)
            {
                var frame = asset?.FilmstripFrames.Count > 0 ? asset.FilmstripFrames[Math.Min(asset.FilmstripFrames.Count - 1,
                    (int)Math.Clamp((clip.In + (frameIndex + .5) / 3 * (clip.Out - clip.In)) / Math.Max(.01, asset.Duration) * asset.FilmstripFrames.Count, 0, asset.FilmstripFrames.Count - 1))] : null;
                if (frame is not null && File.Exists(frame))
                    frames.Children.Add(new Image { Source = new BitmapImage(new Uri(frame)), Width = 48, Height = 57, Stretch = Stretch.UniformToFill });
                else frames.Children.Add(new Border { Width = 48, Height = 57, Background = Brush("#38424C") });
            }
            content.Children.Add(frames);
            content.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = $"{Time(clip.Duration)} · drag to reorder", FontSize = 11, Opacity = .7 });
            var button = new Button { Content = content, Width = 158,
                Height = 119, HorizontalContentAlignment = HorizontalAlignment.Stretch, CanDrag = true, AllowDrop = true,
                Background = i == _selectedClip && !_boundarySelected ? Brush("#334E78") : Brush("#252B31") };
            button.Click += (_, _) => SelectClip(index);
            button.DragOver += Timeline_DragOver;
            button.Drop += async (_, args) =>
            {
                var insertion = index + (args.GetPosition(button).X >= button.ActualWidth / 2 ? 1 : 0);
                await DropAtAsync(args, insertion);
            };
            button.DragStarting += (_, args) =>
            {
                args.Data.SetText(JsonSerializer.Serialize(new DragPayload("timeline", Index: index)));
                args.Data.RequestedOperation = DataPackageOperation.Move;
            };
            TimelineList.Children.Add(button);
            var overlap = i > 0 && clip.TransitionIn != TransitionKind.Cut ?
                Math.Min(Math.Clamp(clip.TransitionDuration, .05, 10), Math.Min(duration, clip.Duration) / 2) : 0;
            duration += clip.Duration - overlap;
        }
        AddBoundarySlot(_project.Clips.Count);
        ProjectDuration.Text = $"{_project.Clips.Count} clips · {Time(duration)}";
        RefreshBoundaryControls();
    }

    private void AddBoundarySlot(int index)
    {
        var kind = index > 0 && index < _project.Clips.Count ? _project.Clips[index].TransitionIn : TransitionKind.Cut;
        var isFade = index == 0 && _project.Clips.Count > 0 && _project.Clips[0].FadeIn ||
            index == _project.Clips.Count && index > 0 && _project.Clips[^1].FadeOut;
        var label = index == 0 ? (isFade ? "Fade in" : "+") :
            index == _project.Clips.Count ? (isFade ? "Fade out" : "+") :
            kind == TransitionKind.Cut ? "+" : TransitionName(kind);
        var slot = new Border { Width = kind == TransitionKind.Cut && !isFade ? 27 : 74, Height = 119,
            CornerRadius = new CornerRadius(4), Background = index == _selectedBoundary && _boundarySelected ? Brush("#775B46") : Brush("#353D45"),
            AllowDrop = true, Tag = index, Child = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 } };
        slot.PointerPressed += (_, _) => SelectBoundary(index);
        slot.DragOver += Timeline_DragOver;
        slot.Drop += async (_, args) => { args.Handled = true; await DropAtAsync(args, index); };
        TimelineList.Children.Add(slot);
    }

    private void SelectBoundary(int index)
    {
        _player.Pause();
        _previewPlayback = false;
        _pendingTimelinePlay = false;
        _selectedBoundary = index;
        _selectedClip = -1;
        _boundarySelected = true;
        RefreshTimeline();
        RefreshInspector();
    }

    private static string TransitionGlyph(TransitionKind kind) => kind switch
    {
        TransitionKind.Cut => "│", TransitionKind.Crossfade => "◧",
        TransitionKind.FadeThroughBlack => "◼", TransitionKind.FadeThroughWhite => "□",
        TransitionKind.WipeLeft => "◀", TransitionKind.WipeRight => "▶",
        TransitionKind.SlideLeft => "⇐", TransitionKind.SlideRight => "⇒", _ => "│"
    };

    private static ComboBoxItem TransitionItem(TransitionKind kind, string label, string? glyph = null)
    {
        var icon = new Border { Width = 32, Height = 23, CornerRadius = new CornerRadius(3),
            Background = Brush("#334E78"), Child = new TextBlock { Text = glyph ?? TransitionGlyph(kind),
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Children =
            { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
        return new ComboBoxItem { Tag = kind, Content = row };
    }

    private void TransitionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls || TransitionCombo.SelectedItem is not ComboBoxItem { Tag: TransitionKind kind }) return;
        ApplyTransition(kind);
    }

    private static string TransitionName(TransitionKind kind) => kind switch
    {
        TransitionKind.Cut => "Cut", TransitionKind.Crossfade => "Dissolve",
        TransitionKind.FadeThroughBlack => "Fade black", TransitionKind.FadeThroughWhite => "Fade white",
        TransitionKind.WipeLeft => "Wipe left", TransitionKind.WipeRight => "Wipe right",
        TransitionKind.SlideLeft => "Slide left", TransitionKind.SlideRight => "Slide right", _ => "Cut"
    };

    private void ApplyTransition(TransitionKind kind)
    {
        if (_project.Clips.Count == 0) return;
        if (_selectedBoundary < 0 || _selectedBoundary > _project.Clips.Count)
            _selectedBoundary = _selectedClip > 0 ? _selectedClip : Math.Min(1, _project.Clips.Count);
        _boundarySelected = true;
        _selectedClip = -1;
        if (_selectedBoundary == 0) _project.Clips[0].FadeIn = kind != TransitionKind.Cut;
        else if (_selectedBoundary == _project.Clips.Count) _project.Clips[^1].FadeOut = kind != TransitionKind.Cut;
        else _project.Clips[_selectedBoundary].TransitionIn = kind;
        RefreshTimeline();
        RefreshInspector();
        InvalidateTimelinePreview();
        Status.Text = $"Applied {TransitionName(kind)} at boundary {_selectedBoundary}.";
    }

    private void RefreshBoundaryControls()
    {
        var valid = _selectedBoundary >= 0 && _selectedBoundary <= _project.Clips.Count && _project.Clips.Count > 0;
        TransitionCombo.IsEnabled = valid;
        BoundaryLabel.Text = !valid ? "Click a + between clips" :
            _selectedBoundary == 0 ? "Start" : _selectedBoundary == _project.Clips.Count ? "End" : $"Before clip {_selectedBoundary + 1}";
        _loadingControls = true;
        TransitionCombo.Items.Clear();
        if (valid)
        {
            var edge = _selectedBoundary == 0 || _selectedBoundary == _project.Clips.Count;
            var clip = _selectedBoundary == 0 ? _project.Clips[0] :
                _selectedBoundary == _project.Clips.Count ? _project.Clips[^1] : _project.Clips[_selectedBoundary];
            if (edge)
            {
                TransitionCombo.Items.Add(TransitionItem(TransitionKind.Cut, "No fade", "│"));
                TransitionCombo.Items.Add(TransitionItem(TransitionKind.Crossfade,
                    _selectedBoundary == 0 ? "Fade in" : "Fade out", _selectedBoundary == 0 ? "◐" : "◑"));
                TransitionCombo.SelectedIndex = (_selectedBoundary == 0 ? clip.FadeIn : clip.FadeOut) ? 1 : 0;
            }
            else
            {
                foreach (var kind in Enum.GetValues<TransitionKind>()) TransitionCombo.Items.Add(TransitionItem(kind, TransitionName(kind)));
                TransitionCombo.SelectedIndex = (int)clip.TransitionIn;
            }
            TransitionSeconds.Value = _selectedBoundary == 0 ? clip.FadeInDuration :
                _selectedBoundary == _project.Clips.Count ? clip.FadeOutDuration : clip.TransitionDuration;
            TransitionSeconds.IsEnabled = edge ? (_selectedBoundary == 0 ? clip.FadeIn : clip.FadeOut) : clip.TransitionIn != TransitionKind.Cut;
        }
        else TransitionSeconds.IsEnabled = false;
        _loadingControls = false;
    }

    private void TransitionSeconds_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loadingControls || _selectedBoundary < 0 || _project.Clips.Count == 0 || double.IsNaN(e.NewValue)) return;
        var value = Math.Clamp(e.NewValue, .05, 10);
        if (_selectedBoundary == 0) _project.Clips[0].FadeInDuration = value;
        else if (_selectedBoundary == _project.Clips.Count) _project.Clips[^1].FadeOutDuration = value;
        else _project.Clips[_selectedBoundary].TransitionDuration = value;
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private void StartSourceDrag(MediaAsset asset, DragStartingEventArgs args)
    {
        if (_selectedAsset != asset) { _selectedAsset = asset; _rangeStart = 0; _rangeEnd = asset.Duration; }
        args.Data.SetText(JsonSerializer.Serialize(new DragPayload("source", asset.Path,
            Math.Min(_rangeStart, _rangeEnd), Math.Max(_rangeStart, _rangeEnd))));
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void Timeline_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Handled = true;
    }

    private async void Timeline_Drop(object sender, DragEventArgs e) => await DropAtAsync(e, _project.Clips.Count);

    private async Task DropAtAsync(DragEventArgs e, int index)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        DragPayload? payload;
        try { payload = JsonSerializer.Deserialize<DragPayload>(await e.DataView.GetTextAsync()); }
        catch { return; }
        if (payload is null) return;
        if (payload.Kind == "source" && payload.Path is not null)
        {
            InsertSource(payload.Path, payload.In, payload.Out, index);
            Status.Text = "Range inserted into timeline.";
        }
        else if (payload.Kind == "timeline" && payload.Index >= 0 && payload.Index < _project.Clips.Count)
        {
            var source = payload.Index;
            var clip = _project.Clips[source];
            _project.Clips.RemoveAt(source);
            if (source < index) index--;
            index = Math.Clamp(index, 0, _project.Clips.Count);
            _project.Clips.Insert(index, clip);
            SelectClip(index);
            InvalidateTimelinePreview();
            Status.Text = "Clip moved.";
        }
        else if (payload.Kind == "transition")
        {
            SelectBoundary(index);
            ApplyTransition(payload.Transition);
        }
        e.Handled = true;
    }

    private void SelectClip(int index)
    {
        if (index < 0 || index >= _project.Clips.Count) return;
        _selectedClip = index;
        _boundarySelected = false;
        _previewPlayback = false;
        _pendingTimelinePlay = false;
        _player.Pause();
        var clip = _project.Clips[index];
        LoadPreview(clip.SourcePath, clip.In);
        PreviewMode.Text = $"Timeline · clip {index + 1}";
        RefreshAssets();
        RefreshTimeline();
        RefreshInspector();
    }

    private void RefreshInspector()
    {
        _loadingControls = true;
        try
        {
            var clip = SelectedClip;
            TrimInBox.Value = clip?.In ?? 0;
            TrimOutBox.Value = clip?.Out ?? 0;
            TrimInBox.IsEnabled = clip is not null;
            TrimOutBox.IsEnabled = clip is not null;
            TitleBox.SelectedIndex = clip is null ? 0 : (int)clip.TitlePreset;
            SpeedButton.Content = clip is null ? "100%…" : $"{clip.Speed * 100:0}%…";
            SpeedButton.IsEnabled = clip is not null;
            VolumeSlider.Value = clip?.Volume ?? 1;
            NormalizeBox.IsChecked = clip?.Normalize ?? false;
            FadeInBox.IsChecked = clip?.FadeIn ?? false;
            FadeOutBox.IsChecked = clip?.FadeOut ?? false;
            TitleOverlay.Visibility = clip is { TitlePreset: not TitlePreset.None } ? Visibility.Visible : Visibility.Collapsed;
            TitleOverlay.Text = clip?.Title ?? "";
            TitleOverlay.VerticalAlignment = clip?.TitlePreset == TitlePreset.LowerThird ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            TitleOverlay.Margin = clip?.TitlePreset == TitlePreset.LowerThird ? new Thickness(0, 0, 0, 40) : new Thickness(0);
        }
        finally { _loadingControls = false; }
    }

    private TimelineClip? SelectedClip => _selectedClip >= 0 && _selectedClip < _project.Clips.Count ? _project.Clips[_selectedClip] : null;

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedClip is not { } clip) return;
        var original = Math.Max(0, clip.Out - clip.In);
        var percent = new NumberBox { Header = "Speed percentage", Minimum = 25, Maximum = 400,
            SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = clip.Speed * 100, Width = 180 };
        var newLength = new TextBlock { Text = $"New clip length: {PreciseTime(clip.Duration)}" };
        percent.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue) && args.NewValue > 0)
                newLength.Text = $"New clip length: {PreciseTime(original / (args.NewValue / 100))}";
        };
        var pitch = new CheckBox { Content = "Keep audio pitch", IsChecked = clip.KeepPitch };
        var stack = new StackPanel { Spacing = 10, Children =
            { percent, new TextBlock { Text = $"Original clip length: {PreciseTime(original)}" }, newLength, pitch } };
        var dialog = new ContentDialog { Title = "Clip speed", Content = stack,
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || double.IsNaN(percent.Value)) return;
        clip.Speed = Math.Clamp(percent.Value / 100, .25, 4);
        clip.KeepPitch = pitch.IsChecked == true;
        _player.PlaybackSession.PlaybackRate = clip.Speed;
        RefreshInspector();
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private void InspectorSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateClip();
    private void InspectorCheck_Changed(object sender, RoutedEventArgs e) => UpdateClip();
    private void InspectorValue_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => UpdateClip();

    private void UpdateClip()
    {
        if (_loadingControls || SelectedClip is not { } clip) return;
        clip.TitlePreset = (TitlePreset)Math.Max(0, TitleBox.SelectedIndex);
        clip.Volume = VolumeSlider.Value;
        clip.Normalize = NormalizeBox.IsChecked == true;
        clip.FadeIn = FadeInBox.IsChecked == true;
        clip.FadeOut = FadeOutBox.IsChecked == true;
        _player.PlaybackSession.PlaybackRate = clip.Speed;
        _player.Volume = Math.Min(1, clip.Volume);
        TitleOverlay.Visibility = clip.TitlePreset == TitlePreset.None ? Visibility.Collapsed : Visibility.Visible;
        TitleOverlay.VerticalAlignment = clip.TitlePreset == TitlePreset.LowerThird ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        TitleOverlay.Margin = clip.TitlePreset == TitlePreset.LowerThird ? new Thickness(0, 0, 0, 40) : new Thickness(0);
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private void Trim_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loadingControls || SelectedClip is not { } clip || double.IsNaN(e.NewValue)) return;
        var maximum = _project.Assets.FirstOrDefault(x => x.Path == clip.SourcePath)?.Duration ?? clip.Out;
        if (sender == TrimInBox) clip.In = Math.Clamp(e.NewValue, 0, Math.Max(0, clip.Out - .08));
        else clip.Out = Math.Clamp(e.NewValue, clip.In + .08, Math.Max(clip.In + .08, maximum));
        _loadingControls = true;
        TrimInBox.Value = clip.In;
        TrimOutBox.Value = clip.Out;
        _loadingControls = false;
        LoadPreview(clip.SourcePath, clip.In);
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private void TitleOverlay_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingControls || SelectedClip is not { } clip) return;
        clip.Title = TitleOverlay.Text;
        InvalidateTimelinePreview();
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveClip(-1);
    private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveClip(1);
    private void MoveClip(int direction)
    {
        var other = _selectedClip + direction;
        if (_selectedClip < 0 || other < 0 || other >= _project.Clips.Count) return;
        (_project.Clips[_selectedClip], _project.Clips[other]) = (_project.Clips[other], _project.Clips[_selectedClip]);
        _selectedClip = other;
        RefreshTimeline();
        RefreshInspector();
        InvalidateTimelinePreview();
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelectedClip();

    private void RemoveSelectedClip()
    {
        if (_selectedClip < 0 || _selectedClip >= _project.Clips.Count) return;
        _project.Clips.RemoveAt(_selectedClip);
        _selectedClip = Math.Min(_selectedClip, _project.Clips.Count - 1);
        _boundarySelected = false;
        _selectedBoundary = Math.Min(_selectedBoundary, _project.Clips.Count);
        if (_selectedClip >= 0) SelectClip(_selectedClip);
        else
        {
            _player.Pause(); _player.Source = null; _previewPath = null; _previewPlayback = false;
            PreviewMode.Text = "";
            RefreshTimeline(); RefreshInspector(); TitleOverlay.Visibility = Visibility.Collapsed;
        }
        InvalidateTimelinePreview();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_exporting || e.Key != VirtualKey.Delete) return;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (focused is TextBox or NumberBox or PasswordBox) return;
        if (_boundarySelected && _selectedBoundary >= 0 && _selectedBoundary <= _project.Clips.Count && _project.Clips.Count > 0)
        {
            if (_selectedBoundary == 0) _project.Clips[0].FadeIn = false;
            else if (_selectedBoundary == _project.Clips.Count) _project.Clips[^1].FadeOut = false;
            else _project.Clips[_selectedBoundary].TransitionIn = TransitionKind.Cut;
            RefreshTimeline();
            RefreshInspector();
            InvalidateTimelinePreview();
            Status.Text = "Transition cleared.";
            e.Handled = true;
        }
        else if (SelectedClip is not null)
        {
            RemoveSelectedClip();
            Status.Text = "Clip removed.";
            e.Handled = true;
        }
    }

    private void CancelPreviewRender()
    {
        _previewRenderVersion++;
        _previewRenderCts?.Cancel();
        _previewRenderCts = null;
        _pendingTimelinePlay = false;
        if (_timelinePreviewPath is not null) _stalePreviews.Add(_timelinePreviewPath);
        _timelinePreviewPath = null;
        CleanupStalePreviews();
    }

    private void InvalidateTimelinePreview()
    {
        var wasWaiting = _pendingTimelinePlay;
        if (_previewPlayback)
        {
            _player.Pause();
            _previewPlayback = false;
            if (SelectedClip is { } clip) LoadPreview(clip.SourcePath, clip.In);
        }
        CancelPreviewRender();
        if (_project.Clips.Count == 0) return;
        _pendingTimelinePlay = wasWaiting;
        var snapshot = JsonSerializer.Deserialize<MovieProject>(JsonSerializer.Serialize(_project))!;
        _previewRenderCts = new CancellationTokenSource();
        var version = _previewRenderVersion;
        Status.Text = "Preparing timeline preview in the background…";
        _ = RenderTimelinePreviewAsync(snapshot, version, _previewRenderCts.Token);
    }

    private async Task RenderTimelinePreviewAsync(MovieProject snapshot, int version, CancellationToken token)
    {
        string? output = null;
        try
        {
            await Task.Delay(650, token);
            Directory.CreateDirectory(_previewDirectory);
            output = System.IO.Path.Combine(_previewDirectory, $"preview-{Guid.NewGuid():N}.mp4");
            IProgress<double> renderProgress = new Progress<double>(value =>
            {
                if (version == _previewRenderVersion && !_exporting)
                    Status.Text = $"Rendering timeline preview… {value:P0}";
            });
            await MediaEngine.ExportAsync(snapshot, output,
                new ExportSettings(854, 480, "Software (H.264)", Preview: true),
                (value, _) => renderProgress.Report(value), token);
            if (token.IsCancellationRequested || version != _previewRenderVersion) return;
            _timelinePreviewPath = output;
            output = null;
            Status.Text = "Timeline preview ready · transitions and audio rendered.";
            CleanupStalePreviews();
            if (_pendingTimelinePlay)
            {
                _pendingTimelinePlay = false;
                PlayTimelinePreview();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _previewRenderVersion)
            {
                _pendingTimelinePlay = false;
                PlayButton.Content = "▶ Play";
                Status.Text = "Could not render preview: " + ex.Message;
            }
        }
        finally { if (output is not null) try { File.Delete(output); } catch { } }
    }

    private void CleanupStalePreviews()
    {
        foreach (var path in _stalePreviews.ToArray())
        {
            if (path == _previewPath) continue;
            try { File.Delete(path); _stalePreviews.Remove(path); } catch { }
        }
    }

    private double TimelineClipStart(int index)
    {
        if (index <= 0 || _project.Clips.Count == 0) return 0;
        var total = _project.Clips[0].Duration;
        for (var i = 1; i <= index && i < _project.Clips.Count; i++)
        {
            var clip = _project.Clips[i];
            var overlap = clip.TransitionIn == TransitionKind.Cut ? 0 :
                Math.Min(Math.Clamp(clip.TransitionDuration, .05, 10), Math.Min(total, clip.Duration) / 2);
            if (i == index) return total - overlap;
            total += clip.Duration - overlap;
        }
        return total;
    }

    private double TimelineDuration()
    {
        if (_project.Clips.Count == 0) return 0;
        var total = _project.Clips[0].Duration;
        for (var i = 1; i < _project.Clips.Count; i++)
        {
            var clip = _project.Clips[i];
            var overlap = clip.TransitionIn == TransitionKind.Cut ? 0 :
                Math.Min(Math.Clamp(clip.TransitionDuration, .05, 10), Math.Min(total, clip.Duration) / 2);
            total += clip.Duration - overlap;
        }
        return total;
    }

    private void PlayTimelinePreview()
    {
        if (_timelinePreviewPath is null || !File.Exists(_timelinePreviewPath) || _project.Clips.Count == 0) return;
        var index = _selectedClip >= 0 ? _selectedClip : Math.Clamp(_selectedBoundary, 0, _project.Clips.Count - 1);
        var clip = _project.Clips[index];
        var offset = _selectedClip >= 0 && _previewPath == clip.SourcePath ?
            Math.Clamp((_player.PlaybackSession.Position.TotalSeconds - clip.In) / Math.Max(.01, clip.Speed), 0, clip.Duration) : 0;
        var position = Math.Min(TimelineDuration(), TimelineClipStart(index) + offset);
        LoadPreview(_timelinePreviewPath, position);
        PreviewSeek.Maximum = Math.Max(.1, TimelineDuration());
        _player.PlaybackSession.PlaybackRate = 1;
        _player.Volume = 1;
        _previewPlayback = true;
        TitleOverlay.Visibility = Visibility.Collapsed;
        PreviewMode.Text = "Timeline · rendered preview";
        _player.Play();
        PlayButton.Content = "⏸ Pause";
    }

    private void LoadPreview(string path, double position)
    {
        try
        {
            if (_previewPath != path)
            {
                _previewPath = path;
                _pendingPosition = position;
                _player.Source = MediaSource.CreateFromUri(new Uri(path));
                PreviewSeek.Maximum = _project.Assets.FirstOrDefault(x => x.Path == path)?.Duration ?? 100;
                CleanupStalePreviews();
            }
            else _player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
            _seeking = true;
            PreviewSeek.Value = position;
            _seeking = false;
            PreviewTime.Text = Time(position);
        }
        catch { Status.Text = "Preview unavailable. Check that the source file still exists."; }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause(); PlayButton.Content = "▶ Play"; return;
        }
        if (_project.Clips.Count > 0 && (SelectedClip is not null || _boundarySelected))
        {
            if (_previewPlayback && _timelinePreviewPath == _previewPath)
            {
                _player.Play();
                PlayButton.Content = "⏸ Pause";
                return;
            }
            if (_timelinePreviewPath is null)
            {
                _pendingTimelinePlay = !_pendingTimelinePlay;
                Status.Text = _pendingTimelinePlay ? "Rendering preview · playback will start when ready." : "Preview playback cancelled.";
                PlayButton.Content = _pendingTimelinePlay ? "… Waiting" : "▶ Play";
                return;
            }
            PlayTimelinePreview();
            return;
        }
        if (_previewPath is null) return;
        _player.Play();
        PlayButton.Content = "⏸ Pause";
    }

    private void PreviewSeek_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_seeking || _previewPath is null || _player.Source is null) return;
        try { _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue); } catch { }
    }

    private void Clock_Tick(object? sender, object e)
    {
        if (_previewPath is null) return;
        var seconds = _player.PlaybackSession.Position.TotalSeconds;
        _seeking = true;
        PreviewSeek.Value = Math.Clamp(seconds, 0, PreviewSeek.Maximum);
        _seeking = false;
        PreviewTime.Text = Time(seconds);
        if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            if (!_pendingTimelinePlay) PlayButton.Content = "▶ Play";
            if (_previewPlayback && seconds >= PreviewSeek.Maximum - .05)
                _previewPlayback = false;
            return;
        }
    }

    private async Task ErrorAsync(string title, Exception error)
    {
        Status.Text = title;
        await MessageAsync(title, error.Message);
    }

    private async Task MessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private static SolidColorBrush Brush(string hex)
    {
        var offset = hex.Length == 9 ? 2 : 0;
        var a = offset == 2 ? Convert.ToByte(hex.Substring(1, 2), 16) : (byte)255;
        var r = Convert.ToByte(hex.Substring(1 + offset, 2), 16);
        var g = Convert.ToByte(hex.Substring(3 + offset, 2), 16);
        var b = Convert.ToByte(hex.Substring(5 + offset, 2), 16);
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static string PreciseTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");
}
