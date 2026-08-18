# Changelog

All notable changes to `kgsm-reactor` are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
