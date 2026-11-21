using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Dito.Linux;

internal sealed class LinuxClipboard(IClipboard clipboard)
{
    private static readonly DataFormat<string> RecallMarker =
        DataFormat.CreateStringPlatformFormat("application/x-dito-recall");

    public async Task<string?> ReadExternalTextAsync()
    {
        using var data = await clipboard.TryGetDataAsync();
        if (data is null || data.Formats.Contains(RecallMarker))
        {
            return null;
        }

        return await data.TryGetTextAsync();
    }

    public async Task<bool> CopyAsync(string text)
    {
        var item = DataTransferItem.CreateText(text);
        item.Set(RecallMarker, "1");
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);
        return ReferenceEquals(data, await clipboard.TryGetInProcessDataAsync());
    }
}
