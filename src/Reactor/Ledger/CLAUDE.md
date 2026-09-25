# The ledger

`Microsoft.Data.Sqlite` over raw ADO — **EF Core is the part that is not AOT-safe**. Every row is
derived from a journal line; the journals are the record.

## Observations and positions

- **The row's identity is its position** — `(producer, segment, offset)` — not its content.
  Content-derived ids collapse two identical events in the same second into one row, which is a real
  defect in the engine's own index, and a rate measured from a ledger with it would under-report
  exactly the bursts a ceiling has to be set above.
- **The line's own id is carried beside the position, and is not the key.** `observations.event_id`,
  `decisions.src_event_id` and `reactor.decided`'s `SourceEventId` all hold the UUIDv7 the line's
  producer minted. The position *finds* the line; the id *proves* it is the right one. **This is what
  makes `--verify` real:** comparing event types misses a shift that lands on the same kind of event,
  which is the likely case — a journal is mostly repetitions of a handful of types. Where either side
  has no id the check falls back to the type, because absence is unknown and never a mismatch.
- **A rewritten segment silently invalidates the ledger.** A position is right only while segments are
  appended to and deleted whole. Deleting one line shifts every byte after it, and a stored position
  then resolves to a real, parseable event of the *wrong kind* — no error, nothing to notice.
  `--verify` is the detector. Do not clean test entries out of a journal: that is the record, not a
  view of it.
- **The ledger holds one event vocabulary.** Both ingest paths store the name an event is called now,
  and `NormalizeEventTypes` brings what earlier builds stored onto it at open — so a query asked in the
  current name reaches every row about that event, and the population report counts one condition
  once instead of splitting it across two spellings. `LegacyEventNames` in the journal package is the
  only thing that knows what a name was called before, and it is asked in the one direction it answers.
  A **segment** keeps whatever its producer wrote, so a stored name and the line it points at are equal
  as events long after they stop being equal as strings — which is why `--verify` compares them through
  the same table.
- **The ledger migrates in place.** `ObservationLedger.AddColumnIfMissing` covers the additive half, for
  both tables: every column added here is nullable, because a row restates a journal line and a new
  reading is something older rows simply do not carry. `CREATE TABLE IF NOT EXISTS` leaves an existing
  table alone, so a new column without a migration means a host stamped with the new schema version and
  no column — throwing on the next insert. Rebuilding instead is safe (every row is derived) and throws
  away `observed_at`, the one reading that cannot be recovered. A migration that rewrites values a row
  already holds is a step of its own; `NormalizeEventTypes` is the one of those.

## Decisions (`DecisionStore`)

- **The ledger upserts, the journal appends.** A state rule re-reads its episode every sweep and the
  ledger folds those into one row, so `DecisionStore.Record` returns a `DecisionChange` and only a
  transition is announced. Emitting per evaluation would make the journal a record of how often the
  reactor looked rather than what it concluded. `Record` compares the **outcome** and nothing else — a
  reason whose figures age as the condition does is the same judgment better informed.
- **Dispatch happens once, judged on `decisions.action_state` and not on the transition.** A state rule
  re-decides its episode every sweep and its reason ages with the condition — "open four minutes"
  becomes "open forty" — so a decision that *changed* is not one that should act again. The row is also
  the only answer that survives a restart.
- **One open offer per episode, enforced by a partial unique index on `decision_id`.** A check-then-
  insert has a window between the two; the index does not.
- **Two people confirming at once perform the action once.** The row is claimed by an `UPDATE ... WHERE
  state = 'open'` *before* the action runs, and only the call that changed a row goes on to do
  anything. Reading the state first and writing afterwards would let both through.
- **A decision carries who shaped the rule, beside the rule that made it.** `rule:<id>` stays the
  actor; `RuleAuthor` is provenance, a stable `provider:name` username. **Copied onto the decision,
  never joined at read time** — otherwise editing a rule rewrites the attribution of everything it ever
  decided, and retiring one erases the trace. **No fallback to the OS user**: a shipped sample, or a
  rule hand-written over SSH, is unattributed and says so.
