namespace Eden.World.Storage;

internal static class SqlTableReferences
{
    internal static bool Contains(string sql, string table)
    {
        int start = 0;
        while (sql.IndexOf(table, start, StringComparison.OrdinalIgnoreCase) is int index && index >= 0)
        {
            int end = index + table.Length;
            if ((index == 0 || !Identifier(sql[index - 1])) && (end == sql.Length || !Identifier(sql[end])))
            {
                return true;
            }
            start = end;
        }
        return false;
    }

    private static bool Identifier(char value) => char.IsLetterOrDigit(value) || value == '_';
}
