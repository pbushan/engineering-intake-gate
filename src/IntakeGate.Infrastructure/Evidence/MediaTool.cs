using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;

namespace IntakeGate.Infrastructure.Evidence;

public sealed record MediaProbeResult(bool Succeeded, double? DurationSeconds, int? Width, int? Height,
    bool HasAudio, string? FailureCategory, IReadOnlyList<string> Warnings);

public sealed record ExtractedMediaFrame(double TimestampSeconds, string Path, int Width, int Height,
    bool SceneChange);

public sealed record FrameExtractionResult(EvidenceSubstageStatus Status,
    IReadOnlyList<ExtractedMediaFrame> Frames, int IntendedFrames, IReadOnlyList<string> Warnings);

public interface IMediaTool
{
    string Version { get; }
    Task<MediaProbeResult> ProbeAsync(string inputPath, AttachmentLimits limits, CancellationToken cancellationToken);
    Task<(EvidenceSubstageStatus Status, string? AudioPath, IReadOnlyList<string> Warnings)> ExtractAudioAsync(
        string inputPath, string workspace, AttachmentLimits limits, CancellationToken cancellationToken);
    Task<FrameExtractionResult> ExtractFramesAsync(string inputPath, string workspace, MediaProbeResult probe,
        AttachmentLimits limits, CancellationToken cancellationToken);
}

public sealed partial class FfmpegMediaTool(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe") : IMediaTool
{
    private const int MaximumProcessOutputCharacters = 1_000_000;
    public string Version => "ffmpeg-bounded-v1";

    public async Task<MediaProbeResult> ProbeAsync(string inputPath, AttachmentLimits limits, CancellationToken cancellationToken)
    {
        var result = await RunAsync(ffprobePath,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height", "-of", "json", inputPath],
            TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds), cancellationToken);
        if (!result.Succeeded) return new(false, null, null, null, false, result.Category, [result.Category]);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            double? duration = null;
            if (document.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationNode) &&
                double.TryParse(durationNode.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                duration = parsed;
            var audio = false;
            int? width = null;
            int? height = null;
            if (document.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var typeNode) ? typeNode.GetString() : null;
                    if (type == "audio") audio = true;
                    if (type == "video")
                    {
                        if (stream.TryGetProperty("width", out var widthNode) && widthNode.TryGetInt32(out var w)) width = w;
                        if (stream.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var h)) height = h;
                    }
                }
            }
            if (duration is <= 0 || duration > limits.MaximumMediaDurationSeconds)
                return new(false, duration, width, height, audio, "MediaDurationLimitExceeded", ["MediaDurationLimitExceeded"]);
            if (width > limits.MaximumMediaDimension || height > limits.MaximumMediaDimension ||
                width is not null && height is not null && (long)width * height > limits.MaximumDecodedPixels)
                return new(false, duration, width, height, audio, "MediaDimensionLimitExceeded", ["MediaDimensionLimitExceeded"]);
            return new(true, duration, width, height, audio, null, []);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(false, null, null, null, false, "MalformedMediaProbeOutput", ["MalformedMediaProbeOutput"]);
        }
    }

    public async Task<(EvidenceSubstageStatus Status, string? AudioPath, IReadOnlyList<string> Warnings)> ExtractAudioAsync(
        string inputPath, string workspace, AttachmentLimits limits, CancellationToken cancellationToken)
    {
        var output = Path.Combine(workspace, $"audio-{Guid.NewGuid():N}.wav");
        var result = await RunAsync(ffmpegPath,
            ["-nostdin", "-v", "error", "-i", inputPath, "-t", limits.MaximumMediaDurationSeconds.ToString(CultureInfo.InvariantCulture),
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", output],
            TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds), cancellationToken);
        if (!result.Succeeded || !File.Exists(output) || new FileInfo(output).Length == 0)
            return (EvidenceSubstageStatus.Failed, null, [result.Category]);
        return (EvidenceSubstageStatus.Completed, output, []);
    }

    public async Task<FrameExtractionResult> ExtractFramesAsync(string inputPath, string workspace,
        MediaProbeResult probe, AttachmentLimits limits, CancellationToken cancellationToken)
    {
        if (probe.DurationSeconds is not { } duration || probe.Width is null || probe.Height is null)
            return new(EvidenceSubstageStatus.NotApplicable, [], 0, ["VideoStreamUnavailable"]);
        var count = Math.Max(2, Math.Min(limits.MaximumSampledFrames, 12));
        var timestamps = Enumerable.Range(0, count)
            .Select(index => index == 0 ? Math.Min(.5, duration / 2) :
                index == count - 1 ? Math.Max(0, duration - .5) : duration * index / (count - 1d))
            .Select(value => Math.Round(value, 3)).Distinct().Order().ToArray();
        var frames = new List<ExtractedMediaFrame>();
        var warnings = new List<string>();
        foreach (var timestamp in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = Path.Combine(workspace, $"frame-{frames.Count:D3}-{Guid.NewGuid():N}.jpg");
            var result = await RunAsync(ffmpegPath,
                ["-nostdin", "-v", "error", "-ss", timestamp.ToString("0.###", CultureInfo.InvariantCulture), "-i", inputPath,
                    "-frames:v", "1", "-vf", $"scale='min({limits.MaximumMediaDimension},iw)':-2", "-q:v", "3", "-y", output],
                TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds), cancellationToken);
            if (!result.Succeeded || !File.Exists(output))
            {
                warnings.Add("FrameExtractionFailed");
                continue;
            }
            var info = new FileInfo(output);
            if (info.Length <= 0 || info.Length > limits.MaximumFrameBytes)
            {
                warnings.Add("FrameByteLimitExceeded");
                File.Delete(output);
                continue;
            }
            frames.Add(new(timestamp, output, probe.Width.Value, probe.Height.Value, false));
        }

        // Scene-change samples complement fixed intervals. The argument list is passed directly;
        // neither paths nor filter text cross a shell.
        var scenePattern = Path.Combine(workspace, $"scene-{Guid.NewGuid():N}-%03d.jpg");
        var sceneResult = await RunAsync(ffmpegPath,
            ["-nostdin", "-v", "info", "-i", inputPath, "-vf",
                $"select=gt(scene\\,0.35),showinfo,scale='min({limits.MaximumMediaDimension},iw)':-2",
                "-vsync", "vfr", "-frames:v", Math.Max(0, limits.MaximumSampledFrames - frames.Count).ToString(CultureInfo.InvariantCulture),
                "-q:v", "3", "-y", scenePattern], TimeSpan.FromSeconds(limits.MediaProcessTimeoutSeconds), cancellationToken);
        if (sceneResult.Succeeded)
        {
            var sceneTimes = PtsTimeRegex().Matches(sceneResult.StandardError).Select(match =>
                double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
            var sceneFiles = Directory.GetFiles(workspace, Path.GetFileName(scenePattern).Replace("%03d", "*", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray();
            for (var index = 0; index < Math.Min(sceneTimes.Length, sceneFiles.Length) && frames.Count < limits.MaximumSampledFrames; index++)
            {
                if (new FileInfo(sceneFiles[index]).Length <= limits.MaximumFrameBytes)
                    frames.Add(new(sceneTimes[index], sceneFiles[index], probe.Width.Value, probe.Height.Value, true));
            }
        }
        else warnings.Add("SceneChangeSamplingUnavailable");

        var ordered = frames.OrderByDescending(frame => frame.SceneChange).ThenBy(frame => frame.TimestampSeconds)
            .Take(limits.MaximumSampledFrames).OrderBy(frame => frame.TimestampSeconds).ToArray();
        var status = ordered.Length == timestamps.Length && warnings.Count == 0
            ? EvidenceSubstageStatus.Completed
            : ordered.Length > 0 ? EvidenceSubstageStatus.Partial : EvidenceSubstageStatus.Failed;
        return new(status, ordered, timestamps.Length, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(false, string.Empty, string.Empty, "MediaProcessStartFailed");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
            try { await process.WaitForExitAsync(timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new(false, string.Empty, string.Empty, "MediaProcessTimeout");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return process.ExitCode == 0
                ? new(true, stdout, stderr, string.Empty)
                : new(false, stdout, stderr, "MediaProcessFailure");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryKill(process);
            return new(false, string.Empty, string.Empty, "MediaToolUnavailable");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4_096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            var remaining = MaximumProcessOutputCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private sealed record ProcessResult(bool Succeeded, string StandardOutput, string StandardError, string Category);

    [GeneratedRegex(@"pts_time:([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex PtsTimeRegex();
}
