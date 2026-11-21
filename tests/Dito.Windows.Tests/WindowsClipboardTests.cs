using System.Text;
using Dito.Windows;

namespace Dito.Tests;

public sealed class WindowsClipboardTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HtmlPayloadKeepsUtf8BytesButNotClipboardPadding(bool nullTerminated)
    {
        const string header = "Version:1.0\r\nEndHTML:";
        const string body = "<b>caf\u00e9</b>";
        var length = Encoding.UTF8.GetByteCount(header + "000000\r\n" + body);
        var padding = nullTerminated ? "\0unused" : "unused";
        var bytes = Encoding.UTF8.GetBytes($"{header}{length:D6}\r\n{body}{padding}");

        Assert.Equal(length, WindowsClipboard.GetHtmlLength(bytes));
        Assert.Equal(body, Encoding.UTF8.GetString(bytes.AsSpan(0, length))[^body.Length..]);
    }
}
