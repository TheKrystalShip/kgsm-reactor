# Changelog

All notable changes to `kgsm-reactor` are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — a rule can be switched off without losing what it would do (0.27.0)

`enabled` is a field of its own on a rule, beside `mode`. The two answer different questions —
whether this host judges the thing, and how far it may go once it has — and a rule that is switched
off is evaluated for nothing while staying listed, editable, and holding the authority it resumes
with.

**That separation is the whole feature.** A single field carrying both would have to overwrite `act`
to say `off`, so switching a rule back on would either invent an authority nobody picked or quietly
demote one somebody did. `/status` reports the pair: `mode` is what the leaf will actually do, which
for a switched-off rule is nothing, and `configuredMode` carries what was asked for — so a surface
can offer to switch a rule on and say what that would start.

`RuleEngine.Effective(definition)` is the one place the switch and the authority combine, so the
engine and the status endpoint cannot disagree about a rule. A switched-off rule is reported among
the live rules rather than dropped from them: reporting only what runs would take a paused rule off
the page along with the control that would restore it.

A rule written by hand as `"mode": "off"` is read as a rule that is switched off, which is what that
spelling means to the person typing it, and a file that never mentions `enabled` describes a rule
that runs.

### Fixed — a pending offer from a switched-off rule answers "no longer applicable"

Redeeming a proposal re-derives its condition against the rule as it stands, and a rule that is
switched off no longer resolves — the same answer retiring or deleting one gives. Switching a rule
off is a statement that this host has stopped wanting what it offered, and an offer already staged
is part of that.

### Changed — no emoji in prose or output

Docs, comments and command output carry no emoji. The information lives in the words, and a status
that has to be read by a machine or a reader with a plain terminal reads the same either way. Marks
that are typography rather than pictures — `✓`, `✗`, arrows, `§` — stay, and a glyph a chat surface
sends to a person is that surface's output rather than prose, so it is untouched.

### Changed — the leaf installs its own sample rules on first start (0.26.0)

The rules a fresh host runs are copied out of `/opt/kgsm-reactor/rules.d` into the state directory
the first time the daemon starts, by the daemon. No package scriptlet, no provisioning step and no
privileged moment: `StateDirectory=` already hands this leaf a directory of its own, and the one
instant it can tell a host is new is the instant it creates one.

**Whether the directory existed is the first-run signal, and nothing else is.** Seeding an empty one
would put the samples back on the next start, and deleting a rule has to stick — "this host judges
nothing" is a state a host is allowed to be in.

The package ships the samples beside the binary, where an upgrade refreshes them whole and nothing
at runtime reads them again. They are deliberately not installed into `/var/lib` and not listed in
`backup=()`: pacman would reinstate a deleted sample on the next upgrade, which is the one behaviour
this must not have. Keeping the pristine copies is also what lets a rule be put back to what it
shipped as without an upgrade reaching a rule somebody is running.

### Added — the leaf writes its own rules (0.25.0)

`PUT /rules/{id}` and `DELETE /rules/{id}` on the status socket. A panel that wrote into the state
directory would be validating with a copy of the catalog against a build it cannot see, and would need
write access to a directory that is not its own. The leaf is the only thing that knows what this build
can honour, so it is the only thing that decides whether a rule is stored — and it refuses in the
response, while the person is still looking at what they wrote.

The route names the rule and a body disagreeing is refused: an id is the actor on every decision a
rule makes, and saving is not how one changes. Authorship is stamped from the caller rather than read
from the body, so a caller cannot sign a rule as somebody else; `by` is required in the same
provider:name shape a redemption takes, and checked for having been named rather than for being
allowed.

`{openedAt}` and `{openFor}` join the catalog's placeholder list. They have been live intrinsics that
`/catalog` never advertised while validation refused to bind either name, so a person could be refused
for colliding with a token nothing had told them existed.

### Fixed — a refusal on the status socket says why instead of 500ing

`Results.BadRequest(string)` serialises its argument as JSON, and this binary is Native AOT with no
reflection fallback, so a bare string had no registered `JsonTypeInfo` and threw inside the response
pipeline. Every prose refusal on the socket — a malformed preview body, an unreadable redemption body
— reached the caller as an empty 500 rather than as the sentence explaining what was wrong. They go
through `Refused`/`Missing` now, which answer as text.

### Changed — a rule is a file, and no rule exists in code (0.24.0)

**Rules are read from `rules.d/`, one JSON file each.** There is no rules document and no compiled
rule set: the four this build ships are files in `deploy/rules.d/`, installed into the state directory
by `setup.sh` and ordinary rules from that moment on.

The reason for removing the code path rather than keeping it as a fallback is that a rule defined in
code never travels through the parser, the validator or the watcher — so the path every hand-written
rule depends on would have been exercised only by hand-written rules. The samples are now what proves
it works. `ShippedRules` in the test project loads those same files through `RuleStore.LoadDirectory`,
which is how a sample that stops parsing or names a dropped signal fails the build.

An empty directory means no rules, and that is a legitimate state rather than a fault.

**A file that cannot be read costs one rule, not the set.** Each is parsed alone and the problem names
the file to fix, where one document meant a typo anywhere took every rule down with it at the moment
somebody was editing one of them. The id inside a file is checked against the filename rather than
derived from it: a file copied and renamed would otherwise install a second rule under the first one's
identity, folding two rules' decisions together under one actor.

**`RuleRegistry` owns the set and everything reads through it**, so the engine and a redemption cannot
come to judge by different rules. A reload replaces the whole set in one assignment, leaving a sweep
that started before a write to finish on the rules it began with. Evaluations still settling are
dropped on a reload — the rule that scheduled one may no longer say the same thing, and the condition
reopens on the next match.

**The directory is watched, so a rule takes effect without a restart.** Debounced, because one save
arrives as several filesystem events. `RuleRegistry.Replace` validates a rule against the set it would
join before anything is written, then writes beside and renames, so a rule is never stored that the
daemon then declines to run.

`Reactor__RulesPath` becomes `Reactor__RulesDirectory`. `deploy.sh` refreshes the pristine samples
under the install prefix and never touches the state directory.

### Changed — a verdict the rule withheld is recorded, not announced (0.23.0)

**A coverage gate is this leaf describing its own evidence, not the host.** *"This instance has not been
measured for long enough to judge"* is unactionable by construction and is the permanent steady state
for anything recently installed — so on a fleet of new servers it is not one line, it is a line per
server, drowning the ones that mean something. Half the reactor's audit rows on a live host were
exactly that.

`Verdict.Withhold` marks a row that concluded `unreadable` on evidence it successfully read, apart from
`Verdict.Unreadable`, which is something refusing to answer. The evaluator is the only place that can
tell them apart; one step out they are both "cannot tell". The distinction is carried on the decision
and stops there — the wire outcome, the payload and every consumer are untouched, because a withheld
verdict is never written.

**A source that would not answer is still announced.** A supervisor that cannot be reached is an
operational fact somebody may need to act on, and collapsing it into the same silence as a coverage gate
would hide a leaf going down behind the noise reduction meant to make it visible.

**And a rule that stops firing says so**, even when what replaces it is a verdict this leaf would
otherwise withhold — a footprint the monitor loses would otherwise take a firing rule quietly off the
air.

Measured on a live host: a ledger rebuild announced **1** decision where it had announced **5**, and the
four it kept quiet about are all on the ledger, where `--decisions` reads them.

### Changed — messages written for somebody who was not watching (0.22.0)

Every sentence this leaf produces is read somewhere the rest of the record is not: an audit row, a push
notification, a Discord line. They are now written for that reader.

**A reason names its own subject and says what is wrong in an operator's terms.** `give_up_backup`
reads *"factorio crashed 2 times in a row and the supervisor has stopped trying to restart it — it
stays down until somebody starts it"*. `MessageQualityTests` holds the rule for every seeded row,
including the unreadable ones, and refuses vocabulary that means nothing outside this process.

**Every action says what it costs.** `ReactorAction.Consequence` states what changes and whether it can
be taken back — that a pinned archive touches nothing about the server, that a rollback is irreversible.
It is served on `/catalog` beside each action and on every open offer, because whether a fault is real
and whether its fix is bearable are two questions and an offer that answers only the first is asking
for authorisation on the strength of the problem it names.

**A performed action names what it produced.** An archive is reported by id, size and — for a restore —
the date and version of the archive it came from. The id was already in the payload and in no sentence,
so an audit row could not lead anybody to the backup it was about.

**`world.restartLimit` reads the restart budget**, so a rule can say "twice out of two" rather than
"twice". Absent when the instance names no figure of its own: the supervisor's own default is not
readable from here and nothing substitutes a plausible number for it.

**`{openedAt}` and `{openFor}` date a condition from when it began.** Answered from the journal line the
episode opened on, carried onto an offer so confirming reads the same instant staging did, and left
unanswered — ending the sentence as *cannot tell* — where nothing observed the condition beginning. A
rule may not bind a measurement under either name, or under `{subject}`, `{settleSeconds}` or
`{reason}`; the file is refused at load rather than resolved by precedence.

**A host with a stored `rules.json` keeps the wording it has.** The file is authoritative and is read
verbatim, so the seeds' improved sentences reach only a host running them from the build. Re-seed by
removing the file, which discards any rule composed in the panel.

### Added — an offer a person answers (0.21.0)

`RuleEngine.Honours` is `act`. A rule still starts at `observe` and still has to be moved by hand on a
named rule; what changes is that the ceiling no longer clamps one that has been.

**Propose stages an action under a handle and performs nothing.** The offer lives in the reactor's own
database beside the decisions, is redeemed through `POST /proposals/{handle}/confirm`, and its lifetime
is a shift rather than the seconds an assistant confirmation gets — a proposal is addressed to whoever
notices next, not to whoever was watching. `Reactor__ProposalLifetimeHours` sets the host-wide default;
a rule may carry its own `proposalLifetimeHours`.

**The lifetime is not what makes it safe, and must not be tuned as though it were.** Confirming
re-evaluates the rule against the world as it is *now*: a server that came back up on its own answers
`no_longer_applicable` and nothing runs. Shortening the window buys nothing and loses the offers nobody
was awake to see.

The four ways an offer ends are kept apart — `confirmed`, `dismissed`, `lapsed`,
`no_longer_applicable` — because each says something different about the rule that staged it. Lapsing
is a sweep and writes its own journal line: an offer nobody answered is the most useful thing a week's
review can count, and it exists nowhere unless it is said.

**Act carries the action out with nobody behind it**, writing `reactor.acted` — kept separate from a
resolution so a surface can answer "what did this host do on its own" without subtracting one set from
another. A confirmed offer names the person who authorised it, with the rule as provenance beside them;
that is the opposite way round.

Dispatch is judged on the decision row rather than on the announcement: a state rule whose reason ages
from "open four minutes" to "open forty" is one judgment being refined, and acting on it twice would be
the reactor doing the same thing to a server every time it looked. Two people confirming at once
perform it once — the row is claimed before the action runs.

### Added — a rule can be narrowed to one server

`subject.id` is a text signal, so scope is an ordinary guard row that previews, reads in the editor and
writes its own sentence when it declines. An "applies to" field beside the rows would have been a
second place a rule can decline from, invisible to the preview that exists to explain exactly that.

### Added — the surfaces a panel needs

`GET /proposals` answers what this host is offering and what it recently offered, in one call so the
two cannot be read a moment apart. `GET /catalog` gains `resolutions` and `proposalLifetimeHours`.

**The status socket takes two writes now, and only these two.** Confirming has to live in the leaf
because it re-derives the condition, which means re-evaluating the rule. The leaf checks that a caller
*named* itself as `provider:name` and never that it was *allowed* to — it holds no identity system and
no tiers, so authority stays with the surface that authenticated the person, exactly as for every other
write on this host.

### Added — what a rule would decide, before it becomes one (`0.20.0`)

`POST /preview` takes a rule in the rules file's own shape and returns what it would conclude about
this host right now: the verdict per subject and the exact sentence it would record, with the figures
filled in from the live world. A state rule enumerates its own subjects; an edge rule is previewed
against a subject the caller names, and the answer says which of the two happened rather than letting
a reader assume the rule found it.

This is what makes composing a rule safe. A rule assembled from a catalog reads plausibly and can
still fire on nothing — a gate set where no instance clears it, a step ordered after one that always
matches first — and none of that is visible while writing it.

**A read, expressed as a POST because the rule is the question.** Nothing is stored, nothing is
dispatched, no decision reaches the ledger or the journal, and the gate is not run — there is no
episode to suppress and no ceiling a hypothetical belongs under. The socket stays a place that answers
questions rather than one that takes instructions.

It runs the same validator the daemon runs at load, so a rule that previews clean is a rule that will
load. A rule that cannot be honoured is reported and nothing is evaluated. A fleet larger than one
answer says how many subjects it left out, because a truncated preview that looked complete would
report a rule as quiet on servers it was never asked about.

### Changed — the rules a host runs are the rules in its file (`0.19.0`)

`rules.json` is what the reactor evaluates. A rule in it names what wakes it, what subjects it decides
about, an ordered list of steps over the signals this build measures, and one of the actions this
build can perform; its authority — off, observe, propose, act — is a field on the rule. A host with no
file runs the four rules this build seeds, all observing, which is the state a rule has to earn its
way out of.

The three mode lists are gone from the descriptor. Which rules are live and what each may do was two
questions answered in different places — a CSV in the leaf config and a predicate in the binary — and
a panel had to reconcile "which list is this rule named in" against "what authority does it actually
run under". There is one field now, still clamped by what the build honours and still reported beside
what the rule asked for.

`GET /catalog` publishes what a rule may be made of: every signal with its kind, unit, arguments and
prose; every subject source and the rule shape it produces; every action and whether it changes the
server; the operators and outcomes in the spellings the file uses; and how much authority this build
will honour. A panel renders an editor from that rather than holding a copy, so it cannot offer a
signal a later build dropped or refuse one a later build added. It stays read-only: publishing what a
rule may be made of is not accepting one over a socket.

`/status` reports each rule's whole definition — its steps in evaluation order, its bindings, its
subject source and who last shaped it — beside `problems`, which names every rule that could not be
honoured and why. A misspelled signal, a step with no sentence, an action outside the catalog or a
duplicate id leaves that rule out and the rest of the file running. Retired rules are reported
separately: they are never evaluated and are kept so that decisions they already made still resolve to
a rule that can be named.

Decisions carry who shaped the rule that made them, as a stable `provider:name` username, on the
ledger and on `reactor.decided`. It sits beside the actor rather than replacing it — the rule
performed the act, a person wrote the rule — and it is copied onto the decision rather than looked up
later, so editing a rule cannot rewrite the attribution of everything it ever decided. A rule nobody
signed produces a decision nobody signed: there is no fallback to the OS user, and a row written
before authorship existed reads back unknown rather than being backfilled.

The silent-rules reading now covers the rules that are live. A retired or muted rule is not failing
to speak — it was stopped on purpose — and listing it would put a rule somebody deleted on a report of
things that need looking at.

### Added — a rule is data assembled from catalogs (`0.18.0`)

A rule is a definition rather than a function: what wakes it, what subjects it decides about, an
ordered list of guard rows over compiled signals, and one of the actions this build can perform.
`Rules/Composition/` holds the catalogs it draws on — signals, subject sources and actions — the
evaluator that runs one, the validator that refuses one it cannot honour, and `RuleStore`, which
reads whole rules out of `rules.json`.

The four rules this build ships are seeded as definitions, and every fixture they are tested against
is run twice: once through the rule written by hand, once through its composition. Both produce the
same verdict **and** the same sentence, including the cases that turn on a source that cannot be
read. A composition reaching the same conclusion while losing the figures would be a decision record
with a hole where the measurement was.

What a rule may do stays a compiler question. `ReactorAction` is a closed union and the action
catalog renders it rather than widening it, so the never-list — never uninstall, never delete a
backup, never rewrite instance config, never moderate a player — is enforced exactly where it was.
A rule cannot wake on what this leaf wrote itself, because `reactor.*` is not something the catalog
offers.

Each guard row owns its prose, with `{alias}` placeholders filled from the same reads its clauses
used, and may carry a second sentence for the case where a signal it needs cannot be read — the
trend reader's own "no working-set series" is true and useless beside the figures a decrement would
have moved. A signal's arguments are bound once at rule level under an alias, so two mentions of the
same lookback cannot come to mean different windows. A comparand is a figure or another of the
rule's own signals, which is what lets one measurement be compared against another.

A rule that names a signal this build does not measure, says something it never binds, is judged the
instant its event lands, would do something outside the catalog, or shares an id with another rule is
left out and reported — the rest of the file runs. An id is minted once and never reused, retired
rules included: it is the actor on every line the rule produced.

Rules carry authorship: who created one and who last changed it, each as a stable `provider:name`
username. It is provenance beside the actor, never instead of it — the rule decided, and a person
shaped the rule. A definition arriving without one is unattributed and stays that way; there is no
fallback to the OS user. A rule can be off — live, listed, silent — which is a different state from
retired, where the definition is kept only so its decisions still resolve to a rule that can be named.

Nothing user-visible changes yet: the engine still evaluates the compiled catalog.

### Fixed — a working-set trend can be read at all (`0.17.0`)

A history point's `ts` is an ISO-8601 instant, which is what kgsm-monitor serves. Reading it as a
number failed the whole response, so every trend reached a rule as unreadable — and an unreadable
trend blocks only a decrement, leaving the rule permanently unable to give the one verdict the trend
exists to permit.

A body this build cannot parse and a monitor that is not answering are reported as the different
things they are. They call for opposite responses — fix the reader, or start the daemon — and one
message for both sends every reader to the wrong one first.

### Added — a rule's thresholds are an operator's, not a rebuild's (`0.17.0`)

Every figure a rule holds a measurement up against is declared as a named parameter beside the
predicate that reads it, and is written in a thresholds file rather than compiled: `rules.json` in
this service's state directory, or wherever `Reactor__RulesPath` points. A missing file is the
ordinary case — each rule then runs on the figure it ships with, which carries the measurement that
chose it.

A rule's predicate, what wakes it and what it may do stay compiled. `ReactorAction` is a closed
union, so the never-list is a compiler error rather than a promise; what a file can move is a number,
not a decision.

`/status` reports each rule's thresholds — the figure in force beside the one it ships with, its
floor, its unit and what moving it changes — so a surface renders the knobs without holding a copy of
the catalog. Beside them it reports the file's path and `tuningProblems`: a rule id this build does
not ship, a parameter a rule does not declare, a figure below its floor, or a file that could not be
parsed. Each leaves the daemon running on something sane, and each would otherwise present as "I set
it and nothing happened".

`memory_declaration_drift` declares six: `min_span_days`, `min_observed_hours`, `min_runs`,
`continuous_run_hours`, `drift_margin_pct` and `settled_growth_pct`. Its evidence gates ask for two
days of calendar span, five cumulative hours of running — summed across sessions, so five evenings of
an hour count the same as one five-hour run — and either two observed starts or one unbroken day. A
gate set to zero is off, which is a supported setting: the rule reports and acts on nothing, so an
early verdict costs a ledger row that names the thin evidence it rests on. The direction that could
do harm is guarded separately and harder, by `settled_growth_pct`, which is what stops a figure being
called settled while the working set behind it is still climbing.

### Changed — the vocabulary is dotted, and both spellings still read (`0.16.0`)

Every event type this leaf names, classifies or writes uses the ecosystem's dotted vocabulary. This
leaf's own two are `reactor.decided` and `reactor.acted`, and the prefix no rule may wake on is
`reactor.`. The classifier buckets on the name's namespace, so a new `backup.*` lands in Maintenance
the day it is emitted.

The journals hold what was written and the ledger is never rebuilt, so both spellings of a renamed
event are on disk at once for one retention period. Every reading resolves a stored name to the
current one before comparing it: `--verify` reports a position as intact when the row and the segment
name the same event under either spelling, a rule's history query reaches rows written under either,
and the population report counts one event once rather than as two conditions half as often.

### Changed — a decision says how much it matters (`0.15.0`)

A decision says how much it matters on the envelope, in the ecosystem's own severity scale, so a
reader renders it without holding a table of what reactor events mean. The rules carry
`EventSeverity` directly rather than a private enum of the same three bands, which is what kept this
leaf writing `warning` where every other producer writes `warn`.

The line also carries what was decided in words — the outcome, the subject and the rule's own reason
— written when the decision is made. The reactor's own six-valued outcome stays in the payload,
where `settled`, `suppressed`, `ceilinged` and `superseded` keep meanings the envelope's three-valued
outcome has no room for.

The ledger holds severities written by earlier builds and is never rebuilt, so a row is read
whichever spelling wrote it. A value that resolves to nothing reads as `info`: a row whose weight
cannot be established is not evidence that it was severe.

### Changed — a packaged install enables and starts the reactor (`0.14.0`)

`packaging/kgsm-reactor.install` applies kgsm-base's `50-kgsm.preset` to this project's units in
`post_install`, so a node comes up with them enabled instead of needing a person to enable each one.
The node's post-transaction hook starts what is enabled, stopped and configured. `post_upgrade` does
not preset: an administrator's `disable` survives every later version.

Starting it changes nothing about a host on its own: every rule ships in `observe` mode, so a node
that came up with the reactor running records what it would have decided and acts on nothing.

`depends=('kgsm-base')`, which carries the `kgsm` account this unit runs as and the `/var/lib/kgsm`
tree it tails the journals from — so this package no longer ships
`/usr/lib/sysusers.d/kgsm-reactor.conf`, and `deploy/sysusers.d/` is gone.

### Added — a fourth rule: what a server holds against what its blueprint declares

`memory_declaration_drift` reads kgsm-monitor for what each instance has been *measured* to hold and
compares it against the `min_ram_mb` its blueprint *declares*. Neither figure is wrong: a blueprint is
curated from vendor documentation and describes a game, a footprint describes one world with its own
map, mods and players. The rule reports that two true statements about different things have drifted
apart, and a person decides what to do about it.

State-shaped, because nothing emits an event when a footprint drifts — it is a slow fact about
accumulated measurement, not a thing that happens — so the sweep rediscovers it and there is no wake to
miss. `Wakes` is therefore empty, which is a first: the catalog's reachability test now asks an edge
rule for events and a state rule for subjects, rather than asking every rule for events.

**It proposes nothing and acts on nothing.** Rewriting instance config is on this leaf's never-list,
so the decision record *is* the output — what was declared, what was measured, how much measurement is
behind it, which way it has been moving. A surface renders that beside the live footprint rather than
storing a copy of a measurement that would go stale.

**Coverage is two gates, because it is two questions**, and conflating them was a real defect caught by
simulating the rule against thirty days of a live host before shipping it. Calendar span asks whether
the observation reaches across enough of a world's life; observed hours ask whether there is enough
measurement in it. A server played most evenings for a month has 57 hours across 25 days — the
second-best evidence on that host — and a single gate reading "days" off the sample count calls it two
days and refuses. Under that gate exactly one instance qualified and the rule fired on nothing.

A third gate wants two independent runs, since one cannot separate an instance's behaviour from a single
session's. But a run boundary is only counted when this host was watching, so an instance running
continuously since before the monitor existed reports zero — a week of unbroken operation satisfies it
instead.

**A launch line that fixes the heap fixes the measurement with it.** An instance carrying `-Xmx` is
reported as such and never as a divergence: what was measured is the value of a flag. The scan reads
the arguments KGSM launches with, so a game whose own start script sets the heap — Project Zomboid's
`install/ProjectZomboid64.json` carries `-Xmx8g` — is invisible to it and will be judged as a measured
instance. Left to say so in the ledger rather than covered by a heuristic guessed at now.

**Upward and downward are not the same judgment.** An instance holding more than it declares is reported
on its figures alone, with any OOM kill or memory stall named beside them. Downward carries the extra
burden of a trend, because a working set still climbing has not found its ceiling and a lower figure
proposed against a number still in motion is how this rule would do harm rather than noise — and a trend
that cannot be read blocks it rather than being guessed at.

`MonitorSocketPath` is new configuration. The monitor is a leaf: absent or unreachable, this rule reports
"cannot tell" and nothing else about the daemon changes.

### Fixed — the status socket reported an authority a rule did not have

A rule configured to `propose` or `act` was reported on `/status` as `propose`/`act`. The engine
clamps both to observe — those phases are not built — and said so, but only as a warning in the
journal. So an operator who granted a rule authority, saw it echoed back on a status page and was
silently observed would believe the host is acting when it is not.

`RuleEngine.Effective` is now the single place that clamp is expressed, `/status` reports what a rule
may actually do, and the configured value travels beside it as `configuredMode` — null when the two
agree, so a healthy host does not render "configured observe, running observe". Neither half alone is
the honest answer: the effective mode hides that somebody asked for more, and the configured one
shows an authority that does not exist.

### Added — `/status` says what it can honour, and what each rule is for

`honours` reports the most authority this build will let any rule have, so a surface offering a mode
control does not have to hard-code which phases exist — a panel that did would go on refusing `act`
after the build that acts is deployed.

Each rule now also carries `wakes` (the event types that bring it to an evaluation) and `actionName`
(what it would do), so a reader can tell what a rule is for without having the source open.

### Added — the decision review is served, not only printed

`GET /decisions` on the status socket answers the same four readings `--decisions` prints: what each
rule concluded and how often, the busiest rolling hour of fired decisions, how far apart a rule's
repeats on one subject were, and the rules that decided nothing at all — plus the decisions
themselves, newest first, each carrying the journal position it was derived from.

**The point is the gate.** Nothing moves to propose or act until a week of decisions has been read
against what a person would actually have done, and until now that review existed only as text on a
terminal on the host. `?days=` defaults to seven for exactly that reason. It is clamped to the
ledger's own retention rather than refused: a caller asking for a year is asking for everything, and
answering the retention span is the true reading where a 400 would be a smaller surface declining a
question it can answer.

`DecisionReview` now holds the arithmetic and `DecisionReport` only renders it, so the terminal and
the Control Panel read one measurement rather than two implementations of it. The text output is
unchanged.

**`?limit=` caps the log and never the readings.** Every figure is computed over every row in the
window; only the list at the end is trimmed. A busiest hour measured over a truncated sample would
under-report exactly the peak a ceiling has to clear, and it would do so silently.

### Changed — the gate runs on measured values instead of placeholders

Thirty days of this host's journals (3687 observations, test instances excluded) answered the three
readings the population report exists for. Every figure below has its basis recorded beside it in
code, and `RuleCatalogTests` pins them so a later edit is a decision rather than a drift.

| knob | was | is | measured basis |
|---|---|---|---|
| `give_up_backup` settle | 60s | **2m** | a give-up that ends on its own takes ≥83.5s (p50 3.1m) |
| `update_regression` settle | 60s | 60s | `crashed→ready` p95 is 38s — already above it |
| `threshold_stuck` settle | **0s** | **45m** | `breached→cleared` max 39.7m, 12 of 12 self-cleared |
| `give_up_backup` suppression | 30m (host) | **15m** | give-ups repeat every 10.3m at p95 |
| `threshold_stuck` suppression | 30m (host) | **4h** | breaches repeat every 4.1h at p50 |
| `MaxActionsPerHour` | 4 | **12** | busiest hour: 4 distinct subjects; fleet is 8 |

**`give_up_backup`'s settle was below the fastest recovery ever observed**, so it fired on every
give-up that was about to fix itself. **`threshold_stuck` had no settle at all**, and every threshold
episode in the window cleared on its own — it would have announced twelve conditions that all ended
without help. At 45m it now decides nothing here, which is the measurement rather than a fault.

`MaxActionsPerHour` counts **decisions, not events**. The busiest hour held 36 events a rule wakes on
but across only 4 subjects — mostly one server crash-looping, which suppression collapses to one
decision. A ceiling at the observed figure would silence a host that lost every server, which is the
story it most needs to let through.

### Added — a rule carries its own suppression window

`Rule.Suppression` is optional and overrides the host-wide setting; null follows the host and says so.
Per rule because the measurement is per rule by three orders of magnitude: 25 seconds between repeat
crashes against four hours between repeat threshold breaches. One number serving both either collapses
a day of threshold episodes into a single decision or lets a crash-loop speak nine times.

`/status` reports each rule's settle and suppression **as resolved**, for the same reason it already
reports mode that way — the gate block shows the host-wide window, and two of the three rules do not
run under it.

### Unchanged — `RegressionWindow`, because there is nothing to measure it from

Thirty days hold two updates followed by a fault on the same server at all, at 112 and 168 minutes,
and neither is plausibly the update's doing. A window fitted to those two would be a causal claim
built from coincidence, which is the opposite of what the field asserts. It stays at 30m and the
reason is now recorded rather than marked pending.

### Added — a reference names the line as well as locating it

An observation and a decision's source pointer both carry the id their line's producer minted, beside
the position rather than instead of it. The position finds the line without reading a segment end to
end; the id proves it is the right one.

- `reactor_decided` gains **`SourceEventId`**, null when the originating line carries none.
- `observations.event_id` and `decisions.src_event_id` — both nullable, both migrated in place.
- `--backfill` reads the id off the line, so history read late gets the same name reading it live
  would have. Lines from before the field existed record null, never a derived stand-in: a plausible
  id is indistinguishable from a real one afterwards.

### Changed — `--verify` compares ids, and reports `IdMismatch` apart from `WrongEvent`

**The check it could not make before.** Comparing event types catches a shift onto a different kind
of event and misses one onto the same kind — and that is the likely case, not the unlikely one: a
journal is mostly repetitions of a handful of types, so a deleted line usually shifts the next
position onto another `instance_started`. The id is unique per line, so it catches the shift whatever
it landed on.

A decision's pointer is now checked as strictly as an observation. It records where a decision came
from and nothing about what was there, so before an id the strongest honest statement about one was
that the offset still starts a readable event.

Where either side has no id the check falls back to the type. Absence is unknown, never a mismatch —
six weeks of this host's observations predate the field.

`IdMismatch` is reported separately because it is the case a type comparison cannot see: a host
showing these and no `WrongEvent` would otherwise read as clean.

### Migration — the ledger takes both columns in place, keeping every row

`CREATE TABLE IF NOT EXISTS` leaves an existing table exactly as it is, so a ledger written by an
earlier build would have been stamped schema 2 with no column and thrown on the next insert.
`ObservationLedger.AddColumnIfMissing` is now the one mechanism for both tables; `DecisionStore` keeps
its documented column list and calls it.

Rebuilding instead would have been safe — every row is derived — and would have thrown away the one
reading that cannot be recovered: `observed_at`, when the reactor actually saw the line.

Verified on this host's live ledger: **3,664 observations and 8 decisions before and after**.

## [0.8.0] - 2026-08-18

### Added — every journal line now carries its own id

Every event this leaf writes carries an `Id`: a UUIDv7 the shared writer mints per line, inherited by pinning
kgsm-lib 4.41.0. Nothing in this repo changed but the pin.

Why it exists: every durable reference to an event on this host is a byte offset into a named segment,
which holds only while a segment is appended to and deleted whole (conformance §2·l). An id makes a
rewrite **detectable** — a reference carrying both finds the line by position and proves it is the
right one by id, where before a shifted offset resolved to a real, parseable event of the wrong kind
with nothing to notice.

Optional and optional forever: lines written before this are on disk for as long as retention holds
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
  - **Observations only. No rule is evaluated and no event is written**, and that is not a
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

- **Every window and ceiling is a placeholder** until the population report has a week behind it.
  They are wired now so the gate's outcomes are recorded from the start, which turns "is 30 minutes
  the right window" from an opinion into a query.
- **Propose and act are unbuilt.** A rule configured into either is clamped to observe and said out
  loud, because an operator who believes the host is acting when it is not is worse off than one who
  was refused.
- `give_up_backup` cannot leave observe until a backup manifest records why an archive was taken
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

Measured in the positive direction only. The deploying user on the development host is in
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
