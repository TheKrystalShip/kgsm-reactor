# Changelog

All notable changes to `kgsm-reactor` are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.8.0] - 2026-08-18

### Added — every journal line now carries its own id

Every event this leaf writes carries an `Id`: a UUIDv7 the shared writer mints per line, inherited by pinning
kgsm-lib 4.41.0. Nothing in this repo changed but the pin.

Why it exists: every durable reference to an event on this host is a byte offset into a named segment,
which holds only while a segment is appended to and deleted whole (conformance §2·l). An id makes a
rewrite **detectable** — a reference carrying both finds the line by position and proves it is the
right one by id, where before a shifted offset resolved to a real, parseable event of the wrong kind
with nothing to notice.

⚠ Optional and optional forever: lines written before this are on disk for as long as retention holds
them, and **absent means unknown, never a mismatch**. Authority: `journal-entry-id-plan.md`.

## [0.7.0] - 2026-08-18

A stored position is only as good as the promise that segments are never rewritten. This checks.

### Added

- **`kgsm-reactor --verify`** — walks every stored position, observations and decision source pointers
  alike, and reports the ones that no longer name the event they were stored for. Exits non-zero on
  drift, so a host can be told rather than have to look.
  - It exists for the failure that **does not announce itself**: an offset past the end of a segment
    or one landing mid-line raises on the next read, but a shifted offset resolves to a real,
    parseable event of the wrong kind, and every reading derived from it looks as trustworthy as one
    derived from the truth.
  - A pruned segment is counted apart from drift. Retention doing its job is not corruption, and a
    check that cried wolf on every host older than its window would be useless where it matters.
  - Read-only, and it repairs nothing. Rebuilding is a decision about data somebody may be reading a
    report from.

### Fixed

- **`--verify` on a ledger with no `decisions` table** threw instead of verifying. That is the state
  `--backfill` leaves on a host where the daemon has never run — exactly the host most worth checking.

## [0.6.0] - 2026-08-18

The reactor tails, so it knew only what had happened since it started. The journals are older than it
is.

### Added

- **`kgsm-reactor --backfill [--days N]`** — reads every producer's journal history into the
  observation ledger. Six weeks of this host, instead of the hours since the last deploy.
  - ⚠ **Observations only. No rule is evaluated and no event is written**, and that is not a
    limitation to be lifted. An observation restates a line that exists, so reading it late changes
    nothing about it; a decision is a judgment made against a world that answered at the time, and the
    rules ask the *live* world. Re-deriving old decisions would record judgments that were never made,
    on evidence that no longer exists, and afterwards they would be indistinguishable from the real
    ones.
  - Idempotent by construction: a row's identity is its position, so a segment read twice costs
    nothing and the mode can be re-run as history accrues with nobody tracking what was covered.
  - It classifies through the daemon's own `EventClassifier`. A second copy of that logic is how two
    readers of one journal come to disagree invisibly, both looking correct.
  - Rows older than the retention window are counted and reported rather than written silently — a
    backfill whose result disappears overnight is worse than one that refused.

### Fixed

- **The ledger waits rather than dropping a batch when two writers meet.** WAL lets a reader and the
  writer coexist; it does not let two writers do so, and there are two whenever a backfill runs against
  a daemon that is still observing. Without a busy timeout the loser got `SQLITE_BUSY` at once and
  dropped its batch — on the daemon's side, silently lost observations.

## [0.5.0] - 2026-08-18

The review that propose and act mode are gated behind, as something that can be performed.

### Added

- **`kgsm-reactor --decisions [--days N]`** — every judgment the reactor has reached, laid out to be
  argued with. Five readings: what each rule concluded and the share that never fired; the busiest
  rolling hour of fired decisions, which is what a ceiling has to tolerate; the gap between a rule's
  repeat fires on one subject, which is what a suppression window is derived from; **the rules that
  decided nothing at all**; and the decisions themselves, each carrying the journal position it was
  derived from so disagreeing with a verdict is a lookup rather than an archaeology.
  - Like the population report it **reports and does not recommend**. No window or ceiling is
    suggested — a printed figure carries an authority the arithmetic behind it has not earned.
  - An absent measurement is said out loud rather than rendered as a zero: one fire yields no repeat
    spacing, and the report says a window derived from it would be derived from nothing.

## [0.4.0] - 2026-08-18

A way to ask what it is doing right now.

### Added

- **A status endpoint on a unix socket** (`/run/kgsm-reactor/status.sock`), Kestrel on the slim
  builder — the same shape kgsm-monitor serves its scrape from. `GET /status` reports the running
  build, uptime, the gate's tuning as it is actually running, per-rule modes as **resolved** rather
  than as configured, and the evaluations woken and waiting out their settle windows. `GET /health` is
  liveness, deliberately not an alias: a reactor that is up but cannot read its ledger has to be able
  to say so on `/status` rather than failing liveness and looking dead.
  - The socket is never a port. Its filesystem permissions are the whole access boundary, so the mode
    is configurable as octal text and an unparseable value falls back rather than widening.
  - A blank `StatusSocketPath` turns the endpoint off and leaves the reactor judging as normal.
- **Ingest drops are surfaced.** A non-zero `droppedSinceStart` means the ledger is missing events
  that really happened and every rate derived from it under-reports — which would otherwise be
  indistinguishable from a quiet host.

### Changed

- **An unrecognised argument is refused.** `kgsm-reactor --version` used to fall through and start a
  second daemon against the same SQLite ledger, silently.
- Hosting, configuration, logging and Kestrel come from the Web SDK's framework reference instead of
  four pinned `PackageReference`s, which is how a package and the runtime beneath it come to disagree.

## [0.3.0] - 2026-08-18

Decisions leave this leaf. What it judges is now a line on the journal, where the rest of the host can
read it; still nothing is dispatched.

### Added

- **`reactor_decided`** — a rule's verdict as an event, carrying the rule, the subject and what kind of
  thing it is, the severity, the mode, the outcome, the reason, the action it would take and the server
  that action names, the decision's id, when the condition opened, and the position of the journal line
  it was all derived from. The field set is what kgsm-bot's `ServerAnnouncement` and kgsm-api's
  `NotificationEvent` each need to render from **one event**, with no second lookup.
- **`SubjectKind` on a decision**, carried from where the subject was observed rather than derived from
  its name. kgsm-bot routes on an instance name, and a host-scoped subject like `k10temp/Tctl` has no
  channel to follow — it has to know that rather than discover it by failing to find one.
- **`EventClass.Reactor`**, so this leaf's own events are classified and recorded like any other. The
  loop is stopped where it belongs instead: **no rule may wake on a `reactor_*` event**, and a test
  fails the build if one does.
- A stable `Name` and `TargetInstance` on every `ReactorAction`, for a reader that switches on the
  action rather than reading its prose.

### Changed

- **`DecisionStore.Record` reports what it changed.** The ledger folds every re-evaluation of one
  episode into a single row that gets better informed; a journal appends. An event is written on a
  transition — the first evaluation, or a changed verdict — so a condition that has held all afternoon
  is one judgment rather than one every thirty seconds.
- A ledger written by an earlier build gains the three new columns on startup. Existing rows read
  `unknown` for the subject's kind and the action's name, because those readings were not taken and
  taking them late would be a guess indistinguishable from a measurement afterwards.

## [0.2.0] - 2026-08-18

The judging half. Rules evaluate and record; nothing is dispatched, and that is how a rule earns the
right to act rather than a stage on the way to it.

### Added

- **A rule table, shipped in code.** Three rules, each having answered the seven questions in
  `kgsm-reactor-plan.md` §P2: `give_up_backup` (edge), `update_regression` (edge + correlation) and
  `threshold_stuck` (state). Configuration enables a rule, sets its mode and tunes its windows; it
  cannot invent one.
- **A three-valued `Verdict`** — holds, does not hold, or no judgment could be formed. The third
  value is the point: "cannot tell" must not be able to masquerade as "no", which would be silence,
  or as "yes", which would be acting blind.
- **A closed `ReactorAction` union**, so the never-list is enforced by the type system rather than by
  the discretion of whoever writes the next rule.
- **The `decisions` table.** Every evaluation is recorded with its reason, including the ones that
  decided not to act — those are the data the windows and the ceiling are tuned from. Each row
  carries the journal position it came from, so a decision being derived rather than a record is a
  column rather than a promise.
- **The gate**: settle window, suppression per (rule, subject), a host-wide hourly ceiling, and
  composition by severity — with one carve-out, that a purely additive action competes with nothing,
  because a regression wants the broken state preserved *and* the rollback offered.
- **Run state read from the supervisor** before every judgment. An event says what happened; only a
  read says what is true now, and an unreachable watchdog reads as "cannot tell" rather than as a
  condition that resolved itself.
- 26 further tests, 82 in total.

### Notes

- ⚠ **Every window and ceiling is a placeholder** until the population report has a week behind it.
  They are wired now so the gate's outcomes are recorded from the start, which turns "is 30 minutes
  the right window" from an opinion into a query.
- ⚠ **Propose and act are unbuilt.** A rule configured into either is clamped to observe and said out
  loud, because an operator who believes the host is acting when it is not is worse off than one who
  was refused.
- ⚠ `give_up_backup` cannot leave observe until a backup manifest records why an archive was taken
  and whether it may be pruned, and `update_regression` cannot name its target for the same reason.


### Fixed — a first setup on a host where nothing is installed yet completes

`deploy/setup.sh` enables its unit at boot and starts it only when something exists at the unit's
`ExecStart`. A host that has never deployed this project has an empty prefix, so the unit is enabled
and left stopped, and the summary names the unit that is enabled but not running and says
`deploy/deploy.sh` is what starts it. The fresh-host path is `setup.sh` → `deploy.sh` with nothing
in between.

The grant verification adapts with it, and still makes two real polkit-gated calls: `daemon-reload`,
plus one `manage-units` call on this project's own service — `start` when the service is running
(systemd queues a no-op job), `try-restart` when it is not (documented to do nothing for a unit that
is not running). Both are dispatched as the same `manage-units` action, so a host without the grant is
refused either way and the probe measures the grant rather than the unit.

⚠ Measured in the positive direction only. The deploying user on the development host is in
`wheel`, and two pre-existing polkit rules there grant that group every
`org.freedesktop.systemd1.*` action outright, so no systemctl call by that user can be refused
and the negative path cannot be exercised on it. That `try-restart` consults polkit before it
decides there is nothing to do is systemd's own dispatch order, not something this host can
demonstrate.

## [0.1.0] - 2026-08-18

The observing half — the leaf runs, watches and records. It decides nothing and acts on nothing.

### Added

- **The leaf itself.** A Native-AOT `net10.0` daemon, deployed by the ecosystem's standard
  `setup.sh` once / `deploy.sh` forever pattern, with a leaf config descriptor generated from its
  typed settings and installed where kgsm-api scans for it.
- **Federated event ingestion.** Reads every producer's journal through one `IEventSource` — all nine
  on a full host — at the tail and with no cursor, which is the ecosystem's rule for a consumer that
  acts.
- **Classification.** Each event reduced to the facts a later rule would be built on: a reporting
  bucket, whether it is about a server, the host or a component, and which one. Raw rather than typed,
  so an event type this build has never heard of is still recorded with its subject.
- **The observation ledger.** SQLite over raw ADO, keyed on `(producer, segment, offset)` so a
  re-read costs nothing, batched behind one transaction, pruned on a retention window.
- **The population report** (`--report [--days N] [--ledger PATH]`) — the three readings the rule
  table has to be designed against: rate and burst shape per event type, repeat interval per
  (type, subject), and how long each candidate condition takes to resolve itself.
- **Lifecycle reporting.** `leaf_ready` once ingestion is genuinely running, `leaf_degraded` when the
  ledger cannot be written, `leaf_stopping` on the way out. The deploy's health gate reads the first
  of those out of this leaf's own journal, bounded by a stamp taken before the service starts.
- 56 tests, hermetic.

[Unreleased]: https://github.com/TheKrystalShip/kgsm-reactor/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/TheKrystalShip/kgsm-reactor/releases/tag/v0.2.0
[0.1.0]: https://github.com/TheKrystalShip/kgsm-reactor/releases/tag/v0.1.0
