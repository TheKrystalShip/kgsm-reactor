# The status socket (`/run/kgsm-reactor/status.sock`)

A unix socket, never a port. `GET /status`, `/catalog`, `/decisions`, `/proposals`; `POST /preview`;
and the two redemptions. The root `CLAUDE.md`'s commands show each.

- **The status socket takes writes on four routes: confirm, dismiss, `PUT /rules/{id}` and `DELETE
  /rules/{id}`.** Everything else answers a question. Confirming has to live here because it
  re-evaluates a rule, which only this leaf can do. **The node's API relays the person's access token as
  `Authorization: Bearer`**; each write route admits a token listing `reactor:rules.write`
  (`KGSM.Sessions`), and the person attributed is the token's subject and name. A confirmation is then
  judged for the offer's own action: the confirmer's token must list it, else `403 refused` and the
  offer is left open. A rule that acts is saved only when the saver's token lists everything it
  performs, else `403` with the problem. With no `Sessions__Issuer` the write routes answer `503`.
  What guards the socket is its mode and the handle being unguessable.
- **The leaf publishes, the panel writes.** `GET /catalog` serves what a rule may be made of, with
  types, units and prose, so a panel renders an editor without holding a copy. `POST /preview` says
  what a proposed rule would decide about this host right now — a read that carries a body, storing
  nothing, dispatching nothing and writing no decision. Composing a rule is the panel's half; the
  socket never edits a rule, and the only instructions it takes are the writes above. Validation
  happens twice — the panel against the catalog it was served, the leaf at load, which is the
  authority. **An outcome is spelled the way `/catalog` spells it** (`doesNotHold`, not an enum name
  lowercased), or a panel classifies against ids that match nothing.
- **What could not be honoured is reported, never swallowed** — `RuleSet.Problems` → `/status.problems`
  and the log (`../Rules/CLAUDE.md`).
