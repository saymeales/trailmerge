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
    private DiagnosticsWindow? _diagnosticsWindow;
    private bool _isUserSeeking;
    private bool _canClose;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Opened += OnOpened;
        Closing += MainWindow_OnClosing;
        ImportFolderMenuItem.Click += ImportFolderButton_OnClick;
        ImportFilesMenuItem.Click += ImportFilesButton_OnClick;
        OpenDiagnosticsMenuItem.Click += OpenDiagnosticsButton_OnClick;
        SeekSlider.AddHandler(PointerPressedEvent, SeekSlider_OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, SeekSlider_OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, SeekSlider_OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, MainWindow_OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        Opened -= OnOpened;
        ImportFolderMenuItem.Click -= ImportFolderButton_OnClick;
        ImportFilesMenuItem.Click -= ImportFilesButton_OnClick;
        OpenDiagnosticsMenuItem.Click -= OpenDiagnosticsButton_OnClick;
        _diagnosticsWindow?.Close();
        VideoView.MediaPlayer = null;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        AttachPlayerToVideoView();
        await _viewModel.RestoreProjectAsync();
    }

    private async void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        if (!_viewModel.CanEditProject) return;
        IsEnabled = false;
        await _viewModel.FlushProjectAsync();
        _canClose = true;
        Close();
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
            Title = "Open Media File",
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
            Title = "Import Media Folder",
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
            Title = "Import Selected Media Files",
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

    private void MergeSelectedTrailsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var trails = TrailListBox.SelectedItems?
            .OfType<TrailSuggestionViewModel>()
            .ToArray() ?? [];

        _viewModel.MergeSelectedTrails(trails);
    }

    private void TrailListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _viewModel.SelectedTrailCount = TrailListBox.SelectedItems?.Count ?? 0;
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

    private void PreviewClipStartButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClipTimelineViewModel clip })
        {
            _viewModel.PreviewClipFromStart(clip);
        }
    }

    private void TransitionPreviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClipTimelineViewModel clip })
        {
            _viewModel.PreviewClipTransition(clip);
        }
    }

    private void VideoSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel.TogglePlaybackFromVideoSurface();
        e.Handled = true;
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
            Title = "Export Selected Trail",
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

    private async void ExportAllTrailsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export All Trails",
            AllowMultiple = false
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (await _viewModel.ExportAllTrailsAsync(path))
        {
            var dialog = new Window
            {
                Title = "Close Project?", Width = 400, Height = 180,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var yes = new Button { Content = "Yes, Close Project", MinWidth = 130 };
            yes.Classes.Add("destructive");
            var no = new Button { Content = "No, Keep Project", MinWidth = 130 };
            yes.Click += (_, _) => dialog.Close(true);
            no.Click += (_, _) => dialog.Close(false);
            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "Export completed. Close project?", FontSize = 18 },
                    new TextBlock { Text = "This clears the saved project. Recordings and exports stay." },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { yes, no } }
                }
            };
            if (await dialog.ShowDialog<bool>(this)) await _viewModel.CloseProjectAsync();
        }
    }

    private void ClearExportOutputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ClearExportOutput();
    }

    private void OpenDiagnosticsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is null)
        {
            _diagnosticsWindow = new DiagnosticsWindow(_viewModel);
            _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        }

        _diagnosticsWindow.Show(this);
        _diagnosticsWindow.Activate();
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
        if (!_viewModel.CanEditProject) return;
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
