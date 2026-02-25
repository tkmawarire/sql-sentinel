using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Services;

public interface IMemoryService
{
    Task<CaptureMemory> SaveCaptureAsync(
        string serverName,
        string sessionName,
        List<ProfilerEvent> events,
        List<DeadlockEvent>? deadlocks = null,
        List<BlockingEvent>? blockingEvents = null,
        List<HealthInsight>? insights = null,
        CancellationToken ct = default);

    Task<List<CaptureMemory>> GetCapturesAsync(
        string? serverName = null,
        string? sessionName = null,
        string? tag = null,
        DateTime? since = null,
        int limit = 20,
        CancellationToken ct = default);

    Task<CaptureMemory?> GetCaptureByIdAsync(string captureId, CancellationToken ct = default);

    Task<QueryMemory?> GetQueryHistoryAsync(
        string serverName,
        string queryFingerprint,
        CancellationToken ct = default);

    Task<List<QueryMemory>> SearchQueriesAsync(
        string serverName,
        string? sqlContains = null,
        string? database = null,
        int? minExecutions = null,
        long? minAvgDurationUs = null,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>
    /// Tag a capture. Returns true if found and tagged, false if not found.
    /// </summary>
    Task<bool> TagCaptureAsync(string captureId, List<string> tags, string? note = null, CancellationToken ct = default);

    Task<MemoryStats> GetMemoryStatsAsync(CancellationToken ct = default);

    Task CleanupExpiredAsync(CancellationToken ct = default);

    Task PurgeServerMemoryAsync(string serverName, CancellationToken ct = default);
}

public class MemoryService : IMemoryService, IHostedService, IDisposable
{
    private readonly string _basePath;
    private readonly ILogger<MemoryService> _logger;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly SemaphoreSlim _queryFileLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _recentSaves = new();
    private Timer? _cleanupTimer;
    private MemoryConfig _config = new();
    private bool _disabled;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonOptionsPretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public MemoryService(ILogger<MemoryService> logger)
    {
        _logger = logger;
        _basePath = Environment.GetEnvironmentVariable("SQLSENTINEL_MEMORY_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlsentinel", "memory");
    }

    // ── IHostedService ───────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Check if memory persistence is disabled
        var memoryEnabled = Environment.GetEnvironmentVariable("SQLSENTINEL_MEMORY_ENABLED");
        if (string.Equals(memoryEnabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            _disabled = true;
            _logger.LogInformation("MemoryService disabled via SQLSENTINEL_MEMORY_ENABLED=false");
            return;
        }

        try
        {
            Directory.CreateDirectory(_basePath);
            Directory.CreateDirectory(Path.Combine(_basePath, "queries"));

            // Set owner-only permissions on Unix systems
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_basePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    var queriesPath = Path.Combine(_basePath, "queries");
                    File.SetUnixFileMode(queriesPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to set directory permissions on {Path}", _basePath);
                }
            }

            await LoadConfigAsync(cancellationToken);
            await CleanupExpiredAsync(cancellationToken);
            _cleanupTimer = new Timer(async _ =>
            {
                try { await CleanupExpiredAsync(CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Scheduled memory cleanup failed"); }
            }, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
            _logger.LogInformation("MemoryService started. Storage path: {Path}", _basePath);
        }
        catch (Exception ex)
        {
            _disabled = true;
            _logger.LogWarning(ex, "MemoryService startup failed — memory features will be unavailable");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _indexLock.Dispose();
        _queryFileLock.Dispose();
    }

    // ── Save Capture ─────────────────────────────────────────────

    public async Task<CaptureMemory> SaveCaptureAsync(
        string serverName,
        string sessionName,
        List<ProfilerEvent> events,
        List<DeadlockEvent>? deadlocks = null,
        List<BlockingEvent>? blockingEvents = null,
        List<HealthInsight>? insights = null,
        CancellationToken ct = default)
    {
        if (_disabled)
            return new CaptureMemory { Id = "disabled", ServerName = serverName, SessionName = sessionName };

        // Ensure storage directories exist (defense-in-depth for late startup)
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(Path.Combine(_basePath, "queries"));

        // Deduplication guard: same server+session within 60s is likely redundant
        var dedupeKey = $"{serverName}|{sessionName}";
        if (_recentSaves.TryGetValue(dedupeKey, out var lastSave) &&
            DateTime.UtcNow - lastSave < TimeSpan.FromSeconds(60))
        {
            _logger.LogDebug("Skipping duplicate save for {Key}", dedupeKey);
            return new CaptureMemory
            {
                Id = "skipped",
                ServerName = serverName,
                SessionName = sessionName
            };
        }
        _recentSaves[dedupeKey] = DateTime.UtcNow;

        // Build capture
        var captureId = Guid.NewGuid().ToString("N")[..12];
        var now = DateTime.UtcNow;

        // Group events by fingerprint for stats
        var queryGroups = events
            .GroupBy(e => e.QueryFingerprint)
            .Select(g => new
            {
                Fingerprint = g.Key,
                Events = g.ToList(),
                Count = g.Count(),
                TotalDuration = g.Sum(e => e.DurationUs),
                TotalCpu = g.Sum(e => e.CpuTimeUs),
                TotalReads = g.Sum(e => e.LogicalReads),
                TotalWrites = g.Sum(e => e.Writes),
                MaxDuration = g.Max(e => e.DurationUs),
                MinDuration = g.Min(e => e.DurationUs),
                Sample = g.First()
            })
            .OrderByDescending(g => g.TotalDuration)
            .ToList();

        var topQueries = queryGroups
            .Take(_config.MaxCaptureTopQueries)
            .Select(g => new QuerySummary
            {
                QueryFingerprint = g.Fingerprint,
                SqlPreview = Truncate(g.Sample.SqlText, 100),
                ExecutionCount = g.Count,
                TotalDurationUs = g.TotalDuration,
                AvgDurationUs = g.Count > 0 ? g.TotalDuration / (double)g.Count : 0,
                MaxDurationUs = g.MaxDuration,
                TotalReads = g.TotalReads
            })
            .ToList();

        // Convert HealthInsights to MemoryInsights
        var memoryInsights = insights?.Select(i => new MemoryInsight
        {
            Severity = i.Severity,
            Category = i.Category,
            Message = i.Message,
            Detail = i.Detail,
            DetectedAt = now
        }).ToList() ?? [];

        var timestamps = events
            .Where(e => e.EventTimestamp.HasValue)
            .Select(e => e.EventTimestamp!.Value)
            .ToList();

        var capture = new CaptureMemory
        {
            Id = captureId,
            ServerName = serverName,
            SessionName = sessionName,
            CapturedAt = now,
            TimeRangeStart = timestamps.Count > 0 ? timestamps.Min() : null,
            TimeRangeEnd = timestamps.Count > 0 ? timestamps.Max() : null,
            TotalEvents = events.Count,
            UniqueQueries = queryGroups.Count,
            TotalDurationUs = events.Sum(e => e.DurationUs),
            TotalCpuUs = events.Sum(e => e.CpuTimeUs),
            TotalReads = events.Sum(e => e.LogicalReads),
            TotalWrites = events.Sum(e => e.Writes),
            DatabasesObserved = events.Select(e => e.DatabaseName).Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList(),
            ApplicationsObserved = events.Select(e => e.ClientAppName).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList(),
            LoginsObserved = events.Select(e => e.LoginName).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList(),
            Insights = memoryInsights,
            TopQueriesByDuration = topQueries,
            DeadlockCount = deadlocks?.Count ?? 0,
            BlockingEventCount = blockingEvents?.Count ?? 0,
            ExpiresAt = now.AddDays(_config.DefaultTtlDays)
        };

        // Persist capture to index (under lock)
        await AppendCaptureAsync(capture, ct);

        // Update per-query files (under separate lock)
        var serverHash = HashServerName(serverName);
        var queriesDir = Path.Combine(_basePath, "queries", serverHash);
        Directory.CreateDirectory(queriesDir);

        await _queryFileLock.WaitAsync(ct);
        try
        {
            foreach (var group in queryGroups)
            {
                try
                {
                    await UpdateQueryMemoryAsync(queriesDir, serverName, captureId, group.Fingerprint,
                        group.Sample.SqlText, group.Count, group.TotalDuration, group.TotalCpu,
                        group.TotalReads, group.TotalWrites, group.MaxDuration, group.MinDuration,
                        group.Events.Select(e => e.DatabaseName).Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList(),
                        group.Events.Select(e => e.ClientAppName).Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList(),
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update query memory for fingerprint {Fingerprint}", group.Fingerprint);
                }
            }
        }
        finally
        {
            _queryFileLock.Release();
        }

        _logger.LogInformation("Saved capture {CaptureId}: {EventCount} events, {UniqueQueries} unique queries from {Server}/{Session}",
            captureId, events.Count, queryGroups.Count, serverName, sessionName);

        return capture;
    }

    // ── Retrieval ────────────────────────────────────────────────

    public async Task<List<CaptureMemory>> GetCapturesAsync(
        string? serverName = null,
        string? sessionName = null,
        string? tag = null,
        DateTime? since = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (_disabled) return [];

        var captures = await ReadAllCapturesAsync(ct);

        var filtered = captures
            .Where(c => c.State != "expired")
            .Where(c => serverName == null || c.ServerName.Contains(serverName, StringComparison.OrdinalIgnoreCase))
            .Where(c => sessionName == null || c.SessionName.Contains(sessionName, StringComparison.OrdinalIgnoreCase))
            .Where(c => tag == null || c.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            .Where(c => since == null || c.CapturedAt >= since.Value)
            .OrderByDescending(c => c.CapturedAt)
            .Take(limit)
            .ToList();

        return filtered;
    }

    public async Task<CaptureMemory?> GetCaptureByIdAsync(string captureId, CancellationToken ct = default)
    {
        var captures = await ReadAllCapturesAsync(ct);
        return captures.FirstOrDefault(c => c.Id == captureId);
    }

    public async Task<QueryMemory?> GetQueryHistoryAsync(
        string serverName,
        string queryFingerprint,
        CancellationToken ct = default)
    {
        var serverHash = HashServerName(serverName);
        var filePath = Path.Combine(_basePath, "queries", serverHash, $"{SanitizeFilename(queryFingerprint)}.json");

        if (!File.Exists(filePath))
            return null;

        await _queryFileLock.WaitAsync(ct);
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<QueryMemory>(json, JsonOptions);
        }
        finally
        {
            _queryFileLock.Release();
        }
    }

    public async Task<List<QueryMemory>> SearchQueriesAsync(
        string serverName,
        string? sqlContains = null,
        string? database = null,
        int? minExecutions = null,
        long? minAvgDurationUs = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        var serverHash = HashServerName(serverName);
        var queriesDir = Path.Combine(_basePath, "queries", serverHash);

        if (!Directory.Exists(queriesDir))
            return [];

        var results = new List<QueryMemory>();
        var files = Directory.GetFiles(queriesDir, "*.json");

        await _queryFileLock.WaitAsync(ct);
        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    var qm = JsonSerializer.Deserialize<QueryMemory>(json, JsonOptions);
                    if (qm == null) continue;

                    if (sqlContains != null &&
                        !qm.SampleSql.Contains(sqlContains, StringComparison.OrdinalIgnoreCase) &&
                        !qm.NormalizedSqlPreview.Contains(sqlContains, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (database != null &&
                        !qm.DatabasesSeen.Any(d => d.Equals(database, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (minExecutions.HasValue && qm.TotalExecutions < minExecutions.Value)
                        continue;

                    if (minAvgDurationUs.HasValue && qm.AvgDurationUs < minAvgDurationUs.Value)
                        continue;

                    results.Add(qm);

                    if (results.Count >= limit * 2) // Collect extra for sorting
                        break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read query file {File}", file);
                }
            }
        }
        finally
        {
            _queryFileLock.Release();
        }

        return results
            .OrderByDescending(q => q.TotalDurationUs)
            .Take(limit)
            .ToList();
    }

    // ── Tagging ──────────────────────────────────────────────────

    public async Task<bool> TagCaptureAsync(string captureId, List<string> tags, string? note = null, CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var indexPath = GetIndexPath();
            if (!File.Exists(indexPath)) return false;

            var lines = await File.ReadAllLinesAsync(indexPath, ct);
            var updated = new List<string>();
            var found = false;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    updated.Add(line);
                    continue;
                }

                try
                {
                    var capture = JsonSerializer.Deserialize<CaptureMemory>(line, JsonOptions);
                    if (capture?.Id == captureId)
                    {
                        found = true;
                        var existingTags = new HashSet<string>(capture.Tags, StringComparer.OrdinalIgnoreCase);
                        foreach (var tag in tags)
                            existingTags.Add(tag);

                        // Use with expression to set all properties cleanly
                        var newCapture = capture with
                        {
                            Tags = existingTags.ToList(),
                            AgentNote = note ?? capture.AgentNote,
                            ExpiresAt = DateTime.UtcNow.AddDays(_config.TaggedTtlDays)
                        };
                        updated.Add(JsonSerializer.Serialize(newCapture, JsonOptions));
                    }
                    else
                    {
                        updated.Add(line);
                    }
                }
                catch
                {
                    updated.Add(line);
                }
            }

            if (found)
            {
                await File.WriteAllLinesAsync(indexPath, updated, ct);
            }

            return found;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    // ── Stats ────────────────────────────────────────────────────

    public async Task<MemoryStats> GetMemoryStatsAsync(CancellationToken ct = default)
    {
        var captures = await ReadAllCapturesAsync(ct);
        var activeCaptures = captures.Where(c => c.State != "expired").ToList();

        var queryCount = 0;
        var queriesDir = Path.Combine(_basePath, "queries");
        if (Directory.Exists(queriesDir))
        {
            foreach (var serverDir in Directory.GetDirectories(queriesDir))
            {
                queryCount += Directory.GetFiles(serverDir, "*.json").Length;
            }
        }

        long diskUsage = 0;
        try
        {
            diskUsage = GetDirectorySize(new DirectoryInfo(_basePath));
        }
        catch { /* ignore */ }

        return new MemoryStats
        {
            TotalCaptures = activeCaptures.Count,
            TotalQueryFingerprints = queryCount,
            DiskUsageBytes = diskUsage,
            OldestCapture = activeCaptures.MinBy(c => c.CapturedAt)?.CapturedAt,
            NewestCapture = activeCaptures.MaxBy(c => c.CapturedAt)?.CapturedAt,
            CapturesPerServer = activeCaptures
                .GroupBy(c => c.ServerName)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    // ── Cleanup ──────────────────────────────────────────────────

    public async Task CleanupExpiredAsync(CancellationToken ct = default)
    {
        try
        {
            await _indexLock.WaitAsync(ct);
            try
            {
                var indexPath = GetIndexPath();
                if (!File.Exists(indexPath)) return;

                var lines = await File.ReadAllLinesAsync(indexPath, ct);
                var kept = new List<string>();
                var expiredCaptureIds = new HashSet<string>();
                var now = DateTime.UtcNow;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var capture = JsonSerializer.Deserialize<CaptureMemory>(line, JsonOptions);
                        if (capture == null) continue;

                        if (capture.ExpiresAt.HasValue && capture.ExpiresAt.Value < now)
                        {
                            expiredCaptureIds.Add(capture.Id);
                            continue;
                        }
                        kept.Add(line);
                    }
                    catch
                    {
                        // Skip malformed lines
                    }
                }

                if (expiredCaptureIds.Count > 0)
                {
                    await File.WriteAllLinesAsync(indexPath, kept, ct);
                    _logger.LogInformation("Cleaned up {Count} expired captures", expiredCaptureIds.Count);
                }

                // Rotate index if too large (>10MB)
                var indexInfo = new FileInfo(indexPath);
                if (indexInfo.Exists && indexInfo.Length > 10 * 1024 * 1024)
                {
                    var rotatedPath = Path.Combine(_basePath, $"index.{now:yyyyMMddHHmmss}.jsonl");
                    File.Move(indexPath, rotatedPath);
                    // Keep only the 2 most recent rotated files
                    var rotatedFiles = Directory.GetFiles(_basePath, "index.*.jsonl")
                        .OrderByDescending(f => f)
                        .Skip(2);
                    foreach (var old in rotatedFiles)
                        File.Delete(old);
                }

                // Enforce disk limit
                var totalSize = GetDirectorySize(new DirectoryInfo(_basePath));
                if (totalSize > _config.MaxMemoryMb * 1024L * 1024L)
                {
                    _logger.LogWarning("Memory storage exceeds {Limit}MB, pruning oldest untagged captures", _config.MaxMemoryMb);
                    await PruneOldestUntaggedAsync(ct);
                }
            }
            finally
            {
                _indexLock.Release();
            }

            // Clean stale deduplication entries
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (var key in _recentSaves.Keys.ToList())
            {
                if (_recentSaves.TryGetValue(key, out var ts) && ts < cutoff)
                    _recentSaves.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory cleanup failed");
        }
    }

    public async Task PurgeServerMemoryAsync(string serverName, CancellationToken ct = default)
    {
        if (serverName.Equals("expired", StringComparison.OrdinalIgnoreCase))
        {
            await CleanupExpiredAsync(ct);
            return;
        }

        await _indexLock.WaitAsync(ct);
        try
        {
            // Remove captures for this server from index
            var indexPath = GetIndexPath();
            if (File.Exists(indexPath))
            {
                var lines = await File.ReadAllLinesAsync(indexPath, ct);
                var kept = new List<string>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var capture = JsonSerializer.Deserialize<CaptureMemory>(line, JsonOptions);
                        if (capture != null && !capture.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                            kept.Add(line);
                    }
                    catch
                    {
                        kept.Add(line);
                    }
                }
                await File.WriteAllLinesAsync(indexPath, kept, ct);
            }

            // Remove query files for this server
            var serverHash = HashServerName(serverName);
            var queriesDir = Path.Combine(_basePath, "queries", serverHash);
            if (Directory.Exists(queriesDir))
                Directory.Delete(queriesDir, recursive: true);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    // ── Helpers ──────────────────────────────────────────────

    public static string ExtractServerName(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return builder.DataSource ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    internal static string HashServerName(string serverName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(serverName.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    internal static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }

    internal static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private string GetIndexPath() => Path.Combine(_basePath, "index.jsonl");

    private async Task AppendCaptureAsync(CaptureMemory capture, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(capture, JsonOptions);
        await _indexLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(GetIndexPath(), json + Environment.NewLine, ct);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<List<CaptureMemory>> ReadAllCapturesAsync(CancellationToken ct)
    {
        var indexPath = GetIndexPath();
        if (!File.Exists(indexPath))
            return [];

        await _indexLock.WaitAsync(ct);
        try
        {
            var captures = new List<CaptureMemory>();
            var lines = await File.ReadAllLinesAsync(indexPath, ct);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var capture = JsonSerializer.Deserialize<CaptureMemory>(line, JsonOptions);
                    if (capture != null)
                        captures.Add(capture);
                }
                catch
                {
                    // Skip malformed lines
                }
            }

            return captures;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Update a per-query memory file. Caller must hold _queryFileLock.
    /// </summary>
    private async Task UpdateQueryMemoryAsync(
        string queriesDir, string serverName, string captureId,
        string fingerprint, string sampleSql,
        int execCount, long totalDuration, long totalCpu,
        long totalReads, long totalWrites, long maxDuration, long minDuration,
        List<string> databases, List<string> applications,
        CancellationToken ct)
    {
        var filePath = Path.Combine(queriesDir, $"{SanitizeFilename(fingerprint)}.json");
        QueryMemory existing;
        var now = DateTime.UtcNow;

        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, ct);
                existing = JsonSerializer.Deserialize<QueryMemory>(json, JsonOptions) ?? CreateNew();
            }
            catch
            {
                existing = CreateNew();
            }
        }
        else
        {
            existing = CreateNew();
        }

        // Merge
        var newTotalExec = existing.TotalExecutions + execCount;
        var newTotalDuration = existing.TotalDurationUs + totalDuration;
        var newTotalCpu = existing.TotalCpuUs + totalCpu;
        var newTotalReads = existing.TotalReads + totalReads;
        var newTotalWrites = existing.TotalWrites + totalWrites;

        var allDatabases = new HashSet<string>(existing.DatabasesSeen, StringComparer.OrdinalIgnoreCase);
        foreach (var db in databases) allDatabases.Add(db);

        var allApps = new HashSet<string>(existing.ApplicationsSeen, StringComparer.OrdinalIgnoreCase);
        foreach (var app in applications) allApps.Add(app);

        var observation = new QueryObservation
        {
            CaptureId = captureId,
            ObservedAt = now,
            ExecutionCount = execCount,
            AvgDurationUs = execCount > 0 ? totalDuration / execCount : 0,
            MaxDurationUs = maxDuration,
            AvgReads = execCount > 0 ? totalReads / execCount : 0,
            AvgCpuUs = execCount > 0 ? totalCpu / execCount : 0
        };

        var observations = new List<QueryObservation>(existing.Observations) { observation };
        if (observations.Count > _config.MaxQueryObservations)
            observations = observations.Skip(observations.Count - _config.MaxQueryObservations).ToList();

        var updated = existing with
        {
            TotalExecutions = newTotalExec,
            TotalDurationUs = newTotalDuration,
            TotalCpuUs = newTotalCpu,
            TotalReads = newTotalReads,
            TotalWrites = newTotalWrites,
            MaxDurationUs = Math.Max(existing.MaxDurationUs, maxDuration),
            MinDurationUs = existing.MinDurationUs == 0 ? minDuration : Math.Min(existing.MinDurationUs, minDuration),
            AvgDurationUs = newTotalExec > 0 ? newTotalDuration / (double)newTotalExec : 0,
            DatabasesSeen = allDatabases.ToList(),
            ApplicationsSeen = allApps.ToList(),
            Observations = observations,
            LastSeenAt = now,
            CaptureCount = existing.CaptureCount + 1,
            SampleSql = string.IsNullOrEmpty(existing.SampleSql) ? Truncate(sampleSql, 500) : existing.SampleSql,
            NormalizedSqlPreview = string.IsNullOrEmpty(existing.NormalizedSqlPreview) ? Truncate(sampleSql, 500) : existing.NormalizedSqlPreview
        };

        var updatedJson = JsonSerializer.Serialize(updated, JsonOptionsPretty);
        await File.WriteAllTextAsync(filePath, updatedJson, ct);

        QueryMemory CreateNew() => new()
        {
            QueryFingerprint = fingerprint,
            ServerName = serverName,
            NormalizedSqlPreview = Truncate(sampleSql, 500),
            SampleSql = Truncate(sampleSql, 500),
            FirstSeenAt = now,
            LastSeenAt = now,
            CaptureCount = 0
        };
    }

    private async Task PruneOldestUntaggedAsync(CancellationToken ct)
    {
        // Caller holds _indexLock
        var indexPath = GetIndexPath();
        if (!File.Exists(indexPath)) return;

        var lines = await File.ReadAllLinesAsync(indexPath, ct);
        var captures = new List<(string line, CaptureMemory capture)>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var capture = JsonSerializer.Deserialize<CaptureMemory>(line, JsonOptions);
                if (capture != null)
                    captures.Add((line, capture));
            }
            catch { /* skip */ }
        }

        // Remove oldest untagged captures (keep tagged ones)
        var sorted = captures
            .OrderBy(c => c.capture.Tags.Count > 0 ? 1 : 0) // Untagged first
            .ThenBy(c => c.capture.CapturedAt)
            .ToList();

        // Remove the oldest 20% of untagged captures
        var toRemove = sorted
            .Where(c => c.capture.Tags.Count == 0)
            .Take(Math.Max(1, sorted.Count / 5))
            .Select(c => c.capture.Id)
            .ToHashSet();

        if (toRemove.Count == 0) return;

        var kept = captures.Where(c => !toRemove.Contains(c.capture.Id)).Select(c => c.line).ToList();
        await File.WriteAllLinesAsync(indexPath, kept, ct);
        _logger.LogInformation("Pruned {Count} oldest untagged captures to reduce disk usage", toRemove.Count);
    }

    private async Task LoadConfigAsync(CancellationToken ct)
    {
        var configPath = Path.Combine(_basePath, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(configPath, ct);
                _config = JsonSerializer.Deserialize<MemoryConfig>(json, JsonOptions) ?? new MemoryConfig();
                return;
            }
            catch { /* use defaults */ }
        }

        // Write default config
        _config = new MemoryConfig();
        var defaultJson = JsonSerializer.Serialize(_config, JsonOptionsPretty);
        await File.WriteAllTextAsync(configPath, defaultJson, ct);
    }

    private static long GetDirectorySize(DirectoryInfo dir)
    {
        if (!dir.Exists) return 0;
        long size = 0;
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                size += file.Length;
        }
        catch { /* ignore access errors */ }
        return size;
    }
}
