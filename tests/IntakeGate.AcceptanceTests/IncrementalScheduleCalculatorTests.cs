using IntakeGate.Application.Configuration;
using IntakeGate.Host.Discovery;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class IncrementalScheduleCalculatorTests
{
    [Theory]
    [InlineData("2026-01-15T00:00:00Z", "2026-01-15T04:00:00Z")]
    [InlineData("2026-07-15T00:00:00Z", "2026-07-15T03:00:00Z")]
    public void NFR_004_E18_DST_ConfiguredTorontoScheduleUsesLocalElevenPmAcrossStandardAndDaylightDates(string now, string expected)
    {
        var calculator = new IncrementalScheduleCalculator();
        var schedule = new ScheduleConfiguration(true, "0 0 23 * * *", "America/Toronto", TimeSpan.FromHours(24));

        var next = calculator.GetNextOccurrenceUtc(schedule, DateTimeOffset.Parse(now));

        Assert.Equal(DateTimeOffset.Parse(expected), next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
    }
}
