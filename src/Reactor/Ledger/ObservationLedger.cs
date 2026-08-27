using Microsoft.Data.Sqlite;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Ledger;

/// <summary>
/// The reactor's own store of what it has seen.
/// </summary>
/// <remarks>
/// <para>
/// <b>SQLite, raw ADO.</b> Everything the gate will ask is a query — has this fired inside the
/// window, how many actions this hour, how long does this condition take to clear — and an
/// append-only file answers each of those with a full scan or with an in-memory index rebuilt on
/// every boot. Raw <c>Microsoft.Data.Sqlite</c> rather than EF Core, which is the part that is not
/// AOT-safe; kgsm-monitor runs the same combination under Native AOT.
/// </para>
/// <para>
/// <b>Writes are batched and idempotent.</b> A busy evening on a popular server is hundreds of
/// player events an hour and the reactor must not be why the host's disk is busy, so observations
/// arrive in batches inside one transaction. Every insert is <c>INSERT OR IGNORE</c> on the
/// position, so a segment read twice costs nothing.
/// </para>
/// </remarks>
internal sealed class ObservationLedger : IDisposable
{
    /// <summary>The schema this build expects. Bumped when a migration becomes necessary.</summary>
    private const int SchemaVersion = 3;

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    /// <summary>
    /// Opens (and creates) the ledger at <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// One long-lived connection behind a lock rather than a pool. The write side is a single
    /// batching worker and the read side is a report that runs at most once in a session, so a pool
    /// would buy contention handling for contention that does not exist — and WAL plus one writer is
    /// the arrangement SQLite is happiest with.
    /// </remarks>
    public ObservationLedger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Fully qualified: this class exposes its own Path, which would otherwise shadow System.IO's.
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        _connection.Open();
        Initialize();
    }

    /// <summary>Where this ledger lives, for the sake of saying so in a log line.</summary>
    public string Path => _connection.DataSource;

    private void Initialize()
    {
        // WAL: a reader (the report) and the writer must not block each other, and a crash mid-write
        // must not cost the file. NORMAL synchronous because every row here is derived — a fsync per
        // commit would buy durability for data whose source of truth is a file on the same disk.
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");

        // WAL lets a reader and the writer coexist; it does not let TWO writers do so, and there are
        // two whenever a backfill runs against the ledger of a daemon that is still observing. Without
        // a busy timeout the loser of that race gets SQLITE_BUSY immediately and drops its batch —
        // which on the daemon's side is silently lost observations. Waiting is the right answer for
        // both: the batches are small and the contention is a burst, not a steady state.
        Execute("PRAGMA busy_timeout=10000;");

        Execute($"PRAGMA user_version={SchemaVersion};");

        Execute("""
            CREATE TABLE IF NOT EXISTS observations (
                producer     TEXT    NOT NULL,
                segment      TEXT    NOT NULL,
                offset       INTEGER NOT NULL,
                event_id     TEXT,
                event_type   TEXT    NOT NULL,
                class        TEXT    NOT NULL,
                subject_kind TEXT    NOT NULL,
                subject      TEXT    NOT NULL,
                actor        TEXT,
                origin       TEXT,
                occurred_at  INTEGER NOT NULL,
                observed_at  INTEGER NOT NULL,
                PRIMARY KEY (producer, segment, offset)
            ) WITHOUT ROWID;
            """);

        // The three questions the population report asks, in index form: rate by type over a window,
        // repeats of one type against one subject, and the whole-host burst shape.
        Execute("CREATE INDEX IF NOT EXISTS ix_obs_type_time ON observations (event_type, occurred_at);");
        Execute("CREATE INDEX IF NOT EXISTS ix_obs_subject_time ON observations (subject, occurred_at);");
        Execute("CREATE INDEX IF NOT EXISTS ix_obs_time ON observations (occurred_at);");

        // For a ledger that predates the column. CREATE TABLE IF NOT EXISTS leaves an existing table
        // exactly as it is, so a host carrying weeks of backfilled observations would otherwise be
        // told its schema is version 2 while the column is absent — and the next INSERT would throw.
        // Rebuilding instead would be safe (every row is derived) and would throw away the one thing
        // that cannot be re-derived: how long ago the reactor read each line.
        AddColumnIfMissing("observations", "event_id", "TEXT");

        NormalizeEventTypes();
    }

    /// <summary>
    /// Brings every stored <c>event_type</c> onto the name its event is called now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ledger holds one vocabulary, so a question asked in the current name reaches every row
    /// about that event and the population report counts one condition once. A row restates a journal
    /// line, and the line it points at is found by position rather than by name — which is what makes
    /// the stored name free to be the current one while the segment keeps whatever its producer wrote.
    /// <see cref="Ingest.JournalVerify"/> compares the two through the same table.
    /// </para>
    /// <para>
    /// Driven by the distinct values actually present rather than by a list of names to look for: the
    /// set is tiny, the rewrite touches only the values that change, and a second run finds nothing to
    /// do. <see cref="LegacyEventNames"/> is the one place that knows what a name was called before,
    /// and this asks it in the only direction it answers.
    /// </para>
    /// </remarks>
    private void NormalizeEventTypes()
    {
        List<string> stored = Query(
            "SELECT DISTINCT event_type FROM observations;", _ => { }, reader => reader.GetString(0));

        foreach (string was in stored)
        {
            string now = LegacyEventNames.Canonical(was);
            if (string.Equals(was, now, StringComparison.Ordinal))
                continue;

            Execute(
                "UPDATE observations SET event_type = $now WHERE event_type = $was;",
                command =>
                {
                    command.Parameters.AddWithValue("$now", now);
                    command.Parameters.AddWithValue("$was", was);
                });
        }
    }

    /// <summary>
    /// Adds a column to an existing table when it is not already there.
    /// </summary>
    /// <remarks>
    /// Every column this ledger adds is nullable and additive, because a row here restates a journal
    /// line and a new reading is something older rows simply do not carry. A migration that rewrites
    /// values a row already holds is a different kind of change and is written as its own step —
    /// <see cref="NormalizeEventTypes"/> is the one of those.
    /// </remarks>
    internal void AddColumnIfMissing(string table, string column, string declaration)
    {
        bool present = Query(
            $"PRAGMA table_info({table});",
            _ => { },
            reader => reader.GetString(1))
            .Any(name => string.Equals(name, column, StringComparison.Ordinal));

        if (!present)
            Execute($"ALTER TABLE {table} ADD COLUMN {column} {declaration};");
    }

    /// <summary>
    /// Records a batch of observations in one transaction.
    /// </summary>
    /// <returns>How many rows were new. A number below the batch size means the rest were already
    /// recorded, which is the ordinary result of re-reading a segment and is not a fault.</returns>
    public int Record(IReadOnlyList<Observation> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
            return 0;

        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO observations
                    (producer, segment, offset, event_id, event_type, class, subject_kind, subject,
                     actor, origin, occurred_at, observed_at)
                VALUES ($producer, $segment, $offset, $eventId, $type, $class, $subjectKind, $subject,
                        $actor, $origin, $occurred, $observed);
                """;

            SqliteParameter producer = command.Parameters.Add("$producer", SqliteType.Text);
            SqliteParameter segment = command.Parameters.Add("$segment", SqliteType.Text);
            SqliteParameter offset = command.Parameters.Add("$offset", SqliteType.Integer);
            SqliteParameter eventId = command.Parameters.Add("$eventId", SqliteType.Text);
            SqliteParameter type = command.Parameters.Add("$type", SqliteType.Text);
            SqliteParameter cls = command.Parameters.Add("$class", SqliteType.Text);
            SqliteParameter subjectKind = command.Parameters.Add("$subjectKind", SqliteType.Text);
            SqliteParameter subject = command.Parameters.Add("$subject", SqliteType.Text);
            SqliteParameter actor = command.Parameters.Add("$actor", SqliteType.Text);
            SqliteParameter origin = command.Parameters.Add("$origin", SqliteType.Text);
            SqliteParameter occurred = command.Parameters.Add("$occurred", SqliteType.Integer);
            SqliteParameter observed = command.Parameters.Add("$observed", SqliteType.Integer);

            int inserted = 0;
            foreach (Observation row in batch)
            {
                producer.Value = row.Producer;
                segment.Value = row.Segment;
                offset.Value = row.Offset;
                // DBNull, not "": absence is a spelling every reader here already handles, and an
                // empty string is a third state that compares unequal to both.
                eventId.Value = (object?)row.EventId ?? DBNull.Value;
                type.Value = row.EventType;
                cls.Value = row.Class.ToString();
                subjectKind.Value = row.SubjectKind.ToString();
                subject.Value = row.Subject;
                actor.Value = (object?)row.Actor ?? DBNull.Value;
                origin.Value = (object?)row.Origin ?? DBNull.Value;
                occurred.Value = row.OccurredAt.ToUnixTimeMilliseconds();
                observed.Value = row.ObservedAt.ToUnixTimeMilliseconds();
                inserted += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return inserted;
        }
    }

    /// <summary>
    /// Drops observations older than <paramref name="retention"/>.
    /// </summary>
    /// <remarks>
    /// Safe to run at any time and losing nothing: an observation restates a line that is still in
    /// its producer's journal, which is pruned on its own, longer schedule by the engine.
    /// </remarks>
    /// <returns>How many rows were removed.</returns>
    public int Prune(TimeSpan retention, DateTimeOffset now)
    {
        long cutoff = now.Subtract(retention).ToUnixTimeMilliseconds();

        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM observations WHERE occurred_at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>How many observations are held.</summary>
    public long Count()
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM observations;";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Runs a read query and projects each row, under the same lock the writer uses.
    /// </summary>
    /// <remarks>
    /// Internal so the population report can ask its own questions without this class growing a
    /// method per reading. The report is the only caller, the SQL is entirely its own, and nothing
    /// here interpolates anything a caller supplied.
    /// </remarks>
    internal List<T> Query<T>(string sql, Action<SqliteCommand> bind, Func<SqliteDataReader, T> project)
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            bind(command);

            var results = new List<T>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                results.Add(project(reader));
            return results;
        }
    }

    /// <summary>
    /// Runs a statement under the same lock every other access takes.
    /// </summary>
    /// <remarks>
    /// Internal so the decision store can own its own table and its own SQL while sharing this
    /// connection. One connection rather than two: the gate's questions cross both tables, and two
    /// connections to one file is how a reader comes to see a half-written view of them.
    /// </remarks>
    internal void Execute(string sql, Action<SqliteCommand>? bind = null)
    {
        lock (_gate)
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Close();
            _connection.Dispose();
        }
    }
}
