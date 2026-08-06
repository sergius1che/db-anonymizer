// Strategies/FormatMaterialBuilder.cs
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Strategies;

public sealed class FormatMaterialBuilder : IStrategyMaterialBuilder
{
    // Несколько строк на type — original одного семантического типа не всегда
    // одной длины (напр. Visa — 13 или 16 цифр). anonymize.format ищет точное
    // совпадение по (type, digit_count); если такого варианта не сконфигурировано,
    // маска не натягивается на чужую длину — permutation идёт без форматирования,
    // но original всё равно не возвращается (см. format.sql)
    private static readonly (string Type, string Template, string FormatMask, int DigitCount)[] Templates =
    [
        ("phone",      @"\d{10}",  "+7 000 000-00-00",    10),
        ("cardnumber", @"\d{13}",  "0000 0000 00000",     13),
        ("cardnumber", @"\d{16}",  "0000 0000 0000 0000", 16),
        ("inn",        @"\d{10}",  "0000000000",          10),
        ("inn",        @"\d{12}",  "000000000000",        12),
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
            @"CREATE TABLE materials.format_templates (
                type TEXT, template TEXT, format_mask TEXT, digit_count INT)",
            conn);
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY materials.format_templates (type, template, format_mask, digit_count) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var (type, template, formatMask, digitCount) in Templates)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(type, cancellationToken);
                await writer.WriteAsync(template, cancellationToken);
                await writer.WriteAsync(formatMask, cancellationToken);
                await writer.WriteAsync(digitCount, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                string.Join("|", Templates.Select(t => $"{t.Type}:{t.Template}:{t.FormatMask}:{t.DigitCount}"))));
        return Convert.ToHexStringLower(bytes);
    }
}
