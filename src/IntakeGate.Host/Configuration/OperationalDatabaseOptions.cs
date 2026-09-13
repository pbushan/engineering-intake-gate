namespace IntakeGate.Host.Configuration;

public sealed class OperationalDatabaseOptions
{
    public const string SectionName = "OperationalDatabase";

    public string Path { get; init; } = "data/intake-gate.db";
}
