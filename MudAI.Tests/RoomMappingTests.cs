using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MudAI.Core.Configuration;
using MudAI.Core.Memory;
using Xunit;

namespace MudAI.Tests;

public class RoomMappingTests
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
    public async Task RecordRoomVisit_IncrementsVisitsAndKeepsZone()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            await store.RecordRoomVisitAsync("Temple", "Midgaard", "n, e");
            await store.RecordRoomVisitAsync("Temple", "Midgaard", "n, e");

            var recall = await store.GetRoomRecallAsync("Temple");
            Assert.NotNull(recall);
            Assert.Equal(2, recall!.Visits);
            Assert.Equal("Midgaard", recall.Zone);
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task RecordExit_IsRecalled()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            await store.RecordRoomVisitAsync("Temple", "", "");
            await store.RecordExitAsync("Temple", "n", "Market Square");

            var recall = await store.GetRoomRecallAsync("Temple");
            Assert.NotNull(recall);
            Assert.Contains(recall!.KnownExits, e => e.Direction == "n" && e.ToRoom == "Market Square");
            Assert.Contains("n->Market Square", recall.ToSummary());
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task UnknownRoom_ReturnsNull()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            Assert.Null(await store.GetRoomRecallAsync("Nowhere"));
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task RoomCount_GrowsAsRoomsAreVisited()
    {
        var (store, path) = await NewStoreAsync();
        try
        {
            await store.RecordRoomVisitAsync("A", "", "");
            await store.RecordRoomVisitAsync("B", "", "");
            Assert.Equal(2, await store.CountAsync("rooms"));
        }
        finally { await store.DisposeAsync(); Cleanup(path); }
    }
}
