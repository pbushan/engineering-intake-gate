using IntakeGate.Application.Configuration;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class PolicyFingerprintTests
{
    [Fact]
    public void AUD_005_SameEffectivePolicyProducesStableFingerprint()
    {
        var policy = CreatePolicy("Guidance");
        Assert.Equal(PolicyFingerprint.Create(policy), PolicyFingerprint.Create(policy));
    }

    [Fact]
    public void AUD_005_MeaningfulPolicyChangeProducesDifferentFingerprint()
    {
        Assert.NotEqual(PolicyFingerprint.Create(CreatePolicy("Guidance A")), PolicyFingerprint.Create(CreatePolicy("Guidance B")));
    }

    private static IntakePolicy CreatePolicy(string guidance) => new(
        new PolicyIdentity("policy", "1"),
        [new IntakeCriterion("criterion", "Criterion", "Description", CriterionApplicability.Required, new NotApplicablePolicy(false, false), guidance)]);
}
