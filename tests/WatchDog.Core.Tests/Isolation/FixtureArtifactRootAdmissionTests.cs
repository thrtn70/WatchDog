using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureArtifactRootAdmissionTests
{
    [Fact]
    public void RunnerAcceptsFreshLauncherRootAndEmptyRedirects()
    {
        WithRunRoot((options, root) =>
        {
            Assert.True(FixtureArtifactRootAdmission.IsAdmitted(options));
            File.WriteAllText(Path.Combine(root, "runner.stdout.txt"), string.Empty);
            File.WriteAllText(Path.Combine(root, "runner.stderr.txt"), string.Empty);
            Assert.True(FixtureArtifactRootAdmission.IsAdmitted(options));
        });
    }

    [Fact]
    public void RunnerRejectsPopulatedOrReusedRootBeforeWriting()
    {
        WithRunRoot((options, root) =>
        {
            var sentinel = Path.Combine(root, "manifest.json");
            File.WriteAllText(sentinel, "existing");
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(options));
            Assert.Equal("existing", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Path.Combine(root, "runner-run.jsonl")));
            File.Delete(sentinel);
            File.WriteAllText(Path.Combine(root, "runner.stdout.txt"), "existing");
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(options));
        });
    }

    [Fact]
    public void RunnerRejectsArbitraryRootEvenWhenEmpty()
    {
        WithRunRoot((options, root) =>
        {
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(options with
            {
                ArtifactRoot = Path.GetDirectoryName(root)!,
            }));
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(options with
            {
                ArtifactRoot = Path.Combine(root, "other"),
            }));
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(options with
            {
                Nonce = Guid.NewGuid(),
            }));
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(options with
            {
                ArtifactRoot = Path.Combine(Path.GetTempPath(), "not-a-qualification-root"),
            }));
        });
    }

    [Fact]
    public void ChildRequiresMatchingRunManifestAndStartedScenario()
    {
        WithRunRoot((options, root) =>
        {
            var scenarioRoot = Path.Combine(root, "ownerdeath");
            Directory.CreateDirectory(scenarioRoot);
            var child = options with
            {
                Role = QualificationRole.Owner,
                Scenario = QualificationScenario.OwnerDeath,
                ArtifactRoot = scenarioRoot,
            };
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(child));
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                $"{{\"nonce\":\"{Guid.NewGuid():D}\"}}");
            File.WriteAllText(Path.Combine(scenarioRoot, "runner-OwnerDeath.jsonl"), "started");
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(child));
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                $"{{\"nonce\":\"{options.Nonce:D}\"}}");
            Assert.True(FixtureArtifactRootAdmission.IsAdmitted(child));
            Assert.True(FixtureArtifactRootAdmission.IsAdmitted(child with
            {
                Role = QualificationRole.Helper,
            }));
            Assert.True(FixtureArtifactRootAdmission.IsAdmitted(child with
            {
                Role = QualificationRole.Descendant,
            }));
            Assert.False(FixtureArtifactRootAdmission.IsAdmitted(child with
            {
                Scenario = QualificationScenario.PostCreateCancellation,
            }));
        });
    }

    private static void WithRunRoot(Action<QualificationOptions, string> action)
    {
        var nonce = Guid.NewGuid();
        var runRoot = Path.Combine(Path.GetTempPath(), "WatchDog-Isolation-Qualification",
            $"{DateTime.UtcNow:yyyyMMddTHHmmss.fffffffZ}-{nonce:D}");
        Directory.CreateDirectory(runRoot);
        try
        {
            var options = new QualificationOptions(
                QualificationRole.Runner, null, nonce, runRoot,
                typeof(FixtureArtifactRootAdmissionTests).Assembly.Location,
                string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty,
                0, 0, 0, 0, false, false);
            action(options, runRoot);
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }
}
