// Models/JobState.cs
namespace DbAnonymizer.Core.Models;

public enum JobStatus
{
    Created = 0,
    Ingesting,
    AwaitingReview,   // детекторы отработали, ждём человека
    Frozen,            // план заморожен
    Enriching,         // воркер вызывает агента
    Applying,          // CTAS + подмена
    Verifying,         // проверки целостности
    Complete,
    Failed
}

public sealed class JobState
{
    public required string Id { get; init; }
    public JobStatus Status { get; init; }
    public string PlanId { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public SourceSpec? Source { get; init; }
    // Явные входы задачи (спека): seed и ширина суффикса. Null — не указаны
    // пользователем, Introspect подставит собственные значения (случайный seed,
    // suffix width по умолчанию) и запишет фактически использованные в план/отчёт.
    public string? Seed { get; init; }
    public int? SuffixWidth { get; init; }
}
