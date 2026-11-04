using System.Text.Json;

namespace PaxMediaManager.Services;

public sealed record StatEntry(DateTime Time, long Bytes);

public readonly record struct PeriodStats(int Count, long Bytes);

public sealed record StatsSummary(PeriodStats Today, PeriodStats Week, PeriodStats Month, PeriodStats AllTime);

/// <summary>Where syncing stands, for display in the UI.</summary>
public sealed record SyncInfo(bool Enabled, bool Reachable, IReadOnlyList<string> OtherMachines, string Message);

/// <summary>
/// Privacy-preserving transfer history: only a timestamp and a byte count are stored per torrent — never a name or path.
/// <para>
/// With a sync folder set, each machine writes only its own <c>stats-&lt;id&gt;.json</c> there and reads everyone else's,
/// so no file is ever written by two machines and there are no conflicts to resolve. Totals are the union of all files.
/// </para>
/// </summary>
public sealed class StatsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly AppSettings? _settings;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private List<StatEntry> _entries = new();
    private Dictionary<string, RemoteMachine> _remote = new();
    private int _lastPushedCount = -1;
    private string _lastPushedFolder = "";

    public StatsStore() { }

    private StatsStore(AppSettings settings) => _settings = settings;

    public event Action? Changed;

    public SyncInfo Sync { get; private set; } = new(false, false, Array.Empty<string>(), "Not syncing.");

    public static string StatsFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PaxMediaManager",
            "stats.json");

    public static StatsStore Load(AppSettings? settings = null)
    {
        var store = settings is null ? new StatsStore() : new StatsStore(settings);
        try
        {
            if (File.Exists(StatsFile))
                store._entries = JsonSerializer.Deserialize<List<StatEntry>>(File.ReadAllText(StatsFile)) ?? new();
        }
        catch
        {
            /* start fresh */
        }

        return store;
    }

    /// <summary>Begins periodic syncing (and does a first sync right away).</summary>
    public void Start() => _ = Task.Run(() => LoopAsync(_cts.Token));

    public void Add(long bytes)
    {
        lock (_lock)
        {
            _entries.Add(new StatEntry(DateTime.Now, Math.Max(0, bytes)));
            Save();
        }

        Changed?.Invoke();
        SyncSoon();
    }

    /// <summary>Clears this machine's history only; other machines' files are left alone.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _entries.Clear();
            Save();
        }

        Changed?.Invoke();
        SyncSoon();
    }

    /// <summary>Call after the sync folder setting changes.</summary>
    public void SyncSoon() => _ = Task.Run(SyncOnceAsync);

    public StatsSummary Summarize(DateTime now)
    {
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var all = AllEntries();

        PeriodStats Sum(Func<StatEntry, bool> p)
        {
            var count = 0;
            long bytes = 0;
            foreach (var e in all)
            {
                if (!p(e))
                    continue;
                count++;
                bytes += e.Bytes;
            }

            return new PeriodStats(count, bytes);
        }

        return new StatsSummary(
            Sum(e => e.Time >= today),
            Sum(e => e.Time >= weekStart),
            Sum(e => e.Time >= monthStart),
            Sum(_ => true));
    }

    /// <summary>Per-day counts and bytes for the last <paramref name="days"/> days, oldest first, ending today.</summary>
    public (DateTime Day, int Count, long Bytes)[] Daily(DateTime now, int days)
    {
        var first = now.Date.AddDays(-(days - 1));
        var all = AllEntries();
        var result = new (DateTime, int, long)[days];
        for (var i = 0; i < days; i++)
        {
            var day = first.AddDays(i);
            var items = all.Where(e => e.Time.Date == day).ToList();
            result[i] = (day, items.Count, items.Sum(e => e.Bytes));
        }

        return result;
    }

    private List<StatEntry> AllEntries()
    {
        lock (_lock)
        {
            var all = new List<StatEntry>(_entries);
            foreach (var m in _remote.Values)
                all.AddRange(m.Entries);
            return all;
        }
    }

    // ---- Sync ----------------------------------------------------------------------------------------------------

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync().ConfigureAwait(false);
                await Task.Delay(SyncInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SyncOnceAsync()
    {
        if (_settings is null)
            return;

        // One sync at a time; if one is already running it will pick up the latest state anyway.
        if (!await _syncGate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            var before = Sync;
            Sync = DoSync();
            // Refresh the dashboard if the merged totals may have changed or the status text did.
            if (Sync != before || Sync.OtherMachines.Count > 0)
                Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Sync = new SyncInfo(true, false, Array.Empty<string>(), "Sync problem: " + ex.Message);
            Changed?.Invoke();
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private SyncInfo DoSync()
    {
        var folder = _settings!.EffectiveSyncFolder?.Trim() ?? "";
        if (folder.Length == 0)
        {
            lock (_lock)
                _remote = new();
            _lastPushedFolder = "";
            return new SyncInfo(false, false, Array.Empty<string>(), _settings.ShareStats
                ? "Not syncing yet. Set the shared settings folder above to combine stats across machines."
                : "Not syncing. Tick the box to combine stats across machines.");
        }

        // On Linux the setting may be an smb:// URL; resolve it to a real path (this can block while the share connects).
        var local = NetworkPath.ToLocal(folder);
        if (local is null || !Directory.Exists(local))
            return new SyncInfo(true, false, OtherNames(), "Sync folder isn't reachable right now. Showing what's known so far.");

        var ownFile = Path.Combine(local, $"stats-{_settings.MachineId}.json");

        // Push: rewrite our file when our entries (or the folder) changed since the last push.
        List<StatEntry> mine;
        lock (_lock)
            mine = new List<StatEntry>(_entries);
        if (mine.Count != _lastPushedCount || folder != _lastPushedFolder || !File.Exists(ownFile))
        {
            var dto = new SyncFile
            {
                MachineId = _settings.MachineId,
                MachineName = Environment.MachineName,
                Entries = mine.Select(e => new SyncEntry(new DateTimeOffset(e.Time), e.Bytes)).ToList(),
            };
            var temp = ownFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(dto, JsonOptions));
            File.Move(temp, ownFile, overwrite: true);
            _lastPushedCount = mine.Count;
            _lastPushedFolder = folder;
        }

        // Pull: everyone else's files.
        var fresh = new Dictionary<string, RemoteMachine>();
        foreach (var file in Directory.EnumerateFiles(local, "stats-*.json"))
        {
            if (string.Equals(file, ownFile, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var dto = JsonSerializer.Deserialize<SyncFile>(File.ReadAllText(file));
                if (dto is null || string.IsNullOrEmpty(dto.MachineId) || dto.MachineId == _settings.MachineId)
                    continue;
                fresh[dto.MachineId] = new RemoteMachine(
                    string.IsNullOrWhiteSpace(dto.MachineName) ? "another machine" : dto.MachineName,
                    dto.Entries.Select(e => new StatEntry(e.Time.LocalDateTime, e.Bytes)).ToList());
            }
            catch
            {
                // A file mid-write or from a newer version: keep what we last knew for that machine, if anything.
            }
        }

        lock (_lock)
        {
            foreach (var kv in _remote)
                if (!fresh.ContainsKey(kv.Key) && File.Exists(Path.Combine(local, $"stats-{kv.Key}.json")))
                    fresh[kv.Key] = kv.Value; // file exists but was unreadable this round
            _remote = fresh;
        }

        var names = OtherNames();
        return new SyncInfo(
            true,
            true,
            names,
            names.Count == 0
                ? "Syncing. No other machines have reported yet."
                : $"Syncing with {string.Join(", ", names)}.");
    }

    private IReadOnlyList<string> OtherNames()
    {
        lock (_lock)
            return _remote.Values.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Caller holds _lock.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatsFile)!);
            File.WriteAllText(StatsFile, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch
        {
            /* best effort */
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _syncGate.Dispose();
    }

    private sealed record RemoteMachine(string Name, List<StatEntry> Entries);

    private sealed class SyncFile
    {
        public string MachineId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public List<SyncEntry> Entries { get; set; } = new();
    }

    private sealed record SyncEntry(DateTimeOffset Time, long Bytes);
}
