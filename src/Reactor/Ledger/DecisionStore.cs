using Microsoft.Data.Sqlite;

using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Ledger;

/// <summary>
/// Where decisions are kept.
/// </summary>
/// <remarks>
/// <para>
/// The same database as the observations, because the gate's questions cross both: "has this rule
/// fired for this subject inside the window" is a decisions query, and "since when has the condition
/// held" is an observations one, and answering them from two files would mean holding a consistent
/// view across two things that can disagree.
/// </para>
/// <para>
/// <b>A row here is working state; the journal line beside it is the record.</b> This upserts, so one
/// episode is a single row that gets better informed as it is re-evaluated — which is why
/// <see cref="Record"/> reports whether anything actually changed, and why only a change is worth a
/// <c>reactor.decided</c> line. A journal appends, and a condition that has held for six hours must not
/// read as seven hundred separate judgments.
/// </para>
/// </remarks>
internal sealed class DecisionStore(ObservationLedger ledger)
{
    /// <summary>Creates the table and brings an older one up to the current shape.</summary>
    /// <remarks>
    /// Idempotent, and called once at startup. The migration is here rather than in a separate step
    /// because a ledger that is one column short of what <see cref="Record"/> writes fails on the first
    /// decision of the run — which is to say, during an incident.
    /// </remarks>
    public void Initialize()
    {
        Create();
        AddMissingColumns();
    }

    private void Create() => ledger.Execute(
        """
        CREATE TABLE IF NOT EXISTS decisions (
            id            TEXT    NOT NULL PRIMARY KEY,
            rule_id       TEXT    NOT NULL,
            subject       TEXT    NOT NULL,
            subject_kind  TEXT    NOT NULL,
            episode_key   TEXT    NOT NULL,
            severity      TEXT    NOT NULL,
            mode          TEXT    NOT NULL,
            outcome       TEXT    NOT NULL,
            reason        TEXT    NOT NULL,
            rule_author   TEXT    NULL,
            action        TEXT    NOT NULL,
            action_name   TEXT    NOT NULL,
            action_inst   TEXT    NULL,
            action_state  TEXT    NOT NULL,
            opened_at     INTEGER NOT NULL,
            decided_at    INTEGER NOT NULL,
            src_producer  TEXT    NOT NULL,
            src_segment   TEXT    NOT NULL,
            src_offset    INTEGER NOT NULL,
            src_event_id  TEXT    NULL
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_dec_rule_subject ON decisions (rule_id, subject, decided_at);
        CREATE INDEX IF NOT EXISTS ix_dec_time ON decisions (decided_at);
        CREATE INDEX IF NOT EXISTS ix_dec_episode ON decisions (episode_key);
        """);

    /// <summary>
    /// The columns a ledger written by an older build does not have.
    /// </summary>
    /// <remarks>
    /// <b>Existing rows keep an honest answer, not a plausible one.</b> A decision recorded before the
    /// subject's kind was carried did not record it, so it reads <c>unknown</c> — the same spelling a
    /// payload that named no subject gets. Backfilling it by guessing from the name is exactly the
    /// fabrication this leaf refuses everywhere else, and the guess would be indistinguishable from a
    /// measurement afterwards.
    /// </remarks>
    private void AddMissingColumns()
    {
        (string Column, string Definition)[] wanted =
        [
            ("subject_kind", "TEXT NOT NULL DEFAULT 'Unknown'"),
            // 'unknown', never 'none': an old row's action was described in prose, it simply had no
            // machine name yet. Defaulting it to 'none' would claim the rule proposed nothing.
            ("action_name", "TEXT NOT NULL DEFAULT 'unknown'"),
            ("action_inst", "TEXT NULL"),
            // No default at all. A decision recorded before ids existed points at a line whose name
            // nobody knows, and null is how this leaf spells that everywhere else.
            ("src_event_id", "TEXT NULL"),
            // Likewise null: a decision taken before rules carried an author was taken by a rule this
            // build shipped, and stamping the build's name on it afterwards would invent a hand that
            // was never on it.
            ("rule_author", "TEXT NULL"),
        ];

        foreach ((string column, string definition) in wanted)
            ledger.AddColumnIfMissing("decisions", column, definition);
    }

    /// <summary>
    /// Writes a decision, replacing any earlier verdict on the same episode.
    /// </summary>
    /// <remarks>
    /// An upsert rather than an insert: a state rule re-evaluates its episode on every sweep, and each
    /// pass is the same decision better informed — the condition that was open for four minutes is now
    /// open for forty. Appending each pass would turn one judgment into a stream and make the
    /// suppression window meaningless.
    /// </remarks>
    public DecisionChange Record(Decision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        List<string> before = ledger.Query(
            "SELECT outcome FROM decisions WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", decision.Id),
            reader => reader.GetString(0));

        DecisionChange change = before.Count == 0
            ? DecisionChange.Opened
            : string.Equals(before[0], decision.Outcome.ToString(), StringComparison.Ordinal)
                ? DecisionChange.Unchanged
                : DecisionChange.Changed;

        ledger.Execute(
            """
            INSERT INTO decisions
                (id, rule_id, subject, subject_kind, episode_key, severity, mode, outcome, reason,
                 rule_author, action, action_name, action_inst, action_state, opened_at, decided_at,
                 src_producer, src_segment, src_offset, src_event_id)
            VALUES ($id, $rule, $subject, $subjectKind, $episode, $severity, $mode, $outcome, $reason,
                    $author, $action, $actionName, $actionInstance, $actionState, $openedAt, $decidedAt,
                    $producer, $segment, $offset, $eventId)
            ON CONFLICT(id) DO UPDATE SET
                outcome      = excluded.outcome,
                reason       = excluded.reason,
                -- Re-stamped with each pass, because a rule edited between two sweeps of one open
                -- episode was a different rule on the second: the decision names the hand that was
                -- on it when this verdict was reached, not the hand that opened the episode.
                rule_author  = excluded.rule_author,
                action_state = excluded.action_state,
                decided_at   = excluded.decided_at;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", decision.Id);
                command.Parameters.AddWithValue("$rule", decision.RuleId);
                command.Parameters.AddWithValue("$subject", decision.Subject);
                command.Parameters.AddWithValue("$subjectKind", decision.SubjectKind.ToString());
                command.Parameters.AddWithValue("$episode", decision.EpisodeKey);
                command.Parameters.AddWithValue("$severity", decision.Severity.ToWire());
                command.Parameters.AddWithValue("$mode", decision.Mode.ToString());
                command.Parameters.AddWithValue("$outcome", decision.Outcome.ToString());
                command.Parameters.AddWithValue("$reason", decision.Reason);
                command.Parameters.AddWithValue(
                    "$author", (object?)decision.RuleAuthor ?? DBNull.Value);
                command.Parameters.AddWithValue("$action", decision.Action);
                command.Parameters.AddWithValue("$actionName", decision.ActionName);
                command.Parameters.AddWithValue(
                    "$actionInstance", (object?)decision.ActionInstance ?? DBNull.Value);
                command.Parameters.AddWithValue("$actionState", decision.ActionState.ToString());
                command.Parameters.AddWithValue("$openedAt", decision.OpenedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$decidedAt", decision.DecidedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$producer", decision.Source.Producer);
                command.Parameters.AddWithValue("$segment", decision.Source.Segment);
                command.Parameters.AddWithValue("$offset", decision.Source.Offset);
                command.Parameters.AddWithValue(
                    "$eventId", (object?)decision.Source.EventId ?? DBNull.Value);
            });

        return change;
    }

    /// <summary>
    /// How far this decision's action already got, or null when the decision is new.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Read before the upsert, and what makes dispatch happen once.</b> A state rule re-decides
    /// its episode every sweep and the reason changes as the condition ages — "open for four minutes"
    /// becomes "open for forty" — so a decision that changed is not a decision that should act again.
    /// The row is the record of whether anything was dispatched for it, which is also what survives a
    /// restart: nothing in memory could answer this after the daemon came back.
    /// </remarks>
    public ActionState? ActionStateOf(string id) =>
        ledger.Query(
            "SELECT action_state FROM decisions WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", id),
            reader => Enum.TryParse(reader.GetString(0), out ActionState state) ? state : ActionState.None)
            .Cast<ActionState?>()
            .FirstOrDefault();

    /// <summary>Records how far a decision's action got, without touching anything else on the row.</summary>
    /// <remarks>
    /// A column write rather than another <see cref="Record"/>: re-recording would re-stamp
    /// <c>decided_at</c> and could report the decision as changed, which would put a second
    /// <c>reactor.decided</c> line on the journal for one judgment.
    /// </remarks>
    public void SetActionState(string id, ActionState state) =>
        ledger.Execute(
            "UPDATE decisions SET action_state = $state WHERE id = $id;",
            command =>
            {
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$state", state.ToString());
            });

    /// <summary>
    /// When this rule last fired for this subject, ignoring the episode currently being decided.
    /// </summary>
    /// <remarks>
    /// The episode is excluded because a rule re-evaluating its own open episode must not suppress
    /// itself — the window exists to stop the <em>next</em> occurrence being announced as news, not to
    /// stop this one being refined.
    /// </remarks>
    public DateTimeOffset? LastFired(string ruleId, string subject, string exceptEpisode)
    {
        List<long> rows = ledger.Query(
            """
            SELECT MAX(decided_at) FROM decisions
            WHERE rule_id = $rule AND subject = $subject AND outcome = 'Fired' AND episode_key <> $episode;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$rule", ruleId);
                command.Parameters.AddWithValue("$subject", subject);
                command.Parameters.AddWithValue("$episode", exceptEpisode);
            },
            reader => reader.IsDBNull(0) ? 0L : reader.GetInt64(0));

        long at = rows.Count > 0 ? rows[0] : 0L;
        return at > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
    }

    /// <summary>How many decisions fired host-wide since <paramref name="since"/>.</summary>
    /// <remarks>
    /// Counts distinct episodes rather than rows, so one long-running episode re-evaluated every
    /// sweep spends one of the hour's budget rather than all of it.
    /// </remarks>
    public int FiredSince(DateTimeOffset since)
    {
        List<int> rows = ledger.Query(
            "SELECT COUNT(DISTINCT episode_key) FROM decisions WHERE outcome = 'Fired' AND decided_at >= $since;",
            command => command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds()),
            reader => reader.GetInt32(0));

        return rows.Count > 0 ? rows[0] : 0;
    }

    /// <summary>
    /// The most severe rule that has already fired on this episode, other than the one asking.
    /// </summary>
    /// <remarks>
    /// ⚠ Read together with <see cref="Rules.ReactorAction.ChangesServerState"/>, which is what
    /// exempts an additive action: a regression wants the broken state preserved <em>and</em> the
    /// rollback offered, so a backup must never be superseded by the proposal beside it.
    /// </remarks>
    public IReadOnlyList<(string RuleId, string Severity)> FiredOnEpisode(string episodeKey, string exceptRule) =>
        ledger.Query(
            """
            SELECT rule_id, severity FROM decisions
            WHERE episode_key = $episode AND outcome = 'Fired' AND rule_id <> $rule;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$episode", episodeKey);
                command.Parameters.AddWithValue("$rule", exceptRule);
            },
            reader => (reader.GetString(0), reader.GetString(1)));

    /// <summary>Every decision since <paramref name="since"/>, newest first. For the report.</summary>
    public IReadOnlyList<Decision> Since(DateTimeOffset since) =>
        ledger.Query(
            """
            SELECT id, rule_id, subject, subject_kind, episode_key, severity, mode, outcome, reason,
                   action, action_name, action_inst, action_state, opened_at, decided_at,
                   src_producer, src_segment, src_offset, src_event_id, rule_author
            FROM decisions WHERE decided_at >= $since ORDER BY decided_at DESC;
            """,
            command => command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds()),
            Read);

    private static Decision Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        RuleId: reader.GetString(1),
        Subject: reader.GetString(2),
        // A row an older build wrote carries the migration's 'Unknown' rather than a kind derived
        // from the subject string now — the reading was not taken, and taking it late would be a
        // guess dressed as one.
        SubjectKind: Enum.TryParse(reader.GetString(3), out Classification.SubjectKind kind)
            ? kind
            : Classification.SubjectKind.Unknown,
        EpisodeKey: reader.GetString(4),
        Severity: ReadSeverity(reader.GetString(5)),
        Mode: Enum.Parse<Rules.RuleMode>(reader.GetString(6)),
        Outcome: Enum.Parse<DecisionOutcome>(reader.GetString(7)),
        Reason: reader.GetString(8),
        // Appended to the projection rather than inserted in its natural place, so every index below
        // keeps meaning what it did. A reader whose columns shift by one is a reader that silently
        // reports the action where the reason was.
        RuleAuthor: reader.FieldCount > 19 && !reader.IsDBNull(19) ? reader.GetString(19) : null,
        Action: reader.GetString(9),
        ActionName: reader.GetString(10),
        ActionInstance: reader.IsDBNull(11) ? null : reader.GetString(11),
        ActionState: Enum.Parse<ActionState>(reader.GetString(12)),
        OpenedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
        DecidedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
        Source: new EventSource(
            reader.GetString(15), reader.GetString(16), reader.GetInt64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18)));

    /// <summary>
    /// Reads a severity a row holds, whichever spelling wrote it.
    /// </summary>
    /// <remarks>
    /// ⚠ Rows outlive the code that wrote them. The ledger is not rebuilt on an upgrade, so a spelling
    /// this build would not produce is still sitting in it — and a strict parse would throw on the
    /// first read of a row nobody has touched in weeks, taking down a report rather than reporting.
    /// An unreadable value falls back to <see cref="EventSeverity.Info"/>, because a row whose weight
    /// cannot be established is not evidence that it was severe.
    /// </remarks>
    private static EventSeverity ReadSeverity(string? stored)
    {
        if (EventSeverities.TryParse(stored, out EventSeverity wire)) return wire;
        if (Enum.TryParse(stored, ignoreCase: true, out EventSeverity named)) return named;
        return stored?.StartsWith("warn", StringComparison.OrdinalIgnoreCase) == true
            ? EventSeverity.Warn
            : EventSeverity.Info;
    }
}
