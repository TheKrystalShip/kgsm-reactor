# The engine: effective mode, verdicts, what is announced

- **Whether a rule runs and how far it may go are two fields.** `enabled` is the switch; `mode` is the
  authority it asks for — `observe`, `propose`, `act` — clamped by what the build honours
  (`RuleEngine.Honours`, a ceiling over the per-rule default of `observe`, never a substitute for it).
  `RuleEngine.Effective(definition)` combines them and is the only place that does, so the engine and
  `/status` cannot disagree about a rule. `RuleMode.Off` is what that resolves to for a rule that is
  switched off, never something a rule asks for: one field carrying both would have to overwrite `act`
  to say `off`, and switching the rule back on could then only guess. `/status` reports the pair —
  `mode` is what the leaf will actually do, `configuredMode` what was asked for — and a switched-off
  rule is reported among the live rules, since dropping it would take it off the page along with the
  control that would restore it. A file written by hand as `"mode": "off"` is read as the switch.
- **Switched off and retired are different.** Switched off is live, listed and one switch from running
  again; retired is gone from the live list and kept only so its decisions still resolve to a rule that
  can be named. Both stop a pending proposal from that rule resolving, because both say this host has
  stopped wanting what it offered.
- **Everything is recorded; `RuleEngine.Announceable` decides what is announced.** The ledger holds
  every evaluation with its reason and `--decisions` reads it; the journal is a different audience — an
  audit log somebody skims, where a line costs attention whether or not it was worth having.
- **A withheld verdict is recorded and never announced.** Both halves of `Unreadable` are "cannot tell"
  and they are not the same news: `Verdict.Unreadable` is *something would not answer* (an operational
  fact — announced), `Verdict.Withhold` is *the rule declined to judge on evidence it read* (every
  coverage gate — recorded only). A gate reports what this leaf cannot yet say about an instance, which
  is unactionable by construction and the steady state for anything recently installed.
  `RuleEvaluator.ConcludeAsync` is the only place that can tell them apart — one step out they are
  indistinguishable. **The one exception:** a withheld verdict replacing a rule that was *firing* is
  announced, because a condition that stops being judged is news exactly when something was being
  judged.
- **It degrades to silence, never to a guess.** Cannot read the world ⇒ no decision.
