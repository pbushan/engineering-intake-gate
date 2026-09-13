namespace IntakeGate.Application.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
