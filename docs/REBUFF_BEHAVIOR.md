# "After casting, re-run says 0 spells" — not a lost profile

**Symptom:** run a profile, it buffs, then `/nbuff` (or START) again reports **0 spells** —
but the profile still shows all its spells in the editor.

**Cause:** working as designed, but silently. The modern selector is *stacking-aware* — it
skips a category that an active enchantment already covers (the corpus recipe: "cast the
highest-power spell in that group the player knows, and skip it if an active enchantment in
that group already has power ≥ your best"). Right after a full cycle every category is
covered, so the next plan is empty. The profile was never touched; the bot just thinks
you're already buffed. The bug was that it didn't *say* so, and gave no way to force a
refresh.

## Fixes

1. **Clear messaging.** An empty plan now explains which case it is:
   - profile genuinely empty → *"'X' has no spells or equips yet. Open the editor (/nbed)…"*
   - already buffed → *"'X' — you're already buffed (N in the profile, all active). Nothing
     to recast. Use /nbuff X force to recast anyway, or /nbset rebuffmins <n> to auto-refresh
     near expiry."*
   - nothing castable (skill/known) → points at `/nbset skillcap 0`.
   No more bare "0 spells."

2. **`force` — recast everything.** `/nbuff <profile> force` (also `-f` or `all`) ignores
   active enchantments and casts the full list. This is the direct "just rebuff now" answer.

3. **Rebuff window — auto top-up.** `/nbset rebuffmins <n>` recasts buffs with fewer than
   *n* minutes of duration left on a re-run, using each enchantment's `TimeRemaining`
   (confirmed Decal `EnchantmentWrapper` member). Default **0** = skip every active buff (the
   mana-saving default, unchanged). Set e.g. `/nbset rebuffmins 5` and a periodic `/nbuff`
   tops up only what's about to expire — the classic maintenance-buff loop.

Persisted per character (`rebuffMinutesRemaining`). Skill capping and stacking are
unaffected; `force` recasts active buffs but still honours the skill-level cap (it won't
throw a level you can't land).

## Quick reference

```
/nbuff mybuffs          -> (default) cast the WHOLE list every time, refreshing active buffs
/nbset recast 0         -> switch to mana-saving mode: skip buffs already up
/nbuff mybuffs force    -> recast everything once, regardless of the recast setting
/nbset rebuffmins 5     -> (when recast=0) refresh buffs with < 5 min left
```

**Default changed (v3.0.2):** `/nbuff` now recasts the whole list by default — the original
NB2 behaviour, and what "I ran it, so buff me" expects. Skipping active buffs is opt-in via
`/nbset recast 0`. An empty plan now reports the *real* reason (already-active vs unresolved),
so a profile whose spells don't resolve is never mislabeled "already buffed."

## Verification

Part of the `NB3.Core.Tests` harness (currently **182 passed, 0 failed**); 4 cases exercise this:
the reported 0-cast-when-active symptom, `force` overriding it, the rebuff window recasting only
near-expiry buffs, and a guard that planning never mutates the profile. `shimcheck: PASS`,
`viewlint: PASS`.
