using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Evidence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntakeGate.AcceptanceTests;

public sealed class EvidenceDiagnosticEndpointTests
{
    [Fact]
    public async Task CNT_004_CNT_007_SEC_001_AttachmentContentIsRedactedDisclosedAndAbsentFromLogs()
    {
        const string secret = "SYNTH_ATTACHMENT_ENDPOINT_SECRET_901";
        var raw = new RawWorkItem(
            "attachment-fixture", "1", "Generic", "Attachment evidence",
            attachments: [new RawAttachmentMetadata(
                "attachment-text", "evidence.log", "text/plain", null,
                System.Text.Encoding.UTF8.GetBytes($"operation failed\npassword={secret}"))]);
        var logs = new CapturingLoggerProvider();

        await using var fixture = await HostFixture.CreateAsync("Development", logs);
        using var response = await fixture.Client.PostAsJsonAsync("/api/diagnostics/evidence/prepare", raw);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("operation failed", body, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, body, StringComparison.Ordinal);
        Assert.Contains("\"processingStatus\":\"processed\"", body, StringComparison.Ordinal);
        Assert.Contains("\"inspectionMode\":\"text\"", body, StringComparison.Ordinal);
        Assert.Contains("ATTACHMENT_PROCESSING", string.Join('\n', logs.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join('\n', logs.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SEC_001_CNT_003_CNT_007_DiagnosticPipelineReturnsOnlySanitizedDisclosedEvidence()
    {
        var fieldSecret = "SYNTH_ENDPOINT_PASSWORD_301";
        var commentSecret = "sk-test_SYNTHETICENDPOINTKEY301";
        var marker = ValidatorCommentMarker.Create(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        var raw = new RawWorkItem(
            "fixture-301",
            "12",
            "Generic Request",
            "Fixture title",
            [new RawWorkItemField("Custom.Arbitrary", null, JsonSerializer.SerializeToElement($"password={fieldSecret}"))],
            "<h1>Human description</h1><p>" + new string('x', 75_100) + "</p>",
            WorkItemContentFormat.Html,
            ["generic"],
            [new RawWorkItemRelation("https://example.invalid/related/1", "related", "Related context")],
            [
                new RawWorkItemComment("human", "Person", DateTimeOffset.Parse("2026-01-01T00:00:00Z"), $"Human evidence api_key={commentSecret}"),
                new RawWorkItemComment("validator", "Different Author", DateTimeOffset.Parse("2026-01-01T00:01:00Z"), $"Generated comment {marker}")
            ],
            [new RawAttachmentMetadata("attachment-1", "synthetic.txt", "text/plain", 123)]);
        var logs = new CapturingLoggerProvider();

        await using var fixture = await HostFixture.CreateAsync("Development", logs);
        using var response = await fixture.Client.PostAsJsonAsync("/api/diagnostics/evidence/prepare", raw);
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(responseJson).RootElement;

        Assert.Contains("Human description", responseJson, StringComparison.Ordinal);
        Assert.Contains("Human evidence", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated comment", responseJson, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, responseJson, StringComparison.Ordinal);
        Assert.True(json.GetProperty("processing").GetProperty("truncationOccurred").GetBoolean());
        Assert.False(json.GetProperty("processing").GetProperty("attachmentContentInspected").GetBoolean());
        Assert.Equal(1, json.GetProperty("processing").GetProperty("excludedValidatorCommentCount").GetInt32());
        AssertSecretAbsent(responseJson, fieldSecret, commentSecret);

        var capturedLogs = string.Join('\n', logs.Messages);
        Assert.Contains("CONTENT_COLLECTION", capturedLogs, StringComparison.Ordinal);
        Assert.Contains("SECRET_REDACTION", capturedLogs, StringComparison.Ordinal);
        Assert.Contains("ATTACHMENT_PROCESSING", capturedLogs, StringComparison.Ordinal);
        AssertSecretAbsent(capturedLogs, fieldSecret, commentSecret);
    }

    [Fact]
    public async Task NFR_006_DiagnosticEvidenceEndpointIsUnavailableInProduction()
    {
        await using var fixture = await HostFixture.CreateAsync("Production", new CapturingLoggerProvider());

        using var response = await fixture.Client.PostAsJsonAsync(
            "/api/diagnostics/evidence/prepare",
            new RawWorkItem("1", "1", "Generic", "Title"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AI_003_AI_007_E3_E20_DiagnosticPipelineUsesSanitizedEvidenceAndRejectsUnsafeResponses()
    {
        var raw = new RawWorkItem(
            "evaluation-fixture",
            "1",
            "Generic",
            "Safe title",
            description: "password=SYNTH_EVALUATION_PASSWORD_501");
        await using var fixture = await HostFixture.CreateAsync("Development", new CapturingLoggerProvider());

        using var pass = await fixture.Client.PostAsJsonAsync("/api/diagnostics/evaluations/pass", raw);
        using var e3 = await fixture.Client.PostAsJsonAsync("/api/diagnostics/evaluations/e3", raw);
        using var e20 = await fixture.Client.PostAsJsonAsync("/api/diagnostics/evaluations/e20", raw);

        pass.EnsureSuccessStatusCode();
        var passBody = await pass.Content.ReadAsStringAsync();
        var e3Body = await e3.Content.ReadAsStringAsync();
        var e20Body = await e20.Content.ReadAsStringAsync();
        Assert.Contains("\"processingStatus\":\"completed\"", passBody, StringComparison.Ordinal);
        Assert.Contains("\"decision\":\"pass\"", passBody, StringComparison.Ordinal);
        Assert.Contains("\"processingStatus\":\"error\"", e3Body, StringComparison.Ordinal);
        Assert.Contains("\"processingStatus\":\"error\"", e20Body, StringComparison.Ordinal);
        Assert.Contains("\"result\":null", e3Body, StringComparison.Ordinal);
        Assert.Contains("\"result\":null", e20Body, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH_EVALUATION_PASSWORD_501", string.Concat(passBody, e3Body, e20Body), StringComparison.Ordinal);
    }

    private static void AssertSecretAbsent(string output, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.False(output.Contains(secret, StringComparison.Ordinal), "Sanitized output or log contained a synthetic secret.");
        }
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly string overridePath;
        private readonly string databasePath;
        private readonly string? previousOverride;
        private readonly WebApplicationFactory<Program> factory;

        private HostFixture(
            string overridePath,
            string databasePath,
            string? previousOverride,
            WebApplicationFactory<Program> factory,
            HttpClient client)
        {
            this.overridePath = overridePath;
            this.databasePath = databasePath;
            this.previousOverride = previousOverride;
            this.factory = factory;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<HostFixture> CreateAsync(string environment, ILoggerProvider loggerProvider)
        {
            var root = FindRepositoryRoot();
            var databasePath = Path.Combine(Path.GetTempPath(), $"intake-gate-evidence-{Guid.NewGuid():N}.db");
            var overridePath = Path.Combine(Path.GetTempPath(), $"intake-gate-evidence-{Guid.NewGuid():N}.json");
            await LegacyProfileTestSeeder.ImportAsync(databasePath,
                Path.Combine(root, "profiles", "example", "profile.yaml"));
            var deploymentOverride = JsonSerializer.Serialize(new
            {
                OperationalDatabase = new { Path = databasePath }
            });
            await File.WriteAllTextAsync(overridePath, deploymentOverride);
            var previousOverride = Environment.GetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH");
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", overridePath);

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            });
            var fixture = new HostFixture(overridePath, databasePath, previousOverride, factory, factory.CreateClient());
            await AuthenticatedTestClient.AuthenticateAdminAsync(fixture.Client);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
            Environment.SetEnvironmentVariable("INTAKE_GATE_CONFIG_PATH", previousOverride);
            File.Delete(overridePath);
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EngineeringIntakeGate.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
