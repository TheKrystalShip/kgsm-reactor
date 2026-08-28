# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-reactor` is the **event-triggered** leaf of the KGSM ecosystem — the sibling of
`kgsm-scheduler`, which is the clock-triggered one. It reads every producer's event journal,
evaluates rules against what it sees, and records every decision. A rule's default mode is `observe`,
so nothing is offered or performed until somebody moves a named rule. The workspace keystone is
`../system-architecture.md`; **the authority for this project is `../kgsm-reactor-plan.md`**, which
holds the design, the boundary contract and every decision still open.

## Commands

```bash
# What it is doing right now. A unix socket, never a port.
curl --unix-socket /run/kgsm-reactor/status.sock http://localhost/status | jq

# The rules as they are actually running, and any file that could not be honoured.
curl -s --unix-socket /run/kgsm-reactor/status.sock http://localhost/status \
  | jq '{rulesDirectory, ruleFiles, problems,
         rules: [.rules[] | {id, mode, author, steps: (.rows | length)}]}'

# What a rule may be MADE of on this build — what the panel renders its editor from.
curl -s --unix-socket /run/kgsm-reactor/status.sock http://localhost/catalog \
  | jq '{honours, signals: [.signals[] | {id, kind, unit}], actions: [.actions[].id]}'

# What a rule WOULD decide right now, without becoming one of this host's rules. Nothing is stored,
# nothing is dispatched, and no decision is written — it is a read that happens to carry a body.
curl -s -X POST --unix-socket /run/kgsm-reactor/status.sock http://localhost/preview \
  -H 'Content-Type: application/json' -d '{"rule": { … }, "subject": "Ketchup"}' | jq

# What it MADE of what it saw — the same review --decisions prints, as JSON.
# ?days= defaults to 7 and is clamped to the ledger's retention; ?limit= caps the log, never the readings.
curl --unix-socket /run/kgsm-reactor/status.sock 'http://localhost/decisions?days=7' | jq

# What this host is offering, and what recently became of its offers.
curl -s --unix-socket /run/kgsm-reactor/status.sock http://localhost/proposals \
  | jq '{honours, open: [.open[] | {handle, rule, subject, action, expiresAt}],
         endings: (.recent | group_by(.state) | map({(.[0].state): length}) | add)}'

# Redeem one. ⚠ `by` is required and must be provider:name — the leaf refuses a confirmation that
# names nobody. Confirming re-derives the condition first, so a server that came back up on its own
# answers no_longer_applicable and nothing runs.
curl -s -X POST --unix-socket /run/kgsm-reactor/status.sock \
  http://localhost/proposals/<handle>/confirm \
  -H 'Content-Type: application/json' -d '{"by":"local:heisen"}' | jq

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

`deploy.sh` verifies against the **`leaf.ready` line this leaf writes to its own journal**, taking a
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
4. **A rule's default mode is `observe`.** Nothing is offered or performed until somebody moves a
   named rule. `RuleEngine.Honours` is a ceiling over that, never a substitute for it.
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
  `decisions.src_event_id` and `reactor.decided`'s `SourceEventId` all hold the UUIDv7 the line's
  producer minted. The position *finds* the line; the id *proves* it is the right one. ⚠ **This is
  what makes `--verify` real:** comparing event types misses a shift that lands on the same kind of
  event, which is the likely case — a journal is mostly repetitions of a handful of types. Where
  either side has no id the check falls back to the type, because absence is unknown and never a
  mismatch.
- **The ledger migrates in place.** `ObservationLedger.AddColumnIfMissing` covers the additive half,
  for both tables: every column added here is nullable, because a row restates a journal line and a
  new reading is something older rows simply do not carry. ⚠ `CREATE TABLE IF NOT EXISTS` leaves an
  existing table alone, so a new column without a migration means a host stamped with the new schema
  version and no column — throwing on the next insert. Rebuilding instead is safe (every row is
  derived) and throws away `observed_at`, the one reading that cannot be recovered. A migration that
  rewrites values a row already holds is a step of its own; `NormalizeEventTypes` is the one of those.
- **The ledger holds one event vocabulary.** Both ingest paths store the name an event is called now,
  and `NormalizeEventTypes` brings what earlier builds stored onto it at open — so a query asked in
  the current name reaches every row about that event, and the population report counts one condition
  once instead of splitting it across two spellings. `LegacyEventNames` in the journal package is the
  only thing that knows what a name was called before, and it is asked in the one direction it
  answers. ⚠ A **segment** keeps whatever its producer wrote, so a stored name and the line it points
  at are equal as events long after they stop being equal as strings — which is why `--verify`
  compares them through the same table.
- **`EventClass` is a reporting bucket, not a judgment, and nothing may start gating on it.** What
  matters about an event is decided per rule against the plan's seven questions, never inherited from
  a bucket assigned at ingest.
- **⚠ The reactor tails its own journal**, so every `reactor.decided` it writes comes straight back to
  it. That is fine and the events are recorded like any other; the loop is stopped by the rule that
  **no rule may wake on a `reactor.*` event**, refused at load by `RuleValidation` rather than left to
  a test over a compiled list.
- **The ledger upserts, the journal appends.** A state rule re-reads its episode every sweep and the
  ledger folds those into one row, so `DecisionStore.Record` returns a `DecisionChange` and only a
  transition is announced. Emitting per evaluation would make the journal a record of how often the
  reactor looked rather than what it concluded. ⚠ `Record` compares the **outcome** and nothing else —
  a reason whose figures age as the condition does is the same judgment better informed.
- **Everything is recorded; `RuleEngine.Announceable` decides what is announced.** The ledger holds
  every evaluation with its reason and `--decisions` reads it; the journal is a different audience —
  an audit log somebody skims, where a line costs attention whether or not it was worth having.
- **⚠ A withheld verdict is recorded and never announced.** Both halves of `Unreadable` are "cannot
  tell" and they are not the same news: `Verdict.Unreadable` is *something would not answer* (an
  operational fact — announced), `Verdict.Withhold` is *the rule declined to judge on evidence it
  read* (every coverage gate — recorded only). A gate reports what this leaf cannot yet say about an
  instance, which is unactionable by construction and the steady state for anything recently
  installed. `RuleEvaluator.ConcludeAsync` is the only place that can tell them apart — one step out
  they are indistinguishable. **The one exception:** a withheld verdict replacing a rule that was
  *firing* is announced, because a condition that stops being judged is news exactly when something
  was being judged.
- **Writing an event needs nothing from kgsm-lib.** `IEventJournalWriter.AppendAsync` takes a
  `JsonElement`, so emission is local and costs no package release. **Typed consumption is the part
  that does** — kgsm-bot reads through `RegisterHandler<T>() where T : KgsmEventDataBase`, which needs
  the class in the library and in `KgsmJsonContext`.
- **⚠ `BackgroundService.StartAsync` returning does not mean `ExecuteAsync` has begun.** A test that
  acts immediately after `StartAsync` can reach a service that has not registered its handler yet —
  which passes alone and fails under a parallel run. `EventIngestServiceTests.StartAndStopAsync` takes
  an explicit readiness condition for this reason. The daemon is unaffected: registration and
  `Initialize` are both inside `ExecuteAsync`, in that order.
- **A rule is data; the catalogs it draws on are code.** One JSON file per rule under `rules.d/` (the
  state directory, or `Reactor__RulesDirectory`) holds what wakes it, where its subjects come from, an
  ordered list of guard rows over signals, and one action. `Rules/Composition/` holds the rest:
  `SignalCatalog`, `SubjectSourceCatalog` and `ActionCatalog` are compiled, so a person composes from
  what this build can do and cannot reach past it.
- **⚠ No rule exists in code.** The four this build ships are files in `deploy/rules.d/`, installed
  into the state directory by `setup.sh` and ordinary rules from that moment. A rule defined in code
  would never travel through the parser, the validator or the watcher, leaving the path every
  hand-written rule depends on exercised only by hand-written rules — so the samples are what proves
  it. `ShippedRules` in the test project loads those same files through `RuleStore.LoadDirectory`.
  **An empty directory means no rules**, which is a state a host is allowed to be in.
- **The id inside a file is the file's name, and the loader checks rather than derives.** A file
  somebody copied and renamed would otherwise install a second rule under the first one's identity,
  folding two rules' decisions together under one actor.
- **A file that cannot be read costs one rule, not the set** — which is the whole reason a rule is a
  file. Each is parsed alone, and the problem names the file to fix.
- **`RuleRegistry` owns the set, and everything reads through it.** The engine judges through these
  rules and a redemption re-derives its condition through the same ones; a holder keeping its own copy
  would leave the two judging by different rules for as long as the daemon ran. A reload replaces the
  whole set in one assignment, so a sweep that started before a write finishes on the rules it began
  with. ⚠ **Evaluations still settling are dropped on a reload** — the rule that scheduled one may no
  longer say the same thing, and the condition reopens on the next match anyway.
- **The directory is watched, so a hand edit applies without a restart.** Debounced, because one save
  arrives as several filesystem events and an editor writing through a temporary file produces a
  burst. A write through the panel goes via `RuleRegistry.Replace`, which validates against the set the
  rule would join, writes beside and renames, and adopts the result — so a rule is never stored that
  the daemon then declines to run.
- **⚠ Signals are compiled because some are derived.** `drift.pctVsDeclared` is a footprint and a
  blueprint compared; expressing that as data needs an expression language, which would arrive one
  convenience at a time and end in predicates that parse while meaning something other than they read.
  A clause therefore holds no functions — `drift.absPctVsDeclared` exists as its own signal because it
  is what `abs(drift)` would have been.
- **⚠ Absent is a value; unreadable is not.** A blueprint declaring no minimum has been read, and the
  answer is "there is none". One that could not be read is a failure that ends the whole rule as
  `Unreadable` with the reader's own words. The four shipped rules turn on that distinction in five
  places.
- **Rows are ordered and the first match decides; a row is an AND.** OR is another row with the same
  outcome — which is why the drift rule has three positive-drift rows, each with its own sentence. A
  row stops at its first false clause, so a source a rule did not need is never read: an instance
  holding more than it was declared to need is reported without the trend ever being asked for.
- **A row owns its prose.** `{alias}` fills from the same reads the clauses used, `{alias#}` from what
  the row compares that signal against, `{alias@key}` from an argument it was bound with, plus
  `{subject}`, `{settleSeconds}`, `{openedAt}` and `{openFor}`. A row may carry a second sentence for
  when a signal it needs cannot be read. ⚠ **A comparand lookup is per row**, because the hours gate
  compares `footprint.observedHours` against 5 in one step and the unbroken-run stand-in compares it
  against 24 in another. ⚠ **Those five names are the evaluator's** and a rule that binds a measurement
  under one is refused at load — they resolve before bindings are consulted, so honouring it would let
  a rule save cleanly and then say something else in every sentence that mentioned it.
- **⚠ A sentence dates its condition or admits it cannot.** `{openedAt}` and `{openFor}` come from the
  journal line the episode opened on, are carried onto an offer so confirming reads the same instant
  staging did, and are **unanswered** for a rule that wakes on nothing — a footprint drifting from a
  declaration did not begin at a moment anybody observed, and the synthetic episode's stamp records
  when this daemon first looked. An unanswered one ends the sentence as `Unreadable`. Filling it from
  the evaluation instant would date a crash loop from the moment somebody glanced at it.
- **Every message is written for somebody who was not watching, and `MessageQualityTests` enforces the
  half of that a test can reach.** A sentence answers, in order: **what is true** (subject, symptom,
  since when), **on what evidence** (figures with units and denominators), **what is offered** (the
  verb, the target, and the artifact it names), and **what it costs**. Two hard rules — a reason names
  its own subject, because a push notification and an audit row carry the sentence and nothing around
  it; and no vocabulary that means something only inside this process (`ceilinged`, `superseded`, the
  settle window) reaches a person, because the wire outcome in the payload is where a program reads it.
- **What an action costs is the action's, and it is a separate sentence from the fault.** `Consequence`
  says what changes and whether it can be taken back — never how likely it is to help, which would be a
  claim about a fault nothing here has diagnosed. ⚠ **It must not name the instance**: `/catalog` serves
  it to an editor that has no instance to build an action for, and `ActionEntry.Consequence` builds
  against an empty name on exactly that understanding.
- **What an action *would* do is written in the infinitive; what it *did* is the performer's to say.**
  `Describe()` is carried by three sentences that are all about something not yet done. The past tense
  comes back on `ActionResult.Detail` from the thing that performed it — which is also the only thing
  that knows the id of what it produced, and an audit row that cannot name the archive it created
  cannot lead anybody to it.
- **Arguments bind once at rule level, under an alias.** Repeating them at each mention is how two
  mentions of "the last update" come to mean different windows. A signal that takes no arguments needs
  no binding: its own id is the alias.
- **Mode is a field on the rule** — `off`, `observe`, `propose`, `act` — clamped by
  `RuleEngine.Effective` and reported beside what the rule asked for. ⚠ **Off and retired are
  different.** Off is live, listed and one field from running again; retired is gone from the live list
  and kept only so its decisions still resolve to a rule that can be named.
- **A proposal is safe because the condition is re-derived at redemption, not because the window is
  short.** `ProposalService.ConfirmAsync` re-evaluates the rule against the world as it is now before
  it performs anything, so an offer answered in the morning about a server that came back up overnight
  ends as `no_longer_applicable`. That is what lets the lifetime be a shift where the assistant's
  confirmations are seconds. ⚠ **Do not tune the lifetime as a safety control** — shortening it buys
  nothing and loses the offers nobody was awake to see.
- **⚠ Unreadable at redemption is not a no.** A world that would not answer leaves the offer open and
  tells the person why. Ending it would record a conclusion nobody reached; performing anyway would act
  on a reading taken hours ago.
- **Redemption re-derives the condition and deliberately does not re-run the gate.** Suppression and
  the hourly ceiling govern how often the *reactor* speaks; at redemption the person is speaking.
- **Dispatch happens once, judged on `decisions.action_state` and not on the transition.** A state rule
  re-decides its episode every sweep and its reason ages with the condition — "open four minutes"
  becomes "open forty" — so a decision that *changed* is not one that should act again. The row is also
  the only answer that survives a restart.
- **One open offer per episode, enforced by a partial unique index on `decision_id`.** A check-then-
  insert has a window between the two; the index does not.
- **Two people confirming at once perform the action once.** The row is claimed by an `UPDATE ... WHERE
  state = 'open'` *before* the action runs, and only the call that changed a row goes on to do
  anything. ⚠ Reading the state first and writing afterwards would let both through.
- **⚠ The status socket takes exactly two writes: confirm and dismiss.** Everything else answers a
  question. These have to live here because confirming re-evaluates a rule, which only this leaf can
  do. **The leaf checks that a caller *named* itself as `provider:name`, never that it was *allowed*
  to** — it holds no identity system and no tiers, so authority stays with the surface that
  authenticated the person. What guards the socket is its mode and the handle being unguessable.
- **A rule is narrowed to one server with an ordinary guard row over `subject.id`.** Scope previews,
  reads in the editor and writes its own sentence when it declines. An "applies to" field beside the
  rows would be a second place a rule can decline from, invisible to the preview that exists to explain
  exactly that.
- **What could not be honoured is reported, never swallowed.** A misspelled signal, a step with no
  sentence, an action outside the catalog, a duplicate id, a rule judged the instant its event lands
  or an unparseable file each leaves that rule out with the rest of the file running, and lands in
  `RuleSet.Problems` → `/status.problems` and the log. All of them otherwise present as "I saved it and
  nothing happened", which is indistinguishable from a rule with nothing to say.
- **The leaf publishes, the panel writes.** `GET /catalog` serves what a rule may be made of, with
  types, units and prose, so a panel renders an editor without holding a copy. `POST /preview` says
  what a proposed rule would decide about this host right now — a read that carries a body, storing
  nothing, dispatching nothing and writing no decision. Composing and storing a rule is the panel's
  half, which writes the file and restarts the unit through the grant it already holds — the socket
  never edits a rule, and the only instructions it takes are the two redemptions. Validation happens
  twice — the panel against the catalog it was served, the leaf at
  load, which is the authority. ⚠ **An outcome is spelled the way `/catalog` spells it**
  (`doesNotHold`, not an enum name lowercased), or a panel classifies against ids that match nothing.
- **The rules stay the leaf's even when the panel edits them.** The directory is inside this daemon's
  own state directory, so a host with no kgsm-api reads and writes it directly; a panel edits a rule by
  asking the leaf to, never by writing into the directory itself. The leaf is told a path and never
  learns whose it is — which is what keeps it from becoming the first leaf to depend on the API.
- **⚠ A deploy never writes into the state directory.** `deploy.sh` refreshes the pristine samples under
  the install prefix, where `--delete` is correct because they are code; `setup.sh` seeds the state
  directory once and only for a file that is not already there. Keeping both copies is what lets the
  panel offer "reset to the sample" without a deploy reaching a rule somebody is running.
- **A decision carries who shaped the rule, beside the rule that made it.** `rule:<id>` stays the
  actor; `RuleAuthor` is provenance, a stable `provider:name` username, on the ledger and on
  `reactor.decided`. ⚠ **Copied onto the decision, never joined at read time** — otherwise editing a
  rule rewrites the attribution of everything it ever decided, and retiring one erases the trace. ⚠
  **No fallback to the OS user**: a shipped sample, or a rule hand-written over SSH, is unattributed and says
  so.
- **Settle and suppression are measured, and they stay measured.** The two windows are properties of
  how a condition behaves over time, read off 30 days of a host and pinned by `ShippedRuleTests` with
  each figure's basis. A composed rule that quietly lost the 45-minute threshold window would be a new
  rule wearing an old one's name, and its decisions would fold into the old one's episodes.
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
