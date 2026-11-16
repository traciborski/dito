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
            Assert.Equal(text, preview.Text);
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            Assert.False(form.Visible);
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
            var recalls = new List<(long Id, bool Paste)>();
            form.RecallRequested += (entryId, paste) => recalls.Add((entryId, paste));
            search.Search("", debounce: false);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            var results = WinFormsTest.Descendants(form).OfType<ListBox>().Single();

            RaiseDoubleClick(results, new Point(5, results.ClientSize.Height - 2));
            Assert.Empty(recalls);

            var row = results.GetItemRectangle(0);
            RaiseDoubleClick(results, new Point(row.Left + 5, row.Top + 5));
            Assert.Equal((id, true), Assert.Single(recalls));
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

    private static void RaiseDoubleClick(ListBox control, Point location)
    {
        // Raise a managed event on our hidden control, not desktop mouse input.
        typeof(Control).GetMethod("OnMouseDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(control, [new MouseEventArgs(MouseButtons.Left, 2, location.X, location.Y, 0)]);
    }
}
