using System.Reflection;

namespace OpenScanner.Server;

public static class SqlLoader
{
    private static readonly Assembly Assembly = typeof(SqlLoader).Assembly;
    private const string Namespace = "OpenScanner.Server.Sql";

    public static string GetSql(string path)
    {
        // Convert path like "Channels/GetAll.sql" to "OpenScanner.Server.Sql.Channels.GetAll.sql"
        var resourceName = $"{Namespace}.{path.Replace("/", ".")}";
        
        using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"SQL resource not found: {resourceName}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
