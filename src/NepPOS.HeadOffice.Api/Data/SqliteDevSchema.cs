using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace NepPOS.HeadOffice.Api.Data;

/// <summary>
/// Developer PCs only (SQLite). EnsureCreated makes the database once and never changes it, so a
/// table added in a later version (e.g. CatalogItems) would be missing from an existing headoffice.db.
/// This adds any missing tables and indexes, keeping the data that's there. SQL Server uses migrations.
/// </summary>
public static partial class SqliteDevSchema
{
    public static void AddMissingTables(HeadOfficeDbContext db, ILogger logger)
    {
        var script = db.Database.GenerateCreateScript();
        script = CreateTable().Replace(script, "CREATE TABLE IF NOT EXISTS ");
        script = CreateIndex().Replace(script, "CREATE ${unique}INDEX IF NOT EXISTS ");

        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // A changed column can't be added this way: delete headoffice.db and run again.
            logger.LogWarning(ex, "Couldn't bring headoffice.db up to date. Delete it to start fresh.");
        }
    }

    [GeneratedRegex(@"\bCREATE TABLE (?!IF NOT EXISTS)")]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"\bCREATE (?<unique>UNIQUE )?INDEX (?!IF NOT EXISTS)")]
    private static partial Regex CreateIndex();
}
