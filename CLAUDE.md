# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-reactor` is the **event-triggered** leaf of the KGSM ecosystem — the sibling of `kgsm-scheduler`,
which is the clock-triggered one. It reads every producer's event journal, evaluates rules against what
it sees, and records every decision. A rule's default mode is `observe`, so nothing is offered or
performed until somebody moves a named rule. The workspace keystone is `../system-architecture.md`;
**the authority for this project is `../kgsm-reactor-plan.md`**, which holds the design, the boundary
contract and every decision still open.

**Each part's rules live in a `CLAUDE.md` beside its code** under `src/Reactor/`: `Ingest/`,
`Classification/`, `Ledger/` (positions, migrations, decisions), `Engine/` (effective mode, verdicts,
what is announced), `Rules/` (rule files, loading, signals, rows and their prose), `Actions/`
(consequences and proposals), `Events/` and `Status/` (the socket).

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

# Redeem one. `by` is required and must be provider:name — the leaf refuses a confirmation that
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

/opt/kgsm-reactor/kgsm-reactor --report --days 7      # the population report, off the live ledger
/opt/kgsm-reactor/kgsm-reactor --decisions --days 7   # the decision review — what the reactor MADE of it
/opt/kgsm-reactor/kgsm-reactor --backfill --days 60   # journal history it was not running for; observations only, idempotent, safe live
/opt/kgsm-reactor/kgsm-reactor --verify               # every stored position still names its event; non-zero on drift
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
4. **A rule's default mode is `observe`.** Nothing is offered or performed until somebody moves a named
   rule. `RuleEngine.Honours` is a ceiling over that, never a substitute for it.
5. **It degrades to silence, never to a guess.** Cannot read the world ⇒ no decision.
6. **Every evaluation is recorded with its reason**, including the ones that decided not to act.
7. **It holds no delivery channel.** No Discord token, no VAPID key, no SMTP.

## Repo-specific rules

- **Never shell out to `kgsm.sh`.** All engine access goes through **kgsm-lib**, consumed as a
  versioned `PackageReference` from the org's GitHub Packages feed.
- **Never fabricate a status or a metric.** Measured, or explicitly unknown.
- **This leaf depends only on kgsm-lib.** Not on the API, not on a sibling leaf.
- **Native AOT, and nothing here needs the exemption.** The ledger is raw ADO over
  `Microsoft.Data.Sqlite`; EF Core is what is not AOT-safe, which is why `kgsm-api` is the ecosystem's
  one deliberate JIT exception and this leaf is not.
- **The settings file and `ReactorSettings` must agree**, in both directions, and
  `SettingsCoverageTests` fails the build when they do not. A key with no property binds to nothing; a
  property with no key is a knob documented nowhere and therefore absent from the leaf descriptor too.
  The descriptor itself needs no test — the generator writes it from the same type every build.
- **Numbers in `ReactorSettings` are nullable on purpose.** A blank env value binds to a non-nullable
  `int` by throwing (taking the unit down at startup) and a JSON null binds to `0` (silently discarding
  a default). Nullable makes both "unset".
- Work directly on **`main`** and commit there.

## Version tracking

- **Version source:** `<Version>` in `src/Reactor/Reactor.csproj`. `./deploy/version.sh` reads it;
  `--pkgver` prints the pacman-safe form. A package never restates a version — it asks for one.
- Bump on any user-facing change; patch for fixes, minor for features, major for breaking changes, with
  a `CHANGELOG.md` entry under `## [Unreleased]` in the same commit.
