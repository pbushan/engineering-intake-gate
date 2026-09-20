using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IntakeGate.Application.Audit;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evaluation;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.Secrets;

namespace IntakeGate.Infrastructure.Ai;

public sealed class ConfiguredAttachmentEvidenceAiProvider(
    HttpClient httpClient,
    DeploymentConfiguration configuration,
    IProviderCredentialResolver credentials) : IAttachmentEvidenceAiProvider
{
    private const string OpenAiTranscriptionModel = "gpt-4o-mini-transcribe";
    public string ProviderIdentity => configuration.Profile.Ai.Provider;
    public string TranscriptionModel => ProviderIdentity == "openai" ? OpenAiTranscriptionModel : "unsupported";
    public string TranscriptionVersion => "timestamped-transcription-v1";
    public string VisionModel => configuration.Profile.Ai.Model;
    public string VisionVersion => "factual-frame-observation-v1";

    public async Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (ProviderIdentity != "openai")
            return new(EvidenceSubstageStatus.Unavailable, [], ["TranscriptionUnsupportedByConfiguredProvider"]);
        var credential = Credential();
        if (credential is null) return new(EvidenceSubstageStatus.Unavailable, [], ["ProviderCredentialMissing"]);
        using var message = AiProviderHttp.OpenAi(HttpMethod.Post,
            new Uri(AiProviderHttp.OpenAiApiBase, "audio/transcriptions"), new SecretValue(credential));
        await using var stream = new FileStream(request.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(OpenAiTranscriptionModel), "model");
        multipart.Add(new StringContent("verbose_json"), "response_format");
        multipart.Add(new StringContent("segment"), "timestamp_granularities[]");
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(file, "file", "evidence.wav");
        message.Content = multipart;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var requestId = ProviderJson.Header(response, "x-request-id");
            var interaction = new AiProviderInteractionUsage(1, ProviderIdentity, OpenAiTranscriptionModel,
                ProviderIdentity, OpenAiTranscriptionModel, requestId, null);
            if (!response.IsSuccessStatusCode)
                return new(EvidenceSubstageStatus.Failed, [], [$"Transcription{ProviderJson.Failure(response.StatusCode).SafeCategory}"], interaction);
            using var document = JsonDocument.Parse(body);
            var segments = new List<TranscriptSegment>();
            if (document.RootElement.TryGetProperty("segments", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (!TryDouble(item, "start", out var start) || !TryDouble(item, "end", out var end) ||
                        !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;
                    segments.Add(new(start, end, text.GetString()?.Trim() ?? string.Empty, ProviderIdentity,
                        OpenAiTranscriptionModel, TranscriptionVersion, []));
                }
            }
            if (segments.Count == 0 && document.RootElement.TryGetProperty("text", out var transcript) &&
                transcript.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(transcript.GetString()))
                segments.Add(new(0, 0, transcript.GetString()!.Trim(), ProviderIdentity,
                    OpenAiTranscriptionModel, TranscriptionVersion, ["TranscriptSegmentTimestampsUnavailable"]));
            var status = segments.Count > 0 ? EvidenceSubstageStatus.Completed : EvidenceSubstageStatus.Unavailable;
            return new(status, segments, status == EvidenceSubstageStatus.Completed ? [] : ["NoSpeechDetected"], interaction);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(EvidenceSubstageStatus.Failed, [], ["TranscriptionProviderTimeout"]);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(EvidenceSubstageStatus.Failed, [], ["TranscriptionProviderFailure"]); }
    }

    public async Task<FrameVisionResult> ObserveFrameAsync(FrameVisionRequest request,
        CancellationToken cancellationToken)
    {
        var credential = Credential();
        if (credential is null) return new(EvidenceSubstageStatus.Unavailable, null, ["ProviderCredentialMissing"]);
        return ProviderIdentity switch
        {
            "openai" => await ObserveOpenAiAsync(request, credential, cancellationToken),
            "anthropic" => await ObserveAnthropicAsync(request, credential, cancellationToken),
            _ => new(EvidenceSubstageStatus.Unavailable, null, ["VisionUnsupportedByConfiguredProvider"])
        };
    }

    private async Task<FrameVisionResult> ObserveOpenAiAsync(FrameVisionRequest request, string credential,
        CancellationToken cancellationToken)
    {
        using var message = AiProviderHttp.OpenAi(HttpMethod.Post, new Uri(AiProviderHttp.OpenAiApiBase, "responses"),
            new SecretValue(credential));
        message.Content = JsonContent.Create(new
        {
            model = VisionModel,
            store = false,
            instructions = ObservationInstruction,
            input = new[] { new { role = "user", content = new object[]
            {
                new { type = "input_text", text = $"Observe the frame at {request.TimestampSeconds:0.###} seconds." },
                new { type = "input_image", image_url = $"data:{request.MediaType};base64,{Convert.ToBase64String(request.Content.Span)}" }
            } } }
        }, options: ProviderJson.SerializerOptions);
        return await SendVisionAsync(message, request, ParseOpenAiObservation, cancellationToken);
    }

    private async Task<FrameVisionResult> ObserveAnthropicAsync(FrameVisionRequest request, string credential,
        CancellationToken cancellationToken)
    {
        using var message = AiProviderHttp.Anthropic(HttpMethod.Post, new Uri(AiProviderHttp.AnthropicApiBase, "messages"),
            new SecretValue(credential));
        message.Content = JsonContent.Create(new
        {
            model = VisionModel,
            max_tokens = 400,
            system = ObservationInstruction,
            messages = new[] { new { role = "user", content = new object[]
            {
                new { type = "text", text = $"Observe the frame at {request.TimestampSeconds:0.###} seconds." },
                new { type = "image", source = new { type = "base64", media_type = request.MediaType,
                    data = Convert.ToBase64String(request.Content.Span) } }
            } } }
        }, options: ProviderJson.SerializerOptions);
        return await SendVisionAsync(message, request, ParseAnthropicObservation, cancellationToken);
    }

    private async Task<FrameVisionResult> SendVisionAsync(HttpRequestMessage message, FrameVisionRequest request,
        Func<string, (string? Text, string? RequestId, string? Model, TokenUsage? Usage)> parser,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var parsed = parser(body);
            var interaction = new AiProviderInteractionUsage(1, ProviderIdentity, VisionModel, ProviderIdentity,
                parsed.Model, parsed.RequestId, parsed.Usage);
            if (!response.IsSuccessStatusCode)
                return new(EvidenceSubstageStatus.Failed, null, [$"Vision{ProviderJson.Failure(response.StatusCode).SafeCategory}"], interaction);
            return string.IsNullOrWhiteSpace(parsed.Text)
                ? new(EvidenceSubstageStatus.Unavailable, null, ["NoVisualObservationProduced"], interaction)
                : new(EvidenceSubstageStatus.Completed, parsed.Text.Trim(), [], interaction);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(EvidenceSubstageStatus.Failed, null, ["VisionProviderTimeout"]); }
        catch (OperationCanceledException) { throw; }
        catch { return new(EvidenceSubstageStatus.Failed, null, ["VisionProviderFailure"]); }
    }

    private static (string?, string?, string?, TokenUsage?) ParseOpenAiObservation(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string? text = root.TryGetProperty("output_text", out var outputText) ? outputText.GetString() : null;
            if (text is null && root.TryGetProperty("output", out var output))
                text = output.EnumerateArray().SelectMany(item => item.GetProperty("content").EnumerateArray())
                    .FirstOrDefault(item => item.TryGetProperty("text", out _)).TryGetProperty("text", out var node) ? node.GetString() : null;
            var metadata = ProviderJson.Metadata(body, null, false);
            return (text, metadata.ProviderRequestId, metadata.ProviderReportedModel, metadata.TokenUsage);
        }
        catch { return (null, null, null, null); }
    }

    private static (string?, string?, string?, TokenUsage?) ParseAnthropicObservation(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var text = root.GetProperty("content").EnumerateArray()
                .FirstOrDefault(item => item.TryGetProperty("text", out _)).TryGetProperty("text", out var node) ? node.GetString() : null;
            var metadata = ProviderJson.Metadata(body, null, true);
            return (text, metadata.ProviderRequestId, metadata.ProviderReportedModel, metadata.TokenUsage);
        }
        catch { return (null, null, null, null); }
    }

    private string? Credential()
    {
        var value = credentials.Resolve(configuration.Profile.Ai.Authentication.EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var node) && node.TryGetDouble(out value) && value >= 0;
    }

    private const string ObservationInstruction = "Describe only factual, engineering-relevant visible evidence in this single screenshot. Include exact visible error text, identifiers, field values, navigation state, calculations, configuration, and before/after state when present. Do not decide PASS/FAIL, severity, priority, ownership, root cause, or solution. If nothing relevant is visible, say so briefly.";
}
