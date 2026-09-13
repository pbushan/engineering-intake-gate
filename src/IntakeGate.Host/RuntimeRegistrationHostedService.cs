using IntakeGate.Application;
using IntakeGate.Application.Configuration;
using IntakeGate.Infrastructure.Persistence;

namespace IntakeGate.Host;

public sealed class RuntimeRegistrationHostedService(
    RuntimeRegistrationService registrationService,
    ILogger<RuntimeRegistrationHostedService> logger,
    IHostEnvironment environment,
    DeploymentConfigurationState configurationState,
    SqliteDatabaseMigrator databaseMigrator) : IHostedService
{
    private readonly Guid _instanceId = Guid.NewGuid();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await databaseMigrator.MigrateAsync(cancellationToken);
        var version = ProductMetadata.Version;
        var record = await registrationService.RegisterAsync(_instanceId, version, cancellationToken);

        if (configurationState.Configuration is { } deploymentConfiguration)
        {
            logger.LogInformation(
                "Persistence initialized and application runtime registered. Event={EventName} Environment={EnvironmentName} SetupStatus={SetupStatus} ProfileId={ProfileId} PolicyId={PolicyId} PolicyVersion={PolicyVersion} PolicyFingerprint={PolicyFingerprint} AiProvider={AiProvider} ExecutionMode={ExecutionMode} InstanceId={InstanceId} RuntimeRecordId={RuntimeRecordId}",
                "ApplicationStarted",
                environment.EnvironmentName,
                "Configured",
                deploymentConfiguration.Profile.Identity.Id,
                deploymentConfiguration.Policy.Identity.Id,
                deploymentConfiguration.Policy.Identity.Version,
                deploymentConfiguration.PolicyFingerprint,
                deploymentConfiguration.Profile.Ai.Provider,
                deploymentConfiguration.Profile.Processing.ExecutionMode,
                _instanceId,
                record.Id);
            return;
        }

        logger.LogInformation(
            "Persistence initialized and application runtime registered without a deployment profile. Event={EventName} Environment={EnvironmentName} SetupStatus={SetupStatus} InstanceId={InstanceId} RuntimeRecordId={RuntimeRecordId}",
            "ApplicationStarted",
            environment.EnvironmentName,
            "Incomplete",
            _instanceId,
            record.Id);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Application is shutting down. Event={EventName} InstanceId={InstanceId}",
            "ApplicationStopping",
            _instanceId);
        return Task.CompletedTask;
    }
}
