using Microsoft.Data.Sqlite;

using TheKrystalShip.Kgsm.Reactor.Classification;
using TheKrystalShip.Kgsm.Reactor.Ledger;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Kgsm.Reactor.Actions;

/// <summary>
/// Where staged proposals are kept.
/// </summary>
/// <remarks>
/// <para>
/// The same database as the decisions and the observations, and for the same reason: a proposal is a
/// decision that was offered, the questions cross both tables, and a second file is a second thing that
/// can disagree.
/// </para>
/// <para>
/// <b>One open offer per episode, enforced by the index rather than by a check.</b> A state rule
/// re-evaluates its episode on every sweep and would otherwise stage a fresh proposal each time — the
/// same offer, thirty seconds apart, until somebody answered a hundred of them. The decision id is
/// stable per rule, subject and episode, so a partial unique index on it is exactly the constraint:
/// staging a duplicate fails at the database and the caller reads that as "already offered".
/// </para>
/// <para>
/// <b>Nothing is deleted and nothing stays open.</b> Every proposal ends, including the ones nobody
/// answered — <see cref="Expired"/> is what makes "how did this rule's offers end" a question a week's
/// review can put to the ledger, rather than a count of what happens to still be lying around.
/// </para>
/// </remarks>
internal sealed class ProposalStore(ObservationLedger ledger)
{
    /// <summary>Creates the table and brings an existing one up to date. Idempotent, called at startup.</summary>
    public void Initialize()
    {
        Create();

        // CREATE TABLE IF NOT EXISTS leaves an existing table exactly as it is, so a column added
        // here reaches a host that already has the table only through this. Nullable, like every
        // additive column in this ledger: an offer staged before the condition was dated does not
        // carry one, and unknown is the honest answer for it.
        ledger.AddColumnIfMissing("proposals", "opened_at", "INTEGER NULL");
    }

    private void Create() => ledger.Execute(
        """
        CREATE TABLE IF NOT EXISTS proposals (
            handle        TEXT    NOT NULL PRIMARY KEY,
            decision_id   TEXT    NOT NULL,
            rule_id       TEXT    NOT NULL,
            rule_author   TEXT    NULL,
            subject       TEXT    NOT NULL,
            subject_kind  TEXT    NOT NULL,
            episode_key   TEXT    NOT NULL,
            severity      TEXT    NOT NULL,
            action_name   TEXT    NOT NULL,
            action        TEXT    NOT NULL,
            action_inst   TEXT    NULL,
            reason        TEXT    NOT NULL,
            opened_at     INTEGER NULL,
            staged_at     INTEGER NOT NULL,
            expires_at    INTEGER NOT NULL,
            state         TEXT    NOT NULL,
            answered_at   INTEGER NULL,
            answered_by   TEXT    NULL,
            ok            INTEGER NULL,
            artifact      TEXT    NULL,
            detail        TEXT    NULL
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_prop_state ON proposals (state, expires_at);
        CREATE INDEX IF NOT EXISTS ix_prop_rule ON proposals (rule_id, staged_at);
        CREATE INDEX IF NOT EXISTS ix_prop_staged ON proposals (staged_at);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_prop_open_decision
            ON proposals (decision_id) WHERE state = 'open';
        """);

    /// <summary>
    /// Stages a proposal, unless one is already open for the same decision.
    /// </summary>
    /// <remarks>
    /// The duplicate is refused by the index rather than by a preceding read, which is what makes it
    /// correct under a sweep and an operator confirming at the same moment: a check-then-insert has a
    /// window between the two, and this does not.
    /// </remarks>
    /// <returns>Whether it was staged. False means an offer for this episode is already open.</returns>
    public bool Stage(Proposal proposal)
    {
        try
        {
            ledger.Execute(
                """
                INSERT INTO proposals (
                    handle, decision_id, rule_id, rule_author, subject, subject_kind, episode_key,
                    severity, action_name, action, action_inst, reason, opened_at, staged_at,
                    expires_at, state, answered_at, answered_by, ok, artifact, detail)
                VALUES (
                    $handle, $decision, $rule, $author, $subject, $kind, $episode,
                    $severity, $actionName, $action, $actionInst, $reason, $opened, $staged,
                    $expires, $state, NULL, NULL, NULL, NULL, NULL);
                """,
                command =>
                {
                    command.Parameters.AddWithValue("$handle", proposal.Handle);
                    command.Parameters.AddWithValue("$decision", proposal.DecisionId);
                    command.Parameters.AddWithValue("$rule", proposal.RuleId);
                    command.Parameters.AddWithValue("$author", (object?)proposal.RuleAuthor ?? DBNull.Value);
                    command.Parameters.AddWithValue("$subject", proposal.Subject);
                    command.Parameters.AddWithValue("$kind", proposal.SubjectKind.ToString());
                    command.Parameters.AddWithValue("$episode", proposal.EpisodeKey);
                    command.Parameters.AddWithValue("$severity", proposal.Severity.ToString());
                    command.Parameters.AddWithValue("$actionName", proposal.ActionName);
                    command.Parameters.AddWithValue("$action", proposal.Action);
                    command.Parameters.AddWithValue("$actionInst", (object?)proposal.ActionInstance ?? DBNull.Value);
                    command.Parameters.AddWithValue("$reason", proposal.Reason);
                    command.Parameters.AddWithValue("$opened",
                        proposal.OpenedAt is { } opened
                            ? opened.ToUnixTimeSeconds()
                            : (object)DBNull.Value);
                    command.Parameters.AddWithValue("$staged", proposal.StagedAt.ToUnixTimeSeconds());
                    command.Parameters.AddWithValue("$expires", proposal.ExpiresAt.ToUnixTimeSeconds());
                    command.Parameters.AddWithValue("$state", Wire(proposal.State));
                });

            return true;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == UniqueViolation)
        {
            return false;
        }
    }

    /// <summary>The proposal a handle names, or null when nothing carries it.</summary>
    public Proposal? Find(string handle) =>
        ledger.Query(
            $"SELECT {Columns} FROM proposals WHERE handle = $handle;",
            command => command.Parameters.AddWithValue("$handle", handle),
            Read).FirstOrDefault();

    /// <summary>
    /// Every open proposal, soonest to expire first.
    /// </summary>
    /// <remarks>
    /// <b>Includes ones whose lifetime has already passed.</b> Filtering by the clock here would hide
    /// a proposal that is about to be lapsed but has not been, and a surface reading this a second
    /// before the sweep would show a different list from the one the sweep is about to close — a
    /// disagreement between two readings of the same instant. Lapsing is a write, and it happens in one
    /// place.
    /// </remarks>
    public IReadOnlyList<Proposal> Open() =>
        ledger.Query(
            $"SELECT {Columns} FROM proposals WHERE state = 'open' ORDER BY expires_at ASC;",
            _ => { },
            Read);

    /// <summary>The most recent proposals, whatever became of them, newest first.</summary>
    public IReadOnlyList<Proposal> Recent(DateTimeOffset notBefore, int limit) =>
        ledger.Query(
            $"""
            SELECT {Columns} FROM proposals
            WHERE staged_at >= $since
            ORDER BY staged_at DESC
            LIMIT $limit;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$since", notBefore.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$limit", Math.Max(limit, 1));
            },
            Read);

    /// <summary>
    /// Ends an open proposal.
    /// </summary>
    /// <remarks>
    /// <b>The <c>WHERE</c> requires it to still be open, and the row count is the answer.</b> Two
    /// people pressing confirm at the same moment both find a redeemable proposal and both come here;
    /// only one changes a row, and the other must not perform the action. Reading the state first and
    /// writing afterwards would let both through.
    /// </remarks>
    /// <returns>Whether this call was the one that ended it.</returns>
    public bool End(
        string handle, ProposalState state, DateTimeOffset at, string? by,
        bool? ok = null, string? artifact = null, string? detail = null) =>
        ledger.Execute(
            """
            UPDATE proposals
               SET state = $state, answered_at = $at, answered_by = $by,
                   ok = $ok, artifact = $artifact, detail = $detail
             WHERE handle = $handle AND state = 'open';
            """,
            command =>
            {
                command.Parameters.AddWithValue("$handle", handle);
                command.Parameters.AddWithValue("$state", Wire(state));
                command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$by", (object?)by ?? DBNull.Value);
                command.Parameters.AddWithValue("$ok", ok is null ? DBNull.Value : ok.Value ? 1 : 0);
                command.Parameters.AddWithValue("$artifact", (object?)artifact ?? DBNull.Value);
                command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            }) == 1;

    /// <summary>
    /// Records how the action on an already-ended proposal went.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="End"/> on purpose, and it is the ordering that matters.</b> A
    /// confirmation claims the row first and performs afterwards, so two people pressing confirm
    /// together cannot both restore the same server. That leaves a moment where the row says confirmed
    /// and nothing yet says how it went, and this is what closes it — written whatever happened,
    /// because a confirmed proposal whose action failed is a complete fact and the one somebody
    /// investigating most needs to find.
    /// </remarks>
    public void Fill(string handle, bool ok, string? artifact, string? detail) =>
        ledger.Execute(
            "UPDATE proposals SET ok = $ok, artifact = $artifact, detail = $detail WHERE handle = $handle;",
            command =>
            {
                command.Parameters.AddWithValue("$handle", handle);
                command.Parameters.AddWithValue("$ok", ok ? 1 : 0);
                command.Parameters.AddWithValue("$artifact", (object?)artifact ?? DBNull.Value);
                command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            });

    /// <summary>
    /// Every open proposal whose lifetime has run out, so the caller can close each one.
    /// </summary>
    /// <remarks>
    /// Read rather than closed in bulk: a lapse is a fact worth a journal line — the offer nobody
    /// answered is the single most useful thing a week's review can count — and a bulk update would
    /// close a dozen of them with nothing able to say which.
    /// </remarks>
    public IReadOnlyList<Proposal> Expired(DateTimeOffset now) =>
        ledger.Query(
            $"SELECT {Columns} FROM proposals WHERE state = 'open' AND expires_at <= $now;",
            command => command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds()),
            Read);

    /// <summary>How proposals staged since <paramref name="notBefore"/> ended, by rule.</summary>
    /// <remarks>
    /// The shape a review reads: a rule whose offers are mostly confirmed is a candidate for acting on
    /// its own, and one whose offers mostly lapse is one nobody wants. Counted here rather than in the
    /// caller because the answer is a group-by and pulling every row to count them in memory would read
    /// a month to answer a question about a week.
    /// </remarks>
    public IReadOnlyList<(string Rule, string State, int Count)> EndingsByRule(DateTimeOffset notBefore) =>
        ledger.Query(
            """
            SELECT rule_id, state, COUNT(*) FROM proposals
            WHERE staged_at >= $since
            GROUP BY rule_id, state
            ORDER BY rule_id ASC, COUNT(*) DESC;
            """,
            command => command.Parameters.AddWithValue("$since", notBefore.ToUnixTimeSeconds()),
            reader => (reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));

    /// <summary>
    /// How a state is spelled on disk.
    /// </summary>
    /// <remarks>
    /// <b>The four ends are <see cref="ReactorResolutions"/>' own spellings</b>, so a row and the
    /// journal line written about it cannot disagree — and neither is derived from a C# enum name,
    /// which would put <c>NoLongerApplicable</c> where every consumer expects
    /// <c>no_longer_applicable</c>.
    /// </remarks>
    public static string Wire(ProposalState state) => state switch
    {
        ProposalState.Open => "open",
        ProposalState.Confirmed => ReactorResolutions.Confirmed,
        ProposalState.Dismissed => ReactorResolutions.Dismissed,
        ProposalState.Lapsed => ReactorResolutions.Lapsed,
        ProposalState.NoLongerApplicable => ReactorResolutions.NoLongerApplicable,
        _ => "open",
    };

    /// <summary>SQLite's code for a uniqueness violation, which is how a duplicate offer is refused.</summary>
    private const int UniqueViolation = 19;

    private const string Columns =
        "handle, decision_id, rule_id, rule_author, subject, subject_kind, episode_key, severity, "
        + "action_name, action, action_inst, reason, opened_at, staged_at, expires_at, state, "
        + "answered_at, answered_by, ok, artifact, detail";

    private static Proposal Read(SqliteDataReader reader) => new(
        Handle: reader.GetString(0),
        DecisionId: reader.GetString(1),
        RuleId: reader.GetString(2),
        RuleAuthor: reader.IsDBNull(3) ? null : reader.GetString(3),
        Subject: reader.GetString(4),
        SubjectKind: Enum.TryParse(reader.GetString(5), out SubjectKind kind) ? kind : SubjectKind.Unknown,
        EpisodeKey: reader.GetString(6),
        Severity: Enum.TryParse(reader.GetString(7), out EventSeverity severity) ? severity : EventSeverity.Info,
        ActionName: reader.GetString(8),
        Action: reader.GetString(9),
        ActionInstance: reader.IsDBNull(10) ? null : reader.GetString(10),
        Reason: reader.GetString(11),
        OpenedAt: reader.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12)),
        StagedAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(13)),
        ExpiresAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(14)),
        State: Parse(reader.GetString(15)),
        AnsweredAt: reader.IsDBNull(16) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(16)),
        AnsweredBy: reader.IsDBNull(17) ? null : reader.GetString(17),
        Ok: reader.IsDBNull(18) ? null : reader.GetInt64(18) != 0,
        Artifact: reader.IsDBNull(19) ? null : reader.GetString(19),
        Detail: reader.IsDBNull(20) ? null : reader.GetString(20));

    private static ProposalState Parse(string state) => state switch
    {
        ReactorResolutions.Confirmed => ProposalState.Confirmed,
        ReactorResolutions.Dismissed => ProposalState.Dismissed,
        ReactorResolutions.Lapsed => ProposalState.Lapsed,
        ReactorResolutions.NoLongerApplicable => ProposalState.NoLongerApplicable,
        _ => ProposalState.Open,
    };
}
