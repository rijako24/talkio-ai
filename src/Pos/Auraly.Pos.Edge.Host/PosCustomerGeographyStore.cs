using Auraly.Contracts.Parties;
using Microsoft.Data.Sqlite;

namespace Auraly.Pos.Edge.Host;

public sealed class PosCustomerGeographyStore(string connectionString)
{
    public async Task ReplaceAsync(
        IReadOnlyCollection<GeographyHierarchyItem> hierarchy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCreatedAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM PosCustomerGeography;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in hierarchy)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PosCustomerGeography(Id,ParentId,Level,Code,Name,IsActive)
                VALUES($id,$parent,$level,$code,$name,$active);
                """;
            insert.Parameters.AddWithValue("$id", item.Id.ToString("D"));
            insert.Parameters.AddWithValue(
                "$parent", item.ParentId is { } parent ? parent.ToString("D") : DBNull.Value);
            insert.Parameters.AddWithValue("$level", item.Level);
            insert.Parameters.AddWithValue("$code", item.Code);
            insert.Parameters.AddWithValue("$name", item.Name);
            insert.Parameters.AddWithValue("$active", item.IsActive ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<CountryItem>> CountriesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync("Country", null, cancellationToken);
        return rows.Select(value => new CountryItem(
            value.Id, value.Code, value.Name, value.IsActive)).ToArray();
    }

    public async Task<IReadOnlyCollection<AdministrativeDivisionItem>> DivisionsAsync(
        Guid countryId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync("Division", countryId, cancellationToken);
        return rows.Select(value => new AdministrativeDivisionItem(
            value.Id, countryId, value.Code, value.Name, "Department", value.IsActive)).ToArray();
    }

    public async Task<IReadOnlyCollection<CityItem>> CitiesAsync(
        Guid divisionId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync("City", divisionId, cancellationToken);
        return rows.Select(value => new CityItem(
            value.Id, divisionId, value.Code, value.Name, value.IsActive)).ToArray();
    }

    private async Task<IReadOnlyCollection<Row>> ReadAsync(
        string level,
        Guid? parentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCreatedAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,Code,Name,IsActive FROM PosCustomerGeography
            WHERE Level=$level AND ($parent IS NULL OR ParentId=$parent)
              AND IsActive=1 ORDER BY Name,Id;
            """;
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue(
            "$parent", parentId is { } parent ? parent.ToString("D") : DBNull.Value);
        var result = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3) == 1));
        return result;
    }

    private static async Task EnsureCreatedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PosCustomerGeography(
              Id TEXT NOT NULL PRIMARY KEY,
              ParentId TEXT NULL,
              Level TEXT NOT NULL,
              Code TEXT NOT NULL,
              Name TEXT NOT NULL,
              IsActive INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PosCustomerGeography_Level_Parent
              ON PosCustomerGeography(Level,ParentId,Name);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record Row(Guid Id, string Code, string Name, bool IsActive);
}
