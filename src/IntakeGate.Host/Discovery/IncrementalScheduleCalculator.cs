using Cronos;
using IntakeGate.Application.Configuration;

namespace IntakeGate.Host.Discovery;

public interface IIncrementalScheduleCalculator
{
    DateTimeOffset GetNextOccurrenceUtc(ScheduleConfiguration schedule, DateTimeOffset nowUtc);
}

/// <summary>Uses Cronos/TimeZoneInfo rather than offset arithmetic, preserving local cron semantics across DST.</summary>
public sealed class IncrementalScheduleCalculator : IIncrementalScheduleCalculator
{
    public DateTimeOffset GetNextOccurrenceUtc(ScheduleConfiguration schedule, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
        var expression = CronExpression.Parse(schedule.Expression, CronFormat.IncludeSeconds);
        var next = expression.GetNextOccurrence(nowUtc.ToUniversalTime(), zone, inclusive: false);
        return next?.ToUniversalTime() ?? throw new InvalidOperationException("The configured schedule has no future occurrence.");
    }
}
