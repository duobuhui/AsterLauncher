using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class PlaytimeJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "playtime-journal-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddHours(-2);
    private static PlaytimeObservation Observation(int seconds = 60, bool complete = false) => new()
    {
        GameId = "test-game", ExecutablePath = @"E:\Games\Example.exe", ProcessId = 42,
        StartedAt = Start, ObservedUntil = Start.AddSeconds(seconds), IsComplete = complete,
        SessionId = PlaytimeIdentity.CreateSessionId("test-game", @"E:\Games\Example.exe", 42, Start)
    };

    [Fact]
    public async Task QueueIsInitialOnlyCamelCaseAtomicAndDoesNotOverwriteExistingRequest()
    {
        var journal = new PlaytimeJournal(_root);
        var observation = Observation(60, true);
        await journal.QueueAsync(observation);
        var path = Path.Combine(journal.RequestDirectory, observation.SessionId + ".json");
        var original = await File.ReadAllTextAsync(path);
        using var json = JsonDocument.Parse(original);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Start, json.RootElement.GetProperty("observedUntil").GetDateTimeOffset());
        Assert.False(json.RootElement.GetProperty("isComplete").GetBoolean());
        await journal.QueueAsync(Observation(100));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFiles(journal.RequestDirectory));
        Assert.False(Directory.Exists(journal.ReceiptDirectory));
        Assert.False(File.Exists(journal.ObserverLockPath));
    }

    [Fact]
    public async Task CompletedReceiptPreventsRequeueUntilSettlement()
    {
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(Observation(60, true));
        await journal.QueueAsync(Observation());
        Assert.False(Directory.Exists(journal.RequestDirectory));
    }

    [Fact]
    public async Task RoundtripRestartAndReadAllAreReadOnly()
    {
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(Observation(120, true));
        var first = await journal.ReadAsync(Observation().SessionId);
        var second = Assert.Single(await new PlaytimeJournal(_root).ReadAllAsync());
        Assert.Equal(Observation(120, true), first);
        Assert.Equal(first, second);
        Assert.Single(Directory.EnumerateFiles(journal.ReceiptDirectory));
        Assert.Null(await journal.ReadAsync(new string('a', 64)));
    }

    [Fact]
    public async Task ReceiptNeverRegressesObservationTimeOrCompletedState()
    {
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(Observation(120));
        await journal.WriteAsync(Observation(60, true));
        Assert.Equal(Observation(120), await journal.ReadAsync(Observation().SessionId));
        await journal.WriteAsync(Observation(120, true));
        await journal.WriteAsync(Observation(180));
        Assert.Equal(Observation(120, true), await journal.ReadAsync(Observation().SessionId));
    }

    [Fact]
    public async Task SavedOlderReceiptCannotDeleteNewerCompletedReceipt()
    {
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(Observation(60, true));
        await journal.WriteAsync(Observation(120, true));
        Assert.False(await journal.DeleteCompletedAsync(Observation(60, true)));
        Assert.False(await journal.DeleteCompletedAsync(Observation(120)));
        Assert.Equal(Observation(120, true), await journal.ReadAsync(Observation().SessionId));
        Assert.True(await journal.DeleteCompletedAsync(Observation(120, true)));
        Assert.Null(await journal.ReadAsync(Observation().SessionId));
        Assert.False(await journal.DeleteCompletedAsync(Observation(120, true)));
    }

    [Fact]
    public async Task MalformedOversizedMismatchedAndFutureFilesAreIgnoredAndPreserved()
    {
        var journal = new PlaytimeJournal(_root);
        Directory.CreateDirectory(journal.ReceiptDirectory);
        var malformed = Path.Combine(journal.ReceiptDirectory, new string('a', 64) + ".json");
        var oversized = Path.Combine(journal.ReceiptDirectory, new string('b', 64) + ".json");
        var mismatch = Path.Combine(journal.ReceiptDirectory, new string('c', 64) + ".json");
        var future = Observation() with { ObservedUntil = DateTimeOffset.UtcNow.AddDays(1) };
        var futurePath = Path.Combine(journal.ReceiptDirectory, future.SessionId + ".json");
        await File.WriteAllTextAsync(malformed, "{");
        await File.WriteAllTextAsync(oversized, new string(' ', PlaytimeJournal.MaximumFileBytes + 1));
        await File.WriteAllTextAsync(mismatch, JsonSerializer.Serialize(Observation(), PlaytimeJournal.JsonOptions));
        await File.WriteAllTextAsync(futurePath, JsonSerializer.Serialize(future, PlaytimeJournal.JsonOptions));
        Assert.Empty(await journal.ReadAllAsync());
        Assert.Equal(4, Directory.EnumerateFiles(journal.ReceiptDirectory).Count());
        Assert.False(await journal.DeleteCompletedAsync(Observation(60, true)));
    }

    [Fact]
    public async Task InvalidIdentityAndUnsafeReceiptNamesNeverWriteOutsideJournal()
    {
        var journal = new PlaytimeJournal(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.WriteAsync(Observation() with { SessionId = "../bad" }));
        await Assert.ThrowsAsync<ArgumentException>(() => journal.ReadAsync("../bad"));
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.QueueAsync(Observation() with { ProcessId = 0 }));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CanceledOperationCannotLeaveTemporaryOrPartialReceipt()
    {
        var journal = new PlaytimeJournal(_root);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.WriteAsync(Observation(), canceled.Token));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task SavedConfigurationCanReplayReceiptAfterInterruptedCleanupWithoutDoubleCounting()
    {
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(Observation(120, true));
        var state = new GameUserState { GameId = "test-game", TotalPlaySeconds = 900 };
        Assert.True(PlaytimeSessionMerger.Apply(state, await journal.ReadAllAsync()));
        var saved = JsonSerializer.Serialize(state);
        var restarted = JsonSerializer.Deserialize<GameUserState>(saved)!;
        Assert.False(PlaytimeSessionMerger.Apply(restarted, await journal.ReadAllAsync()));
        Assert.Equal(1020, restarted.TotalPlaySeconds);
        Assert.Single(restarted.PlaySessions);
        Assert.True(await journal.DeleteCompletedAsync(Observation(120, true)));
    }

    [Fact]
    public async Task AtomicReplacementKeepsOpenReaderSnapshotAndPublishesWholeNextObservation()
    {
        var journal = new PlaytimeJournal(_root);
        await journal.WriteAsync(Observation(60));
        var path = Path.Combine(journal.ReceiptDirectory, Observation().SessionId + ".json");
        await using var previousSnapshot = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        await journal.WriteAsync(Observation(120, true));
        var previous = await JsonSerializer.DeserializeAsync<PlaytimeObservation>(previousSnapshot, PlaytimeJournal.JsonOptions);
        Assert.Equal(Observation(60), previous);
        Assert.Equal(Observation(120, true), await journal.ReadAsync(Observation().SessionId));
        Assert.Single(Directory.EnumerateFiles(journal.ReceiptDirectory));
    }

    [Fact]
    public async Task SerializedUtf8LimitPreventsOversizedOutputAndLeavesNoTemporaryReceipt()
    {
        var journal = new PlaytimeJournal(_root);
        var path = @"E:\Games\" + new string('游', 16000) + ".exe";
        var observation = Observation() with
        {
            ExecutablePath = path,
            SessionId = PlaytimeIdentity.CreateSessionId("test-game", path, 42, Start)
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.WriteAsync(observation));
        Assert.Empty(Directory.EnumerateFiles(journal.ReceiptDirectory));
    }

    public void Dispose()
    {
        var approved = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "playtime-journal-tests")) + Path.DirectorySeparatorChar;
        var actual = Path.GetFullPath(_root);
        if (actual.StartsWith(approved, StringComparison.OrdinalIgnoreCase) && Directory.Exists(actual))
            Directory.Delete(actual, recursive: true);
    }
}
