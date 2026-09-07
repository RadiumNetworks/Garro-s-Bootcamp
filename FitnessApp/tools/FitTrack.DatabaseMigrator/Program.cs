using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

if (args.Length is < 4 or > 5)
{
    Console.Error.WriteLine("Usage: FitTrack.DatabaseMigrator <server> <database> <access-token> <schema-file> [app-identity-name:client-id]");
    return 2;
}

var connectionString = new SqlConnectionStringBuilder
{
    DataSource = args[0],
    InitialCatalog = args[1],
    Encrypt = true,
    TrustServerCertificate = false,
    ConnectTimeout = 30
}.ConnectionString;

await using var connection = new SqlConnection(connectionString) { AccessToken = args[2] };
await connection.OpenAsync();

var script = await File.ReadAllTextAsync(args[3]);
var batches = Regex.Split(script, @"^\s*GO\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
foreach (var batch in batches.Where(value => !string.IsNullOrWhiteSpace(value)))
{
    await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
    await command.ExecuteNonQueryAsync();
}

if (args.Length == 5)
{
    var separator = args[4].LastIndexOf(':');
    if (separator <= 0 || !Guid.TryParse(args[4][(separator + 1)..], out var clientId))
        throw new ArgumentException("The app identity must use the format name:client-id.");

    var identityName = args[4][..separator].Replace("]", "]]", StringComparison.Ordinal);
    var identityNameLiteral = args[4][..separator].Replace("'", "''", StringComparison.Ordinal);
    var sid = Convert.ToHexString(clientId.ToByteArray());
    var permissions = $"""
        IF EXISTS (
            SELECT 1
            FROM sys.database_principals
            WHERE name = N'{identityNameLiteral}' AND sid <> 0x{sid}
        )
            DROP USER [{identityName}];
        IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{identityName}')
            CREATE USER [{identityName}] WITH SID = 0x{sid}, TYPE = E;
        IF IS_ROLEMEMBER(N'db_datareader', N'{identityNameLiteral}') <> 1
            ALTER ROLE db_datareader ADD MEMBER [{identityName}];
        IF IS_ROLEMEMBER(N'db_datawriter', N'{identityNameLiteral}') <> 1
            ALTER ROLE db_datawriter ADD MEMBER [{identityName}];
        """;
    await using var command = new SqlCommand(permissions, connection);
    await command.ExecuteNonQueryAsync();
}

Console.WriteLine("FitTrack database schema and managed-identity permissions are ready.");
return 0;
