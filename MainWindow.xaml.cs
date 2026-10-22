using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace WinMovie;

public sealed partial class MainWindow : Window
{
    private MovieProject _project = new();
    private readonly MediaPlayer _player = new() { AutoPlay = false };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _thumbnailGate = new(2, 2);
    private CancellationTokenSource _mediaLoadCts = new();
    private string _savedSnapshot = "";
    private bool _closeApproved, _closingPrompt;
    private readonly string _thumbDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinMovieThumbs", Guid.NewGuid().ToString("N"));
    private readonly string _previewDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinMoviePreviews", Guid.NewGuid().ToString("N"));
    private MediaAsset? _selectedAsset;
    private int _selectedClip = -1;
    private readonly HashSet<int> _selectedClips = [];
    private int _selectedTitle = -1, _selectedMusic = -1;
    private bool _titleDeleteReady;
    private TitleItem? _titleDraft, _titleEditTarget;
    private TextBox? _titleEditorText;
    private static IReadOnlyList<InstalledFont>? _installedFonts;
    private sealed record InstalledFont(string Name, string Path);
    private MediaAsset? _hoveredAsset;
    private double _hoveredTime;
    private bool _timelineFocused, _spaceSourcePlaying;
    private bool _spaceHeld;
    private string? _projectPath;
    private string? _previewPath;
    private double _rangeStart, _rangeEnd;
    private bool _draggingRange, _loadingControls, _seeking, _previewPlayback, _rangeExplicit, _exporting, _pendingTimelinePlay;
    private bool _boundarySelected;
    private CancellationTokenSource? _previewRenderCts, _exportCts;
    private CancellationTokenSource? _titleGraphicCts;
    private string? _titleGraphicSignature, _titleGraphicPath;
    private string? _timelinePreviewPath;
    private static readonly Lazy<Task<string>> PreviewEncoder = new(() => MediaEngine.DetectBestEncoderAsync());
    private int _previewRenderVersion;
    private readonly List<string> _stalePreviews = [];
    private double? _pendingPosition;
    private DateTime _lastSkim = DateTime.MinValue;
    private readonly HashSet<MediaAsset> _loadingFilmstrips = [];
    private readonly Dictionary<string, Canvas> _mediaStrips = new(StringComparer.OrdinalIgnoreCase);
    private int _clipDragSource = -1, _clipDragInsertion;
    private Windows.Foundation.Point _clipDragOrigin;
    private bool _clipDragMoved, _suppressClipClick;
    private Border? _clipDragMarker;
    private Microsoft.UI.Xaml.Media.Brush? _clipDragMarkerBrush;
    private Canvas? _dragStrip;
    private int _rangeDragMode;
    private int _selectedBoundary = -1;
    private double StripWidth => Math.Max(200, MediaPanel.ActualWidth - 20);
    private const double StripHeight = 74;
    private sealed record DragPayload(string Kind, string? Path = null, double In = 0, double Out = 0, int Index = -1);

    public MainWindow()
    {
        InitializeComponent();
        Title = "My Movie · WinMovie · 1.0 Beta 9";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var windowWidth = Math.Min(1440, workArea.Width - 32);
        var windowHeight = Math.Min(900, workArea.Height - 32);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(workArea.X + (workArea.Width - windowWidth) / 2,
            workArea.Y + (workArea.Height - windowHeight) / 2, windowWidth, windowHeight));
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "WinMovie.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
            AppWindow.SetTaskbarIcon(iconPath);
            AppWindow.SetTitleBarIcon(iconPath);
        }
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Color.FromArgb(255, 24, 33, 45);
            AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 49, 65, 85);
            AppWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;
        }
        Preview.SetMediaPlayer(_player);
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdatePlaybackButton);
        _player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _mediaOpening = false;
            if (_pendingPosition is { } position)
            {
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
                _pendingPosition = null;
            }
            if (_playRequested) _player.Play(); else _player.Pause();
            if (_previewPlayback && _previewPath == _timelinePreviewPath) HideTitlePreview();
            else
            {
                var activeTitle = _titleDraft ?? (_selectedTitle >= 0 && _selectedTitle < _project.Titles.Count ? _project.Titles[_selectedTitle] : null);
                ApplyTitlePreview(activeTitle, SelectedClip);
            }
        });
        _clock.Tick += Clock_Tick;
        _clock.Start();
        _savedSnapshot = ProjectSnapshot();
        _autoSaveTimer.Tick += AutoSave_Tick;
        _autoSaveTimer.Start();
        AppWindow.Closing += Window_Closing;
        _history.Reset(ProjectSnapshot());
        Closed += Window_Closed;
        _player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            _playRequested = false; _mediaOpening = false;
            Status.Text = "Video preview could not open this file. Verify its location and format. " + args.ErrorMessage;
            UpdatePlaybackButton();
        });
        VolumeSlider.Value = 1;
        EditorSurface.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Editor_KeyDown), true);
        EditorSurface.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(Editor_KeyUp), true);
        EditorSurface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SourceRange_PointerMoved), true);
        EditorSurface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SourceRange_PointerReleased), true);
        var initialFocus = true;
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            { _spaceHeld = false; ResetClipDrag(); CancelSourceDrag(); }
            else if (initialFocus)
            { initialFocus = false; DispatcherQueue.TryEnqueue(() => ImportButton.Focus(FocusState.Programmatic)); }
        };
        TimelinePanel.PointerPressed += (_, _) => _timelineFocused = true;
        TimelinePanel.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(TimelineClip_PointerMoved), true);
        TimelinePanel.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(TimelineClip_PointerReleased), true);
        TimelineScroller.ViewChanged += (_, _) => RefreshTimelineRuler();
        TimelineScroller.SizeChanged += (_, _) => UpdateTimelineGeometry();
        RefreshAll();
        ImportButton.Focus(FocusState.Programmatic);
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        { if (!_closed) { _playRequested = false; _spaceSourcePlaying = false; UpdatePlaybackButton(); } });
    }

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private async Task ImportCoreAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".mp4"); picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".mkv"); picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".m4v"); picker.FileTypeFilter.Add(".webm");
        foreach (var ext in MediaEngine.ImageExtensions) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        var imported = 0;
        foreach (var file in files)
        {
            if (_project.Assets.Any(x => string.Equals(x.Path, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                Status.Text = $"Reading {file.Name}…";
                var asset = await MediaEngine.ProbeAsync(file.Path, _lifetime.Token);
                _project.Assets.Add(asset);
                imported++;
            }
            catch (Exception ex) { await ErrorAsync($"Could not import {file.Name}", ex); }
        }
        RefreshAssets();
        if (_selectedAsset is null && _project.Assets.Count > 0) await SelectAssetAsync(_project.Assets[0]);
        RecordEdit();
        Status.Text = $"Imported {imported} media file{(imported == 1 ? "" : "s")}.";
    }

    private async Task NewCoreAsync()
    {
        if (!await ConfirmUnsavedChangesAsync("starting a new project")) return;
        CloseTitleEditor(true);
        PausePreview();
        _project = new MovieProject();
        _projectPath = null;
        _selectedAsset = null;
        _selectedClip = -1;
        _selectedClips.Clear(); _selectedTitle = -1; _selectedMusic = -1;
        _timelineFocused = false; _hoveredAsset = null; _spaceSourcePlaying = false;
        _previewPath = null;
        _player.Source = null;
        CancelPreviewRender();
        _previewPlayback = false;
        _rangeExplicit = false;
        _boundarySelected = false;
        _selectedBoundary = -1;
        _mediaStrips.Clear();
        _savedSnapshot = ProjectSnapshot();
        _history.Reset(_savedSnapshot);
        ResetSelection();
        RefreshAll();
    }

    private async Task OpenCoreAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeFilter.Add(".winmovie");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var project = ProjectDocument.Parse(await File.ReadAllTextAsync(file.Path));
            ProjectDocument.MigrateLegacyTitles(project);
            foreach (var title in project.Titles)
                if (!File.Exists(title.FontFile) && InstalledFonts().FirstOrDefault(x => string.Equals(x.Name, title.Font, StringComparison.OrdinalIgnoreCase)) is { } face) title.FontFile = face.Path;
            if (!await ConfirmUnsavedChangesAsync("opening another project")) return;
            CloseTitleEditor(true);
            _project = project;
            _projectPath = file.Path;
            _savedSnapshot = ProjectSnapshot();
            _history.Reset(_savedSnapshot);
            _selectedAsset = null;
            _selectedClip = -1;
            _selectedClips.Clear(); _selectedTitle = -1; _selectedMusic = -1;
            _timelineFocused = false; _hoveredAsset = null; _spaceSourcePlaying = false;
            _previewPath = null;
            _player.Source = null;
            CancelPreviewRender();
            _previewPlayback = false;
            _rangeExplicit = false;
            _boundarySelected = false;
            _selectedBoundary = -1;
            ResetSelection();
            RefreshAll();
            if (_project.Assets.Count > 0) await SelectAssetAsync(_project.Assets[0]);
            InvalidateTimelinePreview();
            Status.Text = $"Opened {file.Name}";
        }
        catch (Exception ex) { await ErrorAsync("Could not open project", ex); }
    }

    private string ProjectSnapshot() => ProjectDocument.Snapshot(_project);

    private bool HasUnsavedChanges() => ProjectSnapshot() != _savedSnapshot ||
        _titleDraft is not null && (_titleEditTarget is null ||
        JsonSerializer.Serialize(_titleDraft) != JsonSerializer.Serialize(_titleEditTarget));

    private async Task PersistProjectAsync(string path, string snapshot)
    {
        await _saveGate.WaitAsync();
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,
            $".WinMovie-save-{Guid.NewGuid():N}.tmp");
        try
        {
            using var document = JsonDocument.Parse(snapshot);
            var formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporary, formatted);
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
            _saveGate.Release();
        }
    }

    private async Task<bool> SaveProjectAsync()
    {
        string? path = _projectPath;
        if (path is null)
        {
            var picker = new FileSavePicker { SuggestedFileName = "My Movie" };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.FileTypeChoices.Add("WinMovie project", new List<string> { ".winmovie" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;
            path = file.Path;
        }
        try
        {
            if (_titleDraft is not null) CommitTitleDraft();
            var project = _project;
            var snapshot = ProjectSnapshot();
            await PersistProjectAsync(path, snapshot);
            if (ReferenceEquals(_project, project))
            {
                _projectPath = path;
                _savedSnapshot = snapshot;
                Status.Text = $"Saved {System.IO.Path.GetFileName(path)}";
            }
            return true;
        }
        catch (Exception ex) { await ErrorAsync("Could not save project", ex); return false; }
    }

    private async void AutoSave_Tick(object? sender, object e)
    {
        var path = _projectPath;
        if (path is null || _exporting || _titleDraft is not null || _closingPrompt || _commandBusy || _dialogOpen) return;
        var snapshot = ProjectSnapshot();
        if (snapshot == _savedSnapshot) return;
        var project = _project;
        try
        {
            await PersistProjectAsync(path, snapshot);
            if (ReferenceEquals(_project, project) && _projectPath == path)
            {
                _savedSnapshot = snapshot;
                Status.Text = $"Autosaved {System.IO.Path.GetFileName(path)}";
            }
        }
        catch (Exception ex) { Status.Text = $"Autosave failed: {ex.Message}"; }
    }

    private async Task<bool> ConfirmUnsavedChangesAsync(string action)
    {
        _autoSaveTimer.Stop();
        await _saveGate.WaitAsync();
        _saveGate.Release();
        try
        {
            if (!HasUnsavedChanges()) return true;
            var discardStyle = new Style(typeof(Button));
            discardStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#8A3D3D")));
            discardStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#FFFFFFFF")));
            var name = _projectPath is null ? "this movie" : System.IO.Path.GetFileNameWithoutExtension(_projectPath);
            var dialog = new ContentDialog
            {
                Title = "Unsaved changes",
                Content = $"Save changes to {name} before {action}?",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Discard changes",
                SecondaryButtonStyle = discardStyle,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            var choice = await ShowDialogAsync(dialog);
            return choice switch
            {
                ContentDialogResult.Primary => await SaveProjectAsync() && !HasUnsavedChanges(),
                ContentDialogResult.Secondary => true,
                _ => false
            };
        }
        finally { _autoSaveTimer.Start(); }
    }

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved) return;
        if (_exporting || _commandBusy || _dialogOpen) { args.Cancel = true; Status.Text = "Finish the current action before closing."; return; }
        if (!HasUnsavedChanges()) return;
        args.Cancel = true;
        if (_closingPrompt) return;
        _closingPrompt = true;
        try
        {
            if (await ConfirmUnsavedChangesAsync("closing WinMovie"))
            {
                _closeApproved = true;
                Close();
            }
        }
        finally { _closingPrompt = false; }
    }

    private async Task ExportCoreAsync()
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
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var selected = (VideoResolution)((ComboBoxItem)box.SelectedItem).Tag;
        var suggestedName = _projectPath is null ? "My Movie" : System.IO.Path.GetFileNameWithoutExtension(_projectPath);
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        picker.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (_project.Clips.Any(x => string.Equals(x.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase)) ||
            _project.Music.Any(x => string.Equals(x.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
        { await MessageAsync("Choose another filename", "This file is used by the movie. Export to a new filename to keep your original media."); return; }
        CloseTitleEditor();
        var exportProject = ProjectDocument.Clone(_project);
        var stagedOutput = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file.Path)!,
            $".WinMovie-{Guid.NewGuid():N}.mp4");
        var resumePreview = _timelinePreviewPath is null && _project.Clips.Count > 0;
        if (resumePreview) CancelPreviewRender();
        PausePreview();
        ExportOverlayTitle.Text = "Exporting your movie"; ExportCancelButton.Content = "Cancel export";
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
                if (!_exporting) return;
                ExportOverlayProgress.IsIndeterminate = false;
                if (!ExportOverlayProgress.IsIndeterminate) ExportOverlayProgress.Value = p.Value;
                ExportOverlayMessage.Text = p.Message;
                Status.Text = p.Message;
            });
            await MediaEngine.ExportAsync(exportProject, stagedOutput,
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
        else { Status.Text = "Export complete."; await ShowExportCompleteAsync(file.Path); }
        UpdateCommands();
    }

    private async Task ShowExportCompleteAsync(string path)
    {
        var dialog = new ContentDialog
        {
            Title = "Export complete",
            Content = new TextBlock { Text = $"Saved {path}", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Open file",
            SecondaryButtonText = "Open folder",
            CloseButtonText = "Done",
            XamlRoot = Content.XamlRoot
        };
        var choice = await ShowDialogAsync(dialog);
        try
        {
            if (choice == ContentDialogResult.Primary)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (choice == ContentDialogResult.Secondary)
                Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex) { await ErrorAsync("Could not open export", ex); }
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
        RangeLabel.Text = _selectedAsset is null ? "Choose Import media to begin" : FormatRange();
        UpdateProjectState();
    }

    private void RefreshAssets()
    {
        MediaList.Children.Clear();
        _mediaStrips.Clear();
        MediaCount.Text = $"{_project.Assets.Count} {(_project.Assets.Count == 1 ? "file" : "files")}";
        EmptyMediaPanel.Visibility = _project.Assets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaLoading.IsActive = _loadingFilmstrips.Count > 0;
        var stripWidth = StripWidth;
        var frameWidth = stripWidth / 6;
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
            var strip = new Canvas { Width = stripWidth, Height = StripHeight, Tag = asset, Background = Brush("#252B31") };
            for (var i = 0; i < 6; i++)
            {
                var placeholder = new Border { Width = frameWidth, Height = StripHeight, Background = Brush(i % 2 == 0 ? "#303942" : "#39434D"), IsHitTestVisible = false };
                Canvas.SetLeft(placeholder, i * frameWidth);
                strip.Children.Add(placeholder);
                if (i < asset.FilmstripFrames.Count && File.Exists(asset.FilmstripFrames[i]))
                {
                    var frame = new Image { Source = new BitmapImage(new Uri(asset.FilmstripFrames[i])), Width = frameWidth, Height = StripHeight,
                        Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
                    Canvas.SetLeft(frame, i * frameWidth);
                    strip.Children.Add(frame);
                }
            }
            strip.PointerPressed += Filmstrip_PointerPressed;
            strip.PointerMoved += Filmstrip_PointerMoved;
            strip.PointerReleased += Filmstrip_PointerReleased;
            strip.PointerCaptureLost += Filmstrip_PointerCaptureLost;
            strip.PointerEntered += (_, _) => _hoveredAsset = asset;
            strip.PointerExited += (_, _) => { if (_hoveredAsset == asset) _hoveredAsset = null; };
            _mediaStrips[asset.Path] = strip;
            UpdateRangeOverlay(strip, asset);
            var scroller = new ScrollViewer { Content = strip, HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled };
            scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MediaStrip_Wheel), true);
            card.Children.Add(scroller);
            var outer = new Border { Background = asset == _selectedAsset ? Brush("#253A54") : Brush("#1D2329"),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = card };
            var sourceButton = new Button { Content = outer, Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sourceButton, $"{asset.Name}, {Time(asset.Duration)}. Select source media.");
            sourceButton.Click += async (_, _) => { if (!_draggingRange) await SelectAssetAsync(asset); };
            if (!File.Exists(asset.Path))
            {
                duration.Text = "Missing file";
                ToolTipService.SetToolTip(sourceButton, "Source file missing. Right-click to locate it.");
                var locate = new MenuFlyoutItem { Text = "Locate file…" };
                locate.Click += async (_, _) => await RunCommandAsync(() => RelinkMediaAsync(asset));
                sourceButton.ContextFlyout = new MenuFlyout { Items = { locate } };
            }
            MediaList.Children.Add(sourceButton);
            if (asset.FilmstripFrames.Count == 0 && asset.ThumbnailPath != "failed" && !_loadingFilmstrips.Contains(asset) && File.Exists(asset.Path))
                TrackBackground(LoadAssetFilmstripAsync(asset));
        }
    }

    private async Task LoadAssetFilmstripAsync(MediaAsset asset)
    {
        _loadingFilmstrips.Add(asset);
        MediaLoading.IsActive = true;
        var acquired = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _mediaLoadCts.Token);
        try
        {
            await _thumbnailGate.WaitAsync(linked.Token);
            acquired = true;
            var frames = new List<string>();
            for (var i = 0; i < (asset.Kind == MediaKind.Image ? 1 : 6); i++)
            {
                try { frames.Add(await MediaEngine.ThumbnailAsync(asset.Path, asset.Duration * (i + .5) / 6, _thumbDirectory, linked.Token)); }
                catch (OperationCanceledException) { return; }
                catch { break; }
            }
            if (asset.Kind == MediaKind.Image && frames.Count == 1) frames = Enumerable.Repeat(frames[0], 6).ToList();
            asset.FilmstripFrames = frames;
            asset.ThumbnailPath = frames.FirstOrDefault() ?? "failed";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (acquired) _thumbnailGate.Release();
            _loadingFilmstrips.Remove(asset);
            if (!_closed) MediaLoading.IsActive = _loadingFilmstrips.Count > 0;
            if (!_closed && !_draggingRange && _project.Assets.Contains(asset)) { RefreshAssets(); RefreshTimeline(); }
        }
    }

    private async Task SelectAssetAsync(MediaAsset asset)
    {
        CloseTitleEditor();
        _pendingTimelinePlay = false;
        _selectedAsset = asset;
        _selectedClip = -1;
        _selectedClips.Clear(); _selectedTitle = -1; _selectedMusic = -1;
        _timelineFocused = false; _spaceSourcePlaying = false;
        _boundarySelected = false;
        _rangeStart = 0;
        _rangeEnd = asset.Duration;
        _rangeExplicit = false;
        _previewPlayback = false;
        PausePreview();
        _player.PlaybackSession.PlaybackRate = 1;
        _player.Volume = 1;
        if (File.Exists(asset.Path)) LoadPreview(asset.Path, 0);
        else { _previewPath = null; _player.Source = null; Status.Text = "Source file missing. Right-click it and choose Locate file."; }
        RefreshAssets();
        RefreshTimeline();
        RefreshInspector();
        RangeLabel.Text = FormatRange();
        PreviewMode.Text = asset.Name;
        var assetIndex = _project.Assets.IndexOf(asset);
        if (assetIndex >= 0 && assetIndex < MediaList.Children.Count && MediaList.Children[assetIndex] is Button sourceCard)
            sourceCard.Focus(FocusState.Programmatic);
        await Task.CompletedTask;
    }

    private static double FilmTime(MediaAsset asset, double x, double width) => Math.Clamp(x / Math.Max(1, width) * asset.Duration, 0, asset.Duration);

    private double _lastStripWidth;
    private void MediaPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_draggingRange || Math.Abs(StripWidth - _lastStripWidth) < 24) return;
        _lastStripWidth = StripWidth;
        if (_project.Assets.Count > 0) RefreshAssets();
    }

    private void MediaStrip_Wheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        MediaScroller.ChangeView(null, Math.Clamp(MediaScroller.VerticalOffset - delta, 0, MediaScroller.ScrollableHeight), null);
        e.Handled = true;
    }

    private void Filmstrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas strip || strip.Tag is not MediaAsset asset) return;
        if (asset.Kind == MediaKind.Image)
        {
            if (_selectedAsset != asset) TrackBackground(SelectAssetAsync(asset));
            _rangeStart = 0; _rangeEnd = 5; _rangeExplicit = true;
            BeginSourceRangeDrag(strip, asset, e); UpdateRangeOverlay(strip, asset); return;
        }
        if (e.OriginalSource is FrameworkElement { Tag: "selection" }) return;
        CloseTitleEditor();
        if (_selectedAsset != asset)
        {
            _selectedAsset = asset;
            _selectedClip = -1;
            _selectedClips.Clear(); _selectedTitle = -1; _selectedMusic = -1;
            _timelineFocused = false; _spaceSourcePlaying = false;
            _boundarySelected = false;
            _rangeStart = 0;
            _rangeEnd = asset.Duration;
            _rangeExplicit = false;
            _previewPlayback = false;
            PausePreview();
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
        var left = Math.Min(_rangeStart, _rangeEnd) / asset.Duration * strip.Width;
        var right = Math.Max(_rangeStart, _rangeEnd) / asset.Duration * strip.Width;
        _rangeDragMode = _rangeExplicit && Math.Abs(x - left) <= 9 ? 1 :
            _rangeExplicit && Math.Abs(x - right) <= 9 ? 2 : 0;
        if (_rangeDragMode == 0) _rangeStart = _rangeEnd = FilmTime(asset, x, strip.Width);
        strip.CapturePointer(e.Pointer);
        e.Handled = true;
        UpdateRangeOverlay(strip, asset);
    }

    private void Filmstrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas strip || strip.Tag is not MediaAsset asset) return;
        var time = FilmTime(asset, e.GetCurrentPoint(strip).Position.X, strip.Width);
        _hoveredAsset = asset;
        _hoveredTime = time;
        if (_draggingRange && _dragStrip == strip)
        {
            if (_rangeDragMode == 1) _rangeStart = Math.Min(time, _rangeEnd - .08);
            else if (_rangeDragMode == 2) _rangeEnd = Math.Max(time, _rangeStart + .08);
            else _rangeEnd = time;
            UpdateRangeOverlay(strip, asset);
        }
        if ((DateTime.UtcNow - _lastSkim).TotalMilliseconds < 65) return;
        _lastSkim = DateTime.UtcNow;
        if (_spaceSourcePlaying || _playRequested || _pendingTimelinePlay || _closed || !File.Exists(asset.Path)) return;
        PausePreview();
        _previewPlayback = false; _timelineFocused = false;
        _player.PlaybackSession.PlaybackRate = 1; _player.Volume = 1;
        PreviewMode.Text = $"Source · {asset.Name}";
        LoadPreview(asset.Path, time);
        PreviewTime.Text = Time(time);
    }

    private void Filmstrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingRange || sender is not Canvas strip || strip.Tag is not MediaAsset asset || _dragStrip != strip) return;
        var time = FilmTime(asset, e.GetCurrentPoint(strip).Position.X, strip.Width);
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
        e.Handled = true;
        if (asset.FilmstripFrames.Count > 0) RefreshAssets();
    }

    private void Filmstrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) { _draggingRange = false; _dragStrip = null; }

    private void UpdateRangeOverlay(Canvas strip, MediaAsset asset)
    {
        for (var i = strip.Children.Count - 1; i >= 0; i--)
            if (strip.Children[i] is FrameworkElement element && element.Tag?.ToString() == "selection")
                strip.Children.RemoveAt(i);
        if (_selectedAsset != asset || asset.Duration <= 0 || (!_rangeExplicit && !_draggingRange)) return;
        var left = Math.Min(_rangeStart, _rangeEnd) / asset.Duration * strip.Width;
        var width = Math.Abs(_rangeEnd - _rangeStart) / asset.Duration * strip.Width;
        var selection = new Rectangle { Tag = "selection", Width = Math.Max(2, width), Height = StripHeight,
            Fill = Brush("#44FFCE40"), Stroke = Brush("#FFFFD34E"), StrokeThickness = 3, IsHitTestVisible = false };
        Canvas.SetLeft(selection, left);
        strip.Children.Add(selection);
        if (_rangeExplicit && !_draggingRange && width > 24)
        {
            var dragArea = new Border { Tag = "selection", Width = width - 20, Height = StripHeight - 8,
                Background = Brush("#01FFFFFF"),
                Child = new TextBlock { Text = width > 115 ? "↗ Drag range" : "↗", FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brush("#FFFFE6A0"), IsHitTestVisible = false } };
            Canvas.SetLeft(dragArea, left + 10);
            Canvas.SetTop(dragArea, 4);
            dragArea.PointerPressed += (_, args) => BeginSourceRangeDrag(dragArea, asset, args);
            strip.Children.Add(dragArea);
        }
        foreach (var edge in new[] { left, left + width })
        {
            var handle = new Rectangle { Tag = "selection", Width = 6, Height = StripHeight, Fill = Brush("#FFFFD34E"), IsHitTestVisible = false };
            Canvas.SetLeft(handle, Math.Clamp(edge - 3, 0, strip.Width - 6));
            strip.Children.Add(handle);
        }
        RangeLabel.Text = FormatRange();
    }

    private string FormatRange() => _selectedAsset?.Kind == MediaKind.Image ? "Photo · 5 seconds · drag to the movie; adjust duration in Clip settings" : !_rangeExplicit && _selectedAsset is not null ?
        $"Full clip {Time(_selectedAsset.Duration)} · drag to select a range" :
        $"Selected {PreciseTime(Math.Min(_rangeStart, _rangeEnd))}–{PreciseTime(Math.Max(_rangeStart, _rangeEnd))}  ({PreciseTime(Math.Abs(_rangeEnd - _rangeStart))})";

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
        if (!double.IsFinite(start) || !double.IsFinite(end) || end - start < .08 || !File.Exists(path)) return;
        index = Math.Clamp(index, 0, _project.Clips.Count);
        _project.Clips.Insert(index, new TimelineClip { SourcePath = path, Kind = _project.Assets.FirstOrDefault(x => x.Path == path)?.Kind ?? MediaKind.Video, In = start, Out = end });
        SelectClip(index);
        InvalidateTimelinePreview();
    }

    private void RefreshTimeline()
    {
        RemoveOrphanedAudio();
        ResetClipDrag();
        TimelineList.Children.Clear();
        var layout = new TimelineLayout(_project.Clips);
        for (var i = 0; i < _project.Clips.Count; i++)
        {
            AddBoundarySlot(i);
            var clip = _project.Clips[i];
            var index = i;
            var name = clip.Name;
            var asset = _project.Assets.FirstOrDefault(x => x.Path == clip.SourcePath);
            var clipWidth = Math.Max(24, clip.Duration * _pixelsPerSecond - 2);
            var frameWidth = Math.Max(4, (clipWidth - 28) / 3);
            var content = new StackPanel { Spacing = 4 };
            var frames = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Height = 30 };
            for (var frameIndex = 0; frameIndex < 3; frameIndex++)
            {
                var frame = asset?.FilmstripFrames.Count > 0 ? asset.FilmstripFrames[Math.Min(asset.FilmstripFrames.Count - 1,
                    (int)Math.Clamp((clip.In + (frameIndex + .5) / 3 * (clip.Out - clip.In)) / Math.Max(.01, asset.Duration) * asset.FilmstripFrames.Count, 0, asset.FilmstripFrames.Count - 1))] : null;
                if (frame is not null && File.Exists(frame))
                    frames.Children.Add(new Image { Source = new BitmapImage(new Uri(frame)), Width = frameWidth, Height = 30, Stretch = Stretch.UniformToFill });
                else frames.Children.Add(new Border { Width = frameWidth, Height = 30, Background = clip.Kind == MediaKind.Color ? Brush("#" + clip.Color) : Brush("#38424C") });
            }
            content.Children.Add(frames);
            content.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = $"{PreciseTime(clip.Duration)}", FontSize = 10, Opacity = .7 });
            var button = new Button { Content = content, Width = clipWidth, Padding = new Thickness(18, 4, 8, 4),
                Height = 78, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = _selectedClips.Contains(i) ? Brush("#334E78") : Brush("#252B31") };
            button.Click += (_, _) =>
            {
                if (_clipDragMoved || _suppressClipClick) return;
                SelectClip(index, ControlDown());
            };
            button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                if (!args.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
                ClearClipDragMarker();
                _clipDragSource = index;
                _clipDragInsertion = index;
                _clipDragOrigin = args.GetCurrentPoint(TimelineList).Position;
                _clipDragMoved = false;
                _suppressClipClick = false;
            }), true);
            Canvas.SetLeft(button, layout.Starts[i] * _pixelsPerSecond); Canvas.SetTop(button, 3);
            button.ContextFlyout = ClipMenu(index);
            ToolTipService.SetToolTip(button, $"{name} · {PreciseTime(clip.In)}–{PreciseTime(clip.Out)} · drag to reorder. Ctrl-click to select more.");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Clip {i + 1}: {name}, {PreciseTime(clip.Duration)}{(_selectedClips.Contains(i) ? ", selected" : "")}");
            TimelineList.Children.Add(button);
        }
        if (_project.Clips.Count > 0) AddBoundarySlot(_project.Clips.Count);
        ProjectDuration.Text = $"{_project.Clips.Count} clips · {PreciseTime(layout.Duration)}";
        RefreshBoundaryControls();
        UpdateTimelineGeometry();
    }

    private void TimelineClip_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDragSource < 0) return;
        var point = args.GetCurrentPoint(TimelineList);
        if (!point.Properties.IsLeftButtonPressed) { ResetClipDrag(); return; }
        if (!_clipDragMoved && Math.Abs(point.Position.X - _clipDragOrigin.X) +
            Math.Abs(point.Position.Y - _clipDragOrigin.Y) < 8) return;
        _clipDragMoved = true;
        var viewport = args.GetCurrentPoint(TimelineScroller).Position.X;
        if (viewport < 36) TimelineScroller.ChangeView(Math.Max(0, TimelineScroller.HorizontalOffset - 18), null, null, true);
        else if (viewport > TimelineScroller.ViewportWidth - 36) TimelineScroller.ChangeView(Math.Min(TimelineScroller.ScrollableWidth, TimelineScroller.HorizontalOffset + 18), null, null, true);
        _clipDragInsertion = TimelineInsertionIndex(point.Position.X);
        ClearClipDragMarker();
        if (_clipDragInsertion * 2 < TimelineList.Children.Count && TimelineList.Children[_clipDragInsertion * 2] is Border marker)
        {
            _clipDragMarker = marker;
            _clipDragMarkerBrush = marker.Background;
            marker.Background = Brush("#597FC2");
        }
    }

    private void TimelineClip_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_clipDragSource < 0) return;
        var point = args.GetCurrentPoint(TimelineList).Position;
        var source = _clipDragSource;
        var insertion = _clipDragInsertion;
        var moved = _clipDragMoved && point.Y >= -12 && point.Y <= TimelineList.ActualHeight + 12;
        ResetClipDrag();
        if (!moved) return;
        _suppressClipClick = true;
        MoveTimelineClip(source, insertion);
        args.Handled = true;
        DispatcherQueue.TryEnqueue(() => _suppressClipClick = false);
    }

    private void ClearClipDragMarker()
    {
        if (_clipDragMarker is not null && _clipDragMarkerBrush is not null)
            _clipDragMarker.Background = _clipDragMarkerBrush;
        _clipDragMarker = null; _clipDragMarkerBrush = null;
    }

    private void ResetClipDrag()
    {
        ClearClipDragMarker();
        _clipDragSource = -1;
        _clipDragMoved = false;
    }

    private void RefreshTracks()
    {
        var pixelsPerSecond = _pixelsPerSecond;
        TitleTrackCanvas.Children.Clear();
        for (var i = 0; i < _project.Titles.Count; i++)
        {
            var index = i;
            var title = _project.Titles[i];
            var button = new Button { Width = Math.Max(24, title.Duration * pixelsPerSecond - 2), Height = 38,
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = i == _selectedTitle ? Brush("#405B8F") : Brush("#34435A"),
                Content = new StackPanel { Children =
                {
                    new TextBlock { Text = title.Text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 145 },
                    new TextBlock { Text = $"{PreciseTime(title.Start)} · {PreciseTime(title.Duration)}", FontSize = 11, Opacity = .7 }
                } } };
            button.Click += (_, _) =>
            {
                if (_suppressTrackClick || _sourceRangeMoved) return;
                CloseTitleEditor(); SelectTitle(index); BeginTitleEdit(_project.Titles[index]);
                _titleDeleteReady = true;
            };
            button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => BeginTrackDrag(button, "title", index, title.Start, title.Duration, args)), true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Title: {title.Text}, starts {PreciseTime(title.Start)}, duration {PreciseTime(title.Duration)}");
            ToolTipService.SetToolTip(button, $"{title.Text} · {PreciseTime(title.Start)}–{PreciseTime(title.Start + title.Duration)}. Drag to move; click to edit.");
            Canvas.SetLeft(button, title.Start * pixelsPerSecond);
            TitleTrackCanvas.Children.Add(button);
        }
        MusicTrackCanvas.Children.Clear();
        for (var i = 0; i < _project.Music.Count; i++)
        {
            var index = i;
            var music = _project.Music[i];
            var placement = AudioPlacement.Resolve(_project, music);
            var button = new Button { Width = Math.Max(24, placement.Duration * pixelsPerSecond - 2), Height = 38,
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = i == _selectedMusic ? Brush("#376C66") : Brush("#2B4D4A"),
                Content = new StackPanel { Children =
                {
                    new TextBlock { Text = (music.LinkedClipId is not null ? "↔ " : "") + System.IO.Path.GetFileName(music.Path), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 },
                    new TextBlock { Text = $"{PreciseTime(placement.Start)} · {PreciseTime(placement.Duration)}{(music.FadeOut ? " · fade out" : "")}", FontSize = 11, Opacity = .7 }
                } } };
            button.Click += async (_, _) =>
            {
                if (_suppressTrackClick || _sourceRangeMoved) return;
                SelectMusic(index);
                try { await EditMusicAsync(music); }
                catch (Exception ex) { await ErrorAsync("Could not edit audio", ex); }
                if (index < MusicTrackCanvas.Children.Count && MusicTrackCanvas.Children[index] is Button selectedCard)
                    selectedCard.Focus(FocusState.Programmatic);
            };
            button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => BeginTrackDrag(button, "music", index, placement.Start, placement.Duration, args)), true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Audio: {System.IO.Path.GetFileName(music.Path)}, starts {PreciseTime(placement.Start)}, duration {PreciseTime(placement.Duration)}");
            ToolTipService.SetToolTip(button, $"{System.IO.Path.GetFileName(music.Path)} · {PreciseTime(placement.Start)}–{PreciseTime(placement.Start + placement.Duration)}. Drag to move; click to edit.");
            Canvas.SetLeft(button, placement.Start * pixelsPerSecond);
            MusicTrackCanvas.Children.Add(button);
        }
    }

    private (TimelineClip Clip, double Position)? SourceAt(double timelineTime)
    {
        var layout = new TimelineLayout(_project.Clips);
        var index = layout.ClipAt(_project.Clips, timelineTime);
        if (index < 0) return null;
        var clip = _project.Clips[index];
        return (clip, Math.Clamp(clip.In + Math.Max(0, timelineTime - layout.Starts[index]) * clip.Speed, clip.In, clip.Out));
    }

    private void SelectTitle(int index)
    {
        _pendingTimelinePlay = false;
        if (_titleDraft is not null) CloseTitleEditor();
        _selectedTitle = index; _selectedMusic = -1; _selectedClip = -1; _selectedClips.Clear();
        _boundarySelected = false; _timelineFocused = true; _previewPlayback = false; _spaceSourcePlaying = false;
        PausePreview();
        _timelinePosition = _project.Titles[index].Start;
        var source = SourceAt(_timelinePosition);
        if (source is { } current) LoadClipPreview(current.Clip, current.Position);
        PreviewMode.Text = "Timeline · title";
        RefreshTimeline(); RefreshInspector();
    }

    private void SelectMusic(int index)
    {
        _pendingTimelinePlay = false;
        CloseTitleEditor();
        _selectedMusic = index; _selectedTitle = -1; _selectedClip = -1; _selectedClips.Clear();
        _boundarySelected = false; _timelineFocused = true; _spaceSourcePlaying = false;
        PausePreview(); _previewPlayback = false;
        _timelinePosition = AudioPlacement.Resolve(_project, _project.Music[index]).Start;
        ShowTimelineFrame(_timelinePosition);
        RefreshTimeline(); RefreshInspector();
    }

    private async void AddTitle_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Clips.Count == 0) { await MessageAsync("Add video first", "Titles need a timeline to appear over."); return; }
        CloseTitleEditor();
        var start = CurrentTimelinePosition();
        _selectedTitle = -1; _selectedClip = -1; _selectedClips.Clear(); _timelineFocused = true;
        BeginTitleEdit(new TitleItem { Start = start, Duration = Math.Min(5, Math.Max(.1, TimelineDuration() - start)) }, true);
    }

    private double CurrentTimelinePosition()
    {
        var total = TimelineDuration();
        if (_previewPlayback && _timelinePreviewPath == _previewPath)
            return Math.Clamp(_player.PlaybackSession.Position.TotalSeconds, 0, Math.Max(0, total - .1));
        if (_timelineFocused && SelectedClip is { } clip)
            return Math.Clamp(TimelineClipStart(_selectedClip) +
                (_player.PlaybackSession.Position.TotalSeconds - clip.In) / Math.Max(.01, clip.Speed), 0, Math.Max(0, total - .1));
        return Math.Clamp(_timelinePosition, 0, Math.Max(0, total - .1));
    }

    private static IReadOnlyList<InstalledFont> InstalledFonts()
    {
        if (_installedFonts is not null) return _installedFonts;
        var found = new Dictionary<string, InstalledFont>(StringComparer.OrdinalIgnoreCase);
        var systemFonts = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        var userFonts = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null) continue;
            foreach (var valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string file) continue;
                var name = System.Text.RegularExpressions.Regex.Replace(valueName, @"\s*\((TrueType|OpenType)\)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                if (name.StartsWith('@') || name.Length == 0) continue;
                file = Environment.ExpandEnvironmentVariables(file);
                var path = System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(systemFonts, file);
                if (!File.Exists(path)) path = System.IO.Path.Combine(userFonts, file);
                if (File.Exists(path)) found.TryAdd(name, new InstalledFont(name, path));
            }
        }
        if (found.Count == 0) found["Segoe UI"] = new InstalledFont("Segoe UI", System.IO.Path.Combine(systemFonts, "segoeui.ttf"));
        return _installedFonts = found.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static Color ColorFromHex(string value)
    {
        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) hex = "FFFFFF";
        return Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    private static string HexFromColor(Color value) => $"{value.R:X2}{value.G:X2}{value.B:X2}";

    private void CloseTitleEditor(bool discard = false)
    {
        var changed = !discard && _titleDraft is not null && (_titleEditTarget is null ||
            JsonSerializer.Serialize(_titleDraft) != JsonSerializer.Serialize(_titleEditTarget));
        if (changed && _titleDraft is { } draft)
        {
            var index = _titleEditTarget is null ? -1 : _project.Titles.IndexOf(_titleEditTarget);
            if (index >= 0) _project.Titles[index] = draft;
            else _project.Titles.Add(draft);
        }
        _titleDraft = null; _titleEditTarget = null; _titleEditorText = null; _titleDeleteReady = false;
        TitleEditorContent.Children.Clear();
        TitleEditorPanel.Visibility = Visibility.Collapsed;
        MediaPanel.Visibility = Visibility.Visible;
        HideTitlePreview();
        if (changed) { RefreshTracks(); InvalidateTimelinePreview(); Status.Text = "Title applied. Undo with Ctrl+Z."; }
    }

    private void SeekTitleFrame(TitleItem title)
    {
        _timelinePosition = title.Start;
        var source = SourceAt(title.Start + Math.Min(.5, title.Duration / 2));
        if (source is not { } current) return;
        PausePreview(); _previewPlayback = false;
        LoadClipPreview(current.Clip, current.Position);
        PreviewMode.Text = "Timeline · title preview";
    }

    private void BeginTitleEdit(TitleItem target, bool adding = false)
    {
        _titleEditTarget = adding ? null : target;
        _titleDeleteReady = false;
        _titleDraft = JsonSerializer.Deserialize<TitleItem>(JsonSerializer.Serialize(target))!;
        var draft = _titleDraft;
        PausePreview();
        SeekTitleFrame(draft);
        MediaPanel.Visibility = Visibility.Collapsed;
        TitleEditorPanel.Visibility = Visibility.Visible;
        TitleEditorContent.Children.Clear();
        TitleEditorHeading.Text = adding ? "New title" : "Edit title";
        TitleEditorContent.Children.Add(new TextBlock { Text = "Preview updates as you type. Switching clips applies your changes; Undo can restore them.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        var text = new TextBox { Header = "Text", Text = draft.Text, IsSpellCheckEnabled = true,
            IsTextPredictionEnabled = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48 };
        _titleEditorText = text;
        text.PointerPressed += (_, _) => _titleDeleteReady = false;
        var start = new NumberBox { Header = "Start on timeline (seconds)", Minimum = 0, Maximum = Math.Max(0, TimelineDuration() - .1), Value = draft.Start, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var duration = new NumberBox { Header = "Duration (seconds)", Minimum = .1, Maximum = Math.Max(.1, TimelineDuration()), Value = draft.Duration, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var preset = new ComboBox { Header = "Style", ItemsSource = Enum.GetValues<TitlePreset>().Where(x => x != TitlePreset.None).ToArray(), SelectedItem = draft.Preset };
        var font = new ComboBox { Header = "Font · installed on this PC", MinWidth = 230 };
        foreach (var face in InstalledFonts())
            font.Items.Add(new ComboBoxItem { Tag = face, Content = new TextBlock { Text = face.Name, FontFamily = new FontFamily(face.Name), FontSize = 17 } });
        var selectedFont = font.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => string.Equals((x.Tag as InstalledFont)?.Name, draft.Font, StringComparison.OrdinalIgnoreCase));
        if (selectedFont < 0)
        {
            font.Items.Insert(0, new ComboBoxItem { Tag = new InstalledFont(draft.Font, draft.FontFile), Content = new TextBlock { Text = draft.Font + " (unavailable)", FontSize = 17 } });
            selectedFont = 0;
        }
        font.SelectedIndex = selectedFont;
        var size = new NumberBox { Header = "Font size at 1080p", Minimum = 16, Maximum = 160, Value = draft.FontSize, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var foreground = new ColorPicker { Color = ColorFromHex(draft.Color), IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = true, IsHexInputVisible = true };
        var background = new ColorPicker { Color = ColorFromHex(draft.BackgroundColor), IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = true, IsHexInputVisible = true };
        var opacity = new NumberBox { Header = "Background opacity (%)", Minimum = 0, Maximum = 100, Value = draft.BackgroundOpacity * 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var shadow = new CheckBox { Content = "Shadow", IsChecked = draft.Shadow };
        var shadowDistance = new NumberBox { Header = "Shadow distance at 1080p (pixels)", Minimum = 0, Maximum = 30, Value = draft.ShadowDistance, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var shadowBlur = new NumberBox { Header = "Shadow blur at 1080p (pixels)", Minimum = 0, Maximum = 30, Value = draft.ShadowBlur, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var shadowAngle = new NumberBox { Header = "Shadow direction (degrees · 45 = lower right)", Minimum = 0, Maximum = 360, Value = draft.ShadowAngle, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var shadowOpacity = new NumberBox { Header = "Shadow opacity (%)", Minimum = 0, Maximum = 100, Value = draft.ShadowOpacity * 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var shadowControls = new StackPanel { Spacing = 8, Children = { shadowDistance, shadowBlur, shadowAngle, shadowOpacity },
            Visibility = draft.Shadow ? Visibility.Visible : Visibility.Collapsed };
        var outline = new CheckBox { Content = "Outline", IsChecked = draft.Outline };
        var outlineThickness = new NumberBox { Header = "Outline thickness at 1080p (pixels)", Minimum = 1, Maximum = 16,
            Value = draft.OutlineThickness, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Visibility = draft.Outline ? Visibility.Visible : Visibility.Collapsed };
        var fadeIn = new CheckBox { Content = "Fade in", IsChecked = draft.FadeIn };
        var fadeInDuration = new NumberBox { Header = "Fade in seconds", Minimum = .05, Maximum = 10, Value = draft.FadeInDuration, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var fadeOut = new CheckBox { Content = "Fade out", IsChecked = draft.FadeOut };
        var fadeOutDuration = new NumberBox { Header = "Fade out seconds", Minimum = .05, Maximum = 10, Value = draft.FadeOutDuration, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var updating = false;
        void Update(bool moveFrame = false)
        {
            if (_titleDraft != draft || updating) return;
            updating = true;
            try
            {
            draft.Text = text.Text;
            if (!double.IsNaN(start.Value)) draft.Start = Math.Clamp(start.Value, 0, Math.Max(0, TimelineDuration() - .1));
            duration.Maximum = Math.Max(.1, TimelineDuration() - draft.Start);
            draft.Duration = Math.Clamp(double.IsFinite(duration.Value) ? duration.Value : draft.Duration, .1, Math.Max(.1, TimelineDuration() - draft.Start));
            start.Value = draft.Start; duration.Value = draft.Duration;
            if (preset.SelectedItem is TitlePreset placement) draft.Preset = placement;
            if (font.SelectedItem is ComboBoxItem { Tag: InstalledFont selected }) { draft.Font = selected.Name; draft.FontFile = selected.Path; }
            if (!double.IsNaN(size.Value)) draft.FontSize = (int)Math.Clamp(size.Value, 16, 160);
            draft.Color = HexFromColor(foreground.Color);
            draft.BackgroundColor = HexFromColor(background.Color);
            if (!double.IsNaN(opacity.Value)) draft.BackgroundOpacity = Math.Clamp(opacity.Value / 100, 0, 1);
            draft.Shadow = shadow.IsChecked == true; draft.Outline = outline.IsChecked == true;
            shadowControls.Visibility = draft.Shadow ? Visibility.Visible : Visibility.Collapsed;
            outlineThickness.Visibility = draft.Outline ? Visibility.Visible : Visibility.Collapsed;
            if (!double.IsNaN(shadowDistance.Value)) draft.ShadowDistance = Math.Clamp(shadowDistance.Value, 0, 30);
            if (!double.IsNaN(shadowBlur.Value)) draft.ShadowBlur = Math.Clamp(shadowBlur.Value, 0, 30);
            if (!double.IsNaN(shadowAngle.Value)) draft.ShadowAngle = Math.Clamp(shadowAngle.Value, 0, 360);
            if (!double.IsNaN(shadowOpacity.Value)) draft.ShadowOpacity = Math.Clamp(shadowOpacity.Value / 100, 0, 1);
            if (!double.IsNaN(outlineThickness.Value)) draft.OutlineThickness = (int)Math.Clamp(outlineThickness.Value, 1, 16);
            draft.FadeIn = fadeIn.IsChecked == true; draft.FadeOut = fadeOut.IsChecked == true;
            if (!double.IsNaN(fadeInDuration.Value)) draft.FadeInDuration = Math.Clamp(fadeInDuration.Value, .05, 10);
            if (!double.IsNaN(fadeOutDuration.Value)) draft.FadeOutDuration = Math.Clamp(fadeOutDuration.Value, .05, 10);
            if (moveFrame) SeekTitleFrame(draft);
            ApplyTitlePreview(draft);
            }
            finally { updating = false; }
        }
        text.TextChanged += (_, _) => { _titleDeleteReady = false; Update(); };
        start.ValueChanged += (_, _) => Update(true);
        duration.ValueChanged += (_, _) => Update();
        preset.SelectionChanged += (_, _) => Update();
        font.SelectionChanged += (_, _) => Update();
        size.ValueChanged += (_, _) => Update();
        foreground.ColorChanged += (_, _) => Update();
        background.ColorChanged += (_, _) => Update();
        opacity.ValueChanged += (_, _) => Update();
        shadow.Checked += (_, _) => Update(); shadow.Unchecked += (_, _) => Update();
        shadowDistance.ValueChanged += (_, _) => Update(); shadowBlur.ValueChanged += (_, _) => Update();
        shadowAngle.ValueChanged += (_, _) => Update(); shadowOpacity.ValueChanged += (_, _) => Update();
        outline.Checked += (_, _) => Update(); outline.Unchecked += (_, _) => Update();
        outlineThickness.ValueChanged += (_, _) => Update();
        fadeIn.Checked += (_, _) => Update(); fadeIn.Unchecked += (_, _) => Update();
        fadeOut.Checked += (_, _) => Update(); fadeOut.Unchecked += (_, _) => Update();
        fadeInDuration.ValueChanged += (_, _) => Update(); fadeOutDuration.ValueChanged += (_, _) => Update();
        foreach (var control in new UIElement[] { text, start, duration, preset, font, size }) TitleEditorContent.Children.Add(control);
        TitleEditorContent.Children.Add(new Expander { Header = "Text color", HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = foreground });
        TitleEditorContent.Children.Add(new Expander { Header = "Background", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { background, opacity } } });
        TitleEditorContent.Children.Add(new Expander { Header = "Shadow and outline", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { shadow, shadowControls, outline, outlineThickness } } });
        TitleEditorContent.Children.Add(new Expander { Header = "Fade in and out", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { fadeIn, fadeInDuration, fadeOut, fadeOutDuration } } });
        ApplyTitlePreview(draft);
    }

    private void CommitTitleDraft()
    {
        if (_titleDraft is not { } draft) return;
        if (_titleEditTarget is { } existing)
        {
            var position = _project.Titles.IndexOf(existing);
            if (position < 0) return;
            _project.Titles[position] = draft;
            _selectedTitle = position;
        }
        else { _project.Titles.Add(draft); _selectedTitle = _project.Titles.Count - 1; }
        CloseTitleEditor(true); RefreshTracks(); RefreshInspector(); InvalidateTimelinePreview();
    }

    private async Task AddMusicCoreAsync()
    {
        if (_project.Clips.Count == 0) { await MessageAsync("Add a clip first", "Audio needs a movie timeline to play with."); return; }
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        foreach (var ext in new[] { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var length = await MediaEngine.ProbeAudioDurationAsync(file.Path);
            var item = new MusicClip { Path = file.Path, Start = Math.Clamp(_timelinePosition, 0, Math.Max(0, TimelineDuration() - .1)), Out = length };
            if (!await EditMusicAsync(item, true)) return;
            _project.Music.Add(item);
            SelectMusic(_project.Music.Count - 1);
            InvalidateTimelinePreview();
        }
        catch (Exception ex) { await ErrorAsync("Could not add audio", ex); }
    }

    private async Task<bool> EditMusicAsync(MusicClip item, bool adding = false)
    {
        var sourceDuration = await MediaEngine.ProbeAudioDurationAsync(item.Path);
        var placement = AudioPlacement.Resolve(_project, item);
        var start = new NumberBox { Header = "Start on timeline (seconds)", Minimum = 0, Maximum = Math.Max(0, TimelineDuration() - .1), Value = placement.Start, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var trimIn = new NumberBox { Header = "Audio from (seconds)", Minimum = 0, Maximum = sourceDuration, Value = item.In, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var trimOut = new NumberBox { Header = "Audio to (seconds)", Minimum = 0, Maximum = sourceDuration, Value = item.Out, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var volume = new NumberBox { Header = "Volume (%)", Minimum = 0, Maximum = 400, Value = item.Volume * 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var fade = new CheckBox { Content = "Fade out", IsChecked = item.FadeOut };
        var fadeDuration = new NumberBox { Header = "Fade duration (seconds)", Minimum = .05, Maximum = 30, Value = item.FadeOutDuration, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var fadeIn = new CheckBox { Content = "Fade in", IsChecked = item.FadeIn };
        var fadeInDuration = new NumberBox { Header = "Fade in duration (seconds)", Minimum = .05, Maximum = 30, Value = item.FadeInDuration, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var normalize = new CheckBox { Content = "Normalize loudness", IsChecked = item.Normalize };
        var linked = new CheckBox { Content = "Follow video clip when moved or trimmed", IsChecked = item.LinkedClipId is not null, Visibility = item.LinkedClipId is not null ? Visibility.Visible : Visibility.Collapsed };
        var offset = new NumberBox { Header = "Sync offset (seconds · positive = later)", Minimum = -30, Maximum = 30, SmallChange = .01, Value = item.SyncOffset, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Visibility = item.LinkedClipId is not null ? Visibility.Visible : Visibility.Collapsed };
        void UpdateLinkControls() { start.IsEnabled = trimIn.IsEnabled = trimOut.IsEnabled = linked.IsChecked != true; offset.IsEnabled = linked.IsChecked == true; }
        linked.Checked += (_, _) => UpdateLinkControls(); linked.Unchecked += (_, _) => UpdateLinkControls(); UpdateLinkControls();
        var contents = new StackPanel { Spacing = 9, Children = { new TextBlock { Text = System.IO.Path.GetFileName(item.Path), TextTrimming = TextTrimming.CharacterEllipsis }, linked, offset, start, trimIn, trimOut, volume, normalize, fadeIn, fadeInDuration, fade, fadeDuration } };
        var dialog = new ContentDialog { Title = adding ? "Add audio" : "Edit audio", Content = new ScrollViewer { Content = contents, MaxHeight = 490, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (!adding)
        {
            dialog.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, key) =>
            {
                if (key.Key != VirtualKey.Delete || FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or NumberBox or PasswordBox or ComboBox) return;
                var index = _project.Music.IndexOf(item);
                if (index < 0) return;
                key.Handled = true;
                dialog.Hide();
                _project.Music.RemoveAt(index); _selectedMusic = -1;
                RefreshTracks(); RefreshInspector(); InvalidateTimelinePreview();
                Status.Text = "Audio removed.";
            }), true);
        }
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return false;
        if (double.IsNaN(start.Value) || double.IsNaN(trimIn.Value) || double.IsNaN(trimOut.Value) || double.IsNaN(volume.Value) || double.IsNaN(fadeDuration.Value)) return false;
        if (trimOut.Value - trimIn.Value < .08) { await MessageAsync("Audio range too short", "Choose at least 0.08 seconds of audio."); return false; }
        if (!double.IsFinite(offset.Value) || !double.IsFinite(fadeInDuration.Value)) return false;
        if (linked.IsChecked != true) { item.LinkedClipId = null; item.SyncOffset = 0; }
        else item.SyncOffset = Math.Clamp(offset.Value, -30, 30);
        item.Normalize = normalize.IsChecked == true; item.FadeIn = fadeIn.IsChecked == true;
        item.FadeInDuration = Math.Clamp(fadeInDuration.Value, .05, 30);
        item.Start = Math.Clamp(start.Value, 0, Math.Max(0, TimelineDuration() - .1));
        item.In = Math.Clamp(trimIn.Value, 0, sourceDuration);
        item.Out = Math.Clamp(trimOut.Value, item.In + .08, sourceDuration);
        item.Volume = Math.Clamp(volume.Value / 100, 0, 4);
        item.FadeOut = fade.IsChecked == true;
        item.FadeOutDuration = Math.Clamp(fadeDuration.Value, .05, 30);
        RefreshTracks(); InvalidateTimelinePreview();
        return true;
    }

    private void AddBoundarySlot(int index)
    {
        var kind = index > 0 && index < _project.Clips.Count ? _project.Clips[index].TransitionIn : TransitionKind.Cut;
        var isFade = index == 0 && _project.Clips.Count > 0 && _project.Clips[0].FadeIn ||
            index == _project.Clips.Count && index > 0 && _project.Clips[^1].FadeOut;
        var label = index == 0 ? (isFade ? "Fade in" : "+") :
            index == _project.Clips.Count ? (isFade ? "Fade out" : "+") :
            kind == TransitionKind.Cut ? "+" : TransitionName(kind);
        var slot = new Border { Width = 18, Height = 78,
            CornerRadius = new CornerRadius(4), Background = index == _selectedBoundary && _boundarySelected ? Brush("#775B46") : Brush("#353D45"),
            Tag = index, Child = new TextBlock { Text = isFade ? "◐" : TransitionGlyph(kind), TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 } };
        var glyph = slot.Child;
        slot.Child = null;
        var boundaryButton = new Button { Content = glyph, Padding = new Thickness(0), Width = 18, Height = 78, Background = slot.Background };
        slot.Child = boundaryButton;
        boundaryButton.Click += (_, _) => { SelectBoundary(index); TransitionSettingsButton.Flyout.ShowAt(TransitionSettingsButton); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(boundaryButton, $"{(index == 0 ? "Movie start" : index == _project.Clips.Count ? "Movie end" : $"Before clip {index + 1}")}: {label}");
        ToolTipService.SetToolTip(slot, $"{label} · click to choose transition");
        var layout = new TimelineLayout(_project.Clips);
        Canvas.SetLeft(slot, Math.Max(0, (index < layout.Starts.Length ? layout.Starts[index] : layout.Duration) * _pixelsPerSecond - 9));
        Canvas.SetTop(slot, 3); Canvas.SetZIndex(slot, 10);
        TimelineList.Children.Add(slot);
    }

    private void SelectBoundary(int index)
    {
        CloseTitleEditor();
        PausePreview();
        _previewPlayback = false;
        _pendingTimelinePlay = false;
        _selectedBoundary = index;
        _selectedClip = -1;
        _selectedClips.Clear(); _selectedTitle = -1; _selectedMusic = -1;
        _timelineFocused = true; _spaceSourcePlaying = false;
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
        if (_selectedClips.Count > 1 && !_boundarySelected)
        {
            foreach (var index in _selectedClips.Where(x => x > 0 && x < _project.Clips.Count))
                _project.Clips[index].TransitionIn = kind;
            RefreshTimeline(); RefreshInspector(); InvalidateTimelinePreview();
            Status.Text = $"Applied {TransitionName(kind)} to selected incoming boundaries.";
            return;
        }
        if (_selectedBoundary < 0 || _selectedBoundary > _project.Clips.Count)
            _selectedBoundary = _selectedClip > 0 ? _selectedClip : Math.Min(1, _project.Clips.Count);
        _boundarySelected = true;
        _selectedClip = -1;
        _selectedClips.Clear();
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
        var multiple = _selectedClips.Count > 1 && !_boundarySelected;
        var valid = _selectedBoundary >= 0 && _selectedBoundary <= _project.Clips.Count && _project.Clips.Count > 0;
        TransitionSettingsButton.IsEnabled = TransitionCombo.IsEnabled = valid || multiple;
        BoundaryLabel.Text = multiple ? $"{_selectedClips.Count} clips" : !valid ? "Click a + between clips" :
            _selectedBoundary == 0 ? "At start of movie" : _selectedBoundary == _project.Clips.Count ? "At end of movie" : $"Before clip {_selectedBoundary + 1}";
        _loadingControls = true;
        TransitionCombo.Items.Clear();
        if (multiple)
        {
            foreach (var kind in Enum.GetValues<TransitionKind>()) TransitionCombo.Items.Add(TransitionItem(kind, TransitionName(kind)));
            var first = _selectedClips.Where(x => x > 0).Order().FirstOrDefault();
            TransitionCombo.SelectedIndex = (int)_project.Clips[first].TransitionIn;
            TransitionSeconds.Value = _project.Clips[first].TransitionDuration;
            TransitionSeconds.IsEnabled = true;
        }
        else if (valid)
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
        if (_loadingControls || _project.Clips.Count == 0 || double.IsNaN(e.NewValue)) return;
        var value = Math.Clamp(e.NewValue, .05, 10);
        if (_selectedClips.Count > 1 && !_boundarySelected)
        {
            foreach (var index in _selectedClips.Where(x => x > 0 && x < _project.Clips.Count))
                _project.Clips[index].TransitionDuration = value;
            RefreshTimeline(); InvalidateTimelinePreview(); return;
        }
        if (_selectedBoundary < 0) return;
        if (_selectedBoundary == 0) _project.Clips[0].FadeInDuration = value;
        else if (_selectedBoundary == _project.Clips.Count) _project.Clips[^1].FadeOutDuration = value;
        else _project.Clips[_selectedBoundary].TransitionDuration = value;
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private int TimelineInsertionIndex(double x)
    {
        var layout = new TimelineLayout(_project.Clips);
        for (var i = 0; i < _project.Clips.Count; i++)
            if (x < (layout.Starts[i] + _project.Clips[i].Duration / 2) * _pixelsPerSecond) return i;
        return _project.Clips.Count;
    }

    private void MoveTimelineClip(int source, int insertion)
    {
        if (source < 0 || source >= _project.Clips.Count) return;
        var destination = source < insertion ? insertion - 1 : insertion;
        if (destination == source) return;
        var clip = _project.Clips[source];
        _project.Clips.RemoveAt(source);
        destination = Math.Clamp(destination, 0, _project.Clips.Count);
        _project.Clips.Insert(destination, clip);
        SelectClip(destination);
        InvalidateTimelinePreview();
        Status.Text = "Clip moved.";
    }

    private static bool ControlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private void SelectClip(int index, bool additive = false)
    {
        if (index < 0 || index >= _project.Clips.Count) return;
        CloseTitleEditor();
        if (additive)
        {
            if (!_selectedClips.Add(index)) _selectedClips.Remove(index);
            _selectedClip = _selectedClips.Contains(index) ? index : _selectedClips.DefaultIfEmpty(-1).Last();
        }
        else { _selectedClips.Clear(); _selectedClips.Add(index); _selectedClip = index; }
        _selectedTitle = -1; _selectedMusic = -1;
        _timelineFocused = true; _spaceSourcePlaying = false;
        _boundarySelected = false;
        _selectedBoundary = index;
        _previewPlayback = false;
        _pendingTimelinePlay = false;
        PausePreview();
        if (SelectedClip is { } clip) { _timelinePosition = TimelineClipStart(index); LoadClipPreview(clip, clip.In); }
        PreviewMode.Text = _selectedClips.Count > 1 ? $"Timeline · {_selectedClips.Count} clips selected" : $"Timeline · clip {index + 1}";
        RefreshTimeline();
        RefreshInspector();
        UpdateCommands();
        if (index * 2 + 1 < TimelineList.Children.Count && TimelineList.Children[index * 2 + 1] is Button clipCard)
            clipCard.Focus(FocusState.Programmatic);
    }

    private void RefreshInspector()
    {
        _loadingControls = true;
        try
        {
            var clip = SelectedClip;
            RefreshMediaTools(clip);
            TrimInBox.Value = clip?.In ?? 0;
            TrimOutBox.Value = clip?.Out ?? 0;
            TrimInBox.IsEnabled = TrimOutBox.IsEnabled = clip is not null && _selectedClips.Count == 1;
            SpeedButton.Content = clip is null ? "Speed: 100%…" : $"Speed: {clip.Speed * 100:0}%…";
            ClipSettingsButton.IsEnabled = clip is not null;
            ClipSettingsHeading.Text = _selectedClips.Count > 1 ? $"{_selectedClips.Count} clips selected" : "Clip settings";
            VolumeLabel.Text = $"Volume · {(clip?.Volume ?? 1) * 100:0}%";
            SpeedButton.IsEnabled = clip is not null;
            VolumeSlider.Value = clip?.Volume ?? 1;
            NormalizeBox.IsChecked = clip?.Normalize ?? false;
            FadeInBox.IsChecked = clip?.FadeIn ?? false;
            FadeOutBox.IsChecked = clip?.FadeOut ?? false;
            var trackTitle = _titleDraft ?? (_selectedTitle >= 0 && _selectedTitle < _project.Titles.Count ? _project.Titles[_selectedTitle] : null);
            ApplyTitlePreview(trackTitle, clip);
        }
        finally { _loadingControls = false; }
    }

    private TimelineClip? SelectedClip => _selectedClip >= 0 && _selectedClip < _project.Clips.Count ? _project.Clips[_selectedClip] : null;

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Preview.Width = Math.Max(1, e.NewSize.Width);
        Preview.Height = Math.Max(1, e.NewSize.Height);
        var trackTitle = _titleDraft ?? (_selectedTitle >= 0 && _selectedTitle < _project.Titles.Count ? _project.Titles[_selectedTitle] : null);
        ApplyTitlePreview(trackTitle, SelectedClip);
    }

    private void ApplyTitlePreview(TitleItem? title, TimelineClip? legacyClip = null)
    {
        var legacy = title is null && legacyClip is { TitlePreset: not TitlePreset.None };
        if (title is null && !legacy) { HideTitlePreview(); return; }
        var preset = title?.Preset ?? legacyClip?.TitlePreset ?? TitlePreset.Center;
        var lower = preset == TitlePreset.LowerThird;
        var opening = preset == TitlePreset.Opening;
        var videoWidth = Math.Max(1, PreviewHost.ActualWidth);
        var videoHeight = Math.Max(1, PreviewHost.ActualHeight);
        var naturalWidth = StillPreview.Visibility == Visibility.Visible ? 854u : _player.PlaybackSession.NaturalVideoWidth;
        var naturalHeight = StillPreview.Visibility == Visibility.Visible ? 480u : _player.PlaybackSession.NaturalVideoHeight;
        if (naturalWidth > 0 && naturalHeight > 0)
        {
            var scale = Math.Min(videoWidth / naturalWidth, videoHeight / naturalHeight);
            videoWidth = naturalWidth * scale;
            videoHeight = naturalHeight * scale;
        }
        if (title is not null)
        {
            TitleOverlay.Visibility = Visibility.Collapsed;
            TitleBackgroundPreview.Visibility = Visibility.Collapsed;
            TitleGraphicPreview.Visibility = Visibility.Visible;
            TitleGraphicPreview.Width = videoWidth;
            TitleGraphicPreview.Height = videoHeight;
            var renderHeight = (int)Math.Clamp(Math.Round(naturalHeight > 0 ? (double)naturalHeight : 720d), 360, 1080);
            var renderWidth = (int)Math.Max(2, Math.Round(renderHeight * videoWidth / Math.Max(1, videoHeight)));
            ScheduleTitleGraphic(title, renderWidth, renderHeight);
            return;
        }
        CancelTitleGraphic();
        TitleOverlay.Visibility = Visibility.Visible;
        TitleBackgroundPreview.Visibility = Visibility.Visible;
        var text = legacyClip?.Title ?? "";
        var wasLoading = _loadingControls;
        _loadingControls = true;
        if (TitleOverlay.Text != text) TitleOverlay.Text = text;
        _loadingControls = wasLoading;
        var bottomInset = Math.Max(0, (PreviewHost.ActualHeight - videoHeight) / 2);
        var margin = lower ? new Thickness(0, 0, 0, bottomInset + videoHeight * .07) : new Thickness(0);
        TitleOverlay.FontFamily = new FontFamily("Segoe UI");
        TitleOverlay.FontSize = 27;
        TitleOverlay.Foreground = Brush("#FFFFFF");
        TitleOverlay.Background = Brush("#00000000");
        TitleOverlay.Width = Math.Max(100, videoWidth * .8);
        TitleOverlay.VerticalAlignment = lower ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        TitleOverlay.Margin = margin;
        TitleBackgroundPreview.Background = Brush("#73000000");
        TitleBackgroundPreview.HorizontalAlignment = HorizontalAlignment.Center;
        TitleBackgroundPreview.Width = videoWidth;
        TitleBackgroundPreview.VerticalAlignment = lower ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        TitleBackgroundPreview.Height = opening ? videoHeight : Math.Max(55, videoHeight * (lower ? .28 : .3));
        TitleBackgroundPreview.Margin = lower ? new Thickness(0, 0, 0, bottomInset) : new Thickness(0);
    }

    private void HideTitlePreview()
    {
        TitleOverlay.Visibility = Visibility.Collapsed;
        TitleBackgroundPreview.Visibility = Visibility.Collapsed;
        CancelTitleGraphic();
    }

    private void CancelTitleGraphic()
    {
        _titleGraphicCts?.Cancel();
        _titleGraphicCts = null;
        _titleGraphicSignature = null;
        TitleGraphicPreview.Visibility = Visibility.Collapsed;
        TitleGraphicPreview.Source = null;
        if (_titleGraphicPath is { } oldPath)
        {
            _titleGraphicPath = null;
            try { File.Delete(oldPath); } catch { }
        }
    }

    private void ScheduleTitleGraphic(TitleItem title, int width, int height)
    {
        var signature = $"{width}x{height}:{JsonSerializer.Serialize(title)}";
        if (signature == _titleGraphicSignature) return;
        _titleGraphicSignature = signature;
        _titleGraphicCts?.Cancel();
        var tokenSource = new CancellationTokenSource();
        _titleGraphicCts = tokenSource;
        TitleGraphicPreview.Source = null;
        var snapshot = JsonSerializer.Deserialize<TitleItem>(JsonSerializer.Serialize(title))!;
        TrackBackground(RenderTitleGraphicPreviewAsync(snapshot, width, height, signature, tokenSource));
    }

    private async Task RenderTitleGraphicPreviewAsync(TitleItem title, int width, int height,
        string signature, CancellationTokenSource tokenSource)
    {
        var output = System.IO.Path.Combine(_previewDirectory, $"title-{Guid.NewGuid():N}.png");
        try
        {
            await Task.Delay(120, tokenSource.Token);
            await MediaEngine.RenderTitleGraphicAsync(title, width, height, output, tokenSource.Token);
            if (_closed || tokenSource.IsCancellationRequested || signature != _titleGraphicSignature) return;
            var oldPath = _titleGraphicPath;
            TitleGraphicPreview.Source = new BitmapImage(new Uri(output));
            _titleGraphicPath = output;
            if (oldPath is not null) { try { File.Delete(oldPath); } catch { } }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Status.Text = "Title preview unavailable. Check FFmpeg and the selected font."; }
        finally
        {
            if (_titleGraphicCts == tokenSource) _titleGraphicCts = null;
            tokenSource.Dispose();
            if (_titleGraphicPath != output) { try { File.Delete(output); } catch { } }
        }
    }

    private void TitleGraphic_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_titleEditorText is not null) _titleEditorText.Focus(FocusState.Programmatic);
        else if (_selectedTitle >= 0 && _selectedTitle < _project.Titles.Count)
            BeginTitleEdit(_project.Titles[_selectedTitle]);
        e.Handled = true;
    }
    private IEnumerable<TimelineClip> SelectedClips => _selectedClips.Where(x => x >= 0 && x < _project.Clips.Count).Select(x => _project.Clips[x]);

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedClip is not { } clip) return;
        var original = SelectedClips.Sum(x => Math.Max(0, x.Out - x.In));
        var percent = new NumberBox { Header = "Speed percentage", Minimum = 25, Maximum = 400,
            SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = clip.Speed * 100, Width = 180 };
        var newLength = new TextBlock { Text = $"New total length: {PreciseTime(SelectedClips.Sum(x => x.Duration))}" };
        percent.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue) && args.NewValue > 0)
                newLength.Text = $"New total length: {PreciseTime(original / (args.NewValue / 100))}";
        };
        var pitch = new CheckBox { Content = "Keep audio pitch", IsChecked = clip.KeepPitch };
        var stack = new StackPanel { Spacing = 10, Children =
            { percent, new TextBlock { Text = $"Original total length: {PreciseTime(original)}" }, newLength, pitch } };
        var dialog = new ContentDialog { Title = _selectedClips.Count > 1 ? $"Speed · {_selectedClips.Count} clips" : "Clip speed", Content = stack,
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || double.IsNaN(percent.Value)) return;
        foreach (var selected in SelectedClips)
        {
            selected.Speed = Math.Clamp(percent.Value / 100, .25, 4);
            selected.KeepPitch = pitch.IsChecked == true;
        }
        _player.PlaybackSession.PlaybackRate = clip.Speed;
        RefreshInspector();
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private void InspectorCheck_Changed(object sender, RoutedEventArgs e) => UpdateClip();
    private void InspectorValue_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => UpdateClip();

    private void UpdateClip()
    {
        if (_loadingControls || SelectedClip is not { } clip) return;
        foreach (var selected in SelectedClips)
        {
            selected.Volume = VolumeSlider.Value;
            selected.Normalize = NormalizeBox.IsChecked == true;
            selected.FadeIn = FadeInBox.IsChecked == true;
            selected.FadeOut = FadeOutBox.IsChecked == true;
        }
        VolumeLabel.Text = $"Volume · {clip.Volume * 100:0}%";
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
        LoadClipPreview(clip, clip.In);
        RefreshTimeline();
        InvalidateTimelinePreview();
    }

    private void TitleOverlay_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingControls) return;
        if (_titleDraft is { } draft)
        {
            draft.Text = TitleOverlay.Text;
            if (_titleEditorText is { } field && field.Text != draft.Text) field.Text = draft.Text;
            return;
        }
        if (_selectedTitle >= 0 && _selectedTitle < _project.Titles.Count)
        {
            _project.Titles[_selectedTitle].Text = TitleOverlay.Text;
            RefreshTracks(); InvalidateTimelinePreview(); return;
        }
        if (_loadingControls || SelectedClip is not { } clip) return;
        clip.Title = TitleOverlay.Text;
        InvalidateTimelinePreview();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTitle >= 0) { _project.Titles.RemoveAt(_selectedTitle); _selectedTitle = -1; CloseTitleEditor(true); }
        else if (_selectedMusic >= 0) { _project.Music.RemoveAt(_selectedMusic); _selectedMusic = -1; }
        else if (_boundarySelected) { ApplyTransition(TransitionKind.Cut); return; }
        else { RemoveSelectedClip(); return; }
        RefreshTracks(); RefreshInspector(); InvalidateTimelinePreview();
    }

    private void RemoveSelectedClip()
    {
        if (_selectedClips.Count == 0) return;
        var first = _selectedClips.Min();
        foreach (var index in _selectedClips.OrderDescending())
            if (index >= 0 && index < _project.Clips.Count) _project.Clips.RemoveAt(index);
        _selectedClips.Clear();
        _selectedClip = Math.Min(first, _project.Clips.Count - 1);
        _boundarySelected = false;
        _selectedBoundary = Math.Min(_selectedBoundary, _project.Clips.Count);
        if (_selectedClip >= 0) SelectClip(_selectedClip);
        else
        {
            PausePreview(); _player.Source = null; _previewPath = null; _previewPlayback = false;
            PreviewMode.Text = "";
            RefreshTimeline(); RefreshInspector(); TitleOverlay.Visibility = Visibility.Collapsed;
        }
        InvalidateTimelinePreview();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_exporting || _dialogOpen || _commandBusy) return;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (_titleDraft is not null)
        {
            if (e.Key == VirtualKey.Delete && (_titleDeleteReady || focused is not (TextBox or NumberBox or PasswordBox or ComboBox)) && _titleEditTarget is { } target)
            {
                var index = _project.Titles.IndexOf(target);
                if (index >= 0)
                {
                    _project.Titles.RemoveAt(index); _selectedTitle = -1; CloseTitleEditor(true);
                    RefreshTracks(); RefreshInspector(); InvalidateTimelinePreview();
                    Status.Text = "Title removed."; e.Handled = true;
                }
            }
            return;
        }
        if (focused is TextBox or NumberBox or PasswordBox) return;
        if (e.Key == VirtualKey.E && _selectedAsset is not null) { AddRange_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == VirtualKey.A && ControlDown() && _timelineFocused && _project.Clips.Count > 0)
        {
            _selectedClips.Clear();
            foreach (var index in Enumerable.Range(0, _project.Clips.Count)) _selectedClips.Add(index);
            _selectedClip = 0; _selectedTitle = -1; _selectedMusic = -1; _boundarySelected = false;
            RefreshTimeline(); RefreshInspector();
            Status.Text = $"Selected all {_project.Clips.Count} clips.";
            e.Handled = true; return;
        }
        if (e.Key == VirtualKey.Space && focused is not ComboBox)
        {
            e.Handled = true;
            if (_spaceHeld) return;
            _spaceHeld = true;
            if (_spaceSourcePlaying)
            {
                PausePreview(); _spaceSourcePlaying = false;
                UpdatePlaybackButton(); e.Handled = true; return;
            }
            if (_hoveredAsset is { Kind: MediaKind.Video } asset)
            {
                _previewPlayback = false;
                _spaceSourcePlaying = true;
                LoadPreview(asset.Path, _hoveredTime);
                _player.PlaybackSession.PlaybackRate = 1;
                _player.Volume = 1;
                StartPlayback();
                PreviewMode.Text = $"Media · {asset.Name}";
                UpdatePlaybackButton(); e.Handled = true; return;
            }
            if (_timelineFocused && _project.Clips.Count > 0)
            {
                if (_selectedClips.Count == 0 && !_boundarySelected && _selectedTitle < 0 && _selectedMusic < 0) _timelineFocused = true;
                Play_Click(sender, new RoutedEventArgs());
                e.Handled = true; return;
            }
        }
        if (e.Key != VirtualKey.Delete) return;
        if (_selectedTitle >= 0 && _selectedTitle < _project.Titles.Count)
        {
            _project.Titles.RemoveAt(_selectedTitle); _selectedTitle = -1;
            RefreshTracks(); RefreshInspector(); InvalidateTimelinePreview();
            Status.Text = "Title removed."; e.Handled = true; return;
        }
        if (_selectedMusic >= 0 && _selectedMusic < _project.Music.Count)
        {
            _project.Music.RemoveAt(_selectedMusic); _selectedMusic = -1;
            RefreshTracks(); InvalidateTimelinePreview();
            Status.Text = "Audio removed."; e.Handled = true; return;
        }
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

    private void Editor_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space) return;
        _spaceHeld = false;
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot);
        if (_titleDraft is null && focused is not TextBox and not NumberBox and not PasswordBox and not ComboBox)
            e.Handled = true;
    }

    private void CancelPreviewRender()
    {
        _previewRenderVersion++;
        _previewRenderCts?.Cancel();
        _previewRenderCts?.Dispose();
        _previewRenderCts = null;
        _pendingTimelinePlay = false;
        if (_timelinePreviewPath is not null) _stalePreviews.Add(_timelinePreviewPath);
        _timelinePreviewPath = null;
        CleanupStalePreviews();
    }

    private void InvalidateTimelinePreview()
    {
        RecordEdit();
        var wasWaiting = _pendingTimelinePlay;
        if (_previewPlayback)
        {
            PausePreview();
            _previewPlayback = false;
            if (SelectedClip is { } clip) LoadClipPreview(clip, clip.In);
        }
        CancelPreviewRender();
        if (_project.Clips.Count == 0)
        { PreviewRenderLabel.Text = "Add video to start your movie."; PreviewRenderProgress.Visibility = Visibility.Collapsed; return; }
        _pendingTimelinePlay = wasWaiting;
        var snapshot = JsonSerializer.Deserialize<MovieProject>(JsonSerializer.Serialize(_project))!;
        _previewRenderCts = new CancellationTokenSource();
        var version = _previewRenderVersion;
        PreviewRenderLabel.Text = "Preparing movie preview… You can keep editing.";
        PreviewRenderProgress.Visibility = Visibility.Visible; PreviewRenderProgress.Value = 0;
        RetryPreviewButton.Visibility = Visibility.Collapsed;
        TrackBackground(RenderTimelinePreviewAsync(snapshot, version, _previewRenderCts.Token));
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
                if (!_closed && version == _previewRenderVersion && _timelinePreviewPath is null)
                { PreviewRenderProgress.Value = value; PreviewRenderLabel.Text = $"Preparing movie preview · {value:P0}"; }
            });
            var encoder = "Software (H.264)";
            try { encoder = await PreviewEncoder.Value; } catch { /* Keep software fallback. */ }
            token.ThrowIfCancellationRequested();
            await MediaEngine.ExportAsync(snapshot, output,
                new ExportSettings(854, 480, encoder, Preview: true),
                (value, _) => renderProgress.Report(value), token);
            if (token.IsCancellationRequested || version != _previewRenderVersion) return;
            _timelinePreviewPath = output;
            output = null;
            PreviewRenderLabel.Text = "Movie preview ready";
            PreviewRenderProgress.Visibility = Visibility.Collapsed;
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
                PreviewRenderLabel.Text = "Preview unavailable. Verify linked media, then retry.";
                ToolTipService.SetToolTip(PreviewRenderLabel, ex.Message);
                PreviewRenderProgress.Visibility = Visibility.Collapsed; RetryPreviewButton.Visibility = Visibility.Visible;
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
        var layout = new TimelineLayout(_project.Clips);
        return index >= 0 && index < layout.Starts.Length ? layout.Starts[index] : 0;
    }

    private double TimelineDuration() => new TimelineLayout(_project.Clips).Duration;

    private void PlayTimelinePreview()
    {
        if (_timelinePreviewPath is null || !File.Exists(_timelinePreviewPath) || _project.Clips.Count == 0) return;
        var index = _selectedClip >= 0 ? _selectedClip : Math.Clamp(_selectedBoundary, 0, _project.Clips.Count - 1);
        var clip = _project.Clips[index];
        var offset = _selectedClip >= 0 && _previewPath == clip.SourcePath ?
            Math.Clamp((_player.PlaybackSession.Position.TotalSeconds - clip.In) / Math.Max(.01, clip.Speed), 0, clip.Duration) : 0;
        var position = _selectedTitle >= 0 && _selectedTitle < _project.Titles.Count ? _project.Titles[_selectedTitle].Start :
            _selectedMusic >= 0 && _selectedMusic < _project.Music.Count ? AudioPlacement.Resolve(_project, _project.Music[_selectedMusic]).Start :
            _selectedClip >= 0 ? Math.Min(TimelineDuration(), TimelineClipStart(index) + offset) : _timelinePosition;
        _previewPlayback = true;
        LoadPreview(_timelinePreviewPath, position);
        PreviewSeek.Maximum = Math.Max(.1, TimelineDuration());
        _player.PlaybackSession.PlaybackRate = 1;
        _player.Volume = 1;
        _previewPlayback = true;
        HideTitlePreview();
        PreviewMode.Text = "Timeline · rendered preview";
        StartPlayback();
        PlayButton.Content = "⏸ Pause";
    }

    private void LoadPreview(string path, double position)
    {
        if (MediaEngine.ImageExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        { RequestClipFrame(new TimelineClip { Id = path, Kind = MediaKind.Image, SourcePath = path, Out = 5 }, 0); return; }
        CancelClipFrame();
        StillPreview.Visibility = Visibility.Collapsed;
        try
        {
            if (_previewPath != path)
            {
                _previewPath = path;
                _mediaOpening = true;
                _pendingPosition = position;
                _player.Source = MediaSource.CreateFromUri(new Uri(path));
                PreviewSeek.Maximum = path == _timelinePreviewPath ? Math.Max(.1, TimelineDuration()) : _project.Assets.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))?.Duration ?? 100;
                CleanupStalePreviews();
            }
            else if (_mediaOpening) _pendingPosition = position;
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
        if (_playRequested)
        {
            PausePreview(); _spaceSourcePlaying = false; PlayButton.Content = "▶ Play"; return;
        }
        if (_project.Clips.Count > 0 && _timelineFocused &&
            (SelectedClip is not null || _boundarySelected || _selectedTitle >= 0 || _selectedMusic >= 0 || _timelinePosition >= 0))
        {
            if (_previewPlayback && _timelinePreviewPath == _previewPath)
            {
                if (_player.PlaybackSession.Position.TotalSeconds >= TimelineDuration() - .05) _player.PlaybackSession.Position = TimeSpan.Zero;
                StartPlayback();
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
        if (_previewPath is null || StillPreview.Visibility == Visibility.Visible) return;
        StartPlayback();
        PlayButton.Content = "⏸ Pause";
    }

    private void PreviewSeek_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_seeking || _previewPath is null || _player.Source is null) return;
        if (_mediaOpening) { _pendingPosition = e.NewValue; return; }
        try { _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue); } catch { }
    }

    private void Clock_Tick(object? sender, object e)
    {
        if (_closed) return;
        if ((DateTime.UtcNow - _lastProjectState).TotalMilliseconds > 500) { _lastProjectState = DateTime.UtcNow; UpdateProjectState(); }
        if (_previewPath is null || StillPreview.Visibility == Visibility.Visible) return;
        var seconds = _player.PlaybackSession.Position.TotalSeconds;
        _seeking = true;
        PreviewSeek.Value = Math.Clamp(seconds, 0, PreviewSeek.Maximum);
        _seeking = false;
        PreviewTime.Text = Time(seconds);
        UpdatePlaybackButton();
        if (_previewPlayback && _timelinePreviewPath == _previewPath)
        { _timelinePosition = Math.Clamp(seconds, 0, TimelineDuration()); TimelinePlayhead.Margin = new Thickness(_timelinePosition * _pixelsPerSecond, 0, 0, 0); }
        if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            if (!_mediaOpening && seconds >= PreviewSeek.Maximum - .05) _playRequested = false;
            return;
        }
    }

    private void UpdatePlaybackButton()
    {
        if (_closed) return;
        if (_pendingTimelinePlay) { PlayButton.Content = "… Waiting"; return; }
        PlayButton.Content = _playRequested ? "⏸ Pause" : "▶ Play";
    }

    private async Task ErrorAsync(string title, Exception error)
    {
        Status.Text = title;
        await MessageAsync(title, error.Message);
    }

    private async Task MessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot };
        await ShowDialogAsync(dialog);
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
