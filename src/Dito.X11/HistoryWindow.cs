using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Dito.Application;

namespace Dito.X11;

internal sealed class HistoryWindow : Window
{
    private readonly HistorySearch _search;
    private readonly Func<long, CancellationToken, Task<bool>> _copyEntry;
    private readonly TextBox _query = new() { PlaceholderText = "Search clipboard history" };
    private readonly ListBox _entries = new();
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _captureStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _newer = new() { Content = "Newer", IsEnabled = false };
    private readonly Button _older = new() { Content = "Older", IsEnabled = false };
    private readonly Button _copy = new() { Content = "Copy", IsEnabled = false };
    private readonly TextBox _label = new() { PlaceholderText = "Optional label", MinWidth = 140, IsEnabled = false };
    private readonly Button _saveLabel = new() { Content = "Save label", IsEnabled = false };
    private readonly Button _delete = new() { Content = "Delete entry", IsEnabled = false };
    private readonly Button _pause = new() { Content = "Pause Capture", IsEnabled = false };
    private readonly Button _exit = new() { Content = "Exit" };
    private bool _ready;
    private bool _closing;
    private bool _opening;
    private bool _copying;
    private bool _deleteHeld;
    private CancellationTokenSource? _recall;

    public HistoryWindow(HistorySearch search, Func<long, CancellationToken, Task<bool>> copyEntry, WindowIcon icon)
    {
        _search = search;
        _copyEntry = copyEntry;
        Title = "Dito";
        Icon = icon;
        Width = 620;
        Height = 510;
        MinWidth = 410;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = CreateLayout();

        _query.TextChanged += (_, _) =>
        {
            if (_ready && IsVisible && !_opening)
            {
                _search.Search(_query.Text ?? "");
            }
        };
        _entries.SelectionChanged += (_, _) =>
        {
            _recall?.Cancel();
            _search.Select(Selected?.Id);
            _label.Text = Selected?.Label ?? "";
            UpdateEntryActions();
        };
        _entries.DoubleTapped += async (_, args) =>
        {
            if (args.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null)
            {
                await CopySelectedAsync();
            }
        };
        _copy.Click += async (_, _) => await CopySelectedAsync();
        _saveLabel.Click += async (_, _) => await SaveLabelAsync();
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _newer.Click += (_, _) => _search.LoadNewer();
        _older.Click += (_, _) => _search.LoadOlder();
        _pause.Click += (_, _) => ToggleCapture?.Invoke();
        _exit.Click += (_, _) => Quit?.Invoke();
        Closing += (_, args) =>
        {
            if (!_closing)
            {
                args.Cancel = true;
                HideHistory();
            }
        };
        AddHandler(KeyDownEvent, HandleKeyAsync, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, args) =>
        {
            if (args.Key == Key.Delete)
            {
                _deleteHeld = false;
            }
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _deleteHeld = false;
        _search.Loading += ShowLoading;
        _search.ResultsChanged += ShowResults;
        _search.PreviewChanged += text => _preview.Text = text;
        _search.Failed += ShowError;
    }

    public event Action? ToggleCapture;
    public event Action? Quit;

    private HistoryListItem? Selected => (_entries.SelectedItem as DisplayEntry)?.Entry;

    public void OpenHistory()
    {
        if (!IsVisible)
        {
            _opening = true;
            _query.Text = "";
            Show();
            _opening = false;
            if (_ready)
            {
                _search.Search("", debounce: false);
            }
        }

        Activate();
        _query.Focus();
    }

    public void EnableHistory()
    {
        _ready = true;
        if (IsVisible)
        {
            _search.Search(_query.Text ?? "", debounce: false);
        }
    }

    public void ShowError(string message) => _status.Text = message;

    public void ShowCaptureStatus(bool paused, string? message = null, bool canToggle = true)
    {
        _pause.IsEnabled = canToggle;
        _pause.Content = paused ? "Resume Capture" : "Pause Capture";
        _captureStatus.Text = message ?? (paused ? "Capture paused." : "");
    }

    public void Exit()
    {
        _closing = true;
        HideHistory();
        Close();
    }

    private Grid CreateLayout()
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _newer, _older, _copy, _pause, _exit }
        };
        var entryActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _label, _saveLabel, _delete }
        };
        var grid = new Grid
        {
            Margin = new Thickness(12),
            RowSpacing = 8,
            RowDefinitions = new RowDefinitions("Auto,3*,2*,Auto,Auto,Auto,Auto"),
            Children = { _query, _entries, _preview, entryActions, _status, _captureStatus, buttons }
        };
        for (var index = 0; index < grid.Children.Count; index++)
        {
            Grid.SetRow(grid.Children[index], index);
        }

        return grid;
    }

    private void HideHistory()
    {
        _recall?.Cancel();
        _search.Cancel();
        Hide();
    }

    private void ShowLoading()
    {
        _entries.ItemsSource = null;
        _preview.Text = "";
        _status.Text = "Searching...";
        _newer.IsEnabled = false;
        _older.IsEnabled = false;
    }

    private void ShowResults(SearchResults results)
    {
        _entries.ItemsSource = results.Items.Select(entry => new DisplayEntry(entry)).ToArray();
        _entries.SelectedIndex = results.Items.Count == 0 ? -1 : 0;
        _newer.IsEnabled = results.CanLoadNewer;
        _older.IsEnabled = results.CanLoadOlder;
        _status.Text = results.Items.Count > 0 ? "Enter copies the selected text. Paste it in your other app."
            : string.IsNullOrWhiteSpace(_query.Text) ? "Clipboard history is empty." : "No matching entries.";
    }

    private async void HandleKeyAsync(object? sender, KeyEventArgs args)
    {
        if (_label.IsFocused && args.Key != Key.Escape)
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.None)
            {
                args.Handled = true;
                await SaveLabelAsync();
            }

            return;
        }

        switch (args.Key)
        {
            case Key.Escape:
                HideHistory();
                break;
            case Key.Delete when args.KeyModifiers == KeyModifiers.None && (_deleteHeld || Selected is not null):
                args.Handled = true;
                if (!_deleteHeld)
                {
                    _deleteHeld = true;
                    await DeleteSelectedAsync();
                }

                return;
            case Key.Enter when args.KeyModifiers == KeyModifiers.Control ||
                (args.KeyModifiers == KeyModifiers.None && args.Source is not Button):
                args.Handled = true;
                await CopySelectedAsync();
                return;
            case Key.Up:
            case Key.Down:
                if (_entries.ItemCount > 0)
                {
                    var offset = args.Key == Key.Up ? -1 : 1;
                    _entries.SelectedIndex = Math.Clamp(_entries.SelectedIndex + offset, 0, _entries.ItemCount - 1);
                    _entries.ScrollIntoView(_entries.SelectedIndex);
                }

                break;
            case Key.PageUp:
                _search.LoadNewer();
                break;
            case Key.PageDown:
                _search.LoadOlder();
                break;
            default:
                return;
        }

        args.Handled = true;
    }

    private async Task CopySelectedAsync()
    {
        if (_copying || _search.IsChanging || Selected is not { } selected)
        {
            return;
        }

        _copying = true;
        UpdateEntryActions();
        using var recall = new CancellationTokenSource();
        _recall = recall;
        try
        {
            var copied = await _copyEntry(selected.Id, recall.Token);
            if (recall.IsCancellationRequested)
            {
                return;
            }

            if (!copied)
            {
                ShowError("The selected text could not be copied. Try again.");
                return;
            }

            HideHistory();
        }
        catch (OperationCanceledException) when (recall.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            ShowError("The selected text could not be copied. Check clipboard and database access.");
        }
        finally
        {
            _recall = null;
            _copying = false;
            UpdateEntryActions();
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (!_copying && !_search.IsChanging && Selected is { } selected)
        {
            _query.Focus();
            await _search.DeleteEntryAsync(selected.Id);
        }
    }

    private async Task SaveLabelAsync()
    {
        if (!_copying && !_search.IsChanging && Selected is { } selected)
        {
            var label = _label.Text ?? "";
            _query.Focus();
            await _search.SetLabelAsync(selected.Id, label);
        }
    }

    private void UpdateEntryActions()
    {
        var enabled = Selected is not null && !_copying;
        _copy.IsEnabled = enabled;
        _label.IsEnabled = enabled;
        _saveLabel.IsEnabled = enabled;
        _delete.IsEnabled = enabled;
    }

    private sealed record DisplayEntry(HistoryListItem Entry)
    {
        public override string ToString()
        {
            var preview = Entry.Preview.ReplaceLineEndings(" ").Replace('\t', ' ');
            return Entry.Label is null ? preview : $"[{Entry.Label}] {preview}";
        }
    }
}
