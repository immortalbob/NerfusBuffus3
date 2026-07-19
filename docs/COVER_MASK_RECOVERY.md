# Cover-mask encoding — recovered by disassembly

**Goal.** NB3's Profile Editor lets you aim item-enchant spells at armor by ticking coverage
checkboxes (Coat/Legs/Girth/Hands/Head/Feet/Pants/Shirt/Weapon/Shield/Casting Tool). Those
assemble into the `targetcover` mask stored in a `<Spellgroup>`. The sample profile's masks
(`0x7F21`, `0xDE`, `0x00100000`) don't match the game's standard `CLOTHING_PRIORITY` bits, so
NB3 used its own scheme. This recovers it exactly — no guessing (doc 08 §4.2 / §6.1).

## Three independent sources, cross-checked

**1. The checkbox-reader disassembly** (`.text:0x1001C3F2`). NB3 builds the mask as a dword on
the stack, ORing one contribution per ticked checkbox. Decoded (member offset → bit):

| Checkbox | member | instruction | contributes |
|---|---|---|---|
| Coat   | +0x154 | `mov dword[…],0x1A00` | `0x00001A00` |
| Legs   | +0x158 | `or byte[…+1],0x60`   | `0x00006000` |
| Girth  | +0x15C | `or byte[…+1],4`      | `0x00000400` |
| Hands  | +0x160 | `or dword[…],0x20`    | `0x00000020` |
| Head   | +0x164 | `or dword[…],1`       | `0x00000001` |
| Feet   | +0x168 | `or byte[…+1],1`      | `0x00000100` |
| Pants  | +0x16C | `or dword[…],0x40`    | `0x00000040` |
| Shirt  | +0x170 | `or dword[…],2`       | `0x00000002` |
| Weapon | +0x174 | `or word[…+2],0x150`  | `0x01500000` |
| Shield | +0x178 | `or byte[…+2],0x20`   | `0x00200000` |
| Casting Tool (Wand) | +0x17C | `or byte[…+3],1` | `0x01000000` |

**2. The named bit table** in `.rdata:0x3B480` — NB3's own labels beside each bit, e.g.
`Head=0x01, Hnds=0x20, Feet=0x100, Chst=0x200, Grth=0x400, UA=0x800, LA=0x1000, UL=0x2000,
LL=0x4000` (armor layer); `Chst=0x02 … LL=0x80` (under layer); `Mele=0x100000, Shld=0x200000,
Rng=0x400000, Ammo=0x800000, Focs=0x1000000` (held slots — these match the game's LOCATIONS
bits). The checkbox contributions above decompose cleanly into these atomic bits (e.g. Coat =
Chst|UA|LA = `0x1A00`).

**3. The shipped sample profile.** `Coat|Legs|Girth|Head|Feet|Hands` = **exactly** `0x00007F21`
(the sample's "all armor" spellgroup), and the sample's weapon group `0x00100000` is the melee
bit. Two of the three sample masks reproduce from the recovered map with zero slack.

All three encoded in `NB3.Core/CoverageMask.cs` (`CoverageBits` + `CoverageCheckboxes`), with
tests asserting the `0x7F21` reproduction.

## The translation — now implemented (NB3Coverage.cs)

NB3's coverage bits are its **own** compact scheme, not the raw wire `CLOTHING_PRIORITY`. So
when the cycle applies a spellgroup to worn items, the item's live coverage (from
`WorldObject.Values`) must be **translated into NB3's scheme before the `&` match** — a straight
AND of a profile mask against raw game coverage would mismatch on the armor/clothing bits (the
held-weapon bits happen to line up). The old NerfusFilter did this translation internally.

The revival now reproduces it in `NB3.Core/NB3Coverage.cs` (unit-tested): armor/under layers
map from the game's `CLOTHING_PRIORITY_INT` bits (**value-key 4** — doc 16 §3.2; never Decal's
named synthetic "Coverage" key, the doc-13 §6 shipped bug), and held/jewelry slots map from
`LOCATIONS`/`CURRENT_WIELDED_LOCATION` (keys 9/10 — doc 16 §3.1), where NB3's bits are the
game's own values. `DecalGameState.WornItems` filters on key 10 (currently wielded) and feeds
the translated mask to the engine. Remaining live spot-check on Windows: `/nbcovers` on a worn
suit (expect the sample-profile masks to match), plus jewelry handedness if a profile ever
targets a single ring/bracelet.
