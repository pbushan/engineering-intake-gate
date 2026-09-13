namespace IntakeGate.Infrastructure.Ai;

public interface IProviderCredentialResolver
{
    string? Resolve(string environmentVariableName);
}

public sealed class EnvironmentProviderCredentialResolver : IProviderCredentialResolver
{
    public string? Resolve(string environmentVariableName) =>
        string.IsNullOrWhiteSpace(environmentVariableName)
            ? null
            : Environment.GetEnvironmentVariable(environmentVariableName);
}
