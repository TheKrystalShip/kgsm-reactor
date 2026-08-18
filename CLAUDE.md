# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`kgsm-reactor` is the **event-triggered** leaf of the KGSM ecosystem — the sibling of
`kgsm-scheduler`, which is the clock-triggered one. It reads every producer's event journal and, in
time, decides what is worth doing about what it sees. The workspace keystone is
`../system-architecture.md`; **the authority for this project is `../kgsm-reactor-plan.md`**, which
holds the phases, the boundary contract and every decision still open.

## Commands

```bash
dotnet build kgsm-reactor.slnx -c Release
dotnet test  kgsm-reactor.slnx                          # hermetic; no host, no journals, no engine
dotnet test  kgsm-reactor.slnx --filter "FullyQualifiedName~EventClassifier"

# Native AOT — expect 0 IL2026/IL3050/ILC warnings.
dotnet publish src/Reactor/Reactor.csproj -c Release -r linux-x64

# The population report, off the live ledger. Needs nothing stopped.
/opt/kgsm-reactor/kgsm-reactor --report --days 7
```

## Deploying

```bash
./deploy/setup.sh    # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh   # every deploy. NO sudo, NO prompts.
```

`deploy.sh` verifies against the **`leaf_ready` line this leaf writes to its own journal**, taking a
`READY_SINCE` stamp before the start so a line from the previous run cannot satisfy the check. That
matters: this daemon serves no socket and no port, so "systemd launched it" would also be satisfied by
a reactor that came up and then failed to open its ledger.

## The invariants (from the plan — these do not get re-decided)

1. **It is never the only record.** The journals are the record; an observation is derived. A reactor
   that was down during an incident must not be why nobody can reconstruct it.
2. **It never fabricates an actor.** Origin `reactor`, actor the rule id — never a person, never null.
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
- **Tail, no cursor — deliberate, and it is the ecosystem's rule for a consumer that acts** (a
  replayed action is performed again for real). What it costs is events arriving while the process is
  down. The fix for that is *not* a cursor: it is expressing the rules that matter as **state** a rule
  re-derives from the world rather than **edges** it has to catch. See the plan's decision #3.
- **The row's identity is its position** — `(producer, segment, offset)` — not its content.
  Content-derived ids collapse two identical events in the same second into one row, which is a real
  defect in the engine's own index, and a rate measured from a ledger with it would under-report
  exactly the bursts a ceiling has to be set above.
- **`EventClass` is a reporting bucket, not a judgment, and nothing may start gating on it.** What
  matters about an event is decided per rule against the plan's seven questions, never inherited from
  a bucket assigned at ingest.
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
