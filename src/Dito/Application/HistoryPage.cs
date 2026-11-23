namespace Dito.Application;

internal sealed record HistoryListItem(
    long Id,
    long CopyOrder,
    DateTimeOffset LastCopiedAt,
    string Preview,
    string? Label);

internal sealed record HistoryPage(
    IReadOnlyList<HistoryListItem> Items,
    long? NextCursor);
