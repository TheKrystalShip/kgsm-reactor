# Ingest

- **Ingestion is RAW, not typed.** `IEventService.RegisterRawHandler` takes every envelope, known type
  or not, and the subject is pulled out of the payload `JsonElement` by property name. A typed path
  would silently skip exactly the events a later rule might be about, and the skip would look like an
  event that never happened. A payload that names no subject is recorded as `Unknown` rather than
  attributed to a plausible server.
- **Tail, no cursor — deliberate, and it is the ecosystem's rule for a consumer that acts** (a replayed
  action is performed again for real). What it costs is events arriving while the process is down. The
  fix for that is *not* a cursor: it is expressing the rules that matter as **state** a rule re-derives
  from the world rather than **edges** it has to catch. See the plan's decision #3.
- **A backfill fills OBSERVATIONS and never decisions.** Reading a line late changes nothing about the
  line; a decision is a judgment made against a world that answered at the time, and the rules ask the
  *live* world. Re-deriving old decisions would record judgments that were never made, on evidence that
  no longer exists, and nothing afterwards could tell them from the real ones.
- **The reactor tails its own journal**, so every `reactor.decided` it writes comes straight back to
  it. That is fine and the events are recorded like any other; the loop is stopped by the rule that
  **no rule may wake on a `reactor.*` event**, refused at load by `RuleValidation` rather than left to a
  test over a compiled list.
- **`BackgroundService.StartAsync` returning does not mean `ExecuteAsync` has begun.** A test that acts
  immediately after `StartAsync` can reach a service that has not registered its handler yet — which
  passes alone and fails under a parallel run. `EventIngestServiceTests.StartAndStopAsync` takes an
  explicit readiness condition for this reason. The daemon is unaffected: registration and `Initialize`
  are both inside `ExecuteAsync`, in that order.
- The watchdog and the monitor are *optional*: absent, the reactor observes everything else and simply
  never sees the kind of event they produce.
