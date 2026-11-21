using Dito.Application;

namespace Dito.UI;

internal sealed class SearchForm : Form
{
    private readonly HistorySearch _search;
    private readonly Font _uiFont = new("Segoe UI", 10);
    private readonly Font _textFont = new("Consolas", 10);
    private readonly TextBox _searchBox = new() { Dock = DockStyle.Fill, PlaceholderText = "Search history..." };
    private readonly ListBox _results = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed
    };
    private readonly TextBox _preview = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        MaxLength = int.MaxValue
    };
    private readonly Button _newer = new() { Text = "Newer", AutoSize = true, Enabled = false };
    private readonly Button _older = new() { Text = "Older", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private bool _updatingResults;
    private bool _resettingQuery;
    private bool _dismissing;
    private bool _allowClose;
    private bool _recalling;
    private bool _canLoadNewer;
    private bool _canLoadOlder;

    public SearchForm(HistorySearch search)
    {
        _search = search;
        BuildLayout();
        ConnectEvents();
    }

    public event Action<long, RecallMode>? RecallRequested;
    public event Action? Dismissed;

    public void ShowPopup()
    {
        ResetSearch();
        PositionNearCursor();
        Show();
        Activate();
        _searchBox.Focus();
    }

    public void FocusSearch()
    {
        Activate();
        _searchBox.Focus();
    }

    public void HideForRecall()
    {
        HidePopup(notify: false);
    }

    public void SetRecalling(bool recalling)
    {
        _recalling = recalling;
        _searchBox.Enabled = !recalling;
        _results.Enabled = !recalling;
        _newer.Enabled = !recalling && _canLoadNewer;
        _older.Enabled = !recalling && _canLoadOlder;
        if (recalling)
        {
            ShowStatus("Preparing selected text...");
        }
    }

    public void ShowStatus(string message)
    {
        _status.Text = message;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _search.Loading -= OnLoading;
            _search.ResultsChanged -= OnResultsChanged;
            _search.PreviewChanged -= OnPreviewChanged;
            _search.Failed -= ShowStatus;
            _search.Cancel();
        }

        base.Dispose(disposing);
        if (disposing)
        {
            _uiFont.Dispose();
            _textFont.Dispose();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !_dismissing)
        {
            Dismiss();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Dismiss();
        }

        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Dismiss();
            return true;
        }

        var recallMode = keyData switch
        {
            Keys.Enter => RecallMode.PasteText,
            Keys.Shift | Keys.Enter => RecallMode.PasteFormatted,
            Keys.Control | Keys.Enter => RecallMode.CopyText,
            _ => (RecallMode?)null
        };
        if (!_recalling && recallMode.HasValue)
        {
            RequestRecall(recallMode.Value);
            return true;
        }

        if (!_recalling && (keyData == Keys.Up || keyData == Keys.Down))
        {
            MoveSelection(keyData == Keys.Down ? 1 : -1);
            return true;
        }

        if (!_recalling && keyData == Keys.PageDown)
        {
            _search.LoadOlder();
            return true;
        }

        if (!_recalling && keyData == Keys.PageUp)
        {
            _search.LoadNewer();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private void BuildLayout()
    {
        Text = "Dito - clipboard history";
        Size = new Size(780, 620);
        MinimumSize = new Size(540, 380);
        Font = _uiFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        _results.Font = _textFont;
        _preview.Font = _textFont;
        _searchBox.AccessibleName = "Search clipboard history";
        _results.AccessibleName = "History results";
        _preview.AccessibleName = "Original text preview";

        var content = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            Size = new Size(740, 450),
            SplitterDistance = 270,
            Panel1MinSize = 100,
            Panel2MinSize = 80
        };
        content.Panel1.Controls.Add(_results);
        content.Panel2.Controls.Add(_preview);

        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        navigation.Controls.AddRange([_newer, _older]);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_searchBox, 0, 0);
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(navigation, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.Controls.Add(new Label
        {
            Text = "Enter: text   Shift+Enter: formatted   Ctrl+Enter: copy   PgUp/PgDn   Esc",
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        }, 0, 4);
        Controls.Add(layout);
    }

    private void ConnectEvents()
    {
        _searchBox.TextChanged += OnQueryChanged;
        _results.SelectedIndexChanged += OnSelectionChanged;
        _results.MouseDoubleClick += OnResultDoubleClick;
        _results.DrawItem += OnDrawResult;
        _results.HandleCreated += (_, _) => UpdateResultRowHeight();
        _results.DpiChangedAfterParent += (_, _) => UpdateResultRowHeight();
        _results.Format += OnFormatResult;
        _results.FormattingEnabled = true;
        _newer.Click += OnNewerClicked;
        _older.Click += OnOlderClicked;
        _search.Loading += OnLoading;
        _search.ResultsChanged += OnResultsChanged;
        _search.PreviewChanged += OnPreviewChanged;
        _search.Failed += ShowStatus;
    }

    private void ResetSearch()
    {
        _resettingQuery = true;
        _searchBox.Clear();
        _resettingQuery = false;
        _search.Search("", debounce: false);
    }

    private void PositionNearCursor()
    {
        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        Location = new Point(
            Math.Clamp(cursor.X, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(cursor.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void Dismiss()
    {
        HidePopup(notify: true);
    }

    private void HidePopup(bool notify)
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        try
        {
            Hide();
            _search.Cancel();
            if (notify)
            {
                Dismissed?.Invoke();
            }
        }
        finally
        {
            _dismissing = false;
        }
    }

    private void RequestRecall(RecallMode mode)
    {
        if (_results.SelectedItem is HistoryListItem item)
        {
            RecallRequested?.Invoke(item.Id, mode);
        }
    }

    private void MoveSelection(int direction)
    {
        if (_results.Items.Count > 0)
        {
            _results.SelectedIndex = Math.Clamp(
                _results.SelectedIndex + direction, 0, _results.Items.Count - 1);
        }
    }

    private void OnQueryChanged(object? sender, EventArgs e)
    {
        if (!_resettingQuery)
        {
            _search.Search(_searchBox.Text);
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_updatingResults)
        {
            _search.Select((_results.SelectedItem as HistoryListItem)?.Id);
        }
    }

    private void OnResultDoubleClick(object? sender, MouseEventArgs e)
    {
        var index = _results.IndexFromPoint(e.Location);
        if (!_recalling && e.Button == MouseButtons.Left && index >= 0)
        {
            _results.SelectedIndex = index;
            RequestRecall(RecallMode.PasteText);
        }
    }

    private void OnNewerClicked(object? sender, EventArgs e)
    {
        _search.LoadNewer();
        _searchBox.Focus();
    }

    private void OnOlderClicked(object? sender, EventArgs e)
    {
        _search.LoadOlder();
        _searchBox.Focus();
    }

    private void OnLoading()
    {
        _updatingResults = true;
        _results.Items.Clear();
        _updatingResults = false;
        _preview.Clear();
        _canLoadNewer = false;
        _canLoadOlder = false;
        _newer.Enabled = false;
        _older.Enabled = false;
        ShowStatus("Searching...");
    }

    private void OnResultsChanged(SearchResults results)
    {
        _updatingResults = true;
        _results.BeginUpdate();
        _results.Items.Clear();
        _results.Items.AddRange(results.Items.Cast<object>().ToArray());
        _results.EndUpdate();
        _updatingResults = false;
        _canLoadNewer = results.CanLoadNewer;
        _canLoadOlder = results.CanLoadOlder;
        _newer.Enabled = !_recalling && _canLoadNewer;
        _older.Enabled = !_recalling && _canLoadOlder;
        ShowStatus(results.Items.Count == 0
            ? string.IsNullOrWhiteSpace(_searchBox.Text) ? "No clipboard history yet." : "No matches."
            : "");
        if (_results.Items.Count > 0)
        {
            _results.SelectedIndex = 0;
        }
    }

    private void OnPreviewChanged(string text)
    {
        _preview.Text = text;
    }

    private static void OnFormatResult(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is HistoryListItem item)
        {
            e.Value = string.IsNullOrWhiteSpace(item.Preview)
                ? "[Whitespace]"
                : item.Preview.Replace("\r\n", " | ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }
    }

    private void UpdateResultRowHeight()
    {
        using var graphics = _results.CreateGraphics();
        _results.ItemHeight = (int)Math.Ceiling(_results.Font.GetHeight(graphics)) + _results.LogicalToDeviceUnits(4);
    }

    private void OnDrawResult(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _results.Items.Count)
        {
            return;
        }

        var selected = (e.State & DrawItemState.Selected) != 0;
        var background = selected ? SystemColors.Highlight : ResultRowBackground(e.Index);
        var foreground = selected ? SystemColors.HighlightText
            : _results.Enabled ? _results.ForeColor : SystemColors.GrayText;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, _results.GetItemText(_results.Items[e.Index]), _results.Font,
            e.Bounds, foreground, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & (DrawItemState.Focus | DrawItemState.NoFocusRect)) == DrawItemState.Focus)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, foreground, background);
        }
    }

    private Color ResultRowBackground(int index)
    {
        var background = _results.BackColor;
        if (index % 2 == 0 || SystemInformation.HighContrast)
        {
            return background;
        }

        var foreground = _results.ForeColor;
        return Color.FromArgb(
            (15 * background.R + foreground.R) / 16,
            (15 * background.G + foreground.G) / 16,
            (15 * background.B + foreground.B) / 16);
    }
}
