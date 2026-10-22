using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace WinMovie;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _frameCts;
    private string? _frameKey, _framePath;
    private Image? _pictureDialogPreview;
    private TextBlock? _pictureDialogState;

    private void AudioTrackMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
        var add = new MenuFlyoutItem { Text = "Add audio…" };
        add.Click += AddMusic_Click; menu.Items.Add(add);
        if (_project.Music.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var audio in _project.Music)
        {
            var item = new MenuFlyoutItem { Text = $"{(audio.LinkedClipId is null ? "" : "↔ ")}{System.IO.Path.GetFileName(audio.Path)} · {PreciseTime(AudioPlacement.Resolve(_project, audio).Start)}" };
            item.Click += async (_, _) => await RunCommandAsync(async () => { var index = _project.Music.IndexOf(audio); if (index < 0) return; SelectMusic(index); await EditMusicAsync(audio); });
            menu.Items.Add(item);
        }
        menu.ShowAt(AddMusicButton);
    }

    private async void AddColor_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(async () =>
    {
        CloseTitleEditor();
        var previous = _selectedClip;
        var clip = new TimelineClip { Kind = MediaKind.Color, Out = 5 };
        _project.Clips.Add(clip);
        SelectClip(_project.Clips.Count - 1);
        if (!await EditPictureAsync())
        {
            _project.Clips.Remove(clip); CancelClipFrame();
            _selectedClip = -1; _selectedClips.Clear();
            if (_project.Clips.Count > 0) SelectClip(Math.Clamp(previous, 0, _project.Clips.Count - 1));
            else { StillPreview.Visibility = Visibility.Collapsed; StillPreview.Source = null; PreviewMode.Text = ""; RefreshTimeline(); RefreshInspector(); }
        }
    });

    private void RefreshMediaTools(TimelineClip? clip)
    {
        ClipExtraSettings.Children.Clear();
        TrimSettingsPanel.Visibility = clip?.Kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        SpeedButton.Visibility = ClipAudioControls.Visibility = clip?.Kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        if (clip is null) return;
        var picture = new Button { Content = clip.Kind == MediaKind.Color ? "Color and duration…" : "Picture and color…", HorizontalAlignment = HorizontalAlignment.Stretch };
        picture.Click += async (_, _) => { ClipSettingsButton.Flyout.Hide(); await RunCommandAsync(async () => { await EditPictureAsync(); }); };
        ClipExtraSettings.Children.Add(picture);
        if (clip.Kind == MediaKind.Video && _selectedClips.Count == 1)
        {
            var extract = new Button { Content = "Extract audio as WAV…", HorizontalAlignment = HorizontalAlignment.Stretch };
            var replace = new Button { Content = "Replace audio…", HorizontalAlignment = HorizontalAlignment.Stretch };
            extract.Click += async (_, _) => { ClipSettingsButton.Flyout.Hide(); await RunCommandAsync(ExtractAudioAsync); };
            replace.Click += async (_, _) => { ClipSettingsButton.Flyout.Hide(); await RunCommandAsync(ReplaceAudioAsync); };
            ClipExtraSettings.Children.Add(new Expander { Header = "Audio tools", HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 8, Children = { extract, replace,
                    new TextBlock { Text = "Extract the edited range, enhance it in your audio editor, then load the result with Replace audio.", TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 } } } });
        }
    }

    private async Task<bool> EditPictureAsync()
    {
        if (SelectedClip is not { } target) return false;
        var selected = SelectedClips.ToArray();
        var draft = JsonSerializer.Deserialize<TimelineClip>(JsonSerializer.Serialize(target))!;
        var preview = new Image { Height = 165, Stretch = Stretch.Uniform, Source = StillPreview.Source };
        var state = new TextBlock { Text = "Preparing frame…", FontSize = 11, Opacity = .7 };
        _pictureDialogPreview = preview; _pictureDialogState = state;
        var duration = new NumberBox { Header = "Duration (seconds)", Minimum = .1, Maximum = 86400, Value = draft.Duration, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var framing = new ComboBox { Header = "Framing", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (mode, label) in new[] { (ClipFraming.Fit, "Fit · show the whole image"), (ClipFraming.Fill, "Fill · crop to the frame"), (ClipFraming.Stretch, "Stretch · fill without cropping") })
            framing.Items.Add(new ComboBoxItem { Tag = mode, Content = label });
        framing.SelectedIndex = (int)draft.Framing;
        var look = new ComboBox { Header = "Color look", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var mode in Enum.GetValues<ColorLook>()) look.Items.Add(new ComboBoxItem { Tag = mode, Content = mode == ColorLook.BlackAndWhite ? "Black and white" : mode.ToString() });
        look.SelectedIndex = (int)draft.Look;
        static Slider Adjustment(string name, double min, double max, double value) => new() { Header = name, Minimum = min, Maximum = max, Value = value, StepFrequency = .01 };
        var brightness = Adjustment("Brightness", -.5, .5, draft.Brightness);
        var contrast = Adjustment("Contrast", .5, 2, draft.Contrast);
        var saturation = Adjustment("Color intensity", 0, 2, draft.Saturation);
        var warmth = Adjustment("Warmth · cool to warm", -1, 1, draft.Warmth);
        var color = new ColorPicker { Color = ColorFromHex(draft.Color), IsAlphaEnabled = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = true };
        var reset = new Button { Content = "Reset color adjustments" };
        var controls = new StackPanel { Spacing = 9, Children = { preview, state } };
        if (target.Kind != MediaKind.Video && selected.Length == 1) controls.Children.Add(duration);
        if (target.Kind == MediaKind.Color) controls.Children.Add(new Expander { Header = "Solid color · RGB / HEX", Content = color, IsExpanded = true });
        else
        {
            controls.Children.Add(framing); controls.Children.Add(look);
            controls.Children.Add(new Expander { Header = "Adjustments", HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel { Spacing = 8, Children = { brightness, contrast, saturation, warmth, reset } } });
        }
        void Update()
        {
            if (framing.SelectedItem is ComboBoxItem { Tag: ClipFraming mode }) draft.Framing = mode;
            if (look.SelectedItem is ComboBoxItem { Tag: ColorLook grade }) draft.Look = grade;
            draft.Brightness = brightness.Value; draft.Contrast = contrast.Value;
            draft.Saturation = saturation.Value; draft.Warmth = warmth.Value;
            draft.Color = HexFromColor(color.Color);
            if (target.Kind != MediaKind.Video && double.IsFinite(duration.Value)) { draft.In = 0; draft.Speed = 1; draft.Out = duration.Value; }
            RequestClipFrame(draft, draft.In);
        }
        framing.SelectionChanged += (_, _) => Update(); look.SelectionChanged += (_, _) => Update();
        foreach (var slider in new[] { brightness, contrast, saturation, warmth }) slider.ValueChanged += (_, _) => Update();
        color.ColorChanged += (_, _) => Update(); duration.ValueChanged += (_, _) => Update();
        reset.Click += (_, _) => { look.SelectedIndex = 0; brightness.Value = warmth.Value = 0; contrast.Value = saturation.Value = 1; Update(); };
        var dialog = new ContentDialog { Title = selected.Length > 1 ? $"Picture · {selected.Length} clips" : target.Kind == MediaKind.Color ? "Solid color" : "Picture and color",
            Content = new ScrollViewer { Content = controls, MaxHeight = 490, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel" };
        Update();
        ContentDialogResult result;
        try { result = await ShowDialogAsync(dialog); }
        finally { _pictureDialogPreview = null; _pictureDialogState = null; CancelClipFrame(); }
        if (result == ContentDialogResult.Primary)
        {
            foreach (var clip in selected)
            {
                if (target.Kind == MediaKind.Color) { if (clip.Kind == MediaKind.Color) clip.Color = draft.Color; }
                else { clip.Framing = draft.Framing; clip.Look = draft.Look; clip.Brightness = draft.Brightness; clip.Contrast = draft.Contrast; clip.Saturation = draft.Saturation; clip.Warmth = draft.Warmth; }
            }
            if (target.Kind != MediaKind.Video && selected.Length == 1 && double.IsFinite(duration.Value)) { target.In = 0; target.Out = draft.Out; target.Speed = 1; }
            RefreshTimeline(); RefreshInspector(); InvalidateTimelinePreview();
        }
        LoadClipPreview(target, target.In);
        return result == ContentDialogResult.Primary;
    }

    private void RemoveOrphanedAudio() => _project.Music.RemoveAll(x => x.LinkedClipId is not null && !_project.Clips.Any(c => c.Id == x.LinkedClipId));

    private void CancelClipFrame()
    {
        _frameCts?.Cancel();
        _frameCts = null; _frameKey = null;
    }

    private void LoadClipPreview(TimelineClip clip, double time)
    {
        if (clip.Kind != MediaKind.Video || clip.Framing != ClipFraming.Fit || clip.Look != ColorLook.Original || clip.Brightness != 0 || clip.Contrast != 1 || clip.Saturation != 1 || clip.Warmth != 0)
            RequestClipFrame(clip, time);
        else LoadPreview(clip.SourcePath, time);
    }

    private void RequestClipFrame(TimelineClip clip, double time)
    {
        var copy = JsonSerializer.Deserialize<TimelineClip>(JsonSerializer.Serialize(clip))!;
        if (copy.Kind != MediaKind.Video) time = 0;
        var key = JsonSerializer.Serialize(copy) + "|" + MediaEngine.F(time);
        if (_frameKey == key)
        {
            if (_pictureDialogPreview is { } shown) shown.Source = StillPreview.Source;
            if (_pictureDialogState is { } label) label.Text = _frameCts is null ? "Preview · adjustments apply on export" : "Updating frame…";
            return;
        }
        CancelClipFrame(); _frameKey = key;
        PausePreview(); _player.Source = null; _previewPath = null; _mediaOpening = false; _pendingPosition = null;
        PreviewTime.Text = Time(_timelineFocused ? _timelinePosition : time);
        if (_timelineFocused) TimelinePlayhead.Margin = new Thickness(_timelinePosition * _pixelsPerSecond, 0, 0, 0);
        StillPreview.Visibility = Visibility.Visible;
        _frameCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        TrackBackground(PrepareClipFrameAsync(copy, time, key, _frameCts));
        UpdateCommands();
    }

    private async Task PrepareClipFrameAsync(TimelineClip clip, double time, string key, CancellationTokenSource source)
    {
        string? output = null;
        try
        {
            if (_pictureDialogState is { } status) status.Text = "Updating frame…";
            await Task.Delay(180, source.Token);
            Directory.CreateDirectory(_thumbDirectory);
            output = System.IO.Path.Combine(_thumbDirectory, $"picture-{Guid.NewGuid():N}.jpg");
            await MediaEngine.RenderClipFrameAsync(clip, output, time, source.Token);
            if (_closed || source.IsCancellationRequested || _frameKey != key) return;
            var old = _framePath;
            StillPreview.Source = new BitmapImage(new Uri(output)); _framePath = output;
            if (_pictureDialogPreview is { } preview) preview.Source = StillPreview.Source;
            if (_pictureDialogState is { } label) label.Text = "Preview · adjustments apply on export";
            if (old is not null) { try { File.Delete(old); } catch { } }
            ApplyTitlePreview(_titleDraft ?? (_selectedTitle >= 0 && _selectedTitle < _project.Titles.Count ? _project.Titles[_selectedTitle] : null), SelectedClip);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !source.IsCancellationRequested) { if (_frameKey == key) { _frameKey = null; StillPreview.Source = null; } Status.Text = "Frame unavailable: " + ex.Message; if (_pictureDialogState is { } label) label.Text = "Frame unavailable. Check the source file and FFmpeg."; } }
        finally
        {
            if (_frameCts == source) _frameCts = null;
            source.Dispose();
            if (output != _framePath && output is not null) { try { File.Delete(output); } catch { } }
        }
    }

    private async Task ExtractAudioAsync()
    {
        if (SelectedClip is not { Kind: MediaKind.Video } clip) return;
        if (!(await MediaEngine.ProbeAsync(clip.SourcePath, _lifetime.Token)).HasAudio) { await MessageAsync("No clip audio", "This video has no audio stream."); return; }
        var picker = new FileSavePicker { SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(clip.SourcePath) + " - audio" };
        picker.FileTypeChoices.Add("WAV audio", [".wav"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        if (_project.Assets.Any(x => string.Equals(x.Path, file.Path, StringComparison.OrdinalIgnoreCase)) || _project.Music.Any(x => string.Equals(x.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a new filename to preserve linked media.");
        ExportOverlayTitle.Text = "Extracting clip audio"; ExportCancelButton.Content = "Cancel extraction";
        _exporting = true; _exportCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        EditorHost.IsEnabled = false; ExportOverlay.Visibility = Visibility.Visible;
        ExportOverlayMessage.Text = "Extracting edited clip audio · 48 kHz WAV…";
        ExportOverlayProgress.IsIndeterminate = true;
        try
        {
            await MediaEngine.ExtractWaveAsync(clip, file.Path, _exportCts.Token);
            Status.Text = "WAV saved. Enhance it, then choose Replace audio on this clip.";
        }
        catch (OperationCanceledException) { Status.Text = "Audio extraction cancelled."; return; }
        finally
        {
            _exportCts.Dispose(); _exportCts = null; _exporting = false;
            EditorHost.IsEnabled = true; ExportOverlay.Visibility = Visibility.Collapsed; ExportOverlayProgress.IsIndeterminate = false;
        }
        await ShowExportCompleteAsync(file.Path);
    }

    private async Task ReplaceAudioAsync()
    {
        if (SelectedClip is not { Kind: MediaKind.Video } clip) return;
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".wma" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        var length = await MediaEngine.ProbeAudioDurationAsync(file.Path, _lifetime.Token);
        if (length < .08) throw new InvalidOperationException("Choose at least 0.08 seconds of audio.");
        var text = "Align this audio to the start of the edited clip and mute its original sound. Use the WAV extracted from this clip for the closest match. You can adjust sync in Audio settings.";
        if (Math.Abs(length - clip.Duration) > .15) text += $"\n\nClip: {PreciseTime(clip.Duration)} · Audio: {PreciseTime(length)}. Extra audio is trimmed; missing audio is silent.";
        if (await ShowDialogAsync(new ContentDialog { Title = "Replace clip audio", Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = "Replace", CloseButtonText = "Cancel" }) != ContentDialogResult.Primary) return;
        _project.Music.RemoveAll(x => x.LinkedClipId == clip.Id);
        _project.Music.Add(new MusicClip { Path = file.Path, Out = length, Start = TimelineClipStart(_selectedClip), LinkedClipId = clip.Id, LinkedSourceIn = clip.In, LinkedSourceSpeed = clip.Speed });
        clip.Volume = 0;
        RefreshTimeline(); RefreshInspector(); InvalidateTimelinePreview();
        Status.Text = "Original sound muted. Replacement audio follows this clip; Undo restores the previous sound.";
    }
}
