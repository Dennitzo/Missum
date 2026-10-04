using System.Globalization;
using System.Text.Json;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class SubagentPlanetIdentityStoreTests
{
    private static readonly string[] RestartAgentIds = ["first", "second", "third"];
    [Fact]
    public void ThousandsOfAgentsReceiveDistinctUnboundedIndicesInOrdinalOrder()
    {
        using var profile = new Profile();
        var ids = Enumerable.Range(0, 5_000).Select(index => "agent-" + index.ToString("D5", CultureInfo.InvariantCulture)).ToArray();
        var store = new SubagentPlanetIdentityStore(profile.Directory);
        store.EnsureAssigned(ids.Reverse());

        Assert.Equal(ids.Length, store.Count);
        var indices = ids.Select(store.GetOrAssign).ToArray();
        Assert.Equal(Enumerable.Range(0, ids.Length), indices);
        Assert.Equal(ids.Length, indices.Distinct().Count());
        Assert.True(indices[^1] > 4_096);
        Assert.Equal(5_000, store.GetOrAssign("later-agent"));
        Assert.Empty(System.IO.Directory.GetFiles(profile.Directory, "*.tmp"));
        using var saved = JsonDocument.Parse(File.ReadAllText(store.StoragePath));
        Assert.Equal(SubagentPlanetIdentityStore.IdentityVersion, saved.RootElement.GetProperty("version").GetString());
        Assert.Equal(5_001, saved.RootElement.GetProperty("nextIndex").GetInt32());
    }

    [Fact]
    public void ReopeningPersistedProfileRestoresAllIndicesRegardlessOfAgentOrder()
    {
        using var profile = new Profile();
        using var restartedProfile = new Profile();
        var original = new SubagentPlanetIdentityStore(profile.Directory);
        original.EnsureAssigned(["third", "first", "second"]);
        var expected = RestartAgentIds.ToDictionary(id => id, original.GetOrAssign);
        // A fresh path exercises deserialization rather than the shared in-process cache.
        File.Copy(original.StoragePath, Path.Combine(restartedProfile.Directory, "SubagentPlanets.json"));
        var reopened = new SubagentPlanetIdentityStore(restartedProfile.Directory);
        reopened.EnsureAssigned(["second", "third", "first"]);

        Assert.Equal(expected.Count, reopened.Count);
        foreach (var pair in expected) Assert.Equal(pair.Value, reopened.GetOrAssign(pair.Key));
        Assert.Equal(3, reopened.GetOrAssign("fourth"));
    }

    [Fact]
    public void StoreInstancesSharingAProfileCannotAllocateTheSameIndexToDifferentAgents()
    {
        using var profile = new Profile();
        var first = new SubagentPlanetIdentityStore(profile.Directory);
        var second = new SubagentPlanetIdentityStore(profile.Directory);
        Parallel.For(0, 48, index =>
        {
            var store = index % 2 == 0 ? first : second;
            store.GetOrAssign("agent-" + index.ToString(CultureInfo.InvariantCulture));
        });

        var ids = Enumerable.Range(0, 48).Select(index => "agent-" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(48, first.Count);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(48, ids.Select(first.GetOrAssign).Distinct().Count());
        foreach (var id in ids) Assert.Equal(first.GetOrAssign(id), second.GetOrAssign(id));
        Assert.Empty(System.IO.Directory.GetFiles(profile.Directory, "*.tmp"));
    }

    [Fact]
    public void CachedIdentitiesRequireNoReadOrWriteAndKeepTheirIndexIfFilesDisappear()
    {
        using var profile = new Profile();
        var store = new SubagentPlanetIdentityStore(profile.Directory);
        var index = store.GetOrAssign("existing");
        File.Delete(store.StoragePath);
        File.Delete(store.StoragePath + ".bak");

        Assert.Equal(index, store.GetOrAssign("existing"));
        store.EnsureAssigned(["existing"]);
        Assert.False(File.Exists(store.StoragePath));
        Assert.Equal(1, store.GetOrAssign("new-agent"));
        Assert.True(File.Exists(store.StoragePath));
    }

    [Fact]
    public void PartialCorruptionRetainsValidEntriesAndUsesThePersistedHighWatermark()
    {
        using var profile = new Profile();
        var path = Path.Combine(profile.Directory, "SubagentPlanets.json");
        File.WriteAllText(path, """
            {"version":"planets-v1","nextIndex":100,"assignments":{
              "a-valid":0,"b-valid":7,"c-duplicate":7,"d-negative":-2,"e-invalid":"oops","f-overflow":2147483648}}
            """);
        var store = new SubagentPlanetIdentityStore(profile.Directory);

        Assert.Equal(2, store.Count);
        Assert.Equal(0, store.GetOrAssign("a-valid"));
        Assert.Equal(7, store.GetOrAssign("b-valid"));
        Assert.Equal(100, store.GetOrAssign("c-duplicate"));
        Assert.Equal(101, store.GetOrAssign("d-negative"));
        Assert.Empty(System.IO.Directory.GetFiles(profile.Directory, "*.tmp"));
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(4, saved.RootElement.GetProperty("assignments").EnumerateObject().Count());
    }

    [Fact]
    public void AnUnreadablePrimaryDocumentRecoversTheLastValidBackupAfterRestart()
    {
        using var profile = new Profile();
        using var restartedProfile = new Profile();
        var original = new SubagentPlanetIdentityStore(profile.Directory);
        original.EnsureAssigned(["alpha", "beta"]);
        var path = Path.Combine(restartedProfile.Directory, "SubagentPlanets.json");
        File.Copy(original.StoragePath + ".bak", path + ".bak");
        File.WriteAllText(path, "{truncated");
        var recovered = new SubagentPlanetIdentityStore(restartedProfile.Directory);

        Assert.Equal(0, recovered.GetOrAssign("alpha"));
        Assert.Equal(1, recovered.GetOrAssign("beta"));
        Assert.Equal(2, recovered.GetOrAssign("gamma"));
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(3, saved.RootElement.GetProperty("assignments").EnumerateObject().Count());
        Assert.Empty(System.IO.Directory.GetFiles(restartedProfile.Directory, "*.tmp"));
    }

    [Fact]
    public void CachedAssignmentsSurviveCorruptionAndMergeExternalNonconflictingEntries()
    {
        using var profile = new Profile();
        var store = new SubagentPlanetIdentityStore(profile.Directory);
        Assert.Equal(0, store.GetOrAssign("original"));
        File.WriteAllText(store.StoragePath, """
            {"version":"planets-v1","nextIndex":9,"assignments":{"original":4,"external":8}}
            """);

        Assert.Equal(9, store.GetOrAssign("new"));
        Assert.Equal(0, store.GetOrAssign("original"));
        Assert.Equal(8, store.GetOrAssign("external"));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void TemporarilyUnwritableProfileKeepsUniqueAssignmentsAndPersistsThemOnNextAllocation()
    {
        using var profile = new Profile();
        var blockedDirectory = Path.Combine(profile.Directory, "blocked");
        File.WriteAllText(blockedDirectory, "A file currently occupies the profile directory.");
        var store = new SubagentPlanetIdentityStore(blockedDirectory);

        Assert.Equal(0, store.GetOrAssign("first"));
        Assert.Equal(1, store.GetOrAssign("second"));
        Assert.Equal(0, store.GetOrAssign("first"));
        File.Delete(blockedDirectory);
        Assert.Equal(2, store.GetOrAssign("third"));
        using var saved = JsonDocument.Parse(File.ReadAllText(store.StoragePath));
        Assert.Equal(3, saved.RootElement.GetProperty("assignments").EnumerateObject().Count());
        Assert.Empty(System.IO.Directory.GetFiles(blockedDirectory, "*.tmp"));
    }

    [Fact]
    public void Int32BoundaryNeverWrapsOrReusesAnExistingIdentity()
    {
        using var profile = new Profile();
        var path = Path.Combine(profile.Directory, "SubagentPlanets.json");
        File.WriteAllText(path, """
            {"version":"planets-v1","nextIndex":2147483647,"assignments":{"older":2147483646}}
            """);
        var store = new SubagentPlanetIdentityStore(profile.Directory);

        Assert.Equal(int.MaxValue, store.GetOrAssign("last"));
        Assert.Equal(int.MaxValue - 1, store.GetOrAssign("older"));
        Assert.Throws<InvalidOperationException>(() => store.GetOrAssign("overflow"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ExhaustedBatchCannotPartiallyAllocateItsRequestedIdentities()
    {
        using var profile = new Profile();
        File.WriteAllText(Path.Combine(profile.Directory, "SubagentPlanets.json"), """
            {"version":"planets-v1","nextIndex":2147483647,"assignments":{"older":2147483646}}
            """);
        var store = new SubagentPlanetIdentityStore(profile.Directory);

        Assert.Throws<InvalidOperationException>(() => store.EnsureAssigned(["last", "overflow"]));
        Assert.Equal(1, store.Count);
        Assert.Equal(int.MaxValue, store.GetOrAssign("last"));
    }

    private sealed class Profile : IDisposable
    {
        public Profile()
        {
            Directory = Path.Combine(Path.GetTempPath(), "Missum-planet-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
