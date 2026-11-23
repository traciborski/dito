using System.Reflection;
using Dito.Application;
using Dito.UI;
using Dito.Windows;

namespace Dito.Tests;

public sealed class SearchFormTests
{
    [Fact]
    public async Task HiddenFormRendersSearchResultsAndOriginalPreviewOnARealMessageLoop()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        const string text = "  original\r\nsecond line\t ";
        await database.Store.RecordCopyAsync(text, DateTimeOffset.UtcNow);

        await WinFormsTest.RunAsync(async () =>
        {
            using var search = new HistorySearch(database.Store);
            using var form = new SearchForm(search);
            _ = form.Handle;
            var searchBox = WinFormsTest.Descendants(form).OfType<TextBox>()
                .Single(control => control.AccessibleName == "Search clipboard history");
            var preview = WinFormsTest.Descendants(form).OfType<TextBox>()
                .Single(control => control.AccessibleName == "Original text preview");
            var results = WinFormsTest.Descendants(form).OfType<ListBox>().Single();
            var previewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            search.PreviewChanged += value =>
            {
                if (value == text)
                {
                    previewReady.TrySetResult();
                }
            };

            searchBox.Text = "original";
            await previewReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(results.Items.Cast<object>());
            Assert.Equal("  original | second line  ", results.GetItemText(results.Items[0]));
            Assert.Equal(text, preview.Text);
            var status = WinFormsTest.Descendants(form).OfType<Label>().Single(label => label.Dock == DockStyle.Fill);
            Assert.Empty(status.Text);
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            Assert.False(form.Visible);
        });
    }

    [Fact]
    public async Task RecallShortcutsRequestTheirRespectiveModesOnlyWithASelection()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var id = await database.Store.RecordCopyAsync("selected text", DateTimeOffset.UtcNow);

        await WinFormsTest.RunAsync(async () =>
        {
            using var search = new HistorySearch(database.Store);
            using var form = new SearchForm(search);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            search.ResultsChanged += _ => ready.TrySetResult();
            var recalls = new List<(long Id, RecallMode Mode)>();
            form.RecallRequested += (entryId, mode) => recalls.Add((entryId, mode));

            Assert.True(RaiseCommandKey(form, Keys.Shift | Keys.Enter));
            Assert.Empty(recalls);

            search.Search("", debounce: false);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(RaiseCommandKey(form, Keys.Enter));
            Assert.True(RaiseCommandKey(form, Keys.Shift | Keys.Enter));
            Assert.True(RaiseCommandKey(form, Keys.Control | Keys.Enter));
            Assert.Equal([(id, RecallMode.PasteText), (id, RecallMode.PasteFormatted), (id, RecallMode.CopyText)], recalls);
        });
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheSelectedEntryEvenIfTheKeyRepeats()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var remaining = await database.Store.RecordCopyAsync("remaining", DateTimeOffset.UtcNow);
        var selected = await database.Store.RecordCopyAsync("selected", DateTimeOffset.UtcNow);

        await WinFormsTest.RunAsync(async () =>
        {
            using var search = new HistorySearch(database.Store);
            using var form = new SearchForm(search);
            var query = WinFormsTest.Descendants(form).OfType<TextBox>()
                .Single(control => control.AccessibleName == "Search clipboard history");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshed = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
            search.ResultsChanged += results =>
            {
                if (results.Items.Count == 2)
                {
                    loaded.TrySetResult();
                }
                else
                {
                    refreshed.TrySetResult(results);
                }
            };
            search.Search("", debounce: false);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(PreprocessDelete(query));
            var results = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(remaining, Assert.Single(results.Items).Id);
            Assert.True(PreprocessDelete(query, repeat: true));
            Assert.Null(await database.Store.ReadTextAsync(selected));
            Assert.Equal("remaining", await database.Store.ReadTextAsync(remaining));
        });
    }

    [Fact]
    public async Task ResultRowsAlternateWithoutOverridingSelection()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        foreach (var text in new[] { "first", "second", "third" })
        {
            await database.Store.RecordCopyAsync(text, DateTimeOffset.UtcNow);
        }

        await WinFormsTest.RunAsync(async () =>
        {
            using var search = new HistorySearch(database.Store);
            using var form = new SearchForm(search);
            _ = form.Handle;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            search.ResultsChanged += _ => ready.TrySetResult();
            search.Search("", debounce: false);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var results = WinFormsTest.Descendants(form).OfType<ListBox>().Single();
            results.SelectedIndex = 2;
            using var image = new Bitmap(results.Width, results.Height);
            results.DrawToBitmap(image, new Rectangle(Point.Empty, results.Size));

            var evenBackground = RowBackground(0);
            var oddBackground = RowBackground(1);
            Assert.Equal(results.BackColor.ToArgb(), evenBackground);
            Assert.Equal(SystemInformation.HighContrast, evenBackground == oddBackground);
            Assert.Equal(SystemColors.Highlight.ToArgb(), RowBackground(2));

            int RowBackground(int index)
            {
                var row = results.GetItemRectangle(index);
                return image.GetPixel(row.Right - 5, row.Top + row.Height / 2).ToArgb();
            }
        });
    }

    [Fact]
    public async Task NativeMessageWindowHasAHandleWithoutShowingAForm()
    {
        await WinFormsTest.RunAsync(() =>
        {
            using var messages = new MessageWindow();
            Assert.NotEqual(nint.Zero, messages.Handle);
            messages.StartListening();
            messages.StopListening();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DoubleClickOnBlankListSpaceDoesNotRecallTheSelectedEntry()
    {
        await using var database = new TestDatabase();
        await database.Store.InitializeAsync();
        var id = await database.Store.RecordCopyAsync("selected text", DateTimeOffset.UtcNow);

        await WinFormsTest.RunAsync(async () =>
        {
            using var search = new HistorySearch(database.Store);
            using var form = new SearchForm(search);
            _ = form.Handle;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            search.ResultsChanged += _ => ready.TrySetResult();
            var recalls = new List<(long Id, RecallMode Mode)>();
            form.RecallRequested += (entryId, mode) => recalls.Add((entryId, mode));
            search.Search("", debounce: false);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            var results = WinFormsTest.Descendants(form).OfType<ListBox>().Single();

            RaiseDoubleClick(results, new Point(5, results.ClientSize.Height - 2));
            Assert.Empty(recalls);

            var row = results.GetItemRectangle(0);
            RaiseDoubleClick(results, new Point(row.Left + 5, row.Top + 5));
            Assert.Equal((id, RecallMode.PasteText), Assert.Single(recalls));
        });
    }

    [Fact]
    public void InvalidNativePasteTargetFailsBeforeInputInjection()
    {
        var desktop = new WindowsDesktop();
        var clipboardChecked = false;

        var pasted = desktop.TrySendPaste(new PasteTarget(0, 0, 0), () =>
        {
            clipboardChecked = true;
            return true;
        });

        Assert.False(pasted);
        Assert.False(clipboardChecked);
    }

    private static bool RaiseCommandKey(Form form, Keys keys)
    {
        return (bool)typeof(Form).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, [default(Message), keys])!;
    }

    private static bool PreprocessDelete(Control control, bool repeat = false)
    {
        var message = Message.Create(control.Handle, 0x0100, (nint)Keys.Delete, repeat ? 1 << 30 : 0);
        return control.PreProcessMessage(ref message);
    }

    private static void RaiseDoubleClick(ListBox control, Point location)
    {
        // Raise a managed event on our hidden control, not desktop mouse input.
        typeof(Control).GetMethod("OnMouseDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(control, [new MouseEventArgs(MouseButtons.Left, 2, location.X, location.Y, 0)]);
    }
}
