using System.Text.Json;
using System.Text.Json.Serialization;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Persistence;
using IntakeGate.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace IntakeGate.Infrastructure.Persistence;

public sealed class SqliteSingletonProfileRepository : ISingletonProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string connectionString;
    private readonly IDeploymentConfigurationValidator validator;

    public SqliteSingletonProfileRepository(
        string databasePath,
        IDeploymentConfigurationValidator? validator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        this.validator = validator ?? new DeploymentConfigurationValidator();
    }

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM singleton_profile_configuration WHERE singleton_id = 1);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<DeploymentConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, profile_json, policy_json
            FROM singleton_profile_configuration
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var persistedProfileId = reader.GetString(0);
        var profileJson = reader.GetString(1);
        var policyJson = reader.GetString(2);
        try
        {
            var profile = JsonSerializer.Deserialize<DeploymentProfileInput>(profileJson, JsonOptions)
                ?? throw new JsonException("The persisted profile document is empty.");
            var policy = JsonSerializer.Deserialize<IntakePolicyInput>(policyJson, JsonOptions)
                ?? throw new JsonException("The persisted policy document is empty.");
            var configuration = validator.Validate(profile, policy, "persisted SQLite profile", "persisted SQLite policy");
            if (!string.Equals(persistedProfileId, configuration.Profile.Identity.Id, StringComparison.Ordinal))
            {
                throw new ConfigurationDocumentException(
                    "The persisted SQLite profile identity does not match its singleton row identity.");
            }

            return configuration;
        }
        catch (ConfigurationValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ConfigurationDocumentException(
                "The persisted SQLite deployment configuration is malformed or incompatible.", exception);
        }
    }

    public async Task CreateAsync(
        DeploymentConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validated = validator.Validate(
            DeploymentConfigurationDocuments.Profile(configuration.Profile),
            DeploymentConfigurationDocuments.Policy(configuration.Policy),
            "profile selected for SQLite persistence",
            "policy selected for SQLite persistence");
        var profileJson = JsonSerializer.Serialize(DeploymentConfigurationDocuments.Profile(validated.Profile), JsonOptions);
        var policyJson = JsonSerializer.Serialize(DeploymentConfigurationDocuments.Policy(validated.Policy), JsonOptions);
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO singleton_profile_configuration
                (singleton_id, profile_id, profile_json, policy_json, created_at_utc, updated_at_utc)
            SELECT 1, $profile_id, $profile_json, $policy_json, $now, $now
            WHERE NOT EXISTS (SELECT 1 FROM singleton_profile_configuration);
            """;
        command.Parameters.AddWithValue("$profile_id", validated.Profile.Identity.Id);
        command.Parameters.AddWithValue("$profile_json", profileJson);
        command.Parameters.AddWithValue("$policy_json", policyJson);
        command.Parameters.AddWithValue("$now", now);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new SingletonProfileAlreadyExistsException();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            if (await ExistsAsync(cancellationToken)) throw new SingletonProfileAlreadyExistsException();
            throw;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await settings.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
