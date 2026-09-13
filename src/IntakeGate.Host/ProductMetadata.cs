using System.Reflection;

namespace IntakeGate.Host;

internal static class ProductMetadata
{
    public const string ApplicationName = "Engineering Intake Gate";

    public static string Version =>
        typeof(ProductMetadata).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}
