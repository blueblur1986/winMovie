using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;

namespace WinMovie;

public sealed partial class MainWindow
{
    private readonly EditHistory _history = new();
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private bool _commandBusy, _dialogOpen, _restoringHistory, _closed, _playRequested, _mediaOpening;
    private double _pixelsPerSecond = 65;
    private DragPayload? _sourceRangeDrag;
    private UIElement? _sourceRangeCapture;
    private Windows.Foundation.Point _sourceRangeOrigin;
    private bool _sourceRangeMoved;
    private bool _suppressTrackClick;
    private DateTime _lastProjectState = DateTime.MinValue;

    private async Task RunCommandAsync(Func<Task> action)
    {
        if (_commandBusy || _closed) return;
        _commandBusy = true;
        UpdateCommands();
        try { await action(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) await ErrorAsync("Could not complete action", ex); }
        finally { _commandBusy = false; if (!_closed) UpdateCommands(); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(ImportCoreAsync);
    private async void New_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(NewCoreAsync);
    private async void Open_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(OpenCoreAsync);
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(async () => { await SaveProjectAsync(); });
    private async void Export_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(ExportCoreAsync);
    private async void AddMusic_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(AddMusicCoreAsync);

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (_closed) return ContentDialogResult.None;
            _dialogOpen = true;
            PausePreview();
            dialog.XamlRoot = Content.XamlRoot;
            return await dialog.ShowAsync();
        }
        finally { _dialogOpen = false; _dialogGate.Release(); }
    }

    private void TrackBackground(Task task)
    {
        _backgroundTasks.Add(task);
        _ = RemoveCompletedAsync(task);
    }

    private async Task RemoveCompletedAsync(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) Status.Text = "Background work failed: " + ex.Message; }
        finally { _backgroundTasks.Remove(task); }
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _clock.Stop(); _autoSaveTimer.Stop();
        _lifetime.Cancel();
        CancelPreviewRender(); CancelTitleGraphic(); CancelClipFrame();
        _player.Dispose();
        try { await Task.WhenAll(_backgroundTasks.ToArray()); } catch { }
        _mediaLoadCts.Dispose();
        foreach (var path in new[] { _thumbDirectory, _previewDirectory })
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        _lifetime.Dispose();
    }

    private void PausePreview() { _playRequested = false; _player.Pause(); }
    private void StartPlayback() { _playRequested = true; _player.Play(); UpdatePlaybackButton(); }

    private MenuFlyout ClipMenu(int index)
    {
        var earlier = new MenuFlyoutItem { Text = "Move earlier", IsEnabled = index > 0 };
        var later = new MenuFlyoutItem { Text = "Move later", IsEnabled = index < _project.Clips.Count - 1 };
        var edit = new MenuFlyoutItem { Text = "Clip settings" };
        var remove = new MenuFlyoutItem { Text = "Remove" };
        earlier.Click += (_, _) => MoveTimelineClip(index, index - 1);
        later.Click += (_, _) => MoveTimelineClip(index, index + 2);
        edit.Click += (_, _) => { SelectClip(index); ClipSettingsButton.Flyout.ShowAt(ClipSettingsButton); };
        remove.Click += (_, _) => { if (!_selectedClips.Contains(index)) SelectClip(index); RemoveSelectedClip(); };
        return new MenuFlyout { Items = { edit, earlier, later, new MenuFlyoutSeparator(), remove } };
    }

    private void BeginSourceRangeDrag(UIElement element, MediaAsset asset, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;
        _sourceRangeDrag = new DragPayload("source", asset.Path, Math.Min(_rangeStart, _rangeEnd), Math.Max(_rangeStart, _rangeEnd));
        _sourceRangeOrigin = args.GetCurrentPoint(EditorSurface).Position;
        _sourceRangeMoved = false; _sourceRangeCapture = element;
        element.CapturePointer(args.Pointer); args.Handled = true;
    }

    private void BeginTrackDrag(UIElement element, string kind, int index, double start, double duration, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(element).Properties.IsLeftButtonPressed) return;
        _sourceRangeDrag = new DragPayload(kind, In: start, Out: duration, Index: index);
        _sourceRangeOrigin = args.GetCurrentPoint(EditorSurface).Position;
        _sourceRangeMoved = false; _sourceRangeCapture = element; _suppressTrackClick = false;
        element.CapturePointer(args.Pointer);
    }

    private void SourceRange_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (_sourceRangeDrag is null) return;
        var point = args.GetCurrentPoint(EditorSurface);
        if (!point.Properties.IsLeftButtonPressed) { CancelSourceDrag(); return; }
        if (!_sourceRangeMoved && Math.Abs(point.Position.X - _sourceRangeOrigin.X) + Math.Abs(point.Position.Y - _sourceRangeOrigin.Y) < 8) return;
        _sourceRangeMoved = true;
        if (_sourceRangeDrag.Kind != "source")
        {
            _suppressTrackClick = true;
            if (_sourceRangeCapture is FrameworkElement card)
                Canvas.SetLeft(card, DragTrackTime(_sourceRangeDrag, point.Position.X) * _pixelsPerSecond);
        }
        SourceDragText.Text = _sourceRangeDrag.Kind == "source" ? "Add selected footage to movie" : "Move " + _sourceRangeDrag.Kind;
        SourceDragHint.Visibility = Visibility.Visible;
        SourceDragHint.Margin = new Thickness(Math.Clamp(point.Position.X + 12, 0, Math.Max(0, EditorSurface.ActualWidth - 230)),
            Math.Clamp(point.Position.Y + 12, 0, Math.Max(0, EditorSurface.ActualHeight - 45)), 0, 0);
        ClearClipDragMarker();
        var timeline = args.GetCurrentPoint(TimelinePanel).Position;
        if (timeline.X < 0 || timeline.Y < 0 || timeline.X > TimelinePanel.ActualWidth || timeline.Y > TimelinePanel.ActualHeight) return;
        if (_sourceRangeDrag.Kind != "source") return;
        var insertion = TimelineInsertionIndex(args.GetCurrentPoint(TimelineList).Position.X);
        if (insertion * 2 < TimelineList.Children.Count && TimelineList.Children[insertion * 2] is Border marker)
        { _clipDragMarker = marker; _clipDragMarkerBrush = marker.Background; marker.Background = Brush("#597FC2"); }
    }

    private void SourceRange_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (_sourceRangeDrag is not { } payload) return;
        var point = args.GetCurrentPoint(TimelinePanel).Position;
        var newStart = DragTrackTime(payload, args.GetCurrentPoint(EditorSurface).Position.X);
        var insertion = TimelineInsertionIndex(args.GetCurrentPoint(TimelineList).Position.X);
        var insert = _sourceRangeMoved && point.X >= 0 && point.Y >= 0 && point.X <= TimelinePanel.ActualWidth && point.Y <= TimelinePanel.ActualHeight;
        CancelSourceDrag();
        if (insert)
        {
            if (payload.Kind == "source" && payload.Path is { } path)
            { InsertSource(path, payload.In, payload.Out, insertion); Status.Text = "Selected footage added to movie."; }
            else
            {
                CloseTitleEditor();
                if (payload.Kind == "title" && payload.Index < _project.Titles.Count)
                { _project.Titles[payload.Index].Start = newStart; SelectTitle(payload.Index); }
                else if (payload.Kind == "music" && payload.Index < _project.Music.Count)
                { var audio = _project.Music[payload.Index]; if (audio.LinkedClipId is not null) { Status.Text = "Replacement audio follows its clip. Unlink it in Audio settings to move it independently."; } else audio.Start = newStart; SelectMusic(payload.Index); }
                InvalidateTimelinePreview(); Status.Text = "Track moved.";
            }
        }
        DispatcherQueue.TryEnqueue(() => _suppressTrackClick = false);
        args.Handled = true;
    }

    private double DragTrackTime(DragPayload payload, double pointerX) => Math.Clamp(payload.In + (pointerX - _sourceRangeOrigin.X) / _pixelsPerSecond,
        0, Math.Max(0, TimelineDuration() - (payload.Kind == "title" ? payload.Out : .1)));

    private void CancelSourceDrag()
    {
        var capture = _sourceRangeCapture;
        if (_sourceRangeDrag is { Kind: not "source" } payload && capture is FrameworkElement card) Canvas.SetLeft(card, payload.In * _pixelsPerSecond);
        _sourceRangeCapture = null;
        _sourceRangeDrag = null; _sourceRangeMoved = false; SourceDragHint.Visibility = Visibility.Collapsed;
        capture?.ReleasePointerCaptures();
        ClearClipDragMarker();
    }

    private void UpdateCommands()
    {
        if (FileButton is null) return;
        FileButton.IsEnabled = ImportButton.IsEnabled = SaveButton.IsEnabled = !_commandBusy;
        AddColorButton.IsEnabled = !_commandBusy;
        ExportButton.IsEnabled = !_commandBusy && _project.Clips.Count > 0;
        AddMusicButton.IsEnabled = !_commandBusy && _project.Clips.Count > 0;
        AddTitleButton.IsEnabled = _project.Clips.Count > 0;
        AddRangeButton.IsEnabled = _selectedAsset is { } asset && File.Exists(asset.Path);
        PlayButton.IsEnabled = _timelineFocused && _project.Clips.Count > 0 || _previewPath is not null && StillPreview.Visibility != Visibility.Visible;
        PreviewSeek.IsEnabled = _previewPath is not null && StillPreview.Visibility != Visibility.Visible;
        UndoButton.IsEnabled = !_commandBusy && _history.CanUndo;
        RedoButton.IsEnabled = !_commandBusy && _history.CanRedo;
        RemoveSelectionButton.IsEnabled = _selectedClips.Count > 0 || _selectedTitle >= 0 || _selectedMusic >= 0 || _boundarySelected;
    }

    private void UpdateProjectState()
    {
        ProjectNameLabel.Text = _projectPath is null ? "My Movie" : Path.GetFileNameWithoutExtension(_projectPath);
        var dirty = HasUnsavedChanges();
        SaveStateLabel.Text = dirty ? "Unsaved changes" : _projectPath is null ? "Not saved yet" : "All changes saved";
        Title = $"{(dirty ? "* " : "")}{ProjectNameLabel.Text} · WinMovie · 1.0 Beta 9";
        UpdateCommands();
    }

    private void RecordEdit()
    {
        if (!_restoringHistory) _history.Record(ProjectSnapshot());
        UpdateProjectState();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => RestoreHistory(false);
    private void Redo_Click(object sender, RoutedEventArgs e) => RestoreHistory(true);
    private void RestoreHistory(bool redo)
    {
        if (_commandBusy || _dialogOpen) return;
        CloseTitleEditor();
        var state = redo ? _history.Redo() : _history.Undo();
        if (state is null) return;
        var thumbnails = _project.Assets.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        _restoringHistory = true;
        try
        {
            PausePreview();
            _project = ProjectDocument.Parse(state);
            foreach (var asset in _project.Assets)
                if (thumbnails.TryGetValue(asset.Path, out var old)) { asset.FilmstripFrames = old.FilmstripFrames; asset.ThumbnailPath = old.ThumbnailPath; }
            ResetSelection();
            RefreshAll(); InvalidateTimelinePreview();
            _timelineFocused = _project.Clips.Count > 0;
            if (_timelineFocused) ShowTimelineFrame(0);
            ImportButton.Focus(FocusState.Programmatic);
            Status.Text = redo ? "Edit restored." : "Edit undone.";
        }
        finally { _restoringHistory = false; UpdateProjectState(); }
    }

    private void ResetSelection()
    {
        _mediaLoadCts.Cancel(); _mediaLoadCts.Dispose(); _mediaLoadCts = new CancellationTokenSource();
        _selectedAsset = _project.Assets.FirstOrDefault();
        _selectedClip = -1; _selectedClips.Clear(); _selectedTitle = _selectedMusic = -1;
        _selectedBoundary = -1; _boundarySelected = false; _hoveredAsset = null;
        _rangeStart = 0; _rangeEnd = _selectedAsset?.Duration ?? 0; _rangeExplicit = false;
        _timelineFocused = false; _previewPlayback = false; _spaceSourcePlaying = false;
        CancelClipFrame(); StillPreview.Visibility = Visibility.Collapsed; StillPreview.Source = null;
        _previewPath = null; _pendingPosition = null; _player.Source = null;
        _timelinePosition = 0; _mediaOpening = false;
        ResetClipDrag(); HideTitlePreview();
    }

    private void ApplyTitle_Click(object sender, RoutedEventArgs e) => CommitTitleDraft();
    private void CancelTitle_Click(object sender, RoutedEventArgs e) { CloseTitleEditor(true); RefreshInspector(); }
    private void RetryPreview_Click(object sender, RoutedEventArgs e) => InvalidateTimelinePreview();

    private void EditorShortcut_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (sender.Key is Windows.System.VirtualKey.Z or Windows.System.VirtualKey.Y &&
            Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or NumberBox or PasswordBox) return;
        if (_closed || _commandBusy || _dialogOpen || _exporting) { args.Handled = true; return; }
        var key = sender.Key;
        if (key is Windows.System.VirtualKey.Z or Windows.System.VirtualKey.Y)
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or NumberBox or PasswordBox) return;
            RestoreHistory(key == Windows.System.VirtualKey.Y || sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
        }
        else if (key == Windows.System.VirtualKey.S) Save_Click(this, new RoutedEventArgs());
        else if (key == Windows.System.VirtualKey.I) Import_Click(this, new RoutedEventArgs());
        else if (key == Windows.System.VirtualKey.O) Open_Click(this, new RoutedEventArgs());
        else if (key == Windows.System.VirtualKey.N) New_Click(this, new RoutedEventArgs());
        args.Handled = true;
    }

    private void TimelineZoom_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _pixelsPerSecond = e.NewValue;
        if (TimelineList is not null) RefreshTimeline();
    }

    private void UpdateTimelineGeometry()
    {
        var layout = new TimelineLayout(_project.Clips);
        var width = Math.Max(Math.Max(600, TimelineScroller.ViewportWidth), layout.Duration * _pixelsPerSecond + 80);
        TimelineTracks.Width = TimelineList.Width = TitleTrackCanvas.Width = MusicTrackCanvas.Width = TimelineRuler.Width = width;
        RefreshTimelineRuler();
        EmptyTimelineHint.Visibility = _project.Clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TimelinePlayhead.Visibility = _project.Clips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshTracks();
        UpdateCommands();
    }

    private void RefreshTimelineRuler()
    {
        if (_closed || TimelineRuler is null) return;
        TimelineRuler.Children.Clear();
        var step = _pixelsPerSecond < 40 ? 5 : _pixelsPerSecond < 80 ? 2 : 1;
        var start = Math.Floor(TimelineScroller.HorizontalOffset / _pixelsPerSecond / step) * step;
        var end = Math.Min(TimelineDuration() + 1, (TimelineScroller.HorizontalOffset + Math.Max(800, TimelineScroller.ViewportWidth)) / _pixelsPerSecond + step);
        for (double time = start; time <= end; time += step)
        {
            var label = new TextBlock { Text = Time(time), FontSize = 10, Opacity = .55, IsHitTestVisible = false };
            Canvas.SetLeft(label, time * _pixelsPerSecond + 4); Canvas.SetTop(label, 4);
            TimelineRuler.Children.Add(label);
        }
    }

    private void TimelineRuler_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_project.Clips.Count == 0) return;
        CloseTitleEditor(); PausePreview();
        var time = Math.Clamp(e.GetCurrentPoint(TimelineRuler).Position.X / _pixelsPerSecond, 0, TimelineDuration());
        _timelineFocused = true; _timelinePosition = time;
        _selectedClip = -1; _selectedClips.Clear(); _selectedTitle = _selectedMusic = -1; _boundarySelected = false;
        ShowTimelineFrame(time);
        RefreshTimeline(); RefreshInspector(); e.Handled = true;
    }

    private double _timelinePosition;
    private void ShowTimelineFrame(double time)
    {
        _timelinePosition = time;
        _previewPlayback = _timelinePreviewPath is not null;
        if (_timelinePreviewPath is { } path) LoadPreview(path, time);
        else if (SourceAt(time) is { } source) LoadClipPreview(source.Clip, source.Position);
        PreviewMode.Text = "Movie";
        TimelinePlayhead.Margin = new Thickness(time * _pixelsPerSecond, 0, 0, 0);
    }

    private async Task RelinkMediaAsync(MediaAsset asset)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        foreach (var extension in asset.Kind == MediaKind.Image ? MediaEngine.ImageExtensions : new[] { ".mp4", ".mov", ".mkv", ".avi", ".m4v", ".webm" }) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var replacement = await MediaEngine.ProbeAsync(file.Path, _lifetime.Token);
        var clips = _project.Clips.Where(x => string.Equals(x.SourcePath, asset.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (replacement.Kind != asset.Kind) throw new InvalidOperationException("Choose the same type of media as the original.");
        if (asset.Kind == MediaKind.Video && clips.Any(x => x.Out > replacement.Duration + .05))
            throw new InvalidOperationException("The replacement video is shorter than a range used in this movie. Choose the original file or a longer replacement.");
        foreach (var clip in clips) clip.SourcePath = replacement.Path;
        _project.Assets.Remove(asset);
        if (!_project.Assets.Any(x => string.Equals(x.Path, replacement.Path, StringComparison.OrdinalIgnoreCase))) _project.Assets.Add(replacement);
        _selectedAsset = replacement;
        await SelectAssetAsync(replacement);
        InvalidateTimelinePreview(); Status.Text = $"Located {replacement.Name}.";
    }
}
