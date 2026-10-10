# kgsm-reactor

The **event-triggered** leaf of the KGSM ecosystem.

`kgsm-scheduler` acts on a clock. Nothing acts on what it *observes* — the monitor measures, the bot
announces, the API aggregates, the assistant answers. This is the component that watches every
producer's event journal and decides what is worth doing about what it sees.

**A rule's default mode is `observe`: it evaluates and records its decision, and offers or performs
nothing until somebody moves that named rule.** A rule table invented at a desk is a list of things
that sound sensible; the ledger of observations is what shows what this host actually does. The model
is `../kgsm-docs/systems/operations/reactor.md`; the work that remains is
`../kgsm-docs/plans/kgsm-reactor.md`.

## What it does

- Reads **every** producer's journal through one federated source — the engine, the supervisor, the
  monitor, the firewall, the scheduler, the assistant, the bot and the API.
- Classifies each event down to the few facts a rule is built on: what kind of thing it is, what it is
  about, when it happened.
- Records that in a local SQLite ledger, and prunes it on a retention window.
- Evaluates its rules and records each decision with its reason; where a rule's mode allows it, offers
  an action as a proposal or performs it.
- Prints a **population report**: what fires here, how often, in what bursts, how long between repeats
  on one subject, and how long each candidate condition takes to resolve itself.

```bash
kgsm-reactor --report                 # the last 30 days
kgsm-reactor --report --days 7
kgsm-reactor --report --ledger /path/to/reactor.db
```

## What it will never do

- **Act on what another supervisor owns.** `kgsm-watchdog` owns crash-restart, autostart and resource
  caps; `kgsm-scheduler` owns timed restarts, scheduled backups and the update sweep. The reactor acts
  only on what the watchdog has given up on, and never on work the scheduler is responsible for.
- **Fabricate an actor.** Anything it dispatches carries its own provenance — origin
  `reactor`, actor the rule id — so an audit row names the rule, never a person and never null.
- **Hold a delivery channel.** It has no Discord token, no push key and no mail server. It decides;
  the surfaces that already own their channels deliver.

## Build and test

```bash
dotnet build kgsm-reactor.slnx -c Release
dotnet test  kgsm-reactor.slnx

# Native AOT — expect 0 IL2026/IL3050/ILC warnings.
dotnet publish src/Reactor/Reactor.csproj -c Release -r linux-x64
```

## Deploy

```bash
./deploy/setup.sh     # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh    # every deploy. NO sudo, NO prompts.
```

The deploy is verified against the `leaf.ready` line this leaf writes to its own journal — not
against "systemd launched it", which a reactor that came up and then failed to open its ledger would
also satisfy.

## Where truth lives

| Doc | For |
|---|---|
| `../kgsm-docs/systems/operations/reactor.md` | The model: rules, modes, proposals, the invariants |
| `../kgsm-docs/plans/kgsm-reactor.md` | What is not built yet |
| `CONFIGURATION.md` | Every config key, its default and its environment-variable form |
| `CLAUDE.md` | The mental model and the invariants, for anyone changing this repo |
| `CHANGELOG.md` | What changed, per version |
