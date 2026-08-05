// Strategies/FormatMaterialBuilder.cs
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Strategies;

public sealed class FormatMaterialBuilder : IStrategyMaterialBuilder
{
    private static readonly (string Type, string Template)[] Templates =
    [
        ("phone", @"\d{10}"),
        ("cardnumber", @"\d{16}"),
        ("inn", @"\d{10}|\d{12}"),
    ];

    public StrategyType StrategyType => StrategyType.Format;

    public async Task<string> BuildAndLoadAsync(
        string sandboxConnectionString,
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var createSchema = new NpgsqlCommand(
            "CREATE SCHEMA IF NOT EXISTS materials", conn);
        await createSchema.ExecuteNonQueryAsync(cancellationToken);

        await using var dropCmd = new NpgsqlCommand(
            "DROP TABLE IF EXISTS materials.format_templates", conn);
        await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        await using var createCmd = new NpgsqlCommand(
            "CREATE TABLE materials.format_templates (type TEXT, template TEXT)", conn);
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY materials.format_templates (type, template) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var (type, template) in Templates)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(type, cancellationToken);
                await writer.WriteAsync(template, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                string.Join("|", Templates.Select(t => $"{t.Type}:{t.Template}"))));
        return Convert.ToHexStringLower(bytes);
    }
}
