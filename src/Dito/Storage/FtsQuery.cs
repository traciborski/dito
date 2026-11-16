namespace Dito.Storage;

internal static class FtsQuery
{
    public static string Build(string input)
    {
        var terms = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return string.Join(" AND ", terms.Select((term, index) =>
        {
            var literal = "\"" + term.Replace("\"", "\"\"") + "\"";
            return index == terms.Length - 1 ? literal + "*" : literal;
        }));
    }
}
