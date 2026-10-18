using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureBootstrapRejectionTests
{
    [Theory]
    [InlineData((int)QualificationScenario.RejectedBootstrapMaterial,
        "helper.windows.bootstrap.descriptor-rejected", true)]
    [InlineData((int)QualificationScenario.RejectedBootstrapMaterial,
        "helper.windows.bootstrap.protocol-rejected", false)]
    [InlineData((int)QualificationScenario.RejectedBootstrapMaterial,
        "helper.windows.bootstrap.section-map-failed", false)]
    [InlineData((int)QualificationScenario.RejectedBootstrapMaterial,
        "helper.windows.bootstrap.transport-broken", false)]
    [InlineData((int)QualificationScenario.RejectedPartialHandshake,
        "helper.windows.bootstrap.fixture-abort", true)]
    [InlineData((int)QualificationScenario.RejectedPartialHandshake,
        "helper.windows.bootstrap.descriptor-rejected", false)]
    [InlineData((int)QualificationScenario.AuthenticatedBootstrap,
        "helper.windows.bootstrap.descriptor-rejected", false)]
    public void OnlyExpectedStageAndCodeCountAsFixtureRejection(
        int scenarioValue, string failureCode, bool expected)
    {
        Assert.Equal(expected, FixtureBootstrapRejection.IsExpected(
            (QualificationScenario)scenarioValue, failureCode));
    }
}
