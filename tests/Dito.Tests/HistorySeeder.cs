namespace Dito.Tests;

internal static class HistorySeeder
{
    public static void Seed(TestDatabase database)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO clips (text, first_copied_utc_ms, last_copied_utc_ms, copy_order)
            VALUES ($text, $time, $time, $order);
            """;
        var text = command.Parameters.Add("$text", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.AddWithValue("$time", 1_790_000_000_000L);
        var order = command.Parameters.Add("$order", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Prepare();

        for (var index = 0; index < 100_000; index++)
        {
            text.Value = CreateText(index);
            order.Value = index + 1;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string CreateText(int index)
    {
        var header = $"project{index % 100} item {index}: ";
        return header + ((index % 5) switch
        {
            0 => $"server connection configuration\r\nPort = {1000 + index % 5000};\r\n" +
                 string.Concat(Enumerable.Repeat($"setting{index % 31}=enabled; ", index % 40 + 1)),
            1 => $"SELECT customer_id, total FROM invoices WHERE id = {index};\n" +
                 string.Concat(Enumerable.Repeat("ORDER BY created_at DESC;\n", index % 25 + 1)),
            2 => $"# Notes for task {index}\n- Review implementation\n- Check cache invalidation\n" +
                 string.Concat(Enumerable.Repeat("A representative paragraph with words and whitespace. ", index % 30 + 1)),
            3 => $"caf\u00e9 Za\u017c\u00f3\u0142\u0107 \ud83d\udccb\nException: operation {index} failed\n" +
                 string.Concat(Enumerable.Repeat("at Example.Worker.Process(input)\r\n", index % 20 + 1)),
            _ => $"{{ \"id\": {index}, \"state\": \"pending\", \"path\": \"C:\\\\work\\\\project\" }}\n" +
                 string.Concat(Enumerable.Repeat("alpha beta gamma delta ", index % 50 + 1))
        });
    }
}
