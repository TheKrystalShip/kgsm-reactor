using Microsoft.Extensions.Logging;

namespace TheKrystalShip.Kgsm.Reactor.Rules.Composition;

/// <summary>
/// The rules this host is running, and the one place they change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything reads the set through here rather than holding one.</b> The engine judges through
/// these rules and a redemption re-derives its condition through the same ones; if either kept its
/// own copy, an edit would leave them judging by different rules for as long as the daemon ran.
/// </para>
/// <para>
/// <b>A reload replaces the whole set, never part of it.</b> A rule is validated against the set it
/// joins — a duplicate id is refused, and refusing it depends on knowing every other id — so the
/// directory is re-read in full and swapped in one assignment. Readers hold a reference to an
/// immutable set, so one that started a sweep before a write finishes that sweep on the rules it
/// began with instead of stepping between two of them.
/// </para>
/// <para>
/// <b>Watched, because a rule file is editable by hand.</b> The panel's writes come through
/// <see cref="Replace"/> and do not need the watcher, but a file written over SSH or by a
/// configuration tool has nothing else to notice it. Debounced, because a single save arrives as
/// several filesystem events and an editor that writes through a temporary file produces a burst.
/// </para>
/// </remarks>
internal sealed class RuleRegistry : IDisposable
{
    /// <summary>
    /// How long the directory has to stay quiet before a change on disk is read.
    /// </summary>
    /// <remarks>
    /// Sized for an editor rather than for a person: writing through a temporary file and renaming it
    /// produces created/changed/renamed within milliseconds of each other, and reading between them
    /// finds a file that is empty or half-written. Short enough that a hand edit still feels immediate.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _directory;
    private readonly ILogger<RuleRegistry> _logger;
    private readonly Lock _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly ITimer? _debounce;

    private RuleSet _current;

    /// <summary>Raised after the set has been replaced, with the set that is now running.</summary>
    public event Action<RuleSet>? Changed;

    /// <summary>The directory rules are read from and written to.</summary>
    public string Directory => _directory;

    /// <summary>The rules running right now. Immutable, so a caller may hold it across a reload.</summary>
    public RuleSet Current
    {
        get { lock (_gate) return _current; }
    }

    public RuleRegistry(string directory, ILogger<RuleRegistry> logger, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _directory = directory ?? string.Empty;
        _logger = logger;
        _current = RuleStore.LoadDirectory(_directory, logger);

        Announce(_current);

        if (_directory.Length == 0)
            return;

        // The directory is created rather than waited for. A host whose samples were never installed
        // still has somewhere for its first rule to land, and the watcher needs the directory to exist
        // before it can watch it.
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "rules: {Directory} could not be created ({Message}). Rules cannot be written here.",
                _directory, ex.Message);
            return;
        }

        clock ??= TimeProvider.System;
        _debounce = clock.CreateTimer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        try
        {
            _watcher = new FileSystemWatcher(_directory, "*" + RuleStore.RuleFileExtension)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnChanged;

            // A watcher that dies takes live editing with it and nothing else. Say so once and carry on
            // running the rules already loaded, rather than taking the daemon down over an amenity.
            _watcher.Error += (_, e) => _logger.LogWarning(
                "rules: the watch on {Directory} failed ({Message}). Edits made on disk will not be "
                + "picked up until this service restarts.", _directory, e.GetException().Message);

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(
                "rules: {Directory} could not be watched ({Message}). Edits made on disk will not be "
                + "picked up until this service restarts.", _directory, ex.Message);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        _debounce?.Change(SettleDelay, Timeout.InfiniteTimeSpan);

    /// <summary>Re-read the directory and publish whatever it now holds.</summary>
    public RuleSet Reload()
    {
        RuleSet next = RuleStore.LoadDirectory(_directory, _logger);
        return Publish(next);
    }

    /// <summary>
    /// Write one rule and adopt the result, or refuse it and leave the running set alone.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Validated against the set it would join, before anything is written.</b> A rule that
    /// cannot be honoured is refused rather than stored, so the directory never holds a rule this
    /// daemon then declines to run — which is the state that reads as "I saved it and nothing
    /// happened".
    /// </remarks>
    /// <returns>What was wrong with it, or empty when it was written and is now running.</returns>
    public IReadOnlyList<string> Replace(RuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (_directory.Length == 0)
            return ["this host has no rules directory configured, so rules cannot be written"];

        // Judged as part of the set rather than on its own: whether an id collides is a fact about the
        // other rules, and it is the one refusal that cannot be made by looking at a rule alone.
        RuleSet current = Current;
        List<RuleDefinition> proposed =
        [
            .. current.Rules.Concat(current.Retired).Where(r => !string.Equals(r.Id, definition.Id, StringComparison.Ordinal)),
            definition,
        ];

        RuleSet candidate = RuleStore.Resolve(proposed, []);
        if (candidate.Problems.Count > 0)
            return candidate.Problems;

        string path = RuleStore.PathOf(_directory, definition.Id);

        try
        {
            // Written beside and renamed, so the watcher and any reader see the whole file or the old
            // one. A rename within a directory is atomic; writing in place is not, and the watcher is
            // listening for exactly the events a partial write produces.
            string scratch = path + ".writing";
            File.WriteAllText(scratch, RuleStore.Write(definition));
            File.Move(scratch, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"{definition.Id} could not be written: {ex.Message}"];
        }

        Publish(RuleStore.LoadDirectory(_directory, _logger));
        return [];
    }

    /// <summary>
    /// Remove a rule's file outright.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Deleting is not retiring.</b> A retired rule keeps its file so that the decisions it
    /// already made still resolve to something nameable; deleting one leaves those decisions naming an
    /// id nothing can describe. The panel retires; this exists for a rule that was never meant to be.
    /// </remarks>
    public bool Remove(string ruleId)
    {
        if (_directory.Length == 0 || string.IsNullOrWhiteSpace(ruleId))
            return false;

        string path = RuleStore.PathOf(_directory, ruleId);

        try
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("rules: {Rule} could not be removed: {Message}", ruleId, ex.Message);
            return false;
        }

        Publish(RuleStore.LoadDirectory(_directory, _logger));
        return true;
    }

    private RuleSet Publish(RuleSet next)
    {
        lock (_gate)
            _current = next;

        Announce(next);
        Changed?.Invoke(next);
        return next;
    }

    private void Announce(RuleSet set)
    {
        if (set.Rules.Count == 0 && set.Retired.Count == 0)
        {
            _logger.LogInformation(
                "rules: {Directory} holds none, so nothing is being judged.", _directory);
            return;
        }

        _logger.LogInformation(
            "rules: {Live} running, {Retired} retired, {Problems} refused.",
            set.Rules.Count, set.Retired.Count, set.Problems.Count);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
