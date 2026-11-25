using System.Security.Cryptography;
using System.Text;

namespace Kura.Media;

/// <summary>
/// Content-addressed on-disk cache (SHA-256 of the URL) with LRU eviction to a byte quota (SPEC 4.3 G3).
/// Recency is tracked through file write times, so the cache survives restarts with no index to corrupt.
/// </summary>
public sealed class DiskCache
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _evictGate = new(1, 1);
    private long _totalBytes = -1;

    public DiskCache(string directory, long quotaBytes)
    {
        Directory.CreateDirectory(directory);
        Root = directory;
        QuotaBytes = quotaBytes;
        TempDir = Path.Combine(directory, "tmp");
        Directory.CreateDirectory(TempDir);
        foreach (var stale in Directory.EnumerateFiles(TempDir)) TryDelete(stale);
        _ = Task.Run(Scan);
    }

    public string Root { get; }
    public string TempDir { get; }
    public long QuotaBytes { get; set; }
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public string PathFor(string key)
    {
        var h = Hash(key);
        return Path.Combine(Root, h[..2], h);
    }

    public bool TryGet(string key, out string path)
    {
        path = PathFor(key);
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length == 0) return false;
        if (DateTime.UtcNow - fi.LastWriteTimeUtc > TouchInterval)
        {
            try { fi.LastWriteTimeUtc = DateTime.UtcNow; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return true;
    }

    /// <summary>Writes through a temp file and atomically moves it into place.</summary>
    public async Task<string> PutAsync(string key, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        var final = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var tmp = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await write(fs, ct).ConfigureAwait(false);
            var size = new FileInfo(tmp).Length;
            File.Move(tmp, final, overwrite: true);
            if (_totalBytes >= 0) Interlocked.Add(ref _totalBytes, size);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        if (_totalBytes > QuotaBytes) _ = Task.Run(() => EvictAsync(wait: false), CancellationToken.None);
        return final;
    }

    private void Scan()
    {
        long total = 0;
        foreach (var f in EnumerateEntries()) total += f.Length;
        Interlocked.Exchange(ref _totalBytes, total);
        if (total > QuotaBytes) _ = EvictAsync(wait: false);
    }

    private IEnumerable<FileInfo> EnumerateEntries() =>
        new DirectoryInfo(Root).EnumerateDirectories()
            .Where(d => d.Name.Length == 2)
            .SelectMany(d => d.EnumerateFiles());

    /// <summary>Deletes least-recently-used entries until usage is under 90% of the quota.</summary>
    /// <param name="wait">False for opportunistic background runs: skip if an eviction is already running.</param>
    public async Task EvictAsync(bool wait = true)
    {
        if (wait) await _evictGate.WaitAsync().ConfigureAwait(false);
        else if (!await _evictGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var files = EnumerateEntries().OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            var target = (long)(QuotaBytes * 0.9);
            foreach (var f in files)
            {
                if (total <= target) break;
                if (TryDelete(f.FullName)) total -= f.Length;
            }
            Interlocked.Exchange(ref _totalBytes, total);
        }
        finally
        {
            _evictGate.Release();
        }
    }

    public void Clear()
    {
        foreach (var f in EnumerateEntries()) TryDelete(f.FullName);
        Interlocked.Exchange(ref _totalBytes, 0);
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
