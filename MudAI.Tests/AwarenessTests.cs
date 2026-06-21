using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;
using MudAI.Core.Memory;
using Xunit;

namespace MudAI.Tests;

public class AwarenessTests
{
    private static async Task<(SqliteMemoryStore store, string path)> NewStoreAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mudai_test_{Guid.NewGuid():N}.db");
        var store = new SqliteMemoryStore(
            Options.Create(new MudAiOptions { MemoryDbPath = path }),
            NullLogger<SqliteMemoryStore>.Instance);
        await store.InitializeAsync();
        return (store, path);
    }

    private static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AddOrReinforce_InsertsThenReinforcesAndRefreshesFact()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            await store.AddOrReinforceAwarenessAsync("combat", "giant rat", "easy");
            await store.AddOrReinforceAwarenessAsync("combat", "giant rat", "easy, drops nothing");

            var all = await store.GetAllAwarenessAsync();
            var entry = Assert.Single(all);
            Assert.Equal(2, entry.TimesReinforced);
            Assert.Equal("easy, drops nothing", entry.Fact); // latest fact wins
            Assert.True(entry.Confidence > 0.6);
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task UnknownCategory_NormalizesToMisc()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            await store.AddOrReinforceAwarenessAsync("battle", "rat", "easy");
            var entry = Assert.Single(await store.GetAllAwarenessAsync());
            Assert.Equal("misc", entry.Category);
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task SubjectAndFact_AreClamped()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            await store.AddOrReinforceAwarenessAsync("misc", new string('s', 100), new string('f', 300));
            var entry = Assert.Single(await store.GetAllAwarenessAsync());
            Assert.Equal(AwarenessVocabulary.MaxSubjectLength, entry.Subject.Length);
            Assert.Equal(AwarenessVocabulary.MaxFactLength, entry.Fact.Length);
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task EmptySubjectOrFact_IsNoOp()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            await store.AddOrReinforceAwarenessAsync("combat", "", "fact");
            await store.AddOrReinforceAwarenessAsync("combat", "subject", "");
            Assert.Equal(0, await store.CountAsync("awareness"));
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task GetBalanced_RespectsPerCategoryQuotaAndBalances()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            for (int i = 0; i < 5; i++)
                await store.AddOrReinforceAwarenessAsync("combat", $"mob {i}", "info");
            await store.AddOrReinforceAwarenessAsync("geography", "midgaard", "hub");

            var balanced = await store.GetBalancedAwarenessAsync(2);
            Assert.Equal(2, balanced.Count(a => a.Category == "combat"));
            Assert.Equal(1, balanced.Count(a => a.Category == "geography"));
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task Awareness_SurvivesReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mudai_test_{Guid.NewGuid():N}.db");
        try
        {
            var first = new SqliteMemoryStore(
                Options.Create(new MudAiOptions { MemoryDbPath = path }), NullLogger<SqliteMemoryStore>.Instance);
            await first.InitializeAsync();
            await first.AddOrReinforceAwarenessAsync("skills", "bash", "warrior skill, needs shield");
            await first.DisposeAsync();

            var second = new SqliteMemoryStore(
                Options.Create(new MudAiOptions { MemoryDbPath = path }), NullLogger<SqliteMemoryStore>.Instance);
            await second.InitializeAsync();
            var entry = Assert.Single(await second.GetAllAwarenessAsync());
            Assert.Equal("bash", entry.Subject);
            await second.DisposeAsync();
        }
        finally { Cleanup(path); }
    }
}
