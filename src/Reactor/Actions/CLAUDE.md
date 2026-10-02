# Actions and proposals

The reactor acts only on what the watchdog has **given up** on; the watchdog owns crash-restart,
autostart and caps, the scheduler timed restarts, scheduled backups and update sweeps. It holds no
delivery channel — no Discord token, no VAPID key, no SMTP.

## Who it acts as

**Nothing is performed on one account's say-so.** Every action names the engine actions it takes
(`ReactorAction.Performs`), and `ProposalService` performs it only where both this daemon's service
account, `svc:reactor@<node>`, and the person behind it hold every one of them at the server's install
— evaluated from the node's replica at that moment by `Auth.Cluster`'s `AutomationAccess`, the same
check the scheduler makes.

- **A rule acting** acts as its author: whoever last saved it through the node's API, whose account the
  rule file records beside the actor (`updatedBy.account`). A rule restarts only what its author could
  by hand; an author who loses the access stops the rule at its next firing; a rule nobody saved that
  way — a shipped sample, a file written by hand — acts on nothing. A refusal is the action's result,
  `blocked: <why>`, announced like any other failed act, and nothing reaches the engine.
- **A confirmed offer** acts as the person confirming, judged before the offer is claimed, so a person
  who may answer offers but not restore a server leaves it standing for somebody who may.
- **Reading the world** is the service account alone: those reads are declared as its requirements.

## What an action says

- **What an action costs is the action's, and it is a separate sentence from the fault.** `Consequence`
  says what changes and whether it can be taken back — never how likely it is to help, which would be a
  claim about a fault nothing here has diagnosed. **It must not name the instance**: `/catalog` serves
  it to an editor that has no instance to build an action for, and `ActionEntry.Consequence` builds
  against an empty name on exactly that understanding.
- **What an action *would* do is written in the infinitive; what it *did* is the performer's to say.**
  `Describe()` is carried by three sentences that are all about something not yet done. The past tense
  comes back on `ActionResult.Detail` from the thing that performed it — which is also the only thing
  that knows the id of what it produced, and an audit row that cannot name the archive it created
  cannot lead anybody to it.

## Proposals (`ProposalService`)

- **A proposal is safe because the condition is re-derived at redemption, not because the window is
  short.** `ProposalService.ConfirmAsync` re-evaluates the rule against the world as it is now before
  it performs anything, so an offer answered in the morning about a server that came back up overnight
  ends as `no_longer_applicable`. That is what lets the lifetime be a shift where the assistant's
  confirmations are seconds. **Do not tune the lifetime as a safety control** — shortening it buys
  nothing and loses the offers nobody was awake to see.
- **Unreadable at redemption is not a no.** A world that would not answer leaves the offer open and
  tells the person why. Ending it would record a conclusion nobody reached; performing anyway would act
  on a reading taken hours ago.
- **Redemption re-derives the condition and deliberately does not re-run the gate.** Suppression and
  the hourly ceiling govern how often the *reactor* speaks; at redemption the person is speaking.
- Dispatch-once, the one-open-offer index and the claim that makes two confirmations perform once are
  the ledger's (`../Ledger/CLAUDE.md`).
