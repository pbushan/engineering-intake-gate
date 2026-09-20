using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class AnalysisFingerprintTests
{
    [Fact]
    public void CACHE_004_SourceFingerprintExcludesApplicationOwnedTagsButDetectsSemanticChanges()
    {
        var intakeState = new IntakeStateConfiguration("READY", "INCOMPLETE");
        var original = Evidence("Title", ["customer", "READY"]);
        var applicationOnlyChange = Evidence("Title", ["customer", "INCOMPLETE"]);
        var semanticChange = Evidence("Changed title", ["customer", "INCOMPLETE"]);

        Assert.Equal(AnalysisFingerprint.Source(original, intakeState),
            AnalysisFingerprint.Source(applicationOnlyChange, intakeState));
        Assert.NotEqual(AnalysisFingerprint.Source(original, intakeState),
            AnalysisFingerprint.Source(semanticChange, intakeState));
    }

    [Fact]
    public void RET_002_EvidenceRetentionDefaultsToThirtyDaysAndCanBeOverridden()
    {
        Assert.Equal(30, new AuditConfiguration(365).EvidenceRetentionDays);
        Assert.Equal(12, (new AuditConfiguration(365) { EvidenceRetentionDays = 12 }).EvidenceRetentionDays);
        Assert.Equal(6, new AuditConfiguration(365).MaximumSelectedVideoScreenshots);
    }

    [Fact]
    public void CACHE_006_ManifestFingerprintIgnoresExecutionReuseStateButDetectsFilenameChange()
    {
        var processed = new AttachmentManifestItem("a", "trace.log", 10, "hash", "text/plain",
            "artifact", "text", "1", AttachmentProcessingStatus.Processed, false);
        var reused = processed with { CacheReused = true };
        var renamed = reused with { FileName = "renamed.log" };

        Assert.Equal(AnalysisFingerprint.Manifest([processed]), AnalysisFingerprint.Manifest([reused]));
        Assert.NotEqual(AnalysisFingerprint.Manifest([processed]), AnalysisFingerprint.Manifest([renamed]));
    }

    [Fact]
    public void CACHE_006_ManifestFingerprintDetectsAddedRemovedReplacedAndChangedContent()
    {
        var first = new AttachmentManifestItem("a", "trace.log", 10, "hash-a", "text/plain",
            "artifact-a", "text", "1", AttachmentProcessingStatus.Processed, false);
        var second = new AttachmentManifestItem("b", "screen.png", 20, "hash-b", "image/png",
            "artifact-b", "image", "1", AttachmentProcessingStatus.Processed, false);

        Assert.NotEqual(AnalysisFingerprint.Manifest([first]), AnalysisFingerprint.Manifest([first, second]));
        Assert.NotEqual(AnalysisFingerprint.Manifest([first, second]), AnalysisFingerprint.Manifest([second]));
        Assert.NotEqual(AnalysisFingerprint.Manifest([first]),
            AnalysisFingerprint.Manifest([first with { AttachmentId = "replacement" }]));
        Assert.NotEqual(AnalysisFingerprint.Manifest([first]),
            AnalysisFingerprint.Manifest([first with { ContentSha256 = "changed" }]));
    }

    [Fact]
    public void CACHE_006_ArtifactKeyInvalidatesOnProcessorSchemaAndLimitVersions()
    {
        var baseline = AnalysisFingerprint.Artifact("org", "project", "attachment", "content", "text", "1", "limits-v1");

        Assert.NotEqual(baseline, AnalysisFingerprint.Artifact("org", "project", "attachment", "content", "text", "2", "limits-v1"));
        Assert.NotEqual(baseline, AnalysisFingerprint.Artifact("org", "project", "attachment", "content", "text", "1", "limits-v2"));
        Assert.NotEqual(baseline, AnalysisFingerprint.Artifact("org", "project", "attachment", "content", "text", "1", "limits-v1", "normalized-evidence-v3"));
    }

    private static EvaluationEvidence Evidence(string title, IReadOnlyList<string> tags) => new(
        "42", "7", "Bug", title, [], "description", tags, [], [], [],
        new EvidenceProcessingDisclosure(false, false, false, false, false, 0, 0,
            tags.Count, tags.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            false, false, 0),
        new RedactionMetadata(false, 0, new Dictionary<string, int>()));
}
