using System.Diagnostics;
using System.Globalization;
using System.Text;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using SixLabors.ImageSharp;
using UglyToad.PdfPig;

namespace IntakeGate.Infrastructure.Evidence;

public sealed record RenderedPdfPage(int PageNumber, byte[] Content, int Width, int Height);

public interface IPdfPageRenderer
{
    string Version { get; }
    Task<(IReadOnlyList<RenderedPdfPage> Pages, IReadOnlyList<string> Warnings)> RenderAsync(
        byte[] pdf, IReadOnlyList<int> pageNumbers, AttachmentLimits limits, CancellationToken cancellationToken);
}

public sealed class UnavailablePdfPageRenderer : IPdfPageRenderer
{
    public string Version => "unavailable";
    public Task<(IReadOnlyList<RenderedPdfPage> Pages, IReadOnlyList<string> Warnings)> RenderAsync(
        byte[] pdf, IReadOnlyList<int> pageNumbers, AttachmentLimits limits, CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<RenderedPdfPage>, IReadOnlyList<string>)>(([], ["PdfRendererUnavailable"]));
}

public sealed class PopplerPdfPageRenderer(string executable = "pdftoppm") : IPdfPageRenderer
{
    public string Version => "poppler-page-render-v1";

    public async Task<(IReadOnlyList<RenderedPdfPage> Pages, IReadOnlyList<string> Warnings)> RenderAsync(
        byte[] pdf, IReadOnlyList<int> pageNumbers, AttachmentLimits limits, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"intake-gate-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        if (!OperatingSystem.IsWindows())
            new DirectoryInfo(workspace).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            var source = Path.Combine(workspace, $"source-{Guid.NewGuid():N}.pdf");
            await File.WriteAllBytesAsync(source, pdf, cancellationToken);
            var pages = new List<RenderedPdfPage>();
            var warnings = new List<string>();
            foreach (var pageNumber in pageNumbers.Distinct().Order())
            {
                var prefix = Path.Combine(workspace, $"page-{pageNumber}-{Guid.NewGuid():N}");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var argument in new[] { "-f", pageNumber.ToString(CultureInfo.InvariantCulture), "-l",
                             pageNumber.ToString(CultureInfo.InvariantCulture), "-singlefile", "-jpeg", "-r", "120", source, prefix })
                    start.ArgumentList.Add(argument);
                using var process = new Process { StartInfo = start };
                try
                {
                    if (!process.Start()) { warnings.Add("PdfRenderStartFailed"); continue; }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds));
                    var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
                    var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        TryKill(process);
                        await DrainAfterKillAsync(stderr, stdout);
                        warnings.Add("PdfRenderTimeout");
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        TryKill(process);
                        await DrainAfterKillAsync(stderr, stdout);
                        throw;
                    }
                    await Task.WhenAll(stderr, stdout);
                    var path = prefix + ".jpg";
                    if (process.ExitCode != 0 || !File.Exists(path)) { warnings.Add("PdfPageRenderFailed"); continue; }
                    var content = await File.ReadAllBytesAsync(path, cancellationToken);
                    if (content.LongLength > limits.MaximumFrameBytes) { warnings.Add("PdfRenderedPageByteLimitExceeded"); continue; }
                    var info = Image.Identify(content);
                    if (info is null || info.Width > limits.MaximumMediaDimension || info.Height > limits.MaximumMediaDimension ||
                        (long)info.Width * info.Height > limits.MaximumDecodedPixels)
                    { warnings.Add("PdfRenderedPageDimensionLimitExceeded"); continue; }
                    pages.Add(new(pageNumber, content, info.Width, info.Height));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { warnings.Add("PdfRendererUnavailable"); }
            }
            return (pages, warnings.Distinct(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 64 * 1024;
        var buffer = new char[4096];
        var result = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            var remaining = maximumCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(read, remaining));
        }
        return result.ToString();
    }

    private static async Task DrainAfterKillAsync(params Task<string>[] readers)
    {
        try { await Task.WhenAll(readers); }
        catch { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}

public sealed class PdfAttachmentProcessor : IAttachmentProcessor, IAttachmentLimitsFingerprintProvider
{
    private readonly IPdfPageRenderer renderer;
    private readonly IAttachmentEvidenceAiProvider provider;
    private readonly ISecretRedactor redactor;

    public PdfAttachmentProcessor() : this(new UnavailablePdfPageRenderer(),
        new UnavailableAttachmentEvidenceAiProvider(), new SecretRedactor())
    { }

    public PdfAttachmentProcessor(IPdfPageRenderer renderer, IAttachmentEvidenceAiProvider provider,
        ISecretRedactor redactor)
    {
        this.renderer = renderer;
        this.provider = provider;
        this.redactor = redactor;
    }

    public string ProcessorVersion => $"pdf-text-vision-v2:{renderer.Version}:{provider.VisionVersion}";
    public bool CanProcess(DetectedAttachment attachment) => attachment.MediaType == "application/pdf";

    public string CreateLimitsFingerprint(AttachmentLimits limits, int maximumExtractedCharacters) =>
        AnalysisFingerprint.Sha256($"{limits.MaximumPdfPages}|{limits.MaximumMediaDimension}|" +
            $"{limits.MaximumDecodedPixels}|{limits.MaximumFrameBytes}|{limits.MediaProcessTimeoutSeconds}|" +
            $"{maximumExtractedCharacters}");

    public async ValueTask<AttachmentProcessorOutput> ProcessAsync(DetectedAttachment attachment,
        AttachmentLimits limits, int maximumExtractedCharacters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = PdfDocument.Open(attachment.Content);
            var pagesAvailable = document.NumberOfPages;
            var pagesInspected = Math.Min(pagesAvailable, limits.MaximumPdfPages);
            var pageEvidence = new List<PdfPageEvidence>();
            var visualPages = new List<int>();
            for (var pageNumber = 1; pageNumber <= pagesInspected; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = document.GetPage(pageNumber).Text?.Trim() ?? string.Empty;
                var useful = text.Length >= 4;
                if (!useful) visualPages.Add(pageNumber);
                pageEvidence.Add(new(pageNumber, useful, false, useful ? text : string.Empty, []));
            }

            var warnings = new List<string>();
            var interactions = new List<AiProviderInteractionUsage>();
            if (visualPages.Count > 0)
            {
                var rendered = await renderer.RenderAsync(attachment.Content, visualPages, limits, cancellationToken);
                warnings.AddRange(rendered.Warnings);
                foreach (var page in rendered.Pages)
                {
                    var vision = await provider.ObserveFrameAsync(new FrameVisionRequest(page.PageNumber,
                        "image/jpeg", page.Content, TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds)), cancellationToken);
                    if (vision.Interaction is not null) interactions.Add(vision.Interaction);
                    warnings.AddRange(vision.Warnings);
                    var index = pageEvidence.FindIndex(candidate => candidate.PageNumber == page.PageNumber);
                    if (index >= 0 && !string.IsNullOrWhiteSpace(vision.Observation))
                        pageEvidence[index] = pageEvidence[index] with
                        {
                            VisuallyInspected = true,
                            Evidence = redactor.Redact(vision.Observation).Content,
                            Warnings = vision.Warnings
                        };
                }
            }

            var builder = new StringBuilder();
            foreach (var page in pageEvidence)
                builder.Append("[PDF page ").Append(page.PageNumber).Append(page.TextExtracted ? "; text" : "; vision")
                    .Append("]\n").Append(page.Evidence).Append('\n');
            var pagePartial = pagesInspected < pagesAvailable;
            var failedVisual = visualPages.Count > pageEvidence.Count(page => page.VisuallyInspected);
            var bounded = builder.ToString().TrimEnd();
            var characterPartial = bounded.Length > maximumExtractedCharacters;
            if (characterPartial) bounded = bounded[..maximumExtractedCharacters];
            if (pagePartial) warnings.Add("PdfPageLimitExceeded");
            if (failedVisual) warnings.Add("PdfVisualInspectionPartial");
            if (characterPartial) warnings.Add("EvidenceCharacterLimitExceeded");
            var partial = pagePartial || failedVisual || characterPartial;
            var metadata = new PdfEvidenceMetadata(pagesAvailable, pagesInspected,
                pageEvidence.Count(page => page.TextExtracted), pageEvidence.Count(page => page.VisuallyInspected),
                partial, pageEvidence, warnings.Distinct(StringComparer.Ordinal).ToArray());
            return new AttachmentProcessorOutput(
                partial ? AttachmentProcessingStatus.Partial : AttachmentProcessingStatus.Processed,
                metadata.VisuallyInspectedPages > 0 ? AttachmentInspectionMode.PdfTextAndVision : AttachmentInspectionMode.PdfText,
                bounded, partial, pagePartial, pagesAvailable, pagesInspected,
                failedVisual ? "PdfVisualInspectionPartial" : pagePartial ? "PdfPageLimitExceeded" :
                characterPartial ? "EvidenceCharacterLimitExceeded" : null)
            {
                Pdf = metadata,
                Warnings = metadata.Warnings,
                AiInteractions = interactions
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AttachmentProcessorOutput(AttachmentProcessingStatus.Error, AttachmentInspectionMode.None,
                string.Empty, false, false, null, null, "MalformedPdf");
        }
    }
}
