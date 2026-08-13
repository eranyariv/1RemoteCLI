using System.Text.Json;
using OneRemoteCli.Daemon.Agent;

namespace OneRemoteCli.Daemon.Tests;

public class MachineIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"1remote-id-{Guid.NewGuid():N}");

    private string Path_ => System.IO.Path.Combine(_directory, "machine.json");

    [Fact]
    public void GeneratesAndPersistsAnIdentityOnFirstRun()
    {
        MachineIdentity identity = MachineIdentity.Load(Path_);

        Assert.True(Guid.TryParse(identity.MachineId, out _));
        Assert.Equal(Environment.MachineName, identity.DisplayName);
        Assert.True(File.Exists(Path_));
    }

    /// <summary>
    /// The identity must survive restarts: a machine that took a new id on every
    /// launch would appear on the phone as an endless list of strangers.
    /// </summary>
    [Fact]
    public void KeepsTheSameIdentityAcrossLoads()
    {
        MachineIdentity first = MachineIdentity.Load(Path_);
        MachineIdentity second = MachineIdentity.Load(Path_);

        Assert.Equal(first.MachineId, second.MachineId);
    }

    [Fact]
    public void KeepsARenamedDisplayName()
    {
        MachineIdentity identity = MachineIdentity.Load(Path_);
        identity.DisplayName = "Kitchen laptop";
        identity.Save(Path_);

        MachineIdentity reloaded = MachineIdentity.Load(Path_);

        Assert.Equal("Kitchen laptop", reloaded.DisplayName);
        Assert.Equal(identity.MachineId, reloaded.MachineId);
    }

    /// <summary>
    /// A file we cannot read is replaced rather than fatal — an agent that refuses to
    /// start leaves the user with nothing they can act on.
    /// </summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{\"machineId\":\"not-a-guid\",\"displayName\":\"x\"}")]
    public void ReplacesAnUnusableIdentityFile(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, contents);

        var warnings = new List<string>();
        MachineIdentity identity = MachineIdentity.Load(Path_, warnings.Add);

        Assert.True(Guid.TryParse(identity.MachineId, out _));
        Assert.NotEmpty(warnings);

        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(Path_));
        Assert.Equal(identity.MachineId, written.RootElement.GetProperty("machineId").GetString());
    }

    /// <summary>The identity is per machine, so it belongs in local, non-roaming state.</summary>
    [Fact]
    public void LivesUnderLocalAppData()
    {
        string expected = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "1RemoteCLI",
            "machine.json");

        Assert.Equal(expected, MachineIdentity.DefaultPath);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
