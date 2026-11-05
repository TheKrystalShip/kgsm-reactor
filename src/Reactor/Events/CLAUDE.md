# Emitting events

**Writing an event needs nothing from the event catalog.** `IEventJournalWriter.AppendAsync` takes a
`JsonElement`, so emission is local and costs no package release. **Typed consumption is the part that
does** — kgsm-bot reads through `RegisterHandler<T>() where T : KgsmEventDataBase`, which needs the
class in the Journal package and in its `JournalJsonContext`.

Every event the reactor writes carries origin `reactor` and actor `rule:<id>` — never a person, never
null — with the rule's author beside it as provenance.
