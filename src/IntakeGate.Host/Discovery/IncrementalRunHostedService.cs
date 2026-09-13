using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Discovery;
using IntakeGate.Application.Time;

namespace IntakeGate.Host.Discovery;

/// <summary>Single-process scheduler. It intentionally performs no missed-run catch-up after restart.</summary>
public sealed class IncrementalRunHostedService(
    DeploymentConfigurationState configurationState,
    IRuntimeExecutionFactory runtimeFactory,
    IIncrementalScheduleCalculator scheduleCalculator,
    IClock clock,
    ILogger<IncrementalRunHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var generation = configurationState.ActiveGeneration;
            if (generation is null)
            {
                logger.LogInformation(
                    "Incremental scheduler is idle because no runtime generation is active. Event={EventName}",
                    "IncrementalSchedulerNoActiveGeneration");
                await configurationState.WaitForChangeAsync(null, stoppingToken);
                continue;
            }

            var schedule = generation.Configuration.Profile.Schedule;
            if (!schedule.Enabled)
            {
                logger.LogInformation(
                    "Incremental scheduler is alive in Manual Only mode. Event={EventName} GenerationId={GenerationId}",
                    "IncrementalSchedulerManualOnly", generation.GenerationId);
                await configurationState.WaitForChangeAsync(generation.GenerationId, stoppingToken);
                continue;
            }

            DateTimeOffset next;
            try { next = scheduleCalculator.GetNextOccurrenceUtc(schedule, clock.UtcNow); }
            catch (Exception exception)
            {
                logger.LogError(exception, "Incremental scheduler generation is invalid. Event={EventName} GenerationId={GenerationId}",
                    "IncrementalSchedulerConfigurationFailed", generation.GenerationId);
                await configurationState.WaitForChangeAsync(generation.GenerationId, stoppingToken);
                continue;
            }
            var delay = next - clock.UtcNow.ToUniversalTime();
            if (delay > TimeSpan.Zero)
            {
                var changed = configurationState.WaitForChangeAsync(generation.GenerationId, stoppingToken);
                var elapsed = Task.Delay(delay, stoppingToken);
                await Task.WhenAny(changed, elapsed);
                if (changed.IsCompleted) continue;
            }
            if (stoppingToken.IsCancellationRequested) break;
            var capture = await runtimeFactory.CaptureAsync(stoppingToken);
            if (!capture.Succeeded)
            {
                logger.LogWarning(
                    "Scheduled execution could not capture an active generation. Event={EventName} Failure={Failure}",
                    "IncrementalSchedulerCaptureFailed", capture.Failure);
                await configurationState.WaitForChangeAsync(generation.GenerationId, stoppingToken);
                continue;
            }
            var execution = capture.Services!;
            var result = await execution.IncrementalRuns.ExecuteAsync(
                execution.Generation.Configuration, RunTriggerType.Scheduled, stoppingToken);
            logger.LogInformation("Scheduled incremental run completed. Event={EventName} RunId={RunId} Accepted={Accepted} Status={Status} ErrorCategory={ErrorCategory}",
                "IncrementalRunCompleted", result.RunId, result.Accepted, result.Status, result.ErrorCategory);
        }
    }
}
