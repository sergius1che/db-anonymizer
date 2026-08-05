// Strategies/RedactMaterialBuilder.cs
using System.Text.RegularExpressions;
using DbAnonymizer.Core.Models;
using Npgsql;

namespace DbAnonymizer.Core.Strategies;

public sealed class RedactMaterialBuilder : IStrategyMaterialBuilder
{
    private readonly string _storyPath;

    public StrategyType StrategyType => StrategyType.Redact;

    public RedactMaterialBuilder(string storyPath)
    {
        _storyPath = storyPath;
    }

    public async Task<string> BuildAndLoadAsync(
        string sandboxConnectionString,
        Plan plan,
        CancellationToken cancellationToken = default)
    {
        var story = await File.ReadAllTextAsync(_storyPath, cancellationToken);
        var words = SplitWords(story);

        await using var conn = new NpgsqlConnection(sandboxConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var createSchema = new NpgsqlCommand(
            "CREATE SCHEMA IF NOT EXISTS materials", conn);
        await createSchema.ExecuteNonQueryAsync(cancellationToken);

        await using var dropStory = new NpgsqlCommand(
            "DROP TABLE IF EXISTS materials.story", conn);
        await dropStory.ExecuteNonQueryAsync(cancellationToken);
        await using var createStory = new NpgsqlCommand(
            "CREATE TABLE materials.story (content TEXT)", conn);
        await createStory.ExecuteNonQueryAsync(cancellationToken);

        // The importer must be disposed (not only Completed) before the connection
        // can be reused for other commands — CompleteAsync alone leaves it in Copy state.
        await using (var storyWriter = await conn.BeginBinaryImportAsync(
            "COPY materials.story (content) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            await storyWriter.StartRowAsync(cancellationToken);
            await storyWriter.WriteAsync(story, cancellationToken);
            await storyWriter.CompleteAsync(cancellationToken);
        }

        await using var dropWords = new NpgsqlCommand(
            "DROP TABLE IF EXISTS materials.story_words", conn);
        await dropWords.ExecuteNonQueryAsync(cancellationToken);
        await using var createWords = new NpgsqlCommand(
            "CREATE TABLE materials.story_words (len INT, word TEXT, idx BIGINT)", conn);
        await createWords.ExecuteNonQueryAsync(cancellationToken);

        await using var wordsWriter = await conn.BeginBinaryImportAsync(
            "COPY materials.story_words (len, word, idx) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        foreach (var (word, index) in words)
        {
            await wordsWriter.StartRowAsync(cancellationToken);
            await wordsWriter.WriteAsync(word.Length, cancellationToken);
            await wordsWriter.WriteAsync(word, cancellationToken);
            await wordsWriter.WriteAsync((long)index, cancellationToken);
        }
        await wordsWriter.CompleteAsync(cancellationToken);

        return ComputeHash(story);
    }

    private static List<(string Word, int Index)> SplitWords(string story)
    {
        var result = new List<(string Word, int Index)>();
        var matches = Regex.Matches(story, @"\p{L}+");
        for (int i = 0; i < matches.Count; i++)
        {
            result.Add((matches[i].Value, i));
        }
        return result;
    }

    private static string ComputeHash(string story)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(story));
        return Convert.ToHexStringLower(bytes);
    }
}
