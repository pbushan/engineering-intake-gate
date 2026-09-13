using System.Reflection;

namespace IntakeGate.Application.Evaluation;

/// <summary>The versioned prompt artifact is intentionally independent of orchestration code.</summary>
public static class EvaluatorPrompt
{
    public const string Version = "intake-evaluator-v1";
    private const string ResourceName = "IntakeGate.Application.Evaluation.Prompts.intake-evaluator-v1.md";

    public static string Content { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(EvaluatorPrompt).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded evaluator prompt '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
