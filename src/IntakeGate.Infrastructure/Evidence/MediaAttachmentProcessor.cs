using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Infrastructure.Evidence;

public sealed class MediaAttachmentProcessor(
    IMediaTool mediaTool,
    IAttachmentEvidenceAiProvider provider,
    ISecretRedactor redactor) : ICacheAwareAttachmentProcessor, IAttachmentLimitsFingerprintProvider
{
    private const string SamplingVersion = "screen-recording-sampling-v1";
    private const string ScreenshotSelectionVersion = "key-screenshot-selection-v1";
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> MediaGates = new();
    public string ProcessorVersion => $"media-composite-v1:{mediaTool.Version}:{provider.TranscriptionVersion}:{provider.VisionVersion}:{SamplingVersion}";
    public bool CanProcess(DetectedAttachment attachment) => IsAudio(attachment.MediaType) || IsVideo(attachment.MediaType);

    public string CreateLimitsFingerprint(AttachmentLimits limits, int maximumExtractedCharacters) =>
        AnalysisFingerprint.Sha256($"{limits.MaximumMediaDurationSeconds}|{limits.MaximumMediaDimension}|" +
            $"{limits.MaximumDecodedPixels}|{limits.MaximumSampledFrames}|{limits.MaximumFrameBytes}|" +
            $"{limits.MaximumSelectedVideoScreenshots}|{limits.MaximumRetainedScreenshotBytes}|" +
            $"{limits.MaximumTranscriptCharacters}|{limits.MediaProcessTimeoutSeconds}|{maximumExtractedCharacters}");

    public ValueTask<AttachmentProcessorOutput> ProcessAsync(DetectedAttachment attachment, AttachmentLimits limits,
        int maximumExtractedCharacters, CancellationToken cancellationToken) =>
        ProcessCoreAsync(attachment, limits, maximumExtractedCharacters, null, cancellationToken);

    public ValueTask<AttachmentProcessorOutput> ProcessAsync(DetectedAttachment attachment, AttachmentLimits limits,
        int maximumExtractedCharacters, AttachmentProcessorCacheContext cacheContext,
        CancellationToken cancellationToken) =>
        ProcessCoreAsync(attachment, limits, maximumExtractedCharacters, cacheContext, cancellationToken);

    private async ValueTask<AttachmentProcessorOutput> ProcessCoreAsync(DetectedAttachment attachment,
        AttachmentLimits limits, int maximumExtractedCharacters, AttachmentProcessorCacheContext? cacheContext,
        CancellationToken cancellationToken)
    {
        var gate = MediaGates.GetOrAdd(limits.MaximumConcurrentMediaJobs,
            maximum => new SemaphoreSlim(maximum, maximum));
        await gate.WaitAsync(cancellationToken);
        var workspace = Path.Combine(Path.GetTempPath(), $"intake-gate-media-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workspace);
            if (!OperatingSystem.IsWindows())
                new DirectoryInfo(workspace).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var extension = Extension(attachment.MediaType);
            var sourcePath = Path.Combine(workspace, $"source-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(sourcePath, attachment.Content, cancellationToken);
            var probe = await mediaTool.ProbeAsync(sourcePath, limits, cancellationToken);
            if (!probe.Succeeded)
                return new AttachmentProcessorOutput(AttachmentProcessingStatus.Error, AttachmentInspectionMode.None,
                    string.Empty, false, false, null, null, probe.FailureCategory ?? "MediaProbeFailed")
                { Warnings = probe.Warnings };

            return IsAudio(attachment.MediaType)
                ? await ProcessAudioAsync(attachment, sourcePath, workspace, probe, limits, maximumExtractedCharacters,
                    cacheContext, cancellationToken)
                : await ProcessVideoAsync(attachment, sourcePath, workspace, probe, limits,
                    maximumExtractedCharacters, cacheContext, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch { }
            gate.Release();
        }
    }

    private async ValueTask<AttachmentProcessorOutput> ProcessAudioAsync(DetectedAttachment attachment,
        string sourcePath, string workspace, MediaProbeResult probe, AttachmentLimits limits, int maximumExtractedCharacters,
        AttachmentProcessorCacheContext? cacheContext, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var interactions = new List<AiProviderInteractionUsage>();
        var cached = await GetComponentAsync(cacheContext, attachment, "media-transcript",
            TranscriptVersion(), TranscriptLimits(limits), cancellationToken);
        AudioEvidenceMetadata audio;
        if (cached?.Audio is { } cachedAudio)
        {
            audio = cachedAudio with { TranscriptionStatus = EvidenceSubstageStatus.Reused };
        }
        else
        {
            var extraction = await mediaTool.ExtractAudioAsync(sourcePath, workspace, limits, cancellationToken);
            warnings.AddRange(extraction.Warnings);
            var transcription = extraction.AudioPath is null
                ? new AudioTranscriptionResult(EvidenceSubstageStatus.Failed, [], ["AudioPreprocessingFailed"])
                : await provider.TranscribeAsync(new AudioTranscriptionRequest(extraction.AudioPath,
                    "audio/wav", limits.MaximumTranscriptCharacters,
                    TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds)), cancellationToken);
            if (transcription.Interaction is not null) interactions.Add(transcription.Interaction);
            var segments = RedactSegments(transcription.Segments, limits.MaximumTranscriptCharacters, warnings);
            warnings.AddRange(transcription.Warnings);
            audio = new AudioEvidenceMetadata(probe.DurationSeconds, EvidenceSubstageStatus.Completed,
                transcription.Status, provider.ProviderIdentity, provider.TranscriptionModel,
                provider.TranscriptionVersion, segments, warnings.Distinct(StringComparer.Ordinal).ToArray());
            await SaveComponentAsync(cacheContext, attachment, "media-transcript", TranscriptVersion(),
                TranscriptLimits(limits), RenderTranscript(segments), audio, null, warnings, cancellationToken);
        }
        var evidence = Bounded($"Audio: duration={Format(probe.DurationSeconds)}; probe={audio.ProbeStatus}; transcription={audio.TranscriptionStatus}\n{RenderTranscript(audio.Transcript)}",
            maximumExtractedCharacters, out var truncated);
        var usable = audio.Transcript.Count > 0;
        return new AttachmentProcessorOutput(
            usable && audio.TranscriptionStatus == EvidenceSubstageStatus.Completed && !truncated
                ? AttachmentProcessingStatus.Processed : usable ? AttachmentProcessingStatus.Partial : AttachmentProcessingStatus.Unavailable,
            AttachmentInspectionMode.AudioTranscript, evidence, truncated, false, null, null,
            usable ? null : "AudioTranscriptionUnavailable")
        {
            Audio = audio,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            AiInteractions = interactions
        };
    }

    private async ValueTask<AttachmentProcessorOutput> ProcessVideoAsync(DetectedAttachment attachment,
        string sourcePath, string workspace, MediaProbeResult probe, AttachmentLimits limits,
        int maximumExtractedCharacters, AttachmentProcessorCacheContext? cacheContext,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>(probe.Warnings);
        var interactions = new List<AiProviderInteractionUsage>();
        var transcript = Array.Empty<TranscriptSegment>();
        var transcriptionStatus = EvidenceSubstageStatus.NotApplicable;
        var audioExtractionStatus = EvidenceSubstageStatus.NotApplicable;
        if (probe.HasAudio)
        {
            var cachedTranscript = await GetComponentAsync(cacheContext, attachment, "media-transcript",
                TranscriptVersion(), TranscriptLimits(limits), cancellationToken);
            if (cachedTranscript?.Audio is { } cachedAudio)
            {
                transcript = cachedAudio.Transcript.ToArray();
                transcriptionStatus = EvidenceSubstageStatus.Reused;
                audioExtractionStatus = EvidenceSubstageStatus.Reused;
            }
            else
            {
                var extraction = await mediaTool.ExtractAudioAsync(sourcePath, workspace, limits, cancellationToken);
                audioExtractionStatus = extraction.Status;
                warnings.AddRange(extraction.Warnings);
                if (extraction.AudioPath is not null)
                {
                    var transcription = await provider.TranscribeAsync(new AudioTranscriptionRequest(extraction.AudioPath,
                        "audio/wav", limits.MaximumTranscriptCharacters,
                        TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds)), cancellationToken);
                    if (transcription.Interaction is not null) interactions.Add(transcription.Interaction);
                    transcript = RedactSegments(transcription.Segments, limits.MaximumTranscriptCharacters, warnings).ToArray();
                    transcriptionStatus = transcription.Status;
                    warnings.AddRange(transcription.Warnings);
                    var audio = new AudioEvidenceMetadata(probe.DurationSeconds, EvidenceSubstageStatus.Completed,
                        transcriptionStatus, provider.ProviderIdentity, provider.TranscriptionModel,
                        provider.TranscriptionVersion, transcript, warnings.Distinct(StringComparer.Ordinal).ToArray());
                    await SaveComponentAsync(cacheContext, attachment, "media-transcript", TranscriptVersion(),
                        TranscriptLimits(limits), RenderTranscript(transcript), audio, null, warnings, cancellationToken);
                }
            }
        }
        else warnings.Add("VideoHasNoAudioStream");

        var observations = Array.Empty<VisualObservation>();
        var frames = Array.Empty<ExtractedMediaFrame>();
        var frameStatus = EvidenceSubstageStatus.NotApplicable;
        var visionStatus = EvidenceSubstageStatus.NotApplicable;
        FrameSamplingMetadata? sampling = null;
        SelectedScreenshotContent[] selected;
        var cachedVisual = await GetComponentAsync(cacheContext, attachment, "media-vision",
            VisualVersion(), VisualLimits(limits), cancellationToken);
        if (cachedVisual?.Video is { } cachedVideo)
        {
            observations = cachedVideo.VisualObservations.ToArray();
            sampling = cachedVideo.FrameSampling;
            frameStatus = EvidenceSubstageStatus.Reused;
            visionStatus = EvidenceSubstageStatus.Reused;
            selected = await LoadCachedScreenshotsAsync(cacheContext!, cachedVisual, limits, cancellationToken);
            if (selected.Length == 0 && observations.Length > 0)
            {
                var extracted = await mediaTool.ExtractFramesAsync(sourcePath, workspace, probe, limits, cancellationToken);
                frames = extracted.Frames.ToArray();
                warnings.AddRange(extracted.Warnings);
                frameStatus = extracted.Status;
                selected = SelectScreenshots(frames, observations, limits).ToArray();
                await SaveVisualComponentAsync(cacheContext, attachment, cachedVideo, selected, limits,
                    warnings, cancellationToken);
            }
        }
        else
        {
            var extracted = await mediaTool.ExtractFramesAsync(sourcePath, workspace, probe, limits, cancellationToken);
            frameStatus = extracted.Status;
            frames = extracted.Frames.ToArray();
            warnings.AddRange(extracted.Warnings);
            var found = new List<VisualObservation>();
            var uniqueHashes = new HashSet<string>(StringComparer.Ordinal);
            var failed = 0;
            foreach (var frame in frames)
            {
                var bytes = await File.ReadAllBytesAsync(frame.Path, cancellationToken);
                var hash = AnalysisFingerprint.Sha256(bytes);
                if (!uniqueHashes.Add(hash)) continue;
                var vision = await provider.ObserveFrameAsync(new FrameVisionRequest(frame.TimestampSeconds,
                    "image/jpeg", bytes, TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds)), cancellationToken);
                if (vision.Interaction is not null) interactions.Add(vision.Interaction);
                warnings.AddRange(vision.Warnings);
                if (!string.IsNullOrWhiteSpace(vision.Observation))
                    found.Add(new VisualObservation(frame.TimestampSeconds, redactor.Redact(vision.Observation).Content,
                        provider.ProviderIdentity, provider.VisionModel, provider.VisionVersion, hash));
                else failed++;
            }
            observations = found.ToArray();
            visionStatus = observations.Length == 0 ? EvidenceSubstageStatus.Unavailable :
                failed > 0 ? EvidenceSubstageStatus.Partial : EvidenceSubstageStatus.Completed;
            sampling = new FrameSamplingMetadata(SamplingVersion, extracted.IntendedFrames,
                observations.Length, extracted.Status != EvidenceSubstageStatus.Completed || failed > 0);
            var component = new VideoEvidenceMetadata(probe.DurationSeconds, probe.Width, probe.Height,
                mediaTool.Version, [], observations, sampling)
            {
                AudioPresent = probe.HasAudio,
                FrameExtractionStatus = frameStatus,
                VisualAnalysisStatus = visionStatus,
                VisionProvider = provider.ProviderIdentity,
                VisionModel = provider.VisionModel,
                VisionVersion = provider.VisionVersion,
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
            };
            selected = SelectScreenshots(frames, observations, limits).ToArray();
            await SaveVisualComponentAsync(cacheContext, attachment, component, selected, limits,
                warnings, cancellationToken);
        }

        var video = new VideoEvidenceMetadata(probe.DurationSeconds, probe.Width, probe.Height, mediaTool.Version,
            transcript, observations, sampling)
        {
            AudioPresent = probe.HasAudio,
            ProbeStatus = EvidenceSubstageStatus.Completed,
            AudioExtractionStatus = audioExtractionStatus,
            TranscriptionStatus = transcriptionStatus,
            FrameExtractionStatus = frameStatus,
            VisualAnalysisStatus = visionStatus,
            ScreenshotSelectionStatus = selected.Length > 0 ? EvidenceSubstageStatus.Completed : EvidenceSubstageStatus.Unavailable,
            TranscriptionProvider = provider.ProviderIdentity,
            TranscriptionModel = provider.TranscriptionModel,
            TranscriptionVersion = provider.TranscriptionVersion,
            VisionProvider = provider.ProviderIdentity,
            VisionModel = provider.VisionModel,
            VisionVersion = provider.VisionVersion,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };
        var text = $"Video: duration={Format(probe.DurationSeconds)}; dimensions={probe.Width}x{probe.Height}; audio={probe.HasAudio}; probe={video.ProbeStatus}\n" +
                   $"Transcript: {transcriptionStatus}\n{RenderTranscript(transcript)}\n" +
                   $"Visual analysis: {visionStatus}; intended={sampling?.FramesConsidered ?? 0}; inspected={sampling?.FramesInspected ?? 0}\n{RenderObservations(observations)}";
        var evidence = Bounded(text, maximumExtractedCharacters, out var truncated);
        var usable = transcript.Length > 0 || observations.Length > 0;
        var fullyProcessed = (!probe.HasAudio || transcriptionStatus is EvidenceSubstageStatus.Completed or EvidenceSubstageStatus.Reused) &&
                             visionStatus is EvidenceSubstageStatus.Completed or EvidenceSubstageStatus.Reused &&
                             frameStatus is EvidenceSubstageStatus.Completed or EvidenceSubstageStatus.Reused && !truncated;
        return new AttachmentProcessorOutput(
            fullyProcessed ? AttachmentProcessingStatus.Processed : usable ? AttachmentProcessingStatus.Partial : AttachmentProcessingStatus.Unavailable,
            AttachmentInspectionMode.VideoComposite, evidence, truncated || !fullyProcessed, true,
            null, null, usable ? null : "VideoEvidenceUnavailable")
        {
            Video = video,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            SelectedScreenshots = selected,
            AiInteractions = interactions
        };
    }

    private async Task<SelectedScreenshotContent[]> LoadCachedScreenshotsAsync(
        AttachmentProcessorCacheContext context, AttachmentEvidenceArtifact artifact, AttachmentLimits limits,
        CancellationToken cancellationToken)
    {
        var selected = new List<SelectedScreenshotContent>();
        long retainedBytes = 0;
        foreach (var metadata in artifact.SelectedKeyScreenshots.OrderBy(item => item.TimestampSeconds)
                     .Take(Math.Min(6, limits.MaximumSelectedVideoScreenshots)))
        {
            if (metadata.SizeBytes > limits.MaximumRetainedScreenshotBytes - retainedBytes) break;
            var stored = await context.Cache.GetScreenshotAsync(metadata.StorageReference,
                context.Options.Organization, context.Options.Project, context.Options.CreatedAtUtc, cancellationToken);
            if (stored is null) continue;
            selected.Add(new SelectedScreenshotContent(metadata.TimestampSeconds, metadata.Width, metadata.Height,
                metadata.Observation, metadata.Provenance, stored.Content));
            retainedBytes += stored.Content.LongLength;
        }
        return selected.ToArray();
    }

    private async Task SaveVisualComponentAsync(AttachmentProcessorCacheContext? context,
        DetectedAttachment attachment, VideoEvidenceMetadata video, IReadOnlyList<SelectedScreenshotContent> selected,
        AttachmentLimits limits, List<string> warnings, CancellationToken cancellationToken)
    {
        if (context is null) return;
        var identity = "media-vision";
        var version = VisualVersion();
        var limitsFingerprint = VisualLimits(limits);
        var artifactId = AnalysisFingerprint.Artifact(context.Options.Organization, context.Options.Project,
            attachment.AttachmentId, context.ContentSha256, identity, version, limitsFingerprint);
        try
        {
            var artifact = new AttachmentEvidenceArtifact(artifactId, context.Options.Organization,
                context.Options.Project, attachment.AttachmentId, redactor.Redact(attachment.Name).Content,
                attachment.MediaType, attachment.Content.LongLength, context.ContentSha256, identity, version,
                AnalysisContextVersions.NormalizedEvidence, limitsFingerprint, RenderObservations(video.VisualObservations),
                AttachmentProcessingStatus.Processed, AttachmentInspectionMode.VideoComposite, false, true,
                null, null, null, warnings.Distinct(StringComparer.Ordinal).ToArray(),
                context.Options.CreatedAtUtc, context.Options.ExpiresAtUtc)
            { Video = video };
            await context.Cache.SaveAttachmentAsync(artifact, cancellationToken);
            var metadata = new List<SelectedKeyScreenshot>();
            foreach (var screenshot in selected)
            {
                var hash = AnalysisFingerprint.Sha256(screenshot.Content.Span);
                var id = AnalysisFingerprint.Sha256($"{artifactId}|{ScreenshotSelectionVersion}|{screenshot.TimestampSeconds:F3}|{hash}");
                var item = new SelectedKeyScreenshot(attachment.AttachmentId, context.ContentSha256,
                    screenshot.TimestampSeconds, hash, screenshot.Width, screenshot.Height,
                    screenshot.Content.Length, screenshot.Provenance, id, context.Options.ExpiresAtUtc)
                {
                    Observation = redactor.Redact(screenshot.Observation).Content,
                    PipelineVersion = ScreenshotSelectionVersion
                };
                await context.Cache.SaveScreenshotAsync(new SelectedKeyScreenshotArtifact(id, artifactId,
                    context.Options.Organization, context.Options.Project, item, "image/jpeg",
                    screenshot.Content.ToArray(), context.Options.ExpiresAtUtc), cancellationToken);
                metadata.Add(item);
            }
            await context.Cache.SaveAttachmentAsync(artifact with
            {
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
                SelectedKeyScreenshots = metadata
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { warnings.Add("EvidenceCachePersistenceFailed"); }
    }

    private async Task<AttachmentEvidenceArtifact?> GetComponentAsync(AttachmentProcessorCacheContext? context,
        DetectedAttachment attachment, string identity, string version, string limitsFingerprint,
        CancellationToken cancellationToken)
    {
        if (context is null || context.Options.ForceFresh) return null;
        var id = AnalysisFingerprint.Artifact(context.Options.Organization, context.Options.Project,
            attachment.AttachmentId, context.ContentSha256, identity, version, limitsFingerprint);
        return await context.Cache.GetAttachmentAsync(id, context.Options.Organization, context.Options.Project,
            context.Options.CreatedAtUtc, cancellationToken);
    }

    private async Task SaveComponentAsync(AttachmentProcessorCacheContext? context, DetectedAttachment attachment,
        string identity, string version, string limitsFingerprint, string evidence, AudioEvidenceMetadata? audio,
        VideoEvidenceMetadata? video, List<string> warnings, CancellationToken cancellationToken)
    {
        if (context is null) return;
        var id = AnalysisFingerprint.Artifact(context.Options.Organization, context.Options.Project,
            attachment.AttachmentId, context.ContentSha256, identity, version, limitsFingerprint);
        try
        {
            await context.Cache.SaveAttachmentAsync(new AttachmentEvidenceArtifact(id, context.Options.Organization,
                context.Options.Project, attachment.AttachmentId, redactor.Redact(attachment.Name).Content,
                attachment.MediaType, attachment.Content.LongLength, context.ContentSha256, identity, version,
                AnalysisContextVersions.NormalizedEvidence, limitsFingerprint, evidence,
                AttachmentProcessingStatus.Processed,
                identity == "media-transcript" ? AttachmentInspectionMode.AudioTranscript : AttachmentInspectionMode.VideoComposite,
                false, identity == "media-vision", null, null, null, warnings.Distinct(StringComparer.Ordinal).ToArray(),
                context.Options.CreatedAtUtc, context.Options.ExpiresAtUtc)
            { Audio = audio, Video = video }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { warnings.Add("EvidenceCachePersistenceFailed"); }
    }

    private IReadOnlyList<TranscriptSegment> RedactSegments(IReadOnlyList<TranscriptSegment> source, int maximumCharacters,
        List<string> warnings)
    {
        var result = new List<TranscriptSegment>();
        var used = 0;
        foreach (var segment in source.OrderBy(item => item.StartSeconds))
        {
            var text = redactor.Redact(segment.Text).Content.Trim();
            if (used + text.Length > maximumCharacters)
            {
                var remaining = Math.Max(0, maximumCharacters - used);
                if (remaining > 0) result.Add(segment with { Text = text[..Math.Min(text.Length, remaining)] });
                warnings.Add("TranscriptCharacterLimitExceeded");
                break;
            }
            used += text.Length;
            result.Add(segment with { Text = text });
        }
        return result;
    }

    private static IEnumerable<SelectedScreenshotContent> SelectScreenshots(
        IReadOnlyList<ExtractedMediaFrame> frames, IReadOnlyList<VisualObservation> observations, AttachmentLimits limits)
    {
        var candidates = from observation in observations
                         join frame in frames on observation.TimestampSeconds equals frame.TimestampSeconds
                         let score = Relevance(observation.Observation) + (frame.SceneChange ? 2 : 0)
                         orderby score descending, observation.TimestampSeconds
                         select new { frame, observation };
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var retainedBytes = 0L;
        foreach (var candidate in candidates)
        {
            var bytes = File.ReadAllBytes(candidate.frame.Path);
            var hash = AnalysisFingerprint.Sha256(bytes);
            if (!hashes.Add(hash) || bytes.LongLength > limits.MaximumRetainedScreenshotBytes - retainedBytes) continue;
            yield return new SelectedScreenshotContent(candidate.frame.TimestampSeconds, candidate.frame.Width,
                candidate.frame.Height, candidate.observation.Observation,
                $"{SamplingVersion};{ScreenshotSelectionVersion};frame={hash}", bytes);
            retainedBytes += bytes.LongLength;
            if (hashes.Count >= Math.Min(6, limits.MaximumSelectedVideoScreenshots)) yield break;
        }
    }

    private static int Relevance(string observation)
    {
        var value = observation.ToLowerInvariant();
        return new[] { "error", "failed", "incorrect", "expected", "actual", "customer", "order", "id", "configuration", "before", "after" }
            .Count(value.Contains);
    }

    private string TranscriptVersion() => $"{mediaTool.Version}:{provider.ProviderIdentity}:{provider.TranscriptionModel}:{provider.TranscriptionVersion}:chunk-v1";
    private string VisualVersion() => $"{mediaTool.Version}:{SamplingVersion}:{provider.ProviderIdentity}:{provider.VisionModel}:{provider.VisionVersion}:image-v1";
    private static string TranscriptLimits(AttachmentLimits limits) => AnalysisFingerprint.Sha256($"{limits.MaximumMediaDurationSeconds}|{limits.MaximumTranscriptCharacters}|{limits.MediaProcessTimeoutSeconds}");
    private static string VisualLimits(AttachmentLimits limits) => AnalysisFingerprint.Sha256($"{limits.MaximumMediaDurationSeconds}|{limits.MaximumMediaDimension}|{limits.MaximumDecodedPixels}|{limits.MaximumSampledFrames}|{limits.MaximumFrameBytes}|{limits.MediaProcessTimeoutSeconds}");
    private static string RenderTranscript(IEnumerable<TranscriptSegment> segments) => string.Join('\n', segments.Select(segment =>
        $"[{Format(segment.StartSeconds)} - {Format(segment.EndSeconds)}] {segment.Text}"));
    private static string RenderObservations(IEnumerable<VisualObservation> observations) => string.Join('\n', observations.Select(observation =>
        $"[{Format(observation.TimestampSeconds)}] {observation.Observation}"));
    private static string Format(double? seconds) => seconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown";
    private static string Bounded(string value, int maximum, out bool truncated)
    {
        truncated = value.Length > maximum;
        return truncated ? value[..maximum] : value;
    }
    private static bool IsAudio(string mediaType) => mediaType is "audio/mpeg" or "audio/wav" or "audio/mp4";
    private static bool IsVideo(string mediaType) => mediaType is "video/mp4" or "video/quicktime" or "video/webm";
    private static string Extension(string mediaType) => mediaType switch
    {
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        "audio/mp4" => ".m4a",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        _ => ".mp4"
    };
}
