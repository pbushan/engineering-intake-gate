namespace IntakeGate.Application.Configuration;

public class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(string message)
        : base(message)
    {
    }

    public ConfigurationValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>File access or serialization failure, distinct from semantic configuration validation.</summary>
public sealed class ConfigurationDocumentException : ConfigurationValidationException
{
    public ConfigurationDocumentException(string message)
        : base(message)
    {
    }

    public ConfigurationDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
