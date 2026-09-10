using System.Collections.Concurrent;

namespace PoChopAudio.Services.Chop;

/// <summary>One uploaded recording, its decoded audio on disk, and the latest split of it.</summary>
public sealed class ChopJob
{
    public required JobId Id { get; init; }
    public required string OriginalFileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    public string CanonicalPath => Path.Combine(WorkingDirectory, "canonical.wav");

    public AudioEnvelope? Envelope { get; set; }

    public IReadOnlyList<ChopSegment> Segments { get; set; } = [];

    /// <summary>Offset in seconds from the start of the original recording to the trimmed start.</summary>
    public double TrimStart { get; set; }

    /// <summary>Offset in seconds from the trimmed end to the end of the original recording.</summary>
    public double TrimEnd { get; set; }
}

/// <summary>
/// Keeps decoded uploads on local disk for the length of a working session. Nothing here survives a
/// restart by design — a job is scratch space between "upload" and "download the clips".
/// <para>
/// There is no per-job expiry. A two-hour <c>Lifetime</c> and a <c>RemoveExpired</c> sweep used to
/// live here, inherited from when the caller was a server holding jobs for clients it could not
/// see. Nothing ever called the sweep, so the store advertised a behaviour it did not have — and
/// the diagnostics page printed the claim to the user. In-process the caller is the window: a job
/// dies when the user clears it or closes the app, and expiring one still on screen would be a bug
/// rather than housekeeping.
/// </para>
/// </summary>
public sealed class ChopJobStore : IDisposable
{
    /// <summary>
    /// Held open with <see cref="FileShare.None"/> for as long as the store lives, so another
    /// instance can tell "in use" from "left behind by a process that died" without guessing from
    /// timestamps. <see cref="FileOptions.DeleteOnClose"/> makes Windows drop it even on a crash,
    /// which is exactly when the distinction matters.
    /// </summary>
    private const string LockFileName = ".inuse";

    private readonly ConcurrentDictionary<JobId, ChopJob> _jobs = new();
    private readonly FileStream? _lock;

    public ChopJobStore()
    {
        // Each store owns a private subdirectory rather than the shared parent. The old code wiped
        // %TEMP%/PoChopAudio outright on construction and on dispose, so a second instance on the
        // same machine deleted the first one's audio out from under it — two API processes on
        // different ports, or several WebApplicationFactory test hosts running in parallel.
        Directory.CreateDirectory(ParentRoot);

        Root = Path.Combine(ParentRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        _lock = TryTakeLock(Root);

        SweepAbandoned();
    }

    /// <summary>
    /// The shared directory every store's scratch space lives under. Public so a host can report
    /// where the app is writing and offer to clean it up; the store itself only ever owns
    /// <see cref="Root"/> beneath it.
    /// </summary>
    public static string ParentRoot => Path.Combine(Path.GetTempPath(), "PoChopAudio");

    public string Root { get; }

    public int Count => _jobs.Count;

    /// <summary>
    /// Total bytes under <see cref="ParentRoot"/>, this store's own scratch included. Best effort:
    /// a file that vanishes mid-walk is skipped rather than throwing, because the number exists to
    /// be displayed, not to be relied on.
    /// </summary>
    public static long ScratchBytes()
    {
        try
        {
            return Directory.Exists(ParentRoot)
                ? Directory.EnumerateFiles(ParentRoot, "*", SearchOption.AllDirectories)
                    .Sum(SafeLength)
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes scratch directories left behind by processes that died without disposing, and
    /// returns how many went.
    /// <para>
    /// A sibling is skipped whenever its lock file is still held, which is the difference between
    /// "abandoned" and "another copy of this app is using it right now". The earlier version of
    /// this used age instead, and age cannot tell those apart: a second instance five minutes into
    /// a session looks exactly like a crashed one. That was tolerable while the only caller was
    /// this constructor sweeping two-hour-old leftovers, and stops being tolerable the moment a
    /// user can press a button that means "clean up now".
    /// </para>
    /// </summary>
    public int SweepAbandoned()
    {
        var removed = 0;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(ParentRoot))
            {
                if (string.Equals(directory, Root, StringComparison.OrdinalIgnoreCase)
                    || IsInUse(directory))
                {
                    continue;
                }

                TryDeleteDirectory(directory);

                if (!Directory.Exists(directory))
                {
                    removed++;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return removed;
    }

    /// <summary>
    /// True when a live store still holds <paramref name="directory"/>. A directory with no lock
    /// file is treated as free: that is the crashed-process case, because the handle Windows
    /// releases on exit takes the file with it.
    /// </summary>
    private static bool IsInUse(string directory)
    {
        var lockPath = Path.Combine(directory, LockFileName);

        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Marks <paramref name="root"/> as live. Returns null when the lock cannot be taken — a
    /// read-only or exotic temp directory is not a reason to refuse to start; the store simply
    /// looks abandoned to other instances, which is how it behaved before locks existed.
    /// </summary>
    private static FileStream? TryTakeLock(string root)
    {
        try
        {
            return new FileStream(
                Path.Combine(root, LockFileName),
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ChopJob Create(string originalFileName)
    {
        var id = JobId.New();
        var directory = Path.Combine(Root, id.ToString());
        Directory.CreateDirectory(directory);

        var job = new ChopJob
        {
            Id = id,
            OriginalFileName = originalFileName,
            WorkingDirectory = directory,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        _jobs[id] = job;
        return job;
    }

    public ChopJob? Find(JobId id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void Remove(JobId id)
    {
        if (_jobs.TryRemove(id, out var job))
        {
            TryDeleteDirectory(job.WorkingDirectory);
        }
    }

    public void Dispose()
    {
        // DeleteOnClose removes the lock file here, which is what lets the directory delete cleanly.
        _lock?.Dispose();
        TryDeleteDirectory(Root);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file is still open somewhere; the next startup sweeps it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
