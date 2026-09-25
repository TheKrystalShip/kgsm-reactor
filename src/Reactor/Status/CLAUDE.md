# The status socket (`/run/kgsm-reactor/status.sock`)

A unix socket, never a port. `GET /status`, `/catalog`, `/decisions`, `/proposals`; `POST /preview`;
and the two redemptions. The root `CLAUDE.md`'s commands show each.

- **The status socket takes exactly two writes: confirm and dismiss.** Everything else answers a
  question. These have to live here because confirming re-evaluates a rule, which only this leaf can
  do. **The leaf checks that a caller *named* itself as `provider:name`, never that it was *allowed*
  to** — it holds no identity system and no tiers, so authority stays with the surface that
  authenticated the person. What guards the socket is its mode and the handle being unguessable.
- **The leaf publishes, the panel writes.** `GET /catalog` serves what a rule may be made of, with
  types, units and prose, so a panel renders an editor without holding a copy. `POST /preview` says
  what a proposed rule would decide about this host right now — a read that carries a body, storing
  nothing, dispatching nothing and writing no decision. Composing a rule is the panel's half; the
  socket never edits a rule, and the only instructions it takes are the two redemptions. Validation
  happens twice — the panel against the catalog it was served, the leaf at load, which is the
  authority. **An outcome is spelled the way `/catalog` spells it** (`doesNotHold`, not an enum name
  lowercased), or a panel classifies against ids that match nothing.
- **What could not be honoured is reported, never swallowed** — `RuleSet.Problems` → `/status.problems`
  and the log (`../Rules/CLAUDE.md`).
