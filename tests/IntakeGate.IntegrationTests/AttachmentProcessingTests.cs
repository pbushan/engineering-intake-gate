using System.Text;
using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Evaluation;
using IntakeGate.Infrastructure.Evidence;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace IntakeGate.IntegrationTests;

public sealed class AttachmentProcessingTests
{
    private static readonly byte[] ValidPng = CreatePng();

    [Theory]
    [InlineData("evidence.txt", "text/plain", "line one\nline two", AttachmentInspectionMode.Text)]
    [InlineData("trace.log", "text/x-log", "first\r\nsecond", AttachmentInspectionMode.Text)]
    [InlineData("data.json", "application/json", "{\"z\":1,\"a\":2}", AttachmentInspectionMode.StructuredText)]
    [InlineData("data.xml", "application/xml", "<root><value>useful</value></root>", AttachmentInspectionMode.StructuredText)]
    [InlineData("data.csv", "text/csv", "name,count\nalpha,2", AttachmentInspectionMode.StructuredText)]
    public async Task CNT_004_CNT_005_CommonTextAttachmentsProduceReadableEvidence(
        string name, string mediaType, string content, AttachmentInspectionMode mode)
    {
        var result = Assert.Single(await Service().ProcessAsync(
            [Attachment("a", name, mediaType, Encoding.UTF8.GetBytes(content))], Limits(), 10_000));

        Assert.Equal(AttachmentProcessingStatus.Processed, result.ProcessingStatus);
        Assert.Equal(mode, result.InspectionMode);
        Assert.NotEmpty(result.ExtractedEvidence);
        if (mediaType == "application/json") Assert.True(result.ExtractedEvidence.IndexOf("\"a\"", StringComparison.Ordinal) < result.ExtractedEvidence.IndexOf("\"z\"", StringComparison.Ordinal));
        if (mediaType == "text/csv") Assert.Contains("name | count", result.ExtractedEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CNT_005_MalformedJsonFallsBackAndMalformedXmlAndXxeFailClosedWithoutReadingExternalData()
    {
        var externalMarker = "SHOULD_NEVER_BE_READ_7391";
        var path = Path.Combine(Path.GetTempPath(), $"intake-gate-xxe-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, externalMarker);
        try
        {
            var results = await Service().ProcessAsync(
                [
                    Attachment("json", "bad.json", "application/json", Encoding.UTF8.GetBytes("{bad json")),
                    Attachment("xml", "bad.xml", "application/xml", Encoding.UTF8.GetBytes("<root>")),
                    Attachment("xxe", "xxe.xml", "application/xml", Encoding.UTF8.GetBytes($"<!DOCTYPE root [<!ENTITY xxe SYSTEM 'file://{path}'>]><root>&xxe;</root>"))
                ], Limits(), 10_000);

            Assert.Equal(AttachmentProcessingStatus.Partial, results[0].ProcessingStatus);
            Assert.Equal("MalformedJsonTextFallback", results[0].FailureCategory);
            Assert.Equal(AttachmentProcessingStatus.Error, results[1].ProcessingStatus);
            Assert.Equal("MalformedOrUnsafeXml", results[1].FailureCategory);
            Assert.Equal(AttachmentProcessingStatus.Error, results[2].ProcessingStatus);
            Assert.DoesNotContain(externalMarker, JsonSerializer.Serialize(results), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CNT_006_CNT_007_E7_PdfLeadingPageInspectionIsBoundedAndDisclosed()
    {
        var result = Assert.Single(await Service().ProcessAsync(
            [Attachment("pdf", "large.pdf", "application/pdf", CreatePdf(12))],
            Limits() with { MaximumPdfPages = 3 }, 10_000));

        Assert.Equal(AttachmentProcessingStatus.Partial, result.ProcessingStatus);
        Assert.Equal(AttachmentInspectionMode.PdfText, result.InspectionMode);
        Assert.Equal(12, result.PagesAvailable);
        Assert.Equal(3, result.PagesInspected);
        Assert.True(result.Truncated);
        Assert.True(result.Sampled);
        Assert.Equal("PdfPageLimitExceeded", result.FailureCategory);
        Assert.Contains("page-3-evidence", result.ExtractedEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("page-4-evidence", result.ExtractedEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CNT_005_AC_23_MalformedPdfInvalidImageAndUnsupportedBinaryAreExplicit()
    {
        var results = await Service().ProcessAsync(
            [
                Attachment("pdf", "bad.pdf", "application/pdf", "%PDF-not-valid"u8.ToArray()),
                Attachment("image", "bad.png", "image/png", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2 }),
                Attachment("bin", "archive.bin", "application/octet-stream", new byte[] { 0, 1, 2, 3 })
            ], Limits(), 10_000);

        var byId = results.ToDictionary(result => result.AttachmentId, StringComparer.Ordinal);
        Assert.Equal((AttachmentProcessingStatus.Error, "MalformedPdf"), (byId["pdf"].ProcessingStatus, byId["pdf"].FailureCategory));
        Assert.Equal((AttachmentProcessingStatus.Error, "MalformedImage"), (byId["image"].ProcessingStatus, byId["image"].FailureCategory));
        Assert.Equal((AttachmentProcessingStatus.Unsupported, "UnsupportedAttachmentType"), (byId["bin"].ProcessingStatus, byId["bin"].FailureCategory));
    }

    [Fact]
    public async Task CNT_005_ImageBecomesBoundedProviderNeutralVisualEvidence()
    {
        var preprocessor = Preprocessor();
        var raw = new RawWorkItem("42", "1", "Generic", "Title", attachments:
            [Attachment("image", "shot.png", "application/octet-stream", ValidPng)]);

        var evidence = await preprocessor.PrepareAsync(raw, Processing());
        var visual = Assert.Single(evidence.VisualEvidence);
        var attachment = Assert.Single(evidence.Attachments);

        Assert.Equal("image/png", visual.MediaType);
        Assert.Equal(ValidPng, visual.Content.ToArray());
        Assert.Equal(AttachmentInspectionMode.Image, attachment.InspectionMode);
        Assert.True(attachment.RequiresVisualInspection);
        Assert.Contains("visual inspection", attachment.ExtractedEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(ValidPng), JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CNT_006_ByteAggregateCountImageAndCsvLimitsAreDeterministic()
    {
        var service = Service();
        var perFile = await service.ProcessAsync(
            [Attachment("a", "a.txt", "text/plain", Encoding.UTF8.GetBytes("12345"))], Limits() with { MaximumBytesPerAttachment = 4 }, 100);
        var aggregate = await service.ProcessAsync(
            [Attachment("a", "a.txt", "text/plain", Encoding.UTF8.GetBytes("1234")), Attachment("b", "b.txt", "text/plain", Encoding.UTF8.GetBytes("5678"))],
            Limits() with { MaximumAggregateBytes = 6 }, 100);
        var count = await service.ProcessAsync(
            [Attachment("a", "a.txt", "text/plain", "a"u8.ToArray()), Attachment("b", "b.txt", "text/plain", "b"u8.ToArray())],
            Limits() with { MaximumCount = 1 }, 100);
        var imageCount = await service.ProcessAsync(
            [Attachment("a", "a.png", "image/png", ValidPng), Attachment("b", "b.png", "image/png", ValidPng)],
            Limits() with { MaximumImageCount = 1 }, 100);
        var imageBytes = await service.ProcessAsync(
            [Attachment("image", "image.png", "image/png", ValidPng)],
            Limits() with { MaximumImageBytes = ValidPng.Length - 1 }, 100);
        var csv = await service.ProcessAsync(
            [Attachment("csv", "rows.csv", "text/csv", Encoding.UTF8.GetBytes("h\n1\n2"))],
            Limits() with { MaximumCsvRows = 2 }, 100);

        Assert.Equal("PerAttachmentByteLimitExceeded", Assert.Single(perFile).FailureCategory);
        Assert.Equal("AggregateAttachmentByteLimitExceeded", aggregate[1].FailureCategory);
        Assert.Equal("AttachmentCountLimitExceeded", count[1].FailureCategory);
        Assert.Equal("ImageCountLimitExceeded", imageCount[1].FailureCategory);
        Assert.Equal("ImageByteLimitExceeded", Assert.Single(imageBytes).FailureCategory);
        Assert.Equal("CsvRowLimitExceeded", Assert.Single(csv).FailureCategory);
        Assert.All([perFile[0], aggregate[1], count[1], imageCount[1], imageBytes[0], csv[0]], result => Assert.Equal(AttachmentProcessingStatus.Partial, result.ProcessingStatus));
    }

    [Fact]
    public async Task CNT_006_CNT_007_PerAttachmentExtractedCharacterLimitIsEnforcedAndDisclosed()
    {
        var raw = new RawWorkItem("42", "1", "Generic", "Title", attachments:
            [Attachment("text", "long.txt", "text/plain", Encoding.UTF8.GetBytes(new string('x', 200)))]);
        var processing = Processing() with { ContentLimits = new ContentLimits(10_000, 10, 25) };

        var evidence = await Preprocessor().PrepareAsync(raw, processing);
        var attachment = Assert.Single(evidence.Attachments);

        Assert.Equal(25, attachment.ExtractedEvidence.Length);
        Assert.True(attachment.Truncated);
        Assert.Equal(AttachmentProcessingStatus.Partial, attachment.ProcessingStatus);
        Assert.Equal("EvidenceCharacterLimitExceeded", attachment.FailureCategory);
    }

    [Fact]
    public async Task CNT_006_SEC_001_NFR_006_AttachmentTextIsRedactedBeforeEvidenceAndAiRequestAndUsesTotalBudget()
    {
        const string secret = "SYNTH_ATTACHMENT_PASSWORD_7744";
        var raw = new RawWorkItem("42", "1", "Generic", "Title", description: new string('d', 20), attachments:
            [Attachment("text", "evidence.txt", "text/plain", Encoding.UTF8.GetBytes($"result=useful\npassword={secret}\n" + new string('x', 200)))]);
        var processing = Processing() with { ContentLimits = new ContentLimits(100, 10, 60) };

        var evidence = await Preprocessor().PrepareAsync(raw, processing);
        var request = new EvaluationRequestBuilder().Build(evidence, Configuration(processing), "11111111-2222-3333-4444-555555555555");
        var serializedRequest = JsonSerializer.Serialize(request);

        Assert.True(evidence.Redaction.RedactionOccurred);
        Assert.True(evidence.Processing.AggregateTextLimitReached);
        Assert.True(Assert.Single(evidence.Attachments).Truncated);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(evidence), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, serializedRequest, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Placeholder, serializedRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CNT_008_E4_UnavailableContentIsContextAndDoesNotForceAnApplicationDecision()
    {
        var unavailable = new RawAttachmentMetadata("missing", "missing.log", "text/plain", 20, null, new ThrowingContent());
        var evidence = await Preprocessor().PrepareAsync(
            new RawWorkItem("42", "1", "Generic", "Title", description: "Other sufficient evidence", attachments: [unavailable]),
            Processing());
        var captured = new List<EvaluationRequest>();
        var provider = new FakeIntakeAiProvider([request =>
        {
            captured.Add(request);
            return AiProviderResponse.Success(ValidResponse(request, "PASS"));
        }]);

        var result = await Evaluation(provider).EvaluateAsync(evidence, Configuration(Processing()));

        Assert.Equal(IntakeDecision.Pass, result.Outcome);
        Assert.Equal(AttachmentProcessingStatus.Unavailable, Assert.Single(captured[0].Evidence.Attachments).ProcessingStatus);
        Assert.Equal("AttachmentContentUnavailable", captured[0].Evidence.Attachments[0].FailureCategory);
    }

    [Fact]
    public async Task CNT_004_CNT_008_AC_09_AC_23_E6_AttachmentEvidenceAndUnsupportedDisclosureReachSemanticEvaluator()
    {
        var supported = Attachment("supported", "evidence.txt", "text/plain", Encoding.UTF8.GetBytes("material-only-in-attachment"));
        var unsupported = Attachment("unsupported", "dump.bin", "application/octet-stream", new byte[] { 0, 8, 9 });
        var passEvidence = await Preprocessor().PrepareAsync(new RawWorkItem("42", "1", "Generic", "Title", attachments: [supported]), Processing());
        var failEvidence = await Preprocessor().PrepareAsync(new RawWorkItem("43", "1", "Generic", "Evidence is attached", attachments: [unsupported]), Processing());
        var provider = new FakeIntakeAiProvider([
            request => AiProviderResponse.Success(ValidResponse(request,
                request.Evidence.Attachments.Any(item => item.ExtractedEvidence.Contains("material-only-in-attachment", StringComparison.Ordinal)) ? "PASS" : "FAIL")),
            request => AiProviderResponse.Success(ValidResponse(request, "FAIL"))
        ]);
        var service = Evaluation(provider);

        var pass = await service.EvaluateAsync(passEvidence, Configuration(Processing()));
        var fail = await service.EvaluateAsync(failEvidence, Configuration(Processing()));

        Assert.Equal(IntakeDecision.Pass, pass.Outcome);
        Assert.Equal(IntakeDecision.Fail, fail.Outcome);
        Assert.Equal(AttachmentProcessingStatus.Unsupported, Assert.Single(failEvidence.Attachments).ProcessingStatus);
        Assert.Equal("UnsupportedAttachmentType", failEvidence.Attachments[0].FailureCategory);
    }

    private static AttachmentProcessingService Service() => new(
        [new TextAttachmentProcessor(), new PdfAttachmentProcessor(), new ImageAttachmentProcessor()]);

    private static EvidencePreprocessor Preprocessor() => new(
        new HtmlContentNormalizer(), new SecretRedactor(), new NoOpLog(), Service());

    private static RawAttachmentMetadata Attachment(string id, string name, string mediaType, byte[] bytes) =>
        new(id, name, mediaType, bytes.LongLength, bytes);

    private static AttachmentLimits Limits() => new(20, 1_000_000, 5_000_000, 10, 10, 1_000_000, 1_000, 32);
    private static ProcessingConfiguration Processing() => new(ExecutionMode.DryRun, 1, 0, new ContentLimits(20_000, 20, 10_000), Limits());

    private static IntakeEvaluationService Evaluation(IIntakeAiProvider provider) => new(
        new EvaluationRequestBuilder(), provider, new EvaluationResponseParser(), new EvaluationContractValidator(), new NullEvaluationLog());

    private static DeploymentConfiguration Configuration(ProcessingConfiguration processing) => new(
        new DeploymentProfile(new ProfileIdentity("profile", "1"), new IntakePolicyReference("policy.yaml", new Uri("https://example.invalid/policy")),
            new IntakeStateConfiguration("VALID", "INCOMPLETE"),
            new AzureDevOpsConfiguration(new Uri("https://example.invalid"), "project", Guid.Parse("11111111-1111-1111-1111-111111111111"), new CredentialReference("ADO")),
            new AiConfiguration("openai", "model", new CredentialReference("AI")), new ScheduleConfiguration(false, "", "UTC", TimeSpan.FromDays(1)),
            processing, new AuditConfiguration(90), []),
        new IntakePolicy(new PolicyIdentity("policy", "1"),
            [new IntakeCriterion("problem", "Problem", "Problem statement", CriterionApplicability.Required, new NotApplicablePolicy(false, false), "Assess")]),
        "sha256:test");

    private static string ValidResponse(EvaluationRequest request, string decision) => JsonSerializer.Serialize(new
    {
        schemaVersion = "intake-evaluation-v1",
        evaluationId = request.EvaluationId,
        decision,
        applicableCriteria = new[] { "problem" },
        satisfiedCriteria = decision == "PASS" ? new[] { "problem" } : Array.Empty<string>(),
        deficiencies = decision == "FAIL" ? new object[] { new { criterionId = "problem", reason = "Necessary attachment evidence could not be inspected.", requiredSupportAction = "Provide the evidence in a supported format." } } : Array.Empty<object>(),
        ambiguities = Array.Empty<object>(),
        engineeringSummary = "Grounded attachment-aware summary."
    });

    private static byte[] CreatePdf(int pageCount)
    {
        var objects = new List<string>();
        var pageIds = Enumerable.Range(0, pageCount).Select(index => 3 + index * 2).ToArray();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Count {pageCount} /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] >>");
        for (var page = 1; page <= pageCount; page++)
        {
            var pageId = 3 + (page - 1) * 2;
            var contentId = pageId + 1;
            var stream = $"BT /F1 12 Tf 72 720 Td (page-{page}-evidence) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents {contentId} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
        }

        using var output = new MemoryStream();
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(output.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return output.ToArray();
    }

    private static byte[] CreatePng()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(12, 34, 56));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private sealed class ThrowingContent : IAttachmentContentSource
    {
        public long? Length => 20;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => throw new IOException("synthetic unavailable");
    }

    private sealed class NoOpLog : IEvidenceProcessingLog
    {
        public void ContentCollectionCompleted(ContentCollectionLogEntry entry) { }
        public void SecretRedactionCompleted(SecretRedactionLogEntry entry) { }
    }
}
