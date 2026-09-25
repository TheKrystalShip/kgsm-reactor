# Rules

**A rule is data; the catalogs it draws on are code.** One JSON file per rule under `rules.d/` (the
state directory, or `Reactor__RulesDirectory`) holds what wakes it, where its subjects come from, an
ordered list of guard rows over signals, and one action. `Composition/` holds the rest:
`SignalCatalog`, `SubjectSourceCatalog` and `ActionCatalog` are compiled, so a person composes from
what this build can do and cannot reach past it.

## Where rules live

- **No rule exists in code.** The rules this build ships are files in `deploy/rules.d/`, and ordinary
  rules from the moment they land. A rule defined in code would never travel through the parser, the
  validator or the watcher, leaving the path every hand-written rule depends on exercised only by
  hand-written rules — so the samples are what proves it. `ShippedRules` in the test project loads those
  same files through `RuleStore.LoadDirectory`. **An empty directory means no rules**, which is a state
  a host is allowed to be in.
- **Nothing but the leaf writes into the state directory.** The samples live beside the binary in
  `<prefix>/rules.d` — code, refreshed whole by a deploy (where `--delete` is correct) and by a package
  upgrade. `RuleRegistry` copies them into the state directory **the first time it creates one**, which
  is the only first-run signal there is: seeding an empty directory instead would put a deleted rule
  back on the next start. Keeping both copies is what lets the panel offer "reset to the sample" without
  an upgrade reaching a rule somebody is running. **The samples are not packaged into `/var/lib` and not
  in `backup=()`**: pacman would reinstate a deleted sample on the next upgrade, and deleting a rule has
  to stick.
- **The rules stay the leaf's even when the panel edits them.** The directory is inside this daemon's
  own state directory, so a host with no kgsm-api reads and writes it directly; a panel edits a rule by
  asking the leaf to, never by writing into the directory itself. The leaf is told a path and never
  learns whose it is — which is what keeps it from becoming the first leaf to depend on the API.

## Loading (`RuleStore`, `RuleValidation`, `RuleRegistry`)

- **The id inside a file is the file's name, and the loader checks rather than derives.** A file
  somebody copied and renamed would otherwise install a second rule under the first one's identity,
  folding two rules' decisions together under one actor.
- **A file that cannot be read costs one rule, not the set** — which is the whole reason a rule is a
  file. Each is parsed alone, and the problem names the file to fix.
- **What could not be honoured is reported, never swallowed.** A misspelled signal, a step with no
  sentence, an action outside the catalog, a duplicate id, a rule judged the instant its event lands or
  an unparseable file each leaves that rule out with the rest of the file running, and lands in
  `RuleSet.Problems` → `/status.problems` and the log. All of them otherwise present as "I saved it and
  nothing happened", which is indistinguishable from a rule with nothing to say.
- **No rule may wake on a `reactor.*` event** — the reactor tails its own journal.
- **`RuleRegistry` owns the set, and everything reads through it.** The engine judges through these
  rules and a redemption re-derives its condition through the same ones; a holder keeping its own copy
  would leave the two judging by different rules for as long as the daemon ran. A reload replaces the
  whole set in one assignment, so a sweep that started before a write finishes on the rules it began
  with. **Evaluations still settling are dropped on a reload** — the rule that scheduled one may no
  longer say the same thing, and the condition reopens on the next match anyway.
- **The directory is watched, so a hand edit applies without a restart.** Debounced, because one save
  arrives as several filesystem events and an editor writing through a temporary file produces a burst.
  A write through the panel goes via `RuleRegistry.Replace`, which validates against the set the rule
  would join, writes beside and renames, and adopts the result — so a rule is never stored that the
  daemon then declines to run.

## What a rule is made of

- **Signals are compiled because some are derived.** `drift.pctVsDeclared` is a footprint and a
  blueprint compared; expressing that as data needs an expression language, which would arrive one
  convenience at a time and end in predicates that parse while meaning something other than they read.
  A clause therefore holds no functions — `drift.absPctVsDeclared` exists as its own signal because it
  is what `abs(drift)` would have been.
- **Absent is a value; unreadable is not.** A blueprint declaring no minimum has been read, and the
  answer is "there is none". One that could not be read is a failure that ends the whole rule as
  `Unreadable` with the reader's own words. The shipped rules turn on that distinction repeatedly.
- **Rows are ordered and the first match decides; a row is an AND.** OR is another row with the same
  outcome — which is why the drift rule has three positive-drift rows, each with its own sentence. A
  row stops at its first false clause, so a source a rule did not need is never read: an instance
  holding more than it was declared to need is reported without the trend ever being asked for.
- **Arguments bind once at rule level, under an alias.** Repeating them at each mention is how two
  mentions of "the last update" come to mean different windows. A signal that takes no arguments needs
  no binding: its own id is the alias.
- **A rule is narrowed to one server with an ordinary guard row over `subject.id`.** Scope previews,
  reads in the editor and writes its own sentence when it declines. An "applies to" field beside the
  rows would be a second place a rule can decline from, invisible to the preview that exists to explain
  exactly that.
- **Settle and suppression are measured, and they stay measured.** The two windows are properties of
  how a condition behaves over time, read off 30 days of a host and pinned by `ShippedRuleTests` with
  each figure's basis. A composed rule that quietly lost the 45-minute threshold window would be a new
  rule wearing an old one's name, and its decisions would fold into the old one's episodes.

## A row owns its prose

- `{alias}` fills from the same reads the clauses used, `{alias#}` from what the row compares that
  signal against, `{alias@key}` from an argument it was bound with, plus `{subject}`,
  `{settleSeconds}`, `{openedAt}` and `{openFor}`. A row may carry a second sentence for when a signal
  it needs cannot be read. **A comparand lookup is per row**, because the hours gate compares
  `footprint.observedHours` against 5 in one step and the unbroken-run stand-in compares it against 24
  in another. **Those five names are the evaluator's** and a rule that binds a measurement under one is
  refused at load — they resolve before bindings are consulted, so honouring it would let a rule save
  cleanly and then say something else in every sentence that mentioned it.
- **A sentence dates its condition or admits it cannot.** `{openedAt}` and `{openFor}` come from the
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
