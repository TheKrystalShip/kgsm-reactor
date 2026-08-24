# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-reactor` is the **event-triggered** leaf of the KGSM ecosystem — the sibling of
`kgsm-scheduler`, which is the clock-triggered one. It reads every producer's event journal,
evaluates rules against what it sees, and records every decision; a rule's default mode is
`observe`, so nothing acts until somebody moves it. The workspace keystone is
`../system-architecture.md`; **the authority for this project is `../kgsm-reactor-plan.md`**, which
holds the design, the boundary contract and every decision still open.

## Commands

```bash
# What it is doing right now. A unix socket, never a port.
curl --unix-socket /run/kgsm-reactor/status.sock http://localhost/status | jq

# What it MADE of what it saw — the same review --decisions prints, as JSON.
# ?days= defaults to 7 and is clamped to the ledger's retention; ?limit= caps the log, never the readings.
curl --unix-socket /run/kgsm-reactor/status.sock 'http://localhost/decisions?days=7' | jq

dotnet build kgsm-reactor.slnx -c Release
dotnet test  kgsm-reactor.slnx                          # hermetic; no host, no journals, no engine
dotnet test  kgsm-reactor.slnx --filter "FullyQualifiedName~EventClassifier"

# Native AOT — expect 0 IL2026/IL3050/ILC warnings.
dotnet publish src/Reactor/Reactor.csproj -c Release -r linux-x64

# The population report, off the live ledger. Needs nothing stopped.
/opt/kgsm-reactor/kgsm-reactor --report --days 7

# The decision review — what the reactor MADE of it.
/opt/kgsm-reactor/kgsm-reactor --decisions --days 7

# Read journal history the reactor was not running for. Observations only; idempotent; safe live.
/opt/kgsm-reactor/kgsm-reactor --backfill --days 60

# Check every stored position still names its event. Exits non-zero on drift.
/opt/kgsm-reactor/kgsm-reactor --verify
```

## Deploying

```bash
./deploy/setup.sh    # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh   # every deploy. NO sudo, NO prompts.
```

`deploy.sh` verifies against the **`leaf_ready` line this leaf writes to its own journal**, taking a
`READY_SINCE` stamp before the start so a line from the previous run cannot satisfy the check. That is
a stronger check than the status socket would be: `/health` answering only proves Kestrel is listening,
where the journal line is written after the ledger is open and the rules are resolved.

## The invariants (from the plan — these do not get re-decided)

1. **It is never the only record.** The journals are the record; an observation is derived. A reactor
   that was down during an incident must not be why nobody can reconstruct it.
2. **It never fabricates an actor.** Origin `reactor`, actor the rule id — written `rule:<id>`, in the
   ecosystem's `provider:name` actor shape. Never a person, never null.
3. **It never acts on what another supervisor owns.** The watchdog owns crash-restart, autostart and
   caps; the scheduler owns timed restarts, scheduled backups and update sweeps. The reactor acts on
   what the watchdog has **given up** on.
4. **A rule's default mode is `observe`.** Nothing acts until somebody moves it.
5. **It degrades to silence, never to a guess.** Cannot read the world ⇒ no decision.
6. **Every evaluation is recorded with its reason**, including the ones that decided not to act.
7. **It holds no delivery channel.** No Discord token, no VAPID key, no SMTP.

## Things that bite if you don't know them

- **Native AOT, and nothing here needs the exemption.** The ledger is `Microsoft.Data.Sqlite` over raw
  ADO — **EF Core is the part that is not AOT-safe**, which is why `kgsm-api` is the ecosystem's one
  deliberate JIT exception and this leaf is not. `kgsm-monitor` runs the same combination.
- **Ingestion is RAW, not typed.** `IEventService.RegisterRawHandler` takes every envelope, known type
  or not, and the subject is pulled out of the payload `JsonElement` by property name. A typed path
  would silently skip exactly the events a later rule might be about, and the skip would look like an
  event that never happened.
- **⚠ A backfill fills OBSERVATIONS and never decisions.** Reading a line late changes nothing about
  the line; a decision is a judgment made against a world that answered at the time, and the rules ask
  the *live* world. Re-deriving old decisions would record judgments that were never made, on evidence
  that no longer exists, and nothing afterwards could tell them from the real ones.
- **Tail, no cursor — deliberate, and it is the ecosystem's rule for a consumer that acts** (a
  replayed action is performed again for real). What it costs is events arriving while the process is
  down. The fix for that is *not* a cursor: it is expressing the rules that matter as **state** a rule
  re-derives from the world rather than **edges** it has to catch. See the plan's decision #3.
- **⚠ A rewritten segment silently invalidates the ledger.** A position is right only while segments
  are appended to and deleted whole. Deleting one line shifts every byte after it, and a stored
  position then resolves to a real, parseable event of the *wrong kind* — no error, nothing to notice.
  `--verify` is the detector. Do not clean test entries out of a journal: that is the record, not a
  view of it.
- **The row's identity is its position** — `(producer, segment, offset)` — not its content.
  Content-derived ids collapse two identical events in the same second into one row, which is a real
  defect in the engine's own index, and a rate measured from a ledger with it would under-report
  exactly the bursts a ceiling has to be set above.
- **The line's own id is carried beside the position, and is not the key.** `observations.event_id`,
  `decisions.src_event_id` and `reactor_decided`'s `SourceEventId` all hold the UUIDv7 the line's
  producer minted. The position *finds* the line; the id *proves* it is the right one. ⚠ **This is
  what makes `--verify` real:** comparing event types misses a shift that lands on the same kind of
  event, which is the likely case — a journal is mostly repetitions of a handful of types. Where
  either side has no id the check falls back to the type, because absence is unknown and never a
  mismatch.
- **The ledger migrates in place, additively.** `ObservationLedger.AddColumnIfMissing` is the whole
  mechanism, for both tables. Every column added here has been nullable, because a row restates a
  journal line and a new reading is something older rows simply do not carry. ⚠ `CREATE TABLE IF NOT
  EXISTS` leaves an existing table alone, so a new column without a migration means a host stamped
  with the new schema version and no column — throwing on the next insert. Rebuilding instead is safe
  (every row is derived) and throws away `observed_at`, the one reading that cannot be recovered.
- **`EventClass` is a reporting bucket, not a judgment, and nothing may start gating on it.** What
  matters about an event is decided per rule against the plan's seven questions, never inherited from
  a bucket assigned at ingest.
- **⚠ The reactor tails its own journal**, so every `reactor_decided` it writes comes straight back to
  it. That is fine and the events are recorded like any other; the loop is stopped by the rule that
  **no rule may wake on a `reactor_*` event**, which `RuleCatalogTests` fails the build over.
- **The ledger upserts, the journal appends.** A state rule re-reads its episode every sweep and the
  ledger folds those into one row, so `DecisionStore.Record` returns a `DecisionChange` and only a
  transition is announced. Emitting per evaluation would make the journal a record of how often the
  reactor looked rather than what it concluded.
- **Writing an event needs nothing from kgsm-lib.** `IEventJournalWriter.AppendAsync` takes a
  `JsonElement`, so emission is local and costs no package release. **Typed consumption is the part
  that does** — kgsm-bot reads through `RegisterHandler<T>() where T : KgsmEventDataBase`, which needs
  the class in the library and in `KgsmJsonContext`.
- **⚠ `BackgroundService.StartAsync` returning does not mean `ExecuteAsync` has begun.** A test that
  acts immediately after `StartAsync` can reach a service that has not registered its handler yet —
  which passes alone and fails under a parallel run. `EventIngestServiceTests.StartAndStopAsync` takes
  an explicit readiness condition for this reason. The daemon is unaffected: registration and
  `Initialize` are both inside `ExecuteAsync`, in that order.
- **The settings file and `ReactorSettings` must agree**, in both directions, and
  `SettingsCoverageTests` fails the build when they do not. A key with no property binds to nothing;
  a property with no key is a knob documented nowhere and therefore absent from the leaf descriptor
  too. The descriptor itself needs no test — the generator writes it from the same type every build.
- **Numbers in `ReactorSettings` are nullable on purpose.** A blank env value binds to a non-nullable
  `int` by throwing (taking the unit down at startup) and a JSON null binds to `0` (silently
  discarding a default). Nullable makes both "unset".

## Repo-specific rules

- **Never shell out to `kgsm.sh`.** All engine access goes through **kgsm-lib**, consumed as a
  versioned `PackageReference` from the org's GitHub Packages feed. The pinned version is what this
  repo compiles against, never the sibling checkout.
- **Never fabricate a status or a metric.** Measured, or explicitly unknown. It is why a payload that
  names no subject is recorded as `Unknown` rather than attributed to a plausible server.
- **This leaf depends only on kgsm-lib.** Not on the API, not on a sibling leaf. The watchdog and the
  monitor are *optional*: absent, the reactor observes everything else and simply never sees the kind
  of event they produce.
- Work directly on **`main`** and commit there.

## Version tracking

- **Version source:** `<Version>` in `src/Reactor/Reactor.csproj`. `./deploy/version.sh` reads it;
  `--pkgver` prints the pacman-safe form. A package never restates a version — it asks for one.
- Bump on any user-facing change; patch for fixes, minor for features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` in the same commit as the change it describes.

## Documentation & comments: present-tense canon only

Prose in this repo — every doc, `README`/`CLAUDE.md` section, and in-code comment — describes
**how the thing works right now**, nothing else. History lives in the `CHANGELOG` and git
history; never duplicate it into docs or code.

- **No transitions.** Never "was X, now Y", "used to…", "changed from…", "no longer…", or any
  before/after framing. State the current rule flat: a sentence that only makes sense to a reader
  who knows what the code *used to* do is dead weight, because that "before" no longer exists
  anywhere in the code.
- **Tombstones leave no marker.** When something is removed — dying naturally as part of the work,
  or explicitly asked to be deleted — the removal is silent: no *"removed X"*, no *"X is gone"*,
  no *"deprecated, use Y instead"* pointing at a corpse. The prose reads as if it never was. Code
  kept while the thing that justified it was deleted gets a live present-tense reason to exist —
  or goes too.
- **No residue of the active work.** References only meaningful *during* a piece of work don't
  survive it: *"temporary shim for the rework"*, *"added to satisfy the new requirement"*,
  milestone/phase labels (*"per M2"*, *"the Phase 1 step"*). If a line's justification is the work
  that produced it rather than the system as it now stands, it goes.
- **No volatile numbers.** Counts and versions that drift — how many projects/files/tests/
  partials exist, a dependency's pinned version, a file's line count — never go in prose: they are
  stale the moment anything changes, and nothing fails to remind anyone. Name the authoritative
  source instead (the csproj, the directory, the barrel file). A number belongs in prose only when
  it *is* the contract (a port, a timeout, a cap) or a measured fact that is itself the reason a
  design exists.
- **Edits are replacements, not appends.** When changing an existing feature, rewrite the affected
  doc/comment fresh as if writing it for the first time — never append a correction under the
  stale version, and never leave the stale version standing beside the new. The current revision
  does not converse with prior revisions.

A reader six months from now should learn the system from the doc without knowing what it
replaced. If you catch yourself explaining a change, stop — that sentence belongs in the commit
message. When touching prose that already violates this, rewrite it to present-tense canon in
passing.
