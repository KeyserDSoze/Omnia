using Microsoft.Extensions.Configuration;
using Omnia.Api;
using Xunit;

namespace Omnia.Integration.Tests;

public sealed class RecoveryQuarantineGateTests
{
    [Fact]
    public void ProductionRequiresAnExplicitExternalQuarantineFile()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RecoveryQuarantineGate.Verify(Config(null), isDevelopment: false));
        // Existing local and CI OIDC development flows remain possible.
        RecoveryQuarantineGate.Verify(Config(null), isDevelopment: true);
    }

    [Fact]
    public void QuarantineIsFailClosedEvenInDevelopment()
    {
        using var fixture = new PrivateRecoveryFile();
        fixture.Write("quarantined\n");
        Assert.Throws<InvalidOperationException>(() =>
            RecoveryQuarantineGate.Verify(Config(fixture.Path), isDevelopment: true));
        Assert.Throws<InvalidOperationException>(() =>
            RecoveryQuarantineGate.Verify(Config(fixture.Path), isDevelopment: false));
    }

    [Fact]
    public void OnlyExactExternalReleaseStateEnablesProductionStartup()
    {
        using var fixture = new PrivateRecoveryFile();
        fixture.Write("ready\n");
        RecoveryQuarantineGate.Verify(Config(fixture.Path), isDevelopment: false);

        foreach (var invalid in new[]
                 {
                     "ready", "READY\n", " ready\n", "ready\n\n",
                     "quarantined\nready\n", "", "unknown\n",
                     "ready\n# override"
                 })
        {
            fixture.Write(invalid);
            Assert.Throws<InvalidOperationException>(() =>
                RecoveryQuarantineGate.Verify(Config(fixture.Path), isDevelopment: false));
        }
    }

    [Fact]
    public void MissingRelativeSymlinkAndInsecureFilesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RecoveryQuarantineGate.Verify(Config("relative-state.txt"), isDevelopment: true));

        using var fixture = new PrivateRecoveryFile();
        fixture.Write("ready\n");
        var missing = fixture.Path + ".missing";
        Assert.Throws<InvalidOperationException>(() =>
            RecoveryQuarantineGate.Verify(Config(missing), isDevelopment: false));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.Throws<InvalidOperationException>(() =>
                RecoveryQuarantineGate.Verify(Config(fixture.Path), isDevelopment: false));
            File.SetUnixFileMode(fixture.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var alias = fixture.Path + ".link";
            File.CreateSymbolicLink(alias, fixture.Path);
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    RecoveryQuarantineGate.Verify(Config(alias), isDevelopment: false));
            }
            finally { File.Delete(alias); }
        }
    }

    private static IConfiguration Config(string? path) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            path is null ? [] :
            new Dictionary<string, string?> { ["Recovery:AccessStateFile"] = path }
        ).Build();

    private sealed class PrivateRecoveryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "omnia-ci-recovery-state-" + Guid.NewGuid().ToString("N"));
        public void Write(string value)
        {
            File.WriteAllText(Path, value);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
