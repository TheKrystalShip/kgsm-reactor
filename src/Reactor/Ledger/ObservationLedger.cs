using Microsoft.Data.Sqlite;

using TheKrystalShip.Kgsm.Reactor.Classification;

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
    private const int SchemaVersion = 1;

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
                    (producer, segment, offset, event_type, class, subject_kind, subject,
                     actor, origin, occurred_at, observed_at)
                VALUES ($producer, $segment, $offset, $type, $class, $subjectKind, $subject,
                        $actor, $origin, $occurred, $observed);
                """;

            SqliteParameter producer = command.Parameters.Add("$producer", SqliteType.Text);
            SqliteParameter segment = command.Parameters.Add("$segment", SqliteType.Text);
            SqliteParameter offset = command.Parameters.Add("$offset", SqliteType.Integer);
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
