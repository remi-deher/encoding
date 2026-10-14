using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ContractTests
{
    [Fact]
    public void RelayContractsSegmentEligibilityInheritanceAndOldAgentCompatibility() => Harness.RelayContracts();

    [Fact]
    public void NativeConditionsEvaluateNestedGroupsAndMetadataSnapshots()
    {
        var (registry, validator, eligibility) = Harness.Services();
        ConditionScenarios.Run(registry, validator, eligibility);
    }

    [Fact]
    public async Task FlowSimulationCoversBranchesVariablesAndMediaInfoImport()
    {
        var (registry, validator, _) = Harness.Services();
        await FlowSimulationScenarios.RunAsync(registry, validator);
    }
}
