using Microsoft.Data.Sqlite;

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
/// <b>Decisions stay here and do not become journal events yet.</b> Two reasons, both from the plan:
/// the event vocabulary is a contract that is immutable once anything consumes it and is therefore
/// decided last, and under Native AOT a new event type is also a kgsm-lib release. A locally-typed
/// event written to a federated journal would meanwhile make four other services log
/// <c>Unknown event type</c> for every decision this leaf takes.
/// </para>
/// </remarks>
internal sealed class DecisionStore(ObservationLedger ledger)
{
    /// <summary>Creates the table. Idempotent, and called once at startup.</summary>
    public void Initialize() => ledger.Execute(
        """
        CREATE TABLE IF NOT EXISTS decisions (
            id            TEXT    NOT NULL PRIMARY KEY,
            rule_id       TEXT    NOT NULL,
            subject       TEXT    NOT NULL,
            episode_key   TEXT    NOT NULL,
            severity      TEXT    NOT NULL,
            mode          TEXT    NOT NULL,
            outcome       TEXT    NOT NULL,
            reason        TEXT    NOT NULL,
            action        TEXT    NOT NULL,
            action_state  TEXT    NOT NULL,
            opened_at     INTEGER NOT NULL,
            decided_at    INTEGER NOT NULL,
            src_producer  TEXT    NOT NULL,
            src_segment   TEXT    NOT NULL,
            src_offset    INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_dec_rule_subject ON decisions (rule_id, subject, decided_at);
        CREATE INDEX IF NOT EXISTS ix_dec_time ON decisions (decided_at);
        CREATE INDEX IF NOT EXISTS ix_dec_episode ON decisions (episode_key);
        """);

    /// <summary>
    /// Writes a decision, replacing any earlier verdict on the same episode.
    /// </summary>
    /// <remarks>
    /// An upsert rather than an insert: a state rule re-evaluates its episode on every sweep, and each
    /// pass is the same decision better informed — the condition that was open for four minutes is now
    /// open for forty. Appending each pass would turn one judgment into a stream and make the
    /// suppression window meaningless.
    /// </remarks>
    public void Record(Decision decision)
    {
        ledger.Execute(
            """
            INSERT INTO decisions
                (id, rule_id, subject, episode_key, severity, mode, outcome, reason,
                 action, action_state, opened_at, decided_at, src_producer, src_segment, src_offset)
            VALUES ($id, $rule, $subject, $episode, $severity, $mode, $outcome, $reason,
                    $action, $actionState, $openedAt, $decidedAt, $producer, $segment, $offset)
            ON CONFLICT(id) DO UPDATE SET
                outcome      = excluded.outcome,
                reason       = excluded.reason,
                action_state = excluded.action_state,
                decided_at   = excluded.decided_at;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", decision.Id);
                command.Parameters.AddWithValue("$rule", decision.RuleId);
                command.Parameters.AddWithValue("$subject", decision.Subject);
                command.Parameters.AddWithValue("$episode", decision.EpisodeKey);
                command.Parameters.AddWithValue("$severity", decision.Severity.ToString());
                command.Parameters.AddWithValue("$mode", decision.Mode.ToString());
                command.Parameters.AddWithValue("$outcome", decision.Outcome.ToString());
                command.Parameters.AddWithValue("$reason", decision.Reason);
                command.Parameters.AddWithValue("$action", decision.Action);
                command.Parameters.AddWithValue("$actionState", decision.ActionState.ToString());
                command.Parameters.AddWithValue("$openedAt", decision.OpenedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$decidedAt", decision.DecidedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$producer", decision.Source.Producer);
                command.Parameters.AddWithValue("$segment", decision.Source.Segment);
                command.Parameters.AddWithValue("$offset", decision.Source.Offset);
            });
    }

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
            SELECT id, rule_id, subject, episode_key, severity, mode, outcome, reason,
                   action, action_state, opened_at, decided_at, src_producer, src_segment, src_offset
            FROM decisions WHERE decided_at >= $since ORDER BY decided_at DESC;
            """,
            command => command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds()),
            Read);

    private static Decision Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        RuleId: reader.GetString(1),
        Subject: reader.GetString(2),
        EpisodeKey: reader.GetString(3),
        Severity: Enum.Parse<Rules.Severity>(reader.GetString(4)),
        Mode: Enum.Parse<Rules.RuleMode>(reader.GetString(5)),
        Outcome: Enum.Parse<DecisionOutcome>(reader.GetString(6)),
        Reason: reader.GetString(7),
        Action: reader.GetString(8),
        ActionState: Enum.Parse<ActionState>(reader.GetString(9)),
        OpenedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
        DecidedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
        Source: new EventSource(reader.GetString(12), reader.GetString(13), reader.GetInt64(14)));
}
