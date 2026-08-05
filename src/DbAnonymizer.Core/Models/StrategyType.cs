// Models/StrategyType.cs
namespace DbAnonymizer.Core.Models;

public enum StrategyType
{
    Keep = 0,
    Fake,
    Derive,
    Format,
    Secret,
    Shift,
    Redact
}
