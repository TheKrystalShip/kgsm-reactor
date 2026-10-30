# CLAUDE.md

`kgsm-reactor` is the event-triggered leaf, the sibling of `kgsm-scheduler`'s clock. It reads every
producer's journal, evaluates rules against what it sees and records every decision; a rule's default mode
is `observe`. Role, contracts and peers: `../kgsm-docs/components/kgsm-reactor.md`. The model, the seven
invariants and the gate: `../kgsm-docs/systems/operations/reactor.md`; why:
`../kgsm-docs/decisions/0021-reactor-observes-by-default.md`; what is unbuilt:
`../kgsm-docs/plans/kgsm-reactor.md`. Settings: `CONFIGURATION.md`.

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

# Redeem one. The node relays the confirming person's access token as a bearer; the leaf refuses a
# confirmation whose token does not list the offer's action (403 refused, the offer left open).
# Confirming re-derives the condition first, so a server that came back up on its own answers
# no_longer_applicable and nothing runs.
curl -s -X POST --unix-socket /run/kgsm-reactor/status.sock \
  http://localhost/proposals/<handle>/confirm -H "Authorization: Bearer $(tks-login token panel)" | jq

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

## Rules for editing

- **The seven invariants in `systems/operations/reactor.md` are not re-decided here.** Two bite in code:
  the actor is always `rule:<id>` with origin `reactor`, never a person and never null; and
  `RuleEngine.Honours` is a ceiling over a rule's default `observe`, never a substitute for it.
- **Never shell out to `kgsm.sh`.** All engine access goes through kgsm-lib, a versioned
  `PackageReference`. The leaf depends on nothing else: not the API, not a sibling leaf.
- **Native AOT.** The ledger is raw ADO over `Microsoft.Data.Sqlite`; EF Core is not AOT-safe.
- **The settings file and `ReactorSettings` must agree**, in both directions, and
  `SettingsCoverageTests` fails the build when they do not. A key with no property binds to nothing; a
  property with no key is a knob documented nowhere and therefore absent from the leaf descriptor too.
  The descriptor itself needs no test — the generator writes it from the same type every build.
- **Numbers in `ReactorSettings` are nullable on purpose.** A blank env value binds to a non-nullable
  `int` by throwing (taking the unit down at startup) and a JSON null binds to `0` (silently discarding
  a default). Nullable makes both "unset".

## Version tracking

- The version is `<Version>` in `src/Reactor/Reactor.csproj`; `./deploy/version.sh` reads it (`--pkgver`
  for the pacman form).
- Bump on any user-facing change (patch for fixes, minor for features, major for breaking changes), with a
  `CHANGELOG.md` entry under `## [Unreleased]` in the same commit, and tag it
  `git tag -a v<version> -m "kgsm-reactor v<version>"`.

## Read before changing

- `../kgsm-docs/systems/operations/reactor.md`, `../kgsm-docs/decisions/0021-reactor-observes-by-default.md`.
- `../kgsm-docs/systems/authorization/` and `../kgsm-docs/reference/action-manifest.md`: who an action
  runs as and how the leaf declares its actions.
- `../kgsm-docs/reference/event-envelope.md`, `event-journal.md`: what the ingest reads.
- `../kgsm-docs/reference/leaf-config-descriptor.md`, `logging-convention.md`.

Docs and comments describe the system as it is now: present tense, no history, no emoji, no volatile
numbers (workspace `../CLAUDE.md`).
