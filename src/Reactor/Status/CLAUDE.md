# The status socket (`/run/kgsm-reactor/status.sock`)

A unix socket, never a port. `GET /status`, `/catalog`, `/decisions`, `/proposals`; `POST /preview`;
and the two redemptions. The root `CLAUDE.md`'s commands show each.

- **The status socket takes exactly two writes: confirm and dismiss.** Everything else answers a
  question. These have to live here because confirming re-evaluates a rule, which only this leaf can
  do. **The node's API authenticates the person and checks `reactor:rules.write` before it dials
  this**, naming them as `provider:name` (`by`) and by account id (`account`). A confirmation is then
  judged here for the offer's own action at its server — the confirmer and this daemon's service
  account must both hold it (`Auth.Cluster`'s `AutomationAccess`) — and refused with `403 refused`,
  the offer left open, when either does not. A confirmation naming no account is `unattributable`.
  What guards the socket is its mode and the handle being unguessable.
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
