using System.Text.Json;
using IntakeGate.Application.Configuration;
using IntakeGate.Application.Evidence;
using IntakeGate.Application.WorkItems;
using Xunit;

namespace IntakeGate.UnitTests;

public sealed class WorkItemEligibilityEvaluatorTests
{
    [Fact]
    public void DISC_005_AC_14_ConfiguredScalarValueMatchesWithoutProviderSpecificLogic()
    {
        var item = Item(new RawWorkItemField("Generic.Impact", null, JsonSerializer.SerializeToElement("Urgent")));
        var match = new WorkItemEligibilityEvaluator().FindExclusion(item,
            [new ExclusionRule("configured-urgent", "Generic.Impact", ExclusionOperator.EqualsAny, ["urgent"])]);

        Assert.Equal(new ExclusionMatch("configured-urgent", "Generic.Impact"), match);
    }

    [Fact]
    public void DISC_005_MissingOrStructuredExclusionFieldDoesNotMatch()
    {
        var rules = new[] { new ExclusionRule("configured", "Generic.Missing", ExclusionOperator.EqualsAny, ["x"]) };
        Assert.Null(new WorkItemEligibilityEvaluator().FindExclusion(Item(), rules));

        var structured = Item(new RawWorkItemField("Generic.Missing", null,
            JsonSerializer.SerializeToElement(new { value = "x" })));
        Assert.Null(new WorkItemEligibilityEvaluator().FindExclusion(structured, rules));
    }

    private static RawWorkItem Item(params RawWorkItemField[] fields) => new("42", "9", "Generic", "Title", fields);
}
