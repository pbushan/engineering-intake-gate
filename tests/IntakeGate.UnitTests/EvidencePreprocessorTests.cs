using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class EvidencePreprocessorTests
{
    [Fact]
    public void CNT_001_SparseGenericWorkItemProducesExplicitEmptyEvidence()
    {
        var workItem = new RawWorkItem("sparse-1", "3", "Generic", "Sparse title");

        var evidence = CreatePreprocessor().Prepare(workItem, Limits());

        Assert.Empty(evidence.Description);
        Assert.Empty(evidence.Fields);
        Assert.Empty(evidence.Comments);
        Assert.Empty(evidence.Attachments);
        Assert.False(evidence.Processing.AttachmentMetadataAvailable);
        Assert.False(evidence.Processing.AttachmentContentInspected);
        Assert.False(evidence.Processing.TruncationOccurred);
        Assert.False(evidence.Redaction.RedactionOccurred);
    }

    [Fact]
    public void CNT_001_E19_RawGenericWorkItemAndArbitraryFieldsBecomeEvidence()
    {
        var workItem = CreateWorkItem(
            fields:
            [
                Field("Custom.Structured", new { beta = true, alpha = 42 }),
                Field("Any.Team.Field", "useful context")
            ]);

        var evidence = CreatePreprocessor().Prepare(workItem, Limits());

        Assert.Equal("work-42", evidence.WorkItemId);
        Assert.Equal("revision-exact-7", evidence.Revision);
        Assert.Equal(["Any.Team.Field", "Custom.Structured"], evidence.Fields.Select(field => field.ReferenceName));
        Assert.Equal("{\"alpha\":42,\"beta\":true}", evidence.Fields[1].Value);
    }

    [Fact]
    public void CNT_002_CNT_003_AC_10_OnlyValidMachineMarkedCommentsAreExcluded()
    {
        var marker = ValidatorCommentMarker.Create(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var workItem = CreateWorkItem(comments:
        [
            Comment("2", "engineering-intake-gate", "human evidence", 2),
            Comment("1", "Anyone", $"generated evidence {marker}", 1),
            Comment("3", "Anyone", "<!-- engineering-intake-gate:validatorVersion=1;evaluationId=not-a-guid --> human lookalike", 3),
            Comment("4", "Anyone", "<!-- engineering-intake-gate:validatorVersion=2;evaluationId=11111111-2222-3333-4444-555555555555 --> version lookalike", 4),
            Comment("5", "Anyone", "<!-- engineering-intake-gate:validatorVersion=1;evaluationId=00000000-0000-0000-0000-000000000000 --> empty-id lookalike", 5)
        ]);

        var evidence = CreatePreprocessor().Prepare(workItem, Limits());

        Assert.Equal(["2", "3", "4", "5"], evidence.Comments.Select(comment => comment.Id));
        Assert.Equal(1, evidence.Processing.ExcludedValidatorCommentCount);
        Assert.Contains("human evidence", evidence.Comments[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SEC_001_AC_15_E5_RecognizableSecretsAreRedactedWithoutDestroyingBenignWords()
    {
        var assignedSecret = "SYNTH_PASSWORD_VALUE";
        var bearerSecret = "SYNTHETIC.BEARER.VALUE";
        var apiKeySecret = "sk-test_abcdefghijklmnopqrstuv";
        var connectionSecret = "SYNTH_CONNECTION_VALUE";
        var privateKeyBody = "SYNTHETIC_PRIVATE_KEY_BODY_NOT_REAL";
        var basicSecret = "SYNTHETIC_BASIC_AUTH_VALUE";
        var quotedSecret = "SYNTH TOKEN VALUE WITH SPACES";
        var input = $$"""
            The password policy discusses token rotation and secret storage; no credential is present there.
            password={{assignedSecret}}
            Authorization: Bearer {{bearerSecret}}
            Authorization: Basic {{basicSecret}}
            api_key={{apiKeySecret}}
            token: '{{quotedSecret}}'
            Server=local.invalid;User=demo;Password={{connectionSecret}};Database=sample;
            -----BEGIN PRIVATE KEY-----
            {{privateKeyBody}}
            -----END PRIVATE KEY-----
            """;

        var result = new SecretRedactor().Redact(input);

        Assert.Equal(7, result.RedactionCount);
        Assert.Equal(7, CountOccurrences(result.Content, SecretRedactor.Placeholder));
        Assert.Contains("password policy", result.Content, StringComparison.Ordinal);
        Assert.Contains("token rotation", result.Content, StringComparison.Ordinal);
        Assert.Contains("secret storage", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY-----", result.Content, StringComparison.Ordinal);
        AssertSecretAbsent(result.Content, assignedSecret, bearerSecret, apiKeySecret, connectionSecret, privateKeyBody, basicSecret, quotedSecret);
    }

    [Fact]
    public void SEC_001_SEC_002_CompletePipelineLeaksNoDiscoveredSecretToEvidenceOrSafeLogs()
    {
        var fieldSecret = "SYNTH_FIELD_PASSWORD_901";
        var descriptionSecret = "SYNTHETIC.BEARER.DESCRIPTION901";
        var commentSecret = "sk-test_ABCDEFGHIJKLMNOPQRSTUV901";
        var log = new CapturingProcessingLog();
        var workItem = CreateWorkItem(
            fields: [Field("Custom.Configuration", $"password={fieldSecret}")],
            description: $"Authorization: Bearer {descriptionSecret}",
            comments: [Comment("c1", "Human", $"api_key={commentSecret}", 1)]);

        var evidence = CreatePreprocessor(log).Prepare(workItem, Limits());
        var serializedEvidence = JsonSerializer.Serialize(evidence);
        var serializedLogs = JsonSerializer.Serialize(log.Entries);

        AssertSecretAbsent(serializedEvidence, fieldSecret, descriptionSecret, commentSecret);
        AssertSecretAbsent(serializedLogs, fieldSecret, descriptionSecret, commentSecret);
        Assert.Equal(3, evidence.Redaction.RedactionCount);
        Assert.NotEmpty(log.Entries);
    }

    [Fact]
    public void NFR_006_PreprocessingDoesNotMutateRawInputOrSourceCollections()
    {
        var comments = new List<RawWorkItemComment> { Comment("1", "Human", "original", 1) };
        var fields = new List<RawWorkItemField> { Field("Z.Field", "original") };
        var workItem = CreateWorkItem(fields: fields, comments: comments);
        comments.Add(Comment("2", "Later", "not part of immutable snapshot", 2));
        fields.Clear();
        var before = JsonSerializer.Serialize(workItem);

        _ = CreatePreprocessor().Prepare(workItem, Limits());

        Assert.Equal(before, JsonSerializer.Serialize(workItem));
        Assert.Single(workItem.Comments);
        Assert.Single(workItem.Fields);
    }

    [Fact]
    public void CNT_006_CNT_007_PerContentCommentAndAttachmentLimitsAreDeterministicAndDisclosed()
    {
        var workItem = CreateWorkItem(
            description: "description-is-too-long",
            comments:
            [
                Comment("late", "Human", "later-comment-is-too-long", 3),
                Comment("first", "Human", "first-comment-is-too-long", 1),
                Comment("middle", "Human", "middle-comment-is-too-long", 2)
            ],
            attachments: Enumerable.Range(0, 25)
                .Select(index => new RawAttachmentMetadata($"{index:D2}", $"long-file-name-{index:D2}.txt", "text/plain", index))
                .Reverse()
                .ToArray());
        var limits = Limits(total: 500, comments: 2, perContent: 8, attachments: 2);

        var evidence = CreatePreprocessor().Prepare(workItem, limits);

        Assert.True(evidence.Processing.DescriptionTruncated);
        Assert.Equal(["first", "middle"], evidence.Comments.Select(comment => comment.Id));
        Assert.All(evidence.Comments, comment => Assert.True(comment.Truncated));
        Assert.Equal(1, evidence.Processing.OmittedCommentCount);
        Assert.Equal(["00", "01"], evidence.Attachments.Select(attachment => attachment.Reference));
        Assert.Equal(23, evidence.Processing.OmittedAttachmentMetadataCount);
        Assert.True(evidence.Processing.TruncationOccurred);
        Assert.True(evidence.Processing.AttachmentMetadataAvailable);
        Assert.False(evidence.Processing.AttachmentContentInspected);
    }

    [Fact]
    public void CNT_006_CNT_007_AggregateLimitUsesStablePriorityAndDisclosesEveryOmission()
    {
        var workItem = CreateWorkItem(
            title: "12345",
            description: "67890",
            fields: [Field("Field.B", "BBBB"), Field("Field.A", "AAAA")],
            tags: ["second", "first"],
            comments: [Comment("1", "Human", "comment", 1)]);

        var evidence = CreatePreprocessor().Prepare(workItem, Limits(total: 16, perContent: 100));

        Assert.True(evidence.Processing.AggregateTextLimitReached);
        Assert.True(evidence.Processing.TruncationOccurred);
        Assert.True(evidence.Processing.OmittedFieldCount > 0 || evidence.Processing.TruncatedFieldCount > 0);
        Assert.True(evidence.Processing.OmittedTagCount > 0);
        Assert.True(evidence.Processing.OmittedCommentCount > 0);
        Assert.Equal(16, evidence.Processing.IncludedTextCharacters);
    }

    [Fact]
    public void NFR_009_SemanticallyEquivalentInsertionOrdersProduceIdenticalEvidence()
    {
        var first = CreateWorkItem(
            fields: [Field("B", new { z = 2, a = 1 }), Field("A", "one")],
            tags: ["z", "a"],
            comments: [Comment("2", "B", "two", 2), Comment("1", "A", "one", 1)]);
        var second = CreateWorkItem(
            fields: [Field("A", "one"), Field("B", JsonSerializer.Deserialize<JsonElement>("{\"a\":1,\"z\":2}"))],
            tags: ["a", "z"],
            comments: [Comment("1", "A", "one", 1), Comment("2", "B", "two", 2)]);
        var preprocessor = CreatePreprocessor();

        var one = JsonSerializer.Serialize(preprocessor.Prepare(first, Limits()));
        var two = JsonSerializer.Serialize(preprocessor.Prepare(second, Limits()));

        Assert.Equal(one, two);
        Assert.Equal(one, JsonSerializer.Serialize(preprocessor.Prepare(first, Limits())));
    }

    private static EvidencePreprocessor CreatePreprocessor(IEvidenceProcessingLog? log = null) =>
        new(new PassThroughNormalizer(), new SecretRedactor(), log ?? new CapturingProcessingLog());

    private static ProcessingConfiguration Limits(
        int total = 10_000,
        int comments = 100,
        int perContent = 1_000,
        int attachments = 20) =>
        new(ExecutionMode.DryRun, 1, 0, new ContentLimits(total, comments, perContent), new AttachmentLimits(attachments, 1_000, 10_000, 10));

    private static RawWorkItem CreateWorkItem(
        string title = "Generic title",
        IReadOnlyList<RawWorkItemField>? fields = null,
        string description = "Generic description",
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<RawWorkItemComment>? comments = null,
        IReadOnlyList<RawAttachmentMetadata>? attachments = null) =>
        new(
            "work-42",
            "revision-exact-7",
            "GenericType",
            title,
            fields,
            description,
            WorkItemContentFormat.PlainText,
            tags,
            [],
            comments,
            attachments);

    private static RawWorkItemField Field(string name, object value) =>
        new(name, null, value is JsonElement element ? element : JsonSerializer.SerializeToElement(value));

    private static RawWorkItemComment Comment(string id, string author, string content, int minute) =>
        new(id, author, new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero), content);

    private static void AssertSecretAbsent(string output, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.False(output.Contains(secret, StringComparison.Ordinal), "Sanitized output contained a synthetic secret.");
        }
    }

    private static int CountOccurrences(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private sealed class PassThroughNormalizer : IContentNormalizer
    {
        public string Normalize(string? content, WorkItemContentFormat format) => content ?? string.Empty;
    }

    private sealed class CapturingProcessingLog : IEvidenceProcessingLog
    {
        public List<object> Entries { get; } = [];
        public void ContentCollectionCompleted(ContentCollectionLogEntry entry) => Entries.Add(entry);
        public void SecretRedactionCompleted(SecretRedactionLogEntry entry) => Entries.Add(entry);
    }
}
