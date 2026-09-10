using PoChopAudio.Services.Chop;

namespace PoChopAudio.Unit;

/// <summary>
/// Covers the scratch-directory rules the Settings page's "Clean up" button now depends on.
///
/// <para>
/// The property that matters is the one that is easy to get wrong and expensive to get wrong: a
/// sweep must never take a directory another live store is working in. These tests run against the
/// real shared parent directory on purpose, because that is where the hazard lives — two stores in
/// one process is the same situation as two copies of the app.
/// </para>
/// </summary>
public sealed class ChopJobStoreTests
{
    [Fact]
    public void SweepAbandoned_LeavesAnotherLiveStoreAlone()
    {
        using var first = new ChopJobStore();
        using var second = new ChopJobStore();

        // Give the other store real work in it, so a wrong answer here is a data-loss bug.
        var job = second.Create("take.wav");
        File.WriteAllText(job.CanonicalPath, "audio");

        first.SweepAbandoned();

        Assert.True(Directory.Exists(second.Root));
        Assert.True(File.Exists(job.CanonicalPath));
    }

    [Fact]
    public void SweepAbandoned_NeverRemovesItsOwnRoot()
    {
        using var store = new ChopJobStore();

        store.SweepAbandoned();

        Assert.True(Directory.Exists(store.Root));
    }

    [Fact]
    public void SweepAbandoned_RemovesADirectoryNoStoreHolds()
    {
        using var store = new ChopJobStore();

        // What a process that died without disposing leaves behind: a directory with no lock file.
        var abandoned = Path.Combine(ChopJobStore.ParentRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "canonical.wav"), "stale");

        var removed = store.SweepAbandoned();

        Assert.False(Directory.Exists(abandoned));
        Assert.True(removed >= 1);
    }

    [Fact]
    public void Dispose_ReleasesTheLockSoTheDirectoryCanBeSwept()
    {
        var store = new ChopJobStore();
        var root = store.Root;
        store.Dispose();

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ScratchBytes_CountsWhatTheStoreHasWritten()
    {
        using var store = new ChopJobStore();
        var before = ChopJobStore.ScratchBytes();

        var job = store.Create("take.wav");
        File.WriteAllBytes(job.CanonicalPath, new byte[4096]);

        Assert.True(ChopJobStore.ScratchBytes() >= before + 4096);
    }
}
