# Configuration

Every knob `kgsm-reactor` has, where it is declared and how to override it.

## How configuration resolves

Three layers, lowest precedence first:

1. **`/opt/kgsm-reactor/kgsm-reactor.settings.json`** — installed beside the binary by `deploy.sh`.
   It declares the **whole** surface with every default, and it is the floor.
2. **`Environment=` in the unit** (`/etc/kgsm-reactor/systemd/kgsm-reactor.service`).
3. **`/etc/kgsm-reactor/kgsm-reactor.env`** — the operator's file, seeded once from
   `deploy/kgsm-reactor.env.example` and never overwritten again.

An environment variable overrides **one key** of the settings file by spelling that key's path with
`__`: `Reactor__RetentionDays`, `Logging__LogLevel__Default`. A variable naming a key the file does not
declare binds to nothing — check the spelling against the settings file, not against this document.

The same three layers are what the leaf descriptor lists as its floor sources, so the Control Panel's
configuration page shows a value's provenance rather than only its value.

## `Reactor`

| Key | Env | Default | What it decides |
|---|---|---|---|
| `Enabled` | `Reactor__Enabled` | `true` | Whether the reactor observes at all. False leaves the daemon running, recording nothing, and still reporting `leaf_ready` — so "deliberately quiet" and "broken" do not look the same. |
| `KgsmPath` | `Reactor__KgsmPath` | `/usr/bin/kgsm` | The KGSM executable. **Checked at startup; the daemon refuses to run if nothing is there.** Nothing in the observing half calls it — re-reading the world before deciding anything does, and a host where this is wrong should fail now rather than at the moment it matters. |
| `JournalDir` | `Reactor__JournalDir` | `/var/lib/kgsm/events` | The engine's own event journal. Every other producer's is discovered; the engine's is the one that has to be named. |
| `StateRoot` | `Reactor__StateRoot` | *(blank)* | Where producer state directories live, each holding its journal in an `events` subdirectory. Blank uses the library's default. Pointing it elsewhere makes the reactor deaf to everything except the engine. |
| `LedgerPath` | `Reactor__LedgerPath` | *(blank)* | The observation ledger. Blank resolves to `reactor.db` inside `$STATE_DIRECTORY` — the directory systemd creates from `StateDirectory=kgsm-reactor` — falling back to `/var/lib/kgsm-reactor`. |
| `RetentionDays` | `Reactor__RetentionDays` | `30` | How long an observation is kept. Floor 1. The ledger is derived working data: every row restates a line still held in some producer's journal, so pruning loses no record — only how far back the reactor can measure. |
| `FlushIntervalSeconds` | `Reactor__FlushIntervalSeconds` | `5` | How often buffered observations are committed. Floor 1. |

**Numbers are nullable and a blank value means "unset".** That is load-bearing rather than stylistic:
one stray `Reactor__RetentionDays=` in an env file binds to a non-nullable `int` by throwing, which
takes the unit down at startup, and a JSON null binds to `0`, which silently discards the default. A
value that is present but is not a number still fails loudly, which is the point of typing it.

**Out-of-range numbers are raised to their floor, not refused.** The daemon starts on something sane
and says so, rather than failing a service over a typo.

## `Logging`

| Key | Env | Default |
|---|---|---|
| `LogLevel:Default` | `Logging__LogLevel__Default` | `Information` |

Logs go to the journal through `AddSystemdConsole()`, so `journalctl -u kgsm-reactor` renders the
priority levels natively. Per-category filtering works the same way
(`Logging__LogLevel__TheKrystalShip.KGSM.Services.EventJournalReader=Warning` quiets the per-journal
startup lines).

## Paths a host is given

| Path | What |
|---|---|
| `/opt/kgsm-reactor/` | The install prefix, owned by the deploying user. |
| `/etc/kgsm-reactor/kgsm-reactor.env` | Operator config. Seeded once, never overwritten. |
| `/etc/kgsm-reactor/systemd/` | The real unit files, user-owned, symlinked from `/etc/systemd/system/`. |
| `/var/lib/kgsm-reactor/` | The state directory systemd creates. Holds the ledger and this leaf's own journal. |
| `/var/lib/kgsm-reactor/events/` | This leaf's event journal — `leaf_ready`, `leaf_degraded`, `leaf_stopping`. |
| `/var/lib/kgsm/leaves/reactor.json` | The leaf config descriptor kgsm-api scans. |

⚠ **The state directory is `0750` with group `kgsm`, and that is not incidental.** A producer's journal
is read by other components on the host, and a directory cannot be entered without execute on every
directory above it. Closed to the group, the journal inside is hidden **silently** — a reader that
cannot traverse in sees no journal rather than a permission error, which is indistinguishable from a
leaf that has recorded nothing.
