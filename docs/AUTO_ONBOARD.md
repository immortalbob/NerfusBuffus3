# Login auto-onboard — a character-named profile, generated and selected for you

**Goal:** a brand-new user should be able to install NB3, log in, and press **START** — with no
profile to create and no command to learn. The old first-run path still required *some* explicit
step (`/nbnew` or `/nbgen`, then picking the profile in the dropdown). This closes that gap.

## What happens at login

When `CharacterFilter.LoginComplete` fires and the character's skills/name have settled, NB3:

1. Reads your **character name** (`CharacterFilter.Name`) and canonicalizes it to a profile name.
2. If a profile with that name **does not already exist**, generates one from your **trained /
   specialized skills** — the exact `/nbgen` set (casting-stat bootstrap first, then attributes,
   defenses, the weapon/utility masteries you actually have, Life vitals + protections, the banes
   + Impenetrability, and the weapon auras that match how you fight). It's written as
   `%AppData%\NerfusBuffus3\<YourName>.xml`.
3. **Selects that profile in the main window** (`choiceLoadConfig`) and shows it as the current
   config, so the window opens ready to buff.

Net effect for a new character: log in → the main window already has *your* profile selected →
press **START**.

## The load-bearing rule: never clobber an edited profile

Auto-generation happens **only when no profile of that name exists yet**. On every subsequent
login the existing profile is left exactly as you edited it and simply re-selected — so the login
hook is safe to run every time. It is *generate-if-missing*, not *regenerate*. To rebuild from
freshly trained skills on purpose, click **Rescan Character** in the Options window, run
`/nbgen <yourname>` (both overwrite and re-select), or delete the profile and relog.

## Why it's serviced from the poll, not the event handler

`LoginComplete` is the "world is ready" signal (corpus doc 13 §.LoginComplete), but skill training
ranks and the character name settle a beat later, and at cold start the plugin is still inside the
portal-space gate that defers all `Actions` work (doc 13 §10.3). So the handler only **arms** the
onboard; the work runs from the existing ~4 Hz render-frame poll — the same throttled place that
already builds the family catalog and enumerates the profile folder. The servicer:

- waits a short **settle** after login, then retries until skills/name read (up to a ~30 s
  timeout) so it never writes a half-populated or empty profile against not-yet-synced data;
- **refuses to write an empty profile** for a character with no trained Creature/Item/Life school
  (a non-caster has nothing to self-buff) — it simply does nothing, leaving the manual path;
- reloads the **per-character** settings for the live character id (so a character switch onboards
  the new character, not the previous one);
- is wrapped like every other callback — an escaped exception would take the client down (doc 09
  §4.4), so all of it is `try`-guarded and fails soft.

The selection is deferred through a one-shot request that `EnsureProfileCombo` honours the next
time the dropdown (re)populates, so it survives lazy VVS realization and the DecalInject→VVS
rebuild, and it never fights a manual pick afterward.

## Turning it off / on

Per character, persisted, default **on**:

```
/nbset autogen 0     stop auto-generating and auto-selecting a profile at login
/nbset autogen 1     re-enable it (the default)
/nbset               shows autogen along with every other option
```

With `autogen 0`, login does nothing special and you drive it yourself with `/nbgen` / `/nbnew`
and the dropdown, exactly as before.

## Quick reference

```
(log in on a new character)   -> NB3 generates '<YourName>' from your skills and selects it; press START
(log in, profile exists)      -> NB3 just re-selects '<YourName>' (your edits are untouched)
/nbgen <YourName>             -> rebuild the profile from current skills and re-select it (overwrites)
/nbset autogen 0              -> disable the whole login behavior (per character)
```

## Verification

Generation itself is the already-tested `ProfileGenerator` path (the `/nbgen` core), reused
verbatim — the login hook only adds *when* to run it and *which name* to use. The new
per-character `autogen` setting round-trips through the Options XML, and — because onboarding now
parses the per-character config on the render-frame poll — a corrupt/truncated config is asserted
to degrade to defaults instead of throwing. Both are covered in the `NB3.Core.Tests` harness
(currently **182 passed, 0 failed**). The plugin glue continues to type-check offline (`shimcheck: PASS`) at the plugin's
LangVersion 7.3, with the view geometry unchanged (`viewlint: PASS`). The character name comes
from `CharacterFilter.Name` — a corpus-confirmed member (doc 13 §`.LoginComplete`, the adapter
dump), present in the doc-15 shims.

Runtime robustness the offline gates can't exercise, handled in `ServiceAutoOnboard`: it is
wrapped so a filesystem exception (corrupt config, unwritable data folder) stops onboarding for
that login rather than re-running generation at ~4 Hz; it reloads settings on a character switch;
it never writes an empty profile for a non-caster; and the deferred one-shot selection survives
lazy VVS realization and the DecalInject→VVS rebuild without fighting a later manual pick.
