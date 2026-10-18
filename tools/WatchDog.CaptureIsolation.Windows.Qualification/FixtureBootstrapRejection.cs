namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal static class FixtureBootstrapRejection
{
    internal static bool IsExpected(QualificationScenario scenario, string failureCode) =>
        scenario switch
        {
            QualificationScenario.RejectedBootstrapMaterial =>
                failureCode == "helper.windows.bootstrap.descriptor-rejected",
            QualificationScenario.RejectedPartialHandshake =>
                failureCode == "helper.windows.bootstrap.fixture-abort",
            _ => false,
        };
}
