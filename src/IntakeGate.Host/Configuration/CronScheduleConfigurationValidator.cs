using Cronos;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Setup;

namespace IntakeGate.Host.Configuration;

public sealed class CronScheduleConfigurationValidator : IScheduleConfigurationValidator
{
    public bool IsValid(ScheduleConfiguration schedule)
    {
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
            if (!schedule.Enabled) return true;
            var cron = CronExpression.Parse(schedule.Expression, CronFormat.IncludeSeconds);
            return cron.GetNextOccurrence(DateTimeOffset.UtcNow, timezone, inclusive: false) is not null;
        }
        catch (Exception exception) when (exception is CronFormatException or
                                          TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
