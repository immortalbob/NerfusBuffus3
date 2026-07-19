# Build & run Nerfus Buffus III — quickstart for `C:\Games\DecalPlugins\NerfusBuffus3\`

This is the short path from this source tree to a loaded, running plugin in Asheron's Call.
It assumes you extracted the zip so that **`NerfusBuffus3.sln` sits at**
`C:\Games\DecalPlugins\NerfusBuffus3\` (this file is in that folder's `docs\`). Full background is
in the top-level `README.md` and corpus doc 12.

> Heads-up on the name: this is **Nerfus Buffus III**, the successor to the original
> **Nerfus Buffus II** (Pascal Jolin / Nerf Soft, 2001–2003). Everything is branded III now —
> assembly `NerfusBuffus3.dll`, friendly name "Nerfus Buffus III", namespaces `NB3.Core` /
> `NB3.Plugin`, embedded views `nb3-*.xml`, chat prefix `[NB3]`, per-character data folder
> `%AppData%\NerfusBuffus3`. The COM GUID is **unchanged** (`{915ED3D0-…}`), so re-running
> RegAsm on `NerfusBuffus3.dll` **upgrades in place** over a previously-registered revival
> build rather than leaving an orphaned entry. The chat commands are still `/nb*` (no version
> digit). On first run, if `%AppData%\NerfusBuffus3` doesn't exist yet, any profiles/settings
> under the old `%AppData%\NerfusBuffus2` are copied over automatically.

---

## What's already verified (offline, before you build)

All three offline gates were run against this exact tree and pass:

- **Logic:** `NB3.Core` + the harness — **182 / 182 tests pass** against the real recovered data
  (275-family spell table, the shipped sample profile, the 5,891-record 2012 retail dump).
- **Glue:** `shimcheck` — `PluginCore` / `DecalGameState` / `DecalSpellTable` type-check
  against the faithful Decal/VVS shims at LangVersion 7.3.
- **Views:** `viewlint` — all four `Resources/*.xml` views pass the geometry/clipping lints.

The one thing that *can't* be done off-Windows is the final link against the real
`Decal.Adapter.dll` / `Decal.FileService.dll` / `VirindiViewService.dll` COM assemblies —
that's this document.

---

## 0. Prerequisites (one-time)

1. **.NET SDK 8, 9, or 10** — <https://dotnet.microsoft.com/download>. (`dotnet --version` to check.)
2. **.NET Framework 4.8 Developer Pack** — <https://dotnet.microsoft.com/download/dotnet-framework/net48>.
   This provides the on-disk `net48` reference assemblies the build needs.
3. **Decal 3** and **VirindiViewService** installed. Note the two folders — you pass them to the build:
   - `Decal.Adapter.dll` + `Decal.FileService.dll` live in the **Decal** install, commonly
     `C:\Games\Decal 3.0` or `C:\Program Files (x86)\Decal 3.0`.
   - `VirindiViewService.dll` lives in **VVS's own plugin folder**, commonly
     `C:\Games\VirindiPlugins\VirindiViewService` — **not** the Decal folder.

   Not sure where they are? Search: `dir /s /b C:\Games\Decal.Adapter.dll` etc., or read the
   COM `InprocServer32\CodeBase` for VVS's CLSID.

---

## 1. Build

Open a **normal** PowerShell in `C:\Games\DecalPlugins\NerfusBuffus3\` and run the helper
(edit the two paths if yours differ):

```powershell
.\build-windows.ps1 -DecalSdk "C:\Games\Decal 3.0" -VvsSdk "C:\Games\VirindiPlugins\VirindiViewService"
```

It builds `net48 / x86 / Release` and stages the deployable DLLs into `.\dist\`
(`NerfusBuffus3.dll` + `NB3.Core.dll`, plus PDBs).

Prefer the raw command? This is exactly what the script runs:

```powershell
dotnet build src\NB3.Plugin\NerfusBuffus3.csproj -c Release /p:Platform=x86 -p:CoreTfm=net48 `
  /p:DecalSdk="C:\Games\Decal 3.0" `
  /p:VvsSdk="C:\Games\VirindiPlugins\VirindiViewService"
```

**Why `-p:CoreTfm=net48` is not optional:** `NB3.Core` defaults to `netstandard2.0`, and an
ns2.0 build must restore the `NETStandard.Library` metapackage from a NuGet feed. This repo's
`nuget.config` deliberately `<clear/>`s all package sources so the offline gates are hermetic —
which also means an ns2.0 restore fails with **`NU1100`** here *even on a machine with internet*.
`net48` is the plugin's real runtime target and resolves entirely from the on-disk 4.8 dev pack,
no feed. (Building straight in Visual Studio skips this flag and will hit `NU1100`; use the
script / CLI, or set `CoreTfm=net48` in the project's build properties.)

### If the build errors

| Symptom | Fix |
|---|---|
| `NU1100: NETStandard.Library …` | You omitted `-p:CoreTfm=net48`. Add it (or use the script). |
| `MSB3245: Could not resolve "VirindiViewService"` + a cascade of `CS0246` | `-VvsSdk` points at the wrong folder — VVS is in its **own** plugin folder, not Decal's. |
| `MSB3245` on `Decal.Adapter` / `Decal.FileService` | `-DecalSdk` is wrong; point it where those two DLLs actually are. |
| `NETSDK1022: Duplicate 'Compile' items` | You added an explicit `<Compile Include="VirindiViews\*.cs">` — remove it (the SDK globs them). |

---

## 2. Register (once) — from an **Administrator** PowerShell

```powershell
.\register-windows.ps1
```

This runs the **32-bit** RegAsm (`/codebase`) on `.\dist\NerfusBuffus3.dll` and writes the Decal
plugin key under `WOW6432Node`. Equivalent manual steps if you prefer:

```powershell
# from an admin prompt, in C:\Games\DecalPlugins\NerfusBuffus3\dist
C:\Windows\Microsoft.NET\Framework\v4.0.30319\RegAsm.exe /codebase NerfusBuffus3.dll
reg import ..\register-NB3.reg
```

**32-bit RegAsm only** — `Framework\`, never `Framework64\`. The AC client is 32-bit and reads
COM registration from `WOW6432Node`; the 64-bit RegAsm writes where the client can't see it and
Decal silently never finds the plugin.

You only re-run this when the **DLL path** or the **CLSID** changes — not for an ordinary rebuild
in place (see §5).

---

## 3. First launch & smoke test

1. Launch AC through Decal. Open **Manage Plugins**, enable **"Nerfus Buffus III"**, log in.
2. At **LoginComplete** you should see the load banner in chat (it prints post-login, inside the
   cold-start portal gate) plus the UI backend (VirindiViewService vs the DecalInject fallback)
   and wireup health. The main view should appear.
3. Sanity commands:
   - `/nbdiag` — repeats the backend/wireup lines with window-presentation + control-resolution state.
   - `/nbreset` — recovers a window that VVS's persisted per-window state made invisible.
   - `/nbstatus`, `/nbid`, `/nbcovers` — the covers listing is also the live spot-check for the
     game→NB3 coverage translation.
4. **First buff cycle** (no Editor view needed yet):

   ```
   /nbnew                 -> writes the 'default' starter profile (17 self buffs)
   /nbset                 -> show Options (e.g. /nbset regen 1, /nbset aggr 100)
   /nbdebug               -> ON for the first session: logs cast->outcome timings and
                             StatusMessage type ids to nb3-debug.txt
   /nbuff default         -> plan + run; watch the Spells / Fizzles counters in chat
   ```

If `/nbdebug` produced `nb3-debug.txt`, that file closes the two doc-18 §7 open measurements —
send it back and I'll fold the findings in (watchdog timing + StatusMessage type ids).

---

## 4. Confirm-on-Windows list (all soft-fail — none can take the client down)

These are the handful of live-API details the offline gates can't prove; each degrades gracefully
if it's wrong, so the plugin won't crash — but they're worth an eye on the first session:

- `Actions.CombatMode` getter type (compared through `int`, fine either way).
- `CharacterFilter.IsSpellKnown(id)` — fails open to "known" if it throws.
- `Decal.Filters.Spell` field names — read reflectively over candidate names (doc 13 §3), with the
  2012 dump filling anything unreadable, so a mismatch degrades to dump data.
- Jewelry handedness in `NB3Coverage` (cosmetic; no shipped profile targets jewelry).
- The missing-components chat string — if the live wording differs, the timeout watchdog covers
  that cast instead (soft-fail by design).
- Watchdog default (10 s) vs your real cast→outcome timings — `/nbdebug` measures it; tighten if warranted.

---

## 5. Rebuild loop (why a rebuild "didn't take")

Decal reads the plugin DLL when the **client process starts** and holds it. So:

1. **Fully exit AC** before every rebuild (a running client locks/ghosts the DLL).
2. `.\build-windows.ps1` again (the `<Version>` is bumped per build so a stale DLL is detectable).
3. **Relaunch** AC to load the new DLL. **No RegAsm needed** — same path, same CLSID.

Fast tell you're on an old build: add/rename a `/command` and check for it in-game; if it isn't
recognized, you're still on the previous DLL — exit fully and rebuild.

---

## Re-running the offline gates yourself (optional)

On any machine with a .NET SDK (they need no game, no Decal, no network):

```bash
dotnet run --project tests/NB3.Core.Tests -c Release -p:CoreTfm=net8.0   # -> 182 passed, 0 failed
bash tools/shimcheck/run-shimcheck.sh                                     # -> shimcheck: PASS
python3 tools/viewlint/viewlint.py                                        # -> viewlint: PASS
```
