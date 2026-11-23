using Dito.Storage;

namespace Dito.Application;

internal sealed record SearchResults(
    IReadOnlyList<HistoryListItem> Items,
    bool CanLoadNewer,
    bool CanLoadOlder);

internal sealed class HistorySearch : IDisposable
{
    private readonly HistoryStore _history;
    private readonly List<long?> _cursors = [null];
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _previewCancellation;
    private string _query = "";
    private int _version;
    private int _pageIndex;
    private long? _nextCursor;
    private long? _selectedId;
    private bool _loading;
    private bool _disposed;

    public HistorySearch(HistoryStore history)
    {
        _history = history;
    }

    public event Action? Loading;
    public event Action<SearchResults>? ResultsChanged;
    public event Action<string>? PreviewChanged;
    public event Action<string>? Failed;

    public bool IsChanging { get; private set; }

    public Task DeleteEntryAsync(long id) => ChangeEntryAsync(
        () => _history.DeleteEntryAsync(id), "The entry could not be deleted. Check history access.");

    public Task SetLabelAsync(long id, string label) => ChangeEntryAsync(
        () => _history.SetLabelAsync(id, label), "The label could not be saved. Check history access.");

    public void Search(string query, bool debounce = true)
    {
        _query = query;
        _cursors.Clear();
        _cursors.Add(null);
        _pageIndex = 0;
        _nextCursor = null;
        BeginSearch(null, 0, debounce);
    }

    public void LoadOlder()
    {
        if (!_loading && _nextCursor.HasValue)
        {
            BeginSearch(_nextCursor, _pageIndex + 1, debounce: false);
        }
    }

    public void LoadNewer()
    {
        if (!_loading && _pageIndex > 0)
        {
            BeginSearch(_cursors[_pageIndex - 1], _pageIndex - 1, debounce: false);
        }
    }

    public void Select(long? id)
    {
        CancelPreview();
        _selectedId = id;
        PreviewChanged?.Invoke("");
        if (id.HasValue && !_disposed)
        {
            _previewCancellation = new CancellationTokenSource();
            _ = LoadPreviewAsync(id.Value, _previewCancellation.Token);
        }
    }

    public void Cancel()
    {
        _version++;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        CancelPreview();
        _loading = false;
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    private async Task ChangeEntryAsync(Func<Task<bool>> change, string failure)
    {
        if (_disposed || IsChanging)
        {
            return;
        }

        var version = _version;
        IsChanging = true;
        try
        {
            await change();
            if (!_disposed && version == _version)
            {
                Search(_query, debounce: false);
            }
        }
        catch (Exception)
        {
            if (!_disposed && version == _version)
            {
                Failed?.Invoke(failure);
            }
        }
        finally
        {
            IsChanging = false;
        }
    }

    private void BeginSearch(long? cursor, int pageIndex, bool debounce)
    {
        Cancel();
        if (_disposed)
        {
            return;
        }

        _searchCancellation = new CancellationTokenSource();
        _loading = true;
        Loading?.Invoke();
        _ = LoadPageAsync(
            _query, cursor, pageIndex, _version, debounce, _searchCancellation.Token);
    }

    private async Task LoadPageAsync(
        string query,
        long? cursor,
        int pageIndex,
        int version,
        bool debounce,
        CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(150, cancellationToken);
            }

            var page = await _history.SearchAsync(query, cursor, cancellationToken);
            if (_disposed || cancellationToken.IsCancellationRequested || version != _version)
            {
                return;
            }

            AcceptPage(page, cursor, pageIndex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed && version == _version)
            {
                _loading = false;
                Failed?.Invoke("History could not be searched. Check database access and disk space.");
            }
        }
    }

    private void AcceptPage(HistoryPage page, long? cursor, int pageIndex)
    {
        if (_cursors.Count > pageIndex)
        {
            _cursors.RemoveRange(pageIndex, _cursors.Count - pageIndex);
        }

        _cursors.Add(cursor);
        _pageIndex = pageIndex;
        _nextCursor = page.NextCursor;
        _loading = false;
        ResultsChanged?.Invoke(new SearchResults(page.Items, pageIndex > 0, _nextCursor.HasValue));
    }

    private async Task LoadPreviewAsync(long id, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _history.ReadTextAsync(id, cancellationToken);
            if (!_disposed && !cancellationToken.IsCancellationRequested && _selectedId == id)
            {
                PreviewChanged?.Invoke(text ?? "");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed && !cancellationToken.IsCancellationRequested && _selectedId == id)
            {
                Failed?.Invoke("The selected text could not be loaded.");
            }
        }
    }

    private void CancelPreview()
    {
        _selectedId = null;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
    }
}
