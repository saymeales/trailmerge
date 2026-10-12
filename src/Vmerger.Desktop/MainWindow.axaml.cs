using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Vmerger.Desktop.ViewModels;

namespace Vmerger.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private ClipTimelineViewModel? _draggedClip;
    private bool _isUserSeeking;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Opened += OnOpened;
        SeekSlider.AddHandler(PointerPressedEvent, SeekSlider_OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, SeekSlider_OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, SeekSlider_OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, MainWindow_OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        Opened -= OnOpened;
        VideoView.MediaPlayer = null;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        AttachPlayerToVideoView();
    }

    private async void OpenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open media file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video files")
                {
                    Patterns = ["*.mp4", "*.mov", "*.m4v", "*.mkv", "*.avi"]
                },
                FilePickerFileTypes.All
            ]
        });

        var file = files.Count > 0 ? files[0] : null;
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        AttachPlayerToVideoView();
        await _viewModel.OpenMediaAsync(path);
    }

    private async void ImportFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Import media folder",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        await _viewModel.ImportFolderAsync(path);
    }

    private async void ImportFilesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import selected media files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Video files")
                {
                    Patterns = ["*.mp4", "*.mov", "*.m4v"]
                },
                FilePickerFileTypes.All
            ]
        });

        var paths = files
            .Select(static file => file.TryGetLocalPath())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .ToArray();

        if (paths.Length == 0)
        {
            return;
        }

        await _viewModel.ImportFilesAsync(paths);
    }

    private void AddTrailButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddTrail();
    }

    private void DeleteTrailButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSelectedTrail();
    }

    private void MergeTrailButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.MergeSelectedTrailIntoPrevious();
    }

    private void SplitTrailButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.SplitSelectedTrailBeforeSelectedClip();
    }

    private void RemoveClipButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.RemoveSelectedClip();
    }

    private void DuplicateClipButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.DuplicateSelectedClip();
    }

    private void SetStartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetSelectedClipStartFromPlayhead();
    }

    private void SetEndButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetSelectedClipEndFromPlayhead();
    }

    private void ClearRangeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ClearSelectedClipRange();
    }

    private async void ExportTrailButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        if (_viewModel.SelectedTrail is null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export selected trail",
            SuggestedFileName = $"{_viewModel.SelectedTrail.Name}.mp4",
            FileTypeChoices =
            [
                new FilePickerFileType("MP4 video")
                {
                    Patterns = ["*.mp4"]
                }
            ]
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        await _viewModel.ExportSelectedTrailAsync(path);
    }

    private void ClearExportOutputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ClearExportOutput();
    }

    private async void TrailClip_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ClipTimelineViewModel clip })
        {
            return;
        }

        _draggedClip = clip;
        _viewModel.SelectClip(clip);

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(clip.Path));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void TrailDrop_OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _draggedClip is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void TrailDrop_OnDrop(object? sender, DragEventArgs e)
    {
        if (_draggedClip is not null && sender is Control { DataContext: TrailSuggestionViewModel targetTrail })
        {
            _viewModel.MoveClipToTrail(_draggedClip, targetTrail);
        }

        _draggedClip = null;
        e.Handled = true;
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox)
        {
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Delete)
        {
            _viewModel.DeleteSelection();
            e.Handled = true;
        }
        else if (control && e.Key == Key.C)
        {
            _viewModel.CopySelectedClip();
            e.Handled = true;
        }
        else if (control && e.Key == Key.X)
        {
            _viewModel.CutSelectedClip();
            e.Handled = true;
        }
        else if (control && e.Key == Key.V)
        {
            _viewModel.PasteClipToSelectedTrail();
            e.Handled = true;
        }
    }

    private void SeekSlider_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isUserSeeking = true;
    }

    private void SeekSlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CommitSeekFromSlider();
    }

    private void SeekSlider_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CommitSeekFromSlider();
    }

    private void CommitSeekFromSlider()
    {
        if (!_isUserSeeking)
        {
            return;
        }

        _isUserSeeking = false;
        _viewModel.SeekFromUser(SeekSlider.Value);
    }

    private void AttachPlayerToVideoView()
    {
        if (!ReferenceEquals(VideoView.MediaPlayer, _viewModel.MediaPlayer))
        {
            VideoView.MediaPlayer = _viewModel.MediaPlayer;
        }
    }
}
