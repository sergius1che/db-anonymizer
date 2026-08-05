// Strategies/ShiftMaterialBuilder.cs
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Strategies;

public sealed class ShiftMaterialBuilder : IStrategyMaterialBuilder
{
    private static readonly (string Type, int RangeDays)[] Config =
    [
        ("birthdate", 3650),
    ];

    public StrategyType StrategyType => StrategyType.Shift;

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
            "DROP TABLE IF EXISTS materials.shift_config", conn);
        await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        await using var createCmd = new NpgsqlCommand(
            "CREATE TABLE materials.shift_config (type TEXT, range_days INT)", conn);
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY materials.shift_config (type, range_days) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var (type, rangeDays) in Config)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(type, cancellationToken);
                await writer.WriteAsync(rangeDays, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                string.Join("|", Config.Select(c => $"{c.Type}:{c.RangeDays}"))));
        return Convert.ToHexStringLower(bytes);
    }
}
