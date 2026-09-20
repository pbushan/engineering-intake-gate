using System.Text;
using System.Text.Json;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Evaluation;
using IntakeGate.Infrastructure.Evidence;
using IntakeGate.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
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
    public async Task PH2_XlsxProducesBoundedCachedValuesAndLiteralFormulasWithoutCalculation()
    {
        var service = new AttachmentProcessingService([new SpreadsheetAttachmentProcessor()]);
        var result = Assert.Single(await service.ProcessAsync(
            [Attachment("xlsx", "facts.xlsx", "application/octet-stream", CreateXlsx())],
            Limits() with { MaximumSpreadsheetRowsPerSheet = 2, MaximumSpreadsheetColumns = 3 }, 10_000));

        Assert.Equal(AttachmentInspectionMode.Spreadsheet, result.InspectionMode);
        Assert.Equal(AttachmentProcessingStatus.Partial, result.ProcessingStatus);
        Assert.Contains("[Sheet: Evidence]", result.ExtractedEvidence, StringComparison.Ordinal);
        Assert.Contains("FACT-IN-CELL", result.ExtractedEvidence, StringComparison.Ordinal);
        Assert.Contains("=1+1 [cached: 2]", result.ExtractedEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("ROW-3-OMITTED", result.ExtractedEvidence, StringComparison.Ordinal);
        Assert.Contains("SpreadsheetSamplingLimitReached", result.Warnings);
    }

    [Fact]
    public async Task PH2_MediaEvidenceIsCompositeReusableAndForceFreshWhileScreenshotsRemainBounded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"media-cache-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteDatabaseMigrator(path).MigrateAsync();
            var cache = new SqliteAnalysisCacheRepository(path);
            var tool = new FakeMediaTool();
            var provider = new FakeAttachmentEvidenceProvider();
            var processor = new MediaAttachmentProcessor(tool, provider, new SecretRedactor());
            var service = new AttachmentProcessingService([processor], cache, new SecretRedactor());
            var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
            var options = new EvidencePreparationOptions("https://org", "project", false, now, now.AddDays(30));
            var attachment = Attachment("video", "support-recording.mp4", "video/mp4", new byte[] { 0, 1, 2, 3 });

            var first = Assert.Single(await service.ProcessAsync([attachment], Limits(), 20_000, options));
            var reused = Assert.Single(await service.ProcessAsync([attachment], Limits(), 20_000, options));
            var fresh = Assert.Single(await service.ProcessAsync([attachment], Limits(), 20_000,
                options with { ForceFresh = true }));

            Assert.Equal(AttachmentInspectionMode.VideoComposite, first.InspectionMode);
            Assert.Contains("FACT A SPOKEN ONLY", first.ExtractedEvidence, StringComparison.Ordinal);
            Assert.Contains("FACT B VISIBLE ONLY", first.ExtractedEvidence, StringComparison.Ordinal);
            Assert.NotEmpty(first.SelectedKeyScreenshots);
            Assert.True(first.SelectedKeyScreenshots.Count <= 6);
            Assert.True(reused.CacheReused);
            Assert.Equal(first.SelectedKeyScreenshots, reused.SelectedKeyScreenshots);
            Assert.False(fresh.CacheReused);
            Assert.Equal(2, provider.TranscriptionCalls);
            Assert.Equal(4, provider.VisionCalls);
            var screenshot = await cache.GetScreenshotAsync(first.SelectedKeyScreenshots[0].StorageReference,
                "https://org", "project", now);
            Assert.NotNull(screenshot);
            Assert.NotEmpty(screenshot.Content);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PH2_ScreenshotCacheFailurePreservesTextualVideoEvidence()
    {
        var cache = new ScreenshotFailingCache();
        var provider = new FakeAttachmentEvidenceProvider();
        var service = new AttachmentProcessingService(
            [new MediaAttachmentProcessor(new FakeMediaTool(), provider, new SecretRedactor())], cache, new SecretRedactor());
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
        var result = Assert.Single(await service.ProcessAsync(
            [Attachment("video", "support.mp4", "video/mp4", new byte[] { 0, 1, 2, 3 })],
            Limits(), 20_000, new EvidencePreparationOptions("https://org", "project", false, now, now.AddDays(30))));

        Assert.Contains("FACT B VISIBLE ONLY", result.ExtractedEvidence, StringComparison.Ordinal);
        Assert.Contains("ScreenshotPersistenceFailed", result.Warnings);
        Assert.Empty(result.SelectedKeyScreenshots);
        Assert.NotEqual(AttachmentProcessingStatus.Error, result.ProcessingStatus);
    }

    [Fact]
    public async Task PH2_MediaSubartifactInvalidationDoesNotInvalidateIndependentEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"media-version-cache-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteDatabaseMigrator(path).MigrateAsync();
            var cache = new SqliteAnalysisCacheRepository(path);
            var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
            var options = new EvidencePreparationOptions("https://org", "project", false, now, now.AddDays(30));
            var attachment = Attachment("video", "support.mp4", "video/mp4", [0, 1, 2, 3]);
            var originalProvider = new FakeAttachmentEvidenceProvider("t1", "v1");
            await new AttachmentProcessingService(
                [new MediaAttachmentProcessor(new FakeMediaTool(), originalProvider, new SecretRedactor())], cache, new SecretRedactor())
                .ProcessAsync([attachment], Limits(), 20_000, options);

            var transcriptChanged = new FakeAttachmentEvidenceProvider("t2", "v1");
            var transcriptResult = Assert.Single(await new AttachmentProcessingService(
                [new MediaAttachmentProcessor(new FakeMediaTool(), transcriptChanged, new SecretRedactor())], cache, new SecretRedactor())
                .ProcessAsync([attachment], Limits(), 20_000, options));

            Assert.False(transcriptResult.CacheReused);
            Assert.Equal(1, transcriptChanged.TranscriptionCalls);
            Assert.Equal(0, transcriptChanged.VisionCalls);
            Assert.Contains("FACT B VISIBLE ONLY", transcriptResult.ExtractedEvidence, StringComparison.Ordinal);
            Assert.NotEmpty(transcriptResult.SelectedKeyScreenshots);

            var visionChanged = new FakeAttachmentEvidenceProvider("t1", "v2");
            var visionResult = Assert.Single(await new AttachmentProcessingService(
                [new MediaAttachmentProcessor(new FakeMediaTool(), visionChanged, new SecretRedactor())], cache, new SecretRedactor())
                .ProcessAsync([attachment], Limits(), 20_000, options));

            Assert.False(visionResult.CacheReused);
            Assert.Equal(0, visionChanged.TranscriptionCalls);
            Assert.Equal(2, visionChanged.VisionCalls);
            Assert.Contains("FACT A SPOKEN ONLY", visionResult.ExtractedEvidence, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    public async Task PH2_VideoPartialStagesPreserveEverySuccessfulSubresult(
        bool transcriptSucceeds, bool visionSucceeds, bool hasAudio)
    {
        var provider = new PartialAttachmentEvidenceProvider(transcriptSucceeds, visionSucceeds);
        var processor = new MediaAttachmentProcessor(new FakeMediaTool(hasAudio), provider, new SecretRedactor());

        var result = Assert.Single(await new AttachmentProcessingService([processor]).ProcessAsync(
            [Attachment("video", "partial.mp4", "video/mp4", [0, 1, 2, 3])], Limits(), 20_000));

        Assert.NotEqual(AttachmentProcessingStatus.Error, result.ProcessingStatus);
        if (transcriptSucceeds && hasAudio) Assert.Contains("TRANSCRIPT SUCCEEDED", result.ExtractedEvidence, StringComparison.Ordinal);
        if (visionSucceeds) Assert.Contains("VISION SUCCEEDED", result.ExtractedEvidence, StringComparison.Ordinal);
        if (!hasAudio) Assert.Contains("VideoHasNoAudioStream", result.Warnings);
        Assert.NotNull(result.Video);
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

    [Fact]
    public async Task CACHE_003_AttachmentCacheHitsMissesAndForceFreshAreContentAndProcessorVersionAware()
    {
        var path = Path.Combine(Path.GetTempPath(), $"attachment-cache-{Guid.NewGuid():N}.db");
        try
        {
            await new SqliteDatabaseMigrator(path).MigrateAsync();
            var cache = new SqliteAnalysisCacheRepository(path);
            var processorV1 = new CountingTextProcessor("1");
            var serviceV1 = new AttachmentProcessingService([processorV1], cache, new SecretRedactor());
            var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
            var options = new EvidencePreparationOptions("https://org", "project", false, now, now.AddDays(30));
            var original = Attachment("a", "password=filename-secret.txt", "text/plain", "result=ok\npassword=secret-value"u8.ToArray());

            var first = Assert.Single(await serviceV1.ProcessAsync([original], Limits(), 10_000, options));
            var hit = Assert.Single(await serviceV1.ProcessAsync([original], Limits(), 10_000, options));
            var changed = Assert.Single(await serviceV1.ProcessAsync(
                [Attachment("a", "evidence.txt", "text/plain", "result=changed"u8.ToArray())], Limits(), 10_000, options));
            var fresh = Assert.Single(await serviceV1.ProcessAsync([original], Limits(), 10_000,
                options with { ForceFresh = true }));

            Assert.False(first.CacheReused);
            Assert.True(hit.CacheReused);
            Assert.False(changed.CacheReused);
            Assert.False(fresh.CacheReused);
            Assert.Equal(3, processorV1.Calls);
            Assert.NotEqual(first.ArtifactId, changed.ArtifactId);
            Assert.DoesNotContain("secret-value", first.ExtractedEvidence, StringComparison.Ordinal);
            var persisted = await cache.GetAttachmentAsync(first.ArtifactId!, "https://org", "project", now);
            Assert.NotNull(persisted);
            Assert.DoesNotContain("secret-value", persisted.NormalizedEvidence, StringComparison.Ordinal);
            Assert.DoesNotContain("filename-secret", persisted.FileName, StringComparison.Ordinal);

            var processorV2 = new CountingTextProcessor("2");
            var serviceV2 = new AttachmentProcessingService([processorV2], cache, new SecretRedactor());
            var versionMiss = Assert.Single(await serviceV2.ProcessAsync([original], Limits(), 10_000, options));
            Assert.False(versionMiss.CacheReused);
            Assert.Equal(1, processorV2.Calls);
            Assert.NotEqual(first.ArtifactId, versionMiss.ArtifactId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CACHE_003_CacheWriteFailurePreservesSuccessfulAttachmentEvidence()
    {
        var processor = new CountingTextProcessor("1");
        var service = new AttachmentProcessingService([processor], new FailingAttachmentCache(), new SecretRedactor());
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

        var result = Assert.Single(await service.ProcessAsync(
            [Attachment("a", "evidence.txt", "text/plain", "result=useful"u8.ToArray())],
            Limits(), 10_000, new EvidencePreparationOptions("https://org", "project", false, now, now.AddDays(30))));

        Assert.Equal(AttachmentProcessingStatus.Processed, result.ProcessingStatus);
        Assert.Equal("result=useful", result.ExtractedEvidence);
        Assert.Null(result.ArtifactId);
        Assert.Contains("EvidenceCachePersistenceFailed", result.Warnings);
        Assert.Equal(1, processor.Calls);
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
        schemaVersion = "intake-evaluation-v2",
        evaluationId = request.EvaluationId,
        decision,
        applicableCriteria = new[] { "problem" },
        satisfiedCriteria = decision == "PASS" ? new[] { "problem" } : Array.Empty<string>(),
        deficiencies = decision == "FAIL" ? new object[] { new { criterionId = "problem", reason = "Necessary attachment evidence could not be inspected.", requiredSupportAction = "Provide the evidence in a supported format." } } : Array.Empty<object>(),
        ambiguities = Array.Empty<object>(),
        engineeringSummary = "Grounded attachment-aware summary.",
        ticketSummary = new
        {
            issueSummary = "Grounded attachment-aware summary.",
            expectedBehavior = (string?)null,
            actualBehavior = (string?)null,
            reproductionSteps = Array.Empty<string>(),
            affectedExamples = Array.Empty<string>(),
            environment = (string?)null,
            businessImpact = (string?)null,
            attachmentFindings = Array.Empty<string>(),
            investigationWarnings = Array.Empty<string>()
        }
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

    private static byte[] CreateXlsx()
    {
        using var output = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { CellReference = "A1", DataType = CellValues.String, CellValue = new CellValue("Header") },
                    new Cell { CellReference = "B1", DataType = CellValues.String, CellValue = new CellValue("Value") }),
                new Row(new Cell { CellReference = "A2", DataType = CellValues.String, CellValue = new CellValue("FACT-IN-CELL") },
                    new Cell { CellReference = "B2", CellFormula = new CellFormula("1+1"), CellValue = new CellValue("2") }),
                new Row(new Cell { CellReference = "A3", DataType = CellValues.String, CellValue = new CellValue("ROW-3-OMITTED") })));
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Evidence" });
            workbookPart.Workbook.Save();
        }
        return output.ToArray();
    }

    private sealed class ThrowingContent : IAttachmentContentSource
    {
        public long? Length => 20;
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => throw new IOException("synthetic unavailable");
    }

    private sealed class CountingTextProcessor(string version) : IAttachmentProcessor
    {
        public int Calls { get; private set; }
        public string ProcessorIdentity => "counting-text";
        public string ProcessorVersion => version;
        public bool CanProcess(DetectedAttachment attachment) => attachment.MediaType == "text/plain";
        public ValueTask<AttachmentProcessorOutput> ProcessAsync(
            DetectedAttachment attachment,
            AttachmentLimits limits,
            int maximumExtractedCharacters,
            CancellationToken cancellationToken)
        {
            Calls++;
            var content = Encoding.UTF8.GetString(attachment.Content);
            return ValueTask.FromResult(new AttachmentProcessorOutput(
                AttachmentProcessingStatus.Processed, AttachmentInspectionMode.Text,
                content[..Math.Min(content.Length, maximumExtractedCharacters)],
                content.Length > maximumExtractedCharacters, false, null, null, null));
        }
    }

    private sealed class FailingAttachmentCache : IAnalysisCacheRepository
    {
        public Task<AttachmentEvidenceArtifact?> GetAttachmentAsync(string artifactId, string organization, string project, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<AttachmentEvidenceArtifact?>(null);
        public Task SaveAttachmentAsync(AttachmentEvidenceArtifact artifact, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic cache write failure");
        public Task<ReusableEvaluation?> GetEvaluationAsync(string equivalenceKey, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReusableEvaluation?>(null);
        public Task SaveEvaluationAsync(ReusableEvaluation evaluation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextAsync(AnalysisContextSnapshot context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AnalysisContextSnapshot?> GetContextAsync(string snapshotId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<AnalysisContextSnapshot?>(null);
        public Task<EvidenceCleanupResult> DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EvidenceCleanupResult(0, 0, 0, 0));
    }

    private sealed class ScreenshotFailingCache : IAnalysisCacheRepository
    {
        private readonly Dictionary<string, AttachmentEvidenceArtifact> artifacts = new(StringComparer.Ordinal);
        public Task<AttachmentEvidenceArtifact?> GetAttachmentAsync(string artifactId, string organization, string project,
            DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(artifacts.TryGetValue(artifactId, out var value) ? value : null);
        public Task SaveAttachmentAsync(AttachmentEvidenceArtifact artifact, CancellationToken cancellationToken = default)
        { artifacts[artifact.ArtifactId] = artifact; return Task.CompletedTask; }
        public Task SaveScreenshotAsync(SelectedKeyScreenshotArtifact screenshot, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic screenshot write failure");
        public Task<ReusableEvaluation?> GetEvaluationAsync(string equivalenceKey, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult<ReusableEvaluation?>(null);
        public Task SaveEvaluationAsync(ReusableEvaluation evaluation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveContextAsync(AnalysisContextSnapshot context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AnalysisContextSnapshot?> GetContextAsync(string snapshotId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult<AnalysisContextSnapshot?>(null);
        public Task<EvidenceCleanupResult> DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) => Task.FromResult(new EvidenceCleanupResult(0, 0, 0, 0));
    }

    private sealed class FakeMediaTool(bool hasAudio = true) : IMediaTool
    {
        public string Version => "fake-media-v1";
        public Task<MediaProbeResult> ProbeAsync(string inputPath, AttachmentLimits limits, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeResult(true, 30, 1280, 720, hasAudio, null, []));
        public async Task<(EvidenceSubstageStatus Status, string? AudioPath, IReadOnlyList<string> Warnings)> ExtractAudioAsync(
            string inputPath, string workspace, AttachmentLimits limits, CancellationToken cancellationToken)
        {
            var path = Path.Combine(workspace, "audio.wav");
            await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
            return (EvidenceSubstageStatus.Completed, path, []);
        }
        public async Task<FrameExtractionResult> ExtractFramesAsync(string inputPath, string workspace,
            MediaProbeResult probe, AttachmentLimits limits, CancellationToken cancellationToken)
        {
            var frames = new List<ExtractedMediaFrame>();
            foreach (var timestamp in new[] { 5d, 12.7d })
            {
                var path = Path.Combine(workspace, $"frame-{timestamp.ToString(CultureInfo.InvariantCulture)}.jpg");
                await File.WriteAllBytesAsync(path, ValidPng.Concat([(byte)timestamp]).ToArray(), cancellationToken);
                frames.Add(new(timestamp, path, 1280, 720, timestamp > 10));
            }
            return new(EvidenceSubstageStatus.Completed, frames, 2, []);
        }
    }

    private sealed class FakeAttachmentEvidenceProvider(string transcriptionVersion = "1", string visionVersion = "1") : IAttachmentEvidenceAiProvider
    {
        public int TranscriptionCalls { get; private set; }
        public int VisionCalls { get; private set; }
        public string ProviderIdentity => "fake";
        public string TranscriptionModel => "fake-transcribe";
        public string TranscriptionVersion => transcriptionVersion;
        public string VisionModel => "fake-vision";
        public string VisionVersion => visionVersion;
        public Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken)
        {
            TranscriptionCalls++;
            return Task.FromResult(new AudioTranscriptionResult(EvidenceSubstageStatus.Completed,
                [new TranscriptSegment(5, 8.9, "FACT A SPOKEN ONLY", ProviderIdentity, TranscriptionModel, TranscriptionVersion, [])], [],
                new AiProviderInteractionUsage(1, ProviderIdentity, TranscriptionModel, ProviderIdentity,
                    TranscriptionModel, $"transcript-{TranscriptionCalls}", null)));
        }
        public Task<FrameVisionResult> ObserveFrameAsync(FrameVisionRequest request, CancellationToken cancellationToken)
        {
            VisionCalls++;
            var observation = request.TimestampSeconds > 10 ? "FACT B VISIBLE ONLY: error E-42" : "Checkout navigation visible";
            return Task.FromResult(new FrameVisionResult(EvidenceSubstageStatus.Completed, observation, [],
                new AiProviderInteractionUsage(1, ProviderIdentity, VisionModel, ProviderIdentity,
                    VisionModel, $"vision-{VisionCalls}", new TokenUsage(10, 5, 15))));
        }
    }

    private sealed class PartialAttachmentEvidenceProvider(bool transcriptSucceeds, bool visionSucceeds) : IAttachmentEvidenceAiProvider
    {
        public string ProviderIdentity => "fake";
        public string TranscriptionModel => "fake-transcribe";
        public string TranscriptionVersion => "partial-1";
        public string VisionModel => "fake-vision";
        public string VisionVersion => "partial-1";
        public Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(transcriptSucceeds
                ? new AudioTranscriptionResult(EvidenceSubstageStatus.Completed,
                    [new TranscriptSegment(1, 2, "TRANSCRIPT SUCCEEDED", ProviderIdentity, TranscriptionModel, TranscriptionVersion, [])], [])
                : new AudioTranscriptionResult(EvidenceSubstageStatus.Failed, [], ["SyntheticTranscriptionFailure"]));
        public Task<FrameVisionResult> ObserveFrameAsync(FrameVisionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(visionSucceeds
                ? new FrameVisionResult(EvidenceSubstageStatus.Completed, "VISION SUCCEEDED", [])
                : new FrameVisionResult(EvidenceSubstageStatus.Failed, null, ["SyntheticVisionFailure"]));
    }

    private sealed class NoOpLog : IEvidenceProcessingLog
    {
        public void ContentCollectionCompleted(ContentCollectionLogEntry entry) { }
        public void SecretRedactionCompleted(SecretRedactionLogEntry entry) { }
    }
}
