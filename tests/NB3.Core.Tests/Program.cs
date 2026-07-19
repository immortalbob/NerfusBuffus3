using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NB3.Core;
using NB3.Core.Modern;

namespace NB3.Core.Tests
{
    /// <summary>Tiny dependency-free test runner (no xUnit/NuGet). Prints a pass/fail line per
    /// case and exits non-zero on any failure, so it works as a CI gate.</summary>
    internal static class Program
    {
        static int _pass, _fail;

        static void Check(string name, Action body)
        {
            try { body(); _pass++; Console.WriteLine($"  PASS  {name}"); }
            catch (Exception ex) { _fail++; Console.WriteLine($"  FAIL  {name}\n          {ex.Message}"); }
        }

        static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"expected <{expected}> but was <{actual}>");
        }
        static void True(bool c, string m = "expected true") { if (!c) throw new Exception(m); }

        static string Fx(string f) => Path.Combine(AppContext.BaseDirectory, "fixtures", f);
        static SpellTable Table() => SpellTable.Load(Fx("nb3-spells.xml"));
        static Profile Sample() => ProfileXml.Load(Fx("sample-buff-profile.xml"));

        static int Main()
        {
            Console.WriteLine("NB3.Core offline test harness (doc-15 gate)\n");

            Console.WriteLine("SpellTable:");
            Check("loads all 275 families", () => Eq(275, Table().Count));
            Check("Impenetrability maps 7 levels", () =>
            {
                var f = Table().ByEditorName("Impenetrability");
                True(f != null); Eq(0x0033, f.IdAtLevel(1)); Eq(0x05CA, f.IdAtLevel(2));
                Eq(0x05CE, f.IdAtLevel(6)); Eq(0x083C, f.IdAtLevel(7));
            });
            Check("any level id locates back to family", () =>
            {
                True(Table().TryLocate(0x083C, out var loc));
                Eq("Impenetrability", loc.Family.EditorName); Eq(7, loc.Level);
            });
            Check("resolve downgrades to highest known level", () =>
            {
                var t = Table(); var f = t.ByEditorName("Impenetrability");
                int r = t.ResolveCastableId(f.IdAtLevel(7), id => id != f.IdAtLevel(7), 7);
                Eq(f.IdAtLevel(6), r);
            });
            Check("resolve respects max-level cap", () =>
            {
                var t = Table(); var f = t.ByEditorName("Impenetrability");
                Eq(f.IdAtLevel(3), t.ResolveCastableId(f.IdAtLevel(7), _ => true, 3));
            });
            Check("resolve returns 0 when nothing known", () =>
            {
                var t = Table(); var f = t.ByEditorName("Impenetrability");
                Eq(0, t.ResolveCastableId(f.IdAtLevel(7), _ => false, 7));
            });

            Console.WriteLine("\nProfile (round-trip of the shipped sample):");
            Check("parses shipped sample profile", () =>
            {
                var p = Sample();
                Eq("Sample Buff", p.Name);
                Eq(1, p.Nodes.OfType<EquipNode>().Count());
                Eq("Focusing Stone", p.Nodes.OfType<EquipNode>().First().ItemName);
                Eq(20, p.Nodes.OfType<SpellNode>().Count());
                Eq(3, p.Nodes.OfType<SpellGroupNode>().Count());
            });
            Check("spellgroup cover + children parse", () =>
            {
                var g = Sample().Nodes.OfType<SpellGroupNode>().First();
                Eq(0x00007F21, g.TargetCover);
                Eq("05CA,05E8", string.Join(",", g.SpellIds.Select(i => i.ToString("X4"))));
            });
            Check("round-trip preserves structure", () =>
            {
                var p1 = Sample();
                var p2 = ProfileXml.Parse(ProfileXml.ToXml(p1));
                Eq(p1.Name, p2.Name);
                Eq(p1.Nodes.Count, p2.Nodes.Count);
                var g1 = p1.Nodes.OfType<SpellGroupNode>().Last();
                var g2 = p2.Nodes.OfType<SpellGroupNode>().Last();
                Eq(g1.TargetCover, g2.TargetCover);
                Eq(string.Join(",", g1.SpellIds), string.Join(",", g2.SpellIds));
            });

            Console.WriteLine("\nBuffEngine (profile + game state -> cast plan):");
            Check("missing focus item -> warning, no crash", () =>
            {
                var plan = new BuffEngine(Table()).BuildPlan(Sample(), AllKnown());
                True(plan.Warnings.Any(w => w.Message.Contains("Focusing Stone")));
            });
            Check("20 self spells resolve and target self", () =>
            {
                var s = AllKnown(); s.SelfId = 0x1234; s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var plan = new BuffEngine(Table()).BuildPlan(Sample(), s);
                var self = plan.Actions.Where(a => a.Kind == CastKind.CastSelf).ToList();
                Eq(20, self.Count);
                True(self.All(a => a.TargetGuid == 0x1234));
            });
            Check("equip precedes all casts", () =>
            {
                var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var plan = new BuffEngine(Table()).BuildPlan(Sample(), s);
                Eq(CastKind.Equip, plan.Actions.First().Kind);
            });
            Check("spellgroup applies only to items matching coverage", () =>
            {
                var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.Worn.Add(new WornItem(0xBB01, "Breastplate", 0x00000021)); // intersects 0x7F21
                s.Worn.Add(new WornItem(0xBB02, "Ring", 0x00080000));        // no intersection
                var plan = new BuffEngine(Table()).BuildPlan(Sample(), s);
                var item = plan.Actions.Where(a => a.Kind == CastKind.CastItem).ToList();
                True(item.Count > 0);
                True(item.All(a => a.TargetGuid == 0xBB01));
            });
            Check("unknown level-7 self spell falls back to level 6", () =>
            {
                var t = Table(); var f = t.ByEditorName("Impenetrability");
                var p = new Profile { Name = "t" };
                p.Nodes.Add(new SpellNode { SpellId = f.IdAtLevel(7), TargetType = TargetType.Self });
                var s = AllKnown(); s.KnowOnly = new HashSet<int>();
                for (int l = 1; l <= 6; l++) s.KnowOnly.Add(f.IdAtLevel(l));
                var plan = new BuffEngine(t).BuildPlan(p, s);
                Eq(1, plan.Actions.Count);
                Eq(f.IdAtLevel(6), plan.Actions[0].SpellId);
            });

            Console.WriteLine("\nBuffCycle (NB3's own sequential cycle runner):");
            Check("equip precedes casting and counters initialise", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var plan = new BuffEngine(t).BuildPlan(Sample(), s);
                var cyc = new BuffCycle(plan);
                cyc.Start();
                Eq(20, cyc.TotalSpells);          // 20 self spells in the sample
                Eq(1, cyc.TotalEquips);
                Eq(StepKind.Equip, cyc.Tick(s).Kind);
            });
            Check("equips run before Magic mode; no cast until Magic mode is entered", () =>
            {
                // The Sample profile equips a Focusing Stone first. Not in Magic mode yet: the equip
                // must run FIRST (you can't enter the Magic stance with an empty hand), then the
                // EnterMagicMode gate holds until we're in mode, and only then does a cast issue.
                var t = Table(); var s = AllKnown(); s.InMagicCombatMode = false;
                s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s)); cyc.Start();
                Eq(StepKind.Equip, cyc.Tick(s).Kind); cyc.ReportCastResult(true);   // wield first
                Eq(StepKind.EnterMagicMode, cyc.Tick(s).Kind);                       // still out of mode -> gate holds
                s.InMagicCombatMode = true;
                Eq(StepKind.Cast, cyc.Tick(s).Kind);                                 // now it casts
            });
            Check("busy client yields a Busy step and bumps the counter", () =>
            {
                var t = Table(); var s = AllKnown(); s.IsCasting = true;
                s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s)); cyc.Start();
                Eq(StepKind.Busy, cyc.Tick(s).Kind);
                Eq(1, cyc.BusyHits);
            });
            Check("advances through the list on success", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s)); cyc.Start();
                // perform equip
                Eq(StepKind.Equip, cyc.Tick(s).Kind); cyc.ReportCastResult(true);
                int left0 = cyc.SpellsLeft;
                var st = cyc.Tick(s); Eq(StepKind.Cast, st.Kind); cyc.ReportCastResult(true);
                Eq(left0 - 1, cyc.SpellsLeft);
                Eq(1, cyc.SpellsCast);
            });
            Check("fizzle recasts the same spell and counts", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s)); cyc.Start();
                cyc.Tick(s); cyc.ReportCastResult(true);            // equip ok
                var a1 = cyc.Tick(s).Action; cyc.ReportCastResult(false); // fizzle
                var a2 = cyc.Tick(s).Action;
                Eq(a1.SpellId, a2.SpellId);                          // same spell retried
                Eq(1, cyc.Fizzles);
                Eq(0, cyc.SpellsCast);
            });
            Check("mana gate triggers the configured regen mode", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.CurMana = 10; s.DefaultManaCost = 100;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s),
                    new CycleOptions { ManaRegenMode = ManaRegenMode.RevitalizeS2M, AggressivenessPercent = 100 });
                cyc.Start();
                cyc.Tick(s); cyc.ReportCastResult(true); // equip
                var st = cyc.Tick(s);
                Eq(StepKind.RegenMana, st.Kind);
                Eq(ManaRegenMode.RevitalizeS2M, st.RegenMode);
            });
            Check("mana floor regens even when the next spell is affordable (keeps a reserve)", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.CurMana = 200; s.DefaultManaCost = 50;   // 20% of 1000 < 25% floor, yet 200 >= 50 cost
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s),
                    new CycleOptions { ManaRegenMode = ManaRegenMode.RevitalizeS2M }); // default floor 25 / target 90
                cyc.Start();
                cyc.Tick(s); cyc.ReportCastResult(true);   // equip
                var st = cyc.Tick(s);
                Eq(StepKind.RegenMana, st.Kind);           // below the floor -> regen despite affording the spell
                Eq(900, st.RequiredMana);                  // regen up to the 90% target (hysteresis), not the floor
            });
            Check("above the floor and affordable -> cast (no flapping at the floor)", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.CurMana = 300; s.DefaultManaCost = 50;   // 30% > 25% floor, affordable
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s),
                    new CycleOptions { ManaRegenMode = ManaRegenMode.RevitalizeS2M });
                cyc.Start();
                cyc.Tick(s); cyc.ReportCastResult(true);   // equip
                Eq(StepKind.Cast, cyc.Tick(s).Kind);
            });
            Check("mana floor 0 disables the reserve (pure per-spell gate)", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.CurMana = 200; s.DefaultManaCost = 50;   // 20%, but floor off and 200 >= 50 cost
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s),
                    new CycleOptions { ManaRegenMode = ManaRegenMode.RevitalizeS2M, ManaFloorPercent = 0 });
                cyc.Start();
                cyc.Tick(s); cyc.ReportCastResult(true);   // equip
                Eq(StepKind.Cast, cyc.Tick(s).Kind);
            });
            Check("no regen configured -> casts regardless of mana", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.CurMana = 0;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s)); // ManaRegenMode.None
                cyc.Start(); cyc.Tick(s); cyc.ReportCastResult(true);
                Eq(StepKind.Cast, cyc.Tick(s).Kind);
            });
            Check("pause and abort are honoured", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s)); cyc.Start();
                cyc.Pause();  Eq(StepKind.Paused, cyc.Tick(s).Kind);
                cyc.Resume(); Eq(StepKind.Equip, cyc.Tick(s).Kind);
                cyc.Abort();  Eq(StepKind.Done, cyc.Tick(s).Kind);
            });
            Check("cycle reaches Done after the last action", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s)); cyc.Start();
                int guard = 0;
                while (cyc.State == CycleState.Running && guard++ < 500)
                {
                    var st = cyc.Tick(s);
                    if (st.Kind == StepKind.Cast || st.Kind == StepKind.Equip) cyc.ReportCastResult(true);
                    else if (st.Kind == StepKind.Done) break;
                }
                Eq(CycleState.Done, cyc.State);
                Eq(0, cyc.SpellsLeft);
                Eq(20, cyc.SpellsCast);
            });

            Console.WriteLine("\nNB3Settings (per-character Options, round-trip):");
            Check("round-trips every option faithfully", () =>
            {
                var s = new NB3Settings
                {
                    CharacterId = 0x50001234,
                    HealingKits = HealingKitTiers.Treated | HealingKitTiers.Peerless,
                    UsePotions = true,
                    ExpectedPctSpellCost = 85, ManaRegenMode = ManaRegenMode.RestS2M,
                    ManaFloorPercent = 30, ManaRegenTargetPercent = 85,
                    MaxRecoveryLevel = 6, MinCastChancePercent = 80, QuietMode = true, EditorPermaDelete = true,
                    SkillBasedLevel = false, RecastActiveBuffs = false, RebuffMinutesRemaining = 12,
                    HealthFloorPercent = 40, StaminaFloorPercent = 35,
                    AutoGenerateOnLogin = false,   // non-default, to prove it round-trips
                    AutoWieldCaster = false,       // non-default (default is true)
                    BootstrapLevels = false,       // non-default (default is true)
                    UseHealthToMana = false,       // non-default (default is true)
                    MaxAttemptsPerAction = 3, CastTimeoutMs = 7500, CastSettleMs = 250,
                    RegenRetryBackoffMs = 4000, MaxRegenCastFailures = 2
                };
                var s2 = NB3Settings.Parse(s.ToXml());
                Eq(s.CharacterId, s2.CharacterId);
                Eq(s.HealingKits, s2.HealingKits);
                True(s2.UsePotions);
                Eq(85, s2.ExpectedPctSpellCost);
                Eq(ManaRegenMode.RestS2M, s2.ManaRegenMode);
                Eq(30, s2.ManaFloorPercent);
                Eq(85, s2.ManaRegenTargetPercent);
                Eq(6, s2.MaxRecoveryLevel);
                Eq(80, s2.MinCastChancePercent);
                True(s2.QuietMode && s2.EditorPermaDelete);
                True(!s2.SkillBasedLevel);
                True(!s2.RecastActiveBuffs);
                Eq(12, s2.RebuffMinutesRemaining);
                Eq(40, s2.HealthFloorPercent);
                Eq(35, s2.StaminaFloorPercent);
                True(!s2.AutoGenerateOnLogin);
                True(!s2.AutoWieldCaster);
                True(!s2.BootstrapLevels);
                True(!s2.UseHealthToMana);
                Eq(3, s2.MaxAttemptsPerAction);
                Eq(7500, s2.CastTimeoutMs);
                Eq(250, s2.CastSettleMs);
                Eq(4000, s2.RegenRetryBackoffMs);
                Eq(2, s2.MaxRegenCastFailures);
            });
            Check("defaults are sane on empty config", () =>
            {
                var s = NB3Settings.Parse("<NB3Config/>");
                Eq(HealingKitTiers.None, s.HealingKits);
                Eq(100, s.ExpectedPctSpellCost);
                Eq(7, s.MaxRecoveryLevel);
                Eq(ManaRegenMode.SpellRecovery, s.ManaRegenMode);   // default recovery = Spells
                True(s.AutoGenerateOnLogin);   // onboarding on by default (opt-out via /nbset autogen 0)
                True(s.AutoWieldCaster);       // auto-wield a caster at Start, on by default
                True(s.BootstrapLevels);       // level bootstrap (re-check/upgrade capped buffs), on by default
                // Advanced knobs now surfaced in the Options panel keep their documented defaults.
                True(s.UseHealthToMana);            // health->mana (Cannibalize/Heal Self) on by default
                Eq(25, s.ManaFloorPercent);
                Eq(90, s.ManaRegenTargetPercent);
                Eq(8, s.MaxAttemptsPerAction);
                Eq(10000, s.CastTimeoutMs);
                Eq(500, s.CastSettleMs);
                Eq(3000, s.RegenRetryBackoffMs);
                Eq(5, s.MaxRegenCastFailures);
            });
            Check("Load falls back to defaults on a corrupt/missing config (never throws)", () =>
            {
                // A truncated/garbage file must not throw — the login auto-onboard parses this on the
                // render-frame poll, so a crash-corrupted config has to degrade to defaults.
                var missing = NB3Settings.Load(Path.Combine(AppContext.BaseDirectory, "no_such_config_zzz.xml"));
                Eq(100, missing.ExpectedPctSpellCost);
                True(missing.AutoGenerateOnLogin);

                var tmp = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(tmp, "<NB3Config character=\"0x1\"  <<<not valid xml");
                    var s = NB3Settings.Load(tmp);   // must not throw
                    Eq(100, s.ExpectedPctSpellCost); // came back as defaults
                    Eq(7, s.MaxRecoveryLevel);
                    True(s.AutoGenerateOnLogin);
                }
                finally { try { File.Delete(tmp); } catch { } }
            });

            Console.WriteLine("\nRecoverySpells (highest known S2M/H2M/Revit/Heal at or below the max level):");
            Check("resolves the highest known level for all four recovery spells", () =>
            {
                var t = Table(); var s = AllKnown();
                var rec = RecoverySpells.Resolve(t, s.SpellKnown, new NB3Settings()); // default max = 7
                Eq(t.ByEditorName("Stamina to Mana Self").IdAtLevel(7), rec.StaminaToMana);
                Eq(t.ByEditorName("Health to Mana Self").IdAtLevel(7), rec.HealthToMana); // L7 == Cannibalize
                Eq(t.ByEditorName("Revitalize Self").IdAtLevel(7), rec.Revitalize);
                Eq(t.ByEditorName("Heal Self").IdAtLevel(7), rec.HealSelf);
            });
            Check("max level caps every recovery spell (incl. Heal Self)", () =>
            {
                var t = Table(); var s = AllKnown();
                var rec = RecoverySpells.Resolve(t, s.SpellKnown, new NB3Settings { MaxRecoveryLevel = 4 });
                Eq(t.ByEditorName("Stamina to Mana Self").IdAtLevel(4), rec.StaminaToMana);
                Eq(t.ByEditorName("Heal Self").IdAtLevel(4), rec.HealSelf);
            });
            Check("max level 6 caps that recovery at 6", () =>
            {
                var t = Table(); var s = AllKnown();
                var rec = RecoverySpells.Resolve(t, s.SpellKnown, new NB3Settings { MaxRecoveryLevel = 6 });
                Eq(t.ByEditorName("Stamina to Mana Self").IdAtLevel(6), rec.StaminaToMana);
            });
            Check("an unknown top level walks down to the highest known (automatic fallback)", () =>
            {
                var t = Table(); var famS2M = t.ByEditorName("Stamina to Mana Self");
                var s = AllKnown(); s.KnowOnly = new HashSet<int>();
                for (int l = 1; l <= 6; l++) s.KnowOnly.Add(famS2M.IdAtLevel(l)); // knows 1..6, not 7
                var rec = RecoverySpells.Resolve(t, s.SpellKnown, new NB3Settings { MaxRecoveryLevel = 7 });
                Eq(famS2M.IdAtLevel(6), rec.StaminaToMana);   // L7 unknown -> highest known below = L6
            });

            Console.WriteLine("\nManaRegenController (the five modes as micro-sequences):");
            Check("done immediately when mana already at target", () =>
            {
                var s = AllKnown(); s.CurMana = 500;
                var c = new ManaRegenController(ManaRegenMode.RevitalizeS2M, new RecoverySpells(), 400);
                Eq(RegenActionKind.Done, c.Next(s).Kind);
            });
            Check("RevitalizeS2M rests via Revitalize when stamina low, else casts S2M", () =>
            {
                var t = Table();
                var rec = RecoverySpells.Resolve(t, _ => true, new NB3Settings());
                var c = new ManaRegenController(ManaRegenMode.RevitalizeS2M, rec, 400);
                var lowStam = AllKnown(); lowStam.CurMana = 0; lowStam.CurStam = 5;
                var r1 = c.Next(lowStam); Eq(RegenActionKind.Cast, r1.Kind); Eq(rec.Revitalize, r1.SpellId);
                var okStam = AllKnown(); okStam.CurMana = 0; okStam.CurStam = 300;
                var r2 = c.Next(okStam); Eq(RegenActionKind.Cast, r2.Kind); Eq(rec.StaminaToMana, r2.SpellId);
            });
            Check("HealKitH2M heals when health low, else casts H2M", () =>
            {
                var t = Table();
                var rec = RecoverySpells.Resolve(t, _ => true, new NB3Settings());
                var c = new ManaRegenController(ManaRegenMode.HealKitH2M, rec, 400);
                var lowHp = AllKnown(); lowHp.CurMana = 0; lowHp.CurHealth = 5;
                Eq(RegenActionKind.UseHealingKit, c.Next(lowHp).Kind);
                var okHp = AllKnown(); okHp.CurMana = 0; okHp.CurHealth = 200;
                var r = c.Next(okHp); Eq(RegenActionKind.Cast, r.Kind); Eq(rec.HealthToMana, r.SpellId);
            });
            Check("HealKitH2M floor is a PERCENT of max, not absolute HP (the 60/320 bug)", () =>
            {
                // Regression: 60 HP of a 200 max is 30% — well under half — but the old code
                // compared raw 60 against a floor of 50, read "fine", and cast H2M (draining
                // health further) instead of healing. It must now heal.
                var t = Table();
                var rec = RecoverySpells.Resolve(t, _ => true, new NB3Settings());
                var midHp = AllKnown(); midHp.CurMana = 0; midHp.CurHealth = 60;   // 60/200 = 30%
                var c50 = new ManaRegenController(ManaRegenMode.HealKitH2M, rec, 400); // default 50%
                Eq(RegenActionKind.UseHealingKit, c50.Next(midHp).Kind);
                // And the floor is honoured: with a 25% floor, 30% is fine — cast H2M, don't heal.
                var c25 = new ManaRegenController(ManaRegenMode.HealKitH2M, rec, 400,
                    new RegenThresholds { HealthFloor = 25 });
                var r = c25.Next(midHp); Eq(RegenActionKind.Cast, r.Kind); Eq(rec.HealthToMana, r.SpellId);
            });
            Check("S2M modes replenish stamina by PERCENT of max too", () =>
            {
                var t = Table();
                var rec = RecoverySpells.Resolve(t, _ => true, new NB3Settings());
                var c = new ManaRegenController(ManaRegenMode.RestS2M, rec, 400);
                var midStam = AllKnown(); midStam.CurMana = 0; midStam.CurStam = 90; // 90/300 = 30%
                Eq(RegenActionKind.Rest, c.Next(midStam).Kind);                       // < 50% → rest
            });
            Check("TradeManaElixir just drinks", () =>
            {
                var s = AllKnown(); s.CurMana = 0;
                var c = new ManaRegenController(ManaRegenMode.TradeManaElixir, new RecoverySpells(), 400);
                Eq(RegenActionKind.DrinkTradeManaElixir, c.Next(s).Kind);
            });
            Check("mode is Unavailable when its recovery spell is unknown", () =>
            {
                var s = AllKnown(); s.CurMana = 0;
                var c = new ManaRegenController(ManaRegenMode.StaminaElixirS2M, new RecoverySpells(), 400); // S2M=0
                Eq(RegenActionKind.Unavailable, c.Next(s).Kind);
            });

            Console.WriteLine("\nSpellRecovery mode (DEFAULT: S2M + Cannibalize + Revitalize + Heal Self; consumables optional):");
            // A rec with all four recovery families resolved (H2M level 7 == the spell named
            // "Cannibalize", 0x091C). Costs default to the Fake's 50 unless a test overrides them.
            System.Func<RecoverySpells> Rec = () => RecoverySpells.Resolve(
                Table(), _ => true, new NB3Settings());
            Check("cannibalize IS the level-7 Health-to-Mana self spell (0x091C)", () =>
            {
                var fam = Table().ByEditorName("Health to Mana Self");
                True(fam != null); Eq(0x091C, fam.IdAtLevel(7));      // 0x091C == 2332 == EoR 'Cannibalize'
                Eq(fam.IdAtLevel(7), Rec().HealthToMana);             // spell mode's health->mana source
            });
            Check("done when mana already at target", () =>
            {
                var s = AllKnown(); s.CurMana = 500;                  // >= 400 target
                Eq(RegenActionKind.Done, new ManaRegenController(ManaRegenMode.SpellRecovery, Rec(), 400).Next(s).Kind);
            });
            Check("primary: casts S2M while stamina is healthy", () =>
            {
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 300; s.CurHealth = 200; // stam 100% >= 50% floor
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400).Next(s);
                Eq(RegenActionKind.Cast, r.Kind); Eq(rec.StaminaToMana, r.SpellId);
            });
            Check("stamina low but health healthy -> Cannibalize (health->mana)", () =>
            {
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 30; s.CurHealth = 200; // stam 10%<floor, health 100%
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400).Next(s);
                Eq(RegenActionKind.Cast, r.Kind); Eq(rec.HealthToMana, r.SpellId);
            });
            Check("both source vitals low -> Heal Self when health is more depleted (recover from H2M)", () =>
            {
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 140; s.CurHealth = 20; // 46% stam vs 10% health
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400).Next(s);
                Eq(RegenActionKind.Cast, r.Kind); Eq(rec.HealSelf, r.SpellId);
            });
            Check("both source vitals low -> Revitalize when stamina is more depleted", () =>
            {
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 30; s.CurHealth = 80; // 10% stam vs 40% health
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400).Next(s);
                Eq(RegenActionKind.Cast, r.Kind); Eq(rec.Revitalize, r.SpellId);
            });
            Check("restore falls back to the other vital when the preferred restore spell is unknown", () =>
            {
                var t = Table();
                var heal = t.ByEditorName("Heal Self");
                var healIds = new HashSet<int>(); for (int l = 1; l <= 7; l++) healIds.Add(heal.IdAtLevel(l));
                var rec = RecoverySpells.Resolve(t, id => !healIds.Contains(id),
                    new NB3Settings());
                Eq(0, rec.HealSelf);                                  // doesn't know Heal Self
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 140; s.CurHealth = 20; // health worse, but no Heal Self
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400).Next(s);
                Eq(rec.Revitalize, r.SpellId);                       // -> restore stamina instead
            });
            Check("both vitals below floor + nothing affordable/enabled -> waits on natural regen", () =>
            {
                var s = AllKnown(); s.CurMana = 10; s.CurStam = 30; s.CurHealth = 20; // mana < any cast cost
                Eq(RegenActionKind.Unavailable,
                   new ManaRegenController(ManaRegenMode.SpellRecovery, Rec(), 400).Next(s).Kind);
            });

            Console.WriteLine("\nSpellRecovery — health->mana toggle (stamina-only recovery when off):");
            Check("health->mana OFF: stamina low + health healthy -> Revitalize, never Cannibalize", () =>
            {
                // Same inputs that give Cannibalize when the toggle is on (see the test above): with
                // health->mana OFF the loop must NOT convert health; it restores stamina instead.
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 30; s.CurHealth = 200; // stam 10%<floor, health 100%
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400, null, null,
                                                allowHealthToMana: false).Next(s);
                Eq(RegenActionKind.Cast, r.Kind);
                Eq(rec.Revitalize, r.SpellId);
                True(r.SpellId != rec.HealthToMana);   // Cannibalize/H2M never issued
            });
            Check("health->mana OFF: health more depleted -> still restores stamina, never Heal Self", () =>
            {
                // These exact inputs give Heal Self when the toggle is on (health is the worse vital).
                // With health->mana OFF, health-restore is fully disabled: restore stamina, don't heal.
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 140; s.CurHealth = 20; // 46% stam vs 10% health
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400, null, null,
                                                allowHealthToMana: false).Next(s);
                Eq(RegenActionKind.Cast, r.Kind);
                Eq(rec.Revitalize, r.SpellId);
                True(r.SpellId != rec.HealSelf && r.SpellId != rec.HealthToMana);
            });
            Check("health->mana OFF: S2M primary is unaffected while stamina is healthy", () =>
            {
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 300; s.CurHealth = 200; // stam 100% >= floor
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400, null, null,
                                                allowHealthToMana: false).Next(s);
                Eq(RegenActionKind.Cast, r.Kind); Eq(rec.StaminaToMana, r.SpellId);
            });
            Check("health->mana OFF: stamina spent, can't restore -> waits, never drains full health", () =>
            {
                // Stamina below floor and Revitalize unaffordable (mana too low), health full. The
                // health->mana path would be the only option — it's off, so this must wait, not heal-drain.
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 10; s.CurStam = 30; s.CurHealth = 300; // mana < cast cost, health 100%
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400, null, null,
                                                allowHealthToMana: false).Next(s);
                Eq(RegenActionKind.Unavailable, r.Kind);
            });

            Console.WriteLine("\nSpellRecovery — optional, auto-scanned per-vital consumables:");
            Check("FindBestPotion returns the strongest drink for the requested vital", () =>
            {
                var s = AllKnown();
                s.Potions.Add((Vital.Health, 0x111, 25)); s.Potions.Add((Vital.Health, 0x222, 65));
                s.Potions.Add((Vital.Mana, 0x333, 70));
                Eq(0x222, s.FindBestPotion(Vital.Health));           // higher BoostValue wins
                Eq(0x333, s.FindBestPotion(Vital.Mana));
                Eq(0, s.FindBestPotion(Vital.Stamina));              // none carried
            });
            Check("opt-in potions drink the RIGHT vital for the depleted one", () =>
            {
                var cons = new RegenConsumables { Potions = true };
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 140; s.CurHealth = 20; // health more depleted
                s.Potions.Add((Vital.Health, 0xABCD, 65));
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, Rec(), 400, null, cons).Next(s);
                Eq(RegenActionKind.DrinkPotion, r.Kind); Eq(Vital.Health, r.Vital); Eq(0xABCD, r.ItemGuid);
            });
            Check("opt-in mana potion is the direct last resort when no restore is possible", () =>
            {
                var cons = new RegenConsumables { Potions = true };
                var s = AllKnown(); s.CurMana = 10; s.CurStam = 30; s.CurHealth = 20; // can't afford any cast
                s.Potions.Add((Vital.Mana, 0x9999, 70));             // only a mana potion carried
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, Rec(), 400, null, cons).Next(s);
                Eq(RegenActionKind.DrinkPotion, r.Kind); Eq(Vital.Mana, r.Vital); Eq(0x9999, r.ItemGuid);
            });
            Check("opt-in kits heal (auto-scanned) so Cannibalize can resume", () =>
            {
                var cons = new RegenConsumables { Kits = true };
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 140; s.CurHealth = 20; // health more depleted
                s.Kits.Add((0x7777, 300.0));
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, Rec(), 400, null, cons).Next(s);
                Eq(RegenActionKind.UseHealingKit, r.Kind); Eq(0x7777, r.ItemGuid);
            });
            Check("consumables stay OFF by default (spell-only) even when carried", () =>
            {
                var rec = Rec();
                var s = AllKnown(); s.CurMana = 100; s.CurStam = 140; s.CurHealth = 20;
                s.Potions.Add((Vital.Health, 0xABCD, 65)); s.Kits.Add((0x7777, 300.0));
                var r = new ManaRegenController(ManaRegenMode.SpellRecovery, rec, 400).Next(s); // no consumables opted in
                Eq(RegenActionKind.Cast, r.Kind); Eq(rec.HealSelf, r.SpellId);  // uses the spell, ignores the items
            });

            Console.WriteLine("\nDefault mana-regen (regression: the cycle no longer buffs to 0):");
            Check("NB3Settings defaults ManaRegenMode to SpellRecovery", () =>
            {
                Eq(ManaRegenMode.SpellRecovery, new NB3Settings().ManaRegenMode);
                Eq(false, new NB3Settings().UsePotions);             // potions optional, off by default
            });
            Check("Parse: missing manaRegenMode -> SpellRecovery; explicit 0 stays None", () =>
            {
                Eq(ManaRegenMode.SpellRecovery, NB3Settings.Parse("<NB3Config/>").ManaRegenMode);
                Eq(ManaRegenMode.None, NB3Settings.Parse("<NB3Config manaRegenMode=\"0\"/>").ManaRegenMode);
                Eq(ManaRegenMode.SpellRecovery, NB3Settings.Parse("<NB3Config manaRegenMode=\"6\"/>").ManaRegenMode);
            });
            Check("UsePotions round-trips through the config XML", () =>
            {
                var s = new NB3Settings { UsePotions = true };
                Eq(true, NB3Settings.Parse(s.ToXml()).UsePotions);
                Eq(ManaRegenMode.SpellRecovery, NB3Settings.Parse(s.ToXml()).ManaRegenMode);
            });
            Check("SpellRecovery cycle enters RegenMana when mana is low (not Cast-to-empty)", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.CurMana = 10; s.DefaultManaCost = 100;
                var cyc = new BuffCycle(new BuffEngine(t).BuildPlan(Sample(), s),
                    new CycleOptions { ManaRegenMode = ManaRegenMode.SpellRecovery });
                cyc.Start();
                cyc.Tick(s); cyc.ReportCastResult(true);             // equip the focus first
                var st = cyc.Tick(s);
                Eq(StepKind.RegenMana, st.Kind);                     // regen, instead of casting into empty mana
                Eq(ManaRegenMode.SpellRecovery, st.RegenMode);
            });

            Console.WriteLine("\nProfileGenerator (character-specific /nbgen):");
            Check("prefix order: Focus, Willpower, Creature Ench, Mana Conversion, Item Ench, Hermetic Link, Life Magic", () =>
            {
                // Creature/Item/Life castable; Mana Conversion + Life Magic + a weapon skill present.
                var tr = new Dictionary<int, int> { {31,3},{32,3},{33,3},{16,3},{11,2},{6,2} };
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                Eq("Focus Self", names[0]);
                Eq("Willpower Self", names[1]);
                Eq("Creature Magic Self", names[2]);
                Eq("Mana Conversion Self", names[3]);
                Eq("Item Magic Self", names[4]);            // Item Enchantment mastery, moved up (owner)
                Eq("Hermetic Link", names[5]);              // Hermetic Link right after it (owner)
                Eq("Life Magic Mastery Self", names[6]);
            });
            Check("Hermetic Link is cast right after Item Enchantment mastery, and only once", () =>
            {
                // A melee caster (Item + Creature castable, Sword trained): Hermetic Link is now a
                // fixed-prefix Core entry, so it lands right after Item Magic Self — not down among
                // the weapon auras — and the old AddWeaponAuras copy is gone (no duplicate).
                var tr = new Dictionary<int, int> { {31,3},{32,3},{16,3},{11,2} };
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                int item = names.IndexOf("Item Magic Self"), herm = names.IndexOf("Hermetic Link");
                True(item >= 0 && herm == item + 1, "Hermetic Link immediately follows Item Magic Self");
                Eq(1, names.Count(n => n == "Hermetic Link"));   // exactly one, not duplicated by AddWeaponAuras
            });
            Check("generated profile checkpoints the level bootstrap right after Creature Enchantment", () =>
            {
                var tr = new Dictionary<int, int> { {31,3},{32,3},{33,3},{16,3},{11,2} };
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                Eq("Focus Self", names[0]); Eq("Willpower Self", names[1]); Eq("Creature Magic Self", names[2]);
                Eq(3, r.Profile.CastingStatPrefixCount());        // prefix = Focus, Willpower, Creature Enchantment
            });
            Check("CastingStatPrefixCount is 0 without the mastery, and survives a profile round-trip", () =>
            {
                var none = new ModernProfile { Name = "n" };
                none.Buffs.Add(new ModernBuffEntry { DisplayName = "Strength Self", Category = 6, Target = SpellTarget.Self });
                Eq(0, none.CastingStatPrefixCount());

                var p = new ModernProfile { Name = "p" };
                p.Buffs.Add(new ModernBuffEntry { DisplayName = "Focus Self", Category = 1, Target = SpellTarget.Self });
                p.Buffs.Add(new ModernBuffEntry { DisplayName = "Creature Magic Self", Category = 3, Target = SpellTarget.Self });
                Eq(2, ModernProfile.Parse(p.ToXml()).CastingStatPrefixCount());  // DisplayName persists -> checkpoint survives
            });
            Check("skill masteries gate on trained/spec (Sword in, Bow out)", () =>
            {
                var tr = new Dictionary<int, int> { {31,3},{11,2},{2,0} }; // Creature spec; Sword trained; Bow untrained
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                True(names.Contains("Sword Mastery Self"), "Sword mastery expected");
                True(!names.Contains("Bow Mastery Self"), "Bow mastery should be skipped");
                True(r.SkippedUntrained.Contains("Bow Mastery Self"), "Bow reported as skipped-untrained");
            });
            Check("melee archetype: Blood Drinker + Heart Seeker + Defender + Swift Killer", () =>
            {
                var tr = new Dictionary<int, int> { {32,3},{11,2} }; // Item castable; Sword (melee) trained
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                True(names.Contains("Blood Drinker") && names.Contains("Heart Seeker"), "melee damage+accuracy");
                True(names.Contains("Defender") && names.Contains("Swift Killer"), "defence+speed");
                True(names.Contains("Hermetic Link"), "Hermetic Link benefits every archetype");
                True(!names.Contains("Spirit Drinker"), "physical uses Blood Drinker, not the caster Spirit Drinker label");
            });
            Check("missile archetype drops Heart Seeker only", () =>
            {
                var tr = new Dictionary<int, int> { {32,3},{2,2} }; // Item castable; Bow (missile) trained
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                True(names.Contains("Blood Drinker") && names.Contains("Defender") && names.Contains("Swift Killer"), "keeps the rest");
                True(names.Contains("Hermetic Link"), "Hermetic Link benefits every archetype");
                True(!names.Contains("Heart Seeker"), "Heart Seeker is melee-only");
            });
            Check("war/void caster gets Spirit Drinker + Hermetic Link only (no melee auras)", () =>
            {
                var tr = new Dictionary<int, int> { {32,3},{34,3} }; // Item castable; War Magic spec; no weapon skills
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                True(names.Contains("Spirit Drinker") && names.Contains("Hermetic Link"), "caster pair present");
                True(!names.Contains("Blood Drinker") && !names.Contains("Heart Seeker")
                     && !names.Contains("Defender") && !names.Contains("Swift Killer"), "no melee/missile auras");
            });
            Check("no Item magic => no banes, impen, or auras", () =>
            {
                var tr = new Dictionary<int, int> { {31,3},{11,2},{34,3} }; // Creature only; Item untrained
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var names = r.Profile.Buffs.Select(b => b.DisplayName).ToList();
                True(!names.Contains("Impenetrability") && !names.Contains("Acid Bane"), "no banes/impen");
                True(!names.Contains("Blood Drinker") && !names.Contains("Spirit Drinker") && !names.Contains("Hermetic Link"), "no auras");
                True(!r.ItemCastable, "Item not castable");
            });

            Console.WriteLine("\nBane/aura targeting (self-cast whole-suit — the /nbgen end-to-end):");
            Check("/nbgen banes+impen resolve to a CastSelf on the player (were silently dropped)", () =>
            {
                // The catalog exactly as the plugin builds it: classic 275-family table + 2012 dump.
                var dump = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var catalog = EditorCatalog.Build(Table(), dump, new List<string>());
                // Item Enchantment (32) trained -> banes are generated; give the schools + a weapon.
                var tr = new Dictionary<int, int> { { 31, 3 }, { 32, 3 }, { 33, 3 }, { 11, 3 } };
                var gen = ProfileGenerator.Generate(catalog, id => tr.TryGetValue(id, out var v) ? v : 0);

                // Plan with the dump AS the live table: banes classified target=item — the exact
                // case where a profile's Target=Self bane produced no cast at all (the reported bug).
                var st = new Fake { SelfId = 0x50000001 };        // KnowOnly=null => knows every spell
                var plan = new ModernBuffPlanner(dump).Plan(gen.Profile, st);

                string[] banes = { "Flame Bane", "Frost Bane", "Acid Bane", "Lightning Bane",
                                   "Blade Bane", "Piercing Bane", "Bludgeon Bane", "Impenetrability" };
                foreach (var n in banes)
                {
                    True(gen.Profile.Buffs.Any(b => b.DisplayName == n), $"{n} generated into the profile");
                    True(!plan.Unresolved.Contains(n), $"{n} must not be dropped as unresolved");
                    var acts = plan.Actions.Where(a => (a.Description ?? "").Contains(n)).ToList();
                    Eq(1, acts.Count);                             // one whole-suit self-cast
                    Eq(CastKind.CastSelf, acts[0].Kind);           // on the player, not per-item
                    Eq(0x50000001, acts[0].TargetGuid);            // the player's own guid
                }
            });
            Check("selector bridges a Self request onto a bane in an ITEM-ONLY category", () =>
            {
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(200, "Blade Bane VI", 174, 6, "Item", 10, SpellTarget.Item));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(174, SpellTarget.Self) }, _ => true, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(200, picks[0].SpellId);
                Eq(SpellTarget.Self, picks[0].Target);             // aimed at the player => CastSelf
            });
            Check("a Self request never bridges to an item spell sharing a MIXED category", () =>
            {
                // Category 83 in the real table: Mana Boost (Self) coexists with Essence Lull (Item).
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(300, "Mana Boost Self VI", 83, 6, "Life", 10, SpellTarget.Self));
                t.Add(new SpellInfo(301, "Essence Lull",       83, 1, "Item", 10, SpellTarget.Item));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(83, SpellTarget.Self) }, _ => true, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(300, picks[0].SpellId);                         // the real Self buff, NEVER the item spell
            });
            Check("the deliberate item-cast route still resolves the same bane (banes are self AND item)", () =>
            {
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(200, "Blade Bane VI", 174, 6, "Item", 10, SpellTarget.Item));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(174, SpellTarget.Item) }, _ => true, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(SpellTarget.Item, picks[0].Target);             // still available for an item target
            });

            Console.WriteLine("\nSpellTargetClassifier (aim from the live record, off-client):");
            Check("untargetted flag is a definitive Self, whatever the name/dump says", () =>
            {
                Eq(SpellTarget.Self, SpellTargetClassifier.Classify(true, null, "Inferno's Bane"));
                Eq(SpellTarget.Self, SpellTargetClassifier.Classify(true, SpellTarget.Item, "Infected Caress"));
                Eq(SpellTarget.Self, SpellTargetClassifier.Classify(true, null, "Aura of Blood Drinker Other VII"));
            });
            Check("'Aura of X Other' classifies Other, not Self (the cast-on-self hang)", () =>
            {
                // The modern weapon buffs ship as matched Self/Other auras; the " Other" token must
                // beat the "Aura of" prefix or the bot casts the fellow-buff on itself and hangs.
                Eq(SpellTarget.Other, SpellTargetClassifier.Classify(false, null, "Aura of Blood Drinker Other VII"));
                Eq(SpellTarget.Other, SpellTargetClassifier.Classify(null,  null, "Aura of Spirit Drinker Other VII"));
            });
            Check("'Aura of X Self' classifies Self (even if the flag is misread false)", () =>
            {
                Eq(SpellTarget.Self, SpellTargetClassifier.Classify(true,  null, "Aura of Blood Drinker Self VII"));
                Eq(SpellTarget.Self, SpellTargetClassifier.Classify(false, null, "Aura of Blood Drinker Self VII"));
                Eq(SpellTarget.Self, SpellTargetClassifier.Classify(null,  null, "Aura of Heart Seeker Self"));
            });
            Check("a live-TARGETED spell is never Self (stale dump / bare 'Aura of' prefix)", () =>
            {
                Eq(SpellTarget.Other, SpellTargetClassifier.Classify(false, SpellTarget.Self, "Some Renamed Buff"));
                Eq(SpellTarget.Other, SpellTargetClassifier.Classify(false, null, "Aura of Mysterious Thing"));
            });
            Check("classic item weapon buffs stay Item; ordinary Other/Item unchanged", () =>
            {
                Eq(SpellTarget.Item,  SpellTargetClassifier.Classify(false, SpellTarget.Item, "Infected Caress"));
                Eq(SpellTarget.Item,  SpellTargetClassifier.Classify(null,  SpellTarget.Item, "Blood Drinker VI"));
                Eq(SpellTarget.Other, SpellTargetClassifier.Classify(null,  SpellTarget.Other, "Strength Other VI"));
            });

            Console.WriteLine("\nWeapon-aura selection (self aura wins; item/Other never cast on the player):");
            Check("a Self weapon-buff request never bridges onto the classic item spell", () =>
            {
                // Group 154 with ONLY the classic item spell known (no aura): a Self request resolves
                // to nothing, NOT a cast of "Infected Caress" on the player (the reported hang).
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(2096, "Infected Caress", 154, 300, "Item", 70, SpellTarget.Item));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(154, SpellTarget.Self) }, _ => true, Array.Empty<int>());
                Eq(0, picks.Count);
            });
            Check("a Self weapon-buff request picks the self aura over the Other-aura and item spell", () =>
            {
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(2096, "Infected Caress",                 154, 300, "Item", 70, SpellTarget.Item));
                t.Add(new SpellInfo(5998, "Aura of Blood Drinker Other VII", 154, 300, "Item", 70, SpellTarget.Other));
                t.Add(new SpellInfo(5997, "Aura of Blood Drinker Self VII",  154, 300, "Item", 70, SpellTarget.Self));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(154, SpellTarget.Self) }, _ => true, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(5997, picks[0].SpellId);                        // the Self aura, never 5998 (Other) or 2096 (item)
                Eq(SpellTarget.Self, picks[0].Target);
            });
            Check("an armor bane still bridges a Self request onto its item spell (unchanged)", () =>
            {
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(2102, "Inferno's Bane", 170, 300, "Item", 70, SpellTarget.Item));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(170, SpellTarget.Self) }, _ => true, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(2102, picks[0].SpellId);
                Eq(SpellTarget.Self, picks[0].Target);
            });

            Console.WriteLine("\nSchool disambiguation (cross-school stacking group 67: Heal Self vs Healing Mastery):");
            Check("a Creature entry resolves to the Creature buff, never the Life burst in the group", () =>
            {
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(6,    "Heal Self I",                        67, 1,   "Life",     10, SpellTarget.Self));
                t.Add(new SpellInfo(4311, "Incantation of Heal Self",           67, 400, "Life",     50, SpellTarget.Self));
                t.Add(new SpellInfo(874,  "Healing Mastery Self I",             67, 1,   "Creature", 10, SpellTarget.Self));
                t.Add(new SpellInfo(4556, "Incantation of Healing Mastery Self", 67, 400, "Creature", 50, SpellTarget.Self));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(67, SpellTarget.Self, 0, "Creature") }, _ => true, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(4556, picks[0].SpellId);                    // Healing Mastery, never Heal Self (4311)
            });
            Check("skips rather than cast the wrong-school pollutant when its own school isn't known", () =>
            {
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(4311, "Incantation of Heal Self", 67, 400, "Life", 50, SpellTarget.Self)); // only Life known
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(67, SpellTarget.Self, 0, "Creature") }, _ => true, Array.Empty<int>());
                Eq(0, picks.Count);                            // no Creature buff known -> skip, never cast Heal Self
            });
            Check("no school constraint keeps the old behaviour (highest known, unconstrained)", () =>
            {
                var t = new FakeSpellTable();
                t.Add(new SpellInfo(874,  "Healing Mastery Self I",   67, 1,   "Creature", 10, SpellTarget.Self));
                t.Add(new SpellInfo(4311, "Incantation of Heal Self", 67, 400, "Life",     50, SpellTarget.Self));
                var picks = new ModernBuffSelector(t)
                    .Select(new[] { new DesiredBuff(67, SpellTarget.Self) }, _ => true, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(4311, picks[0].SpellId);                    // unconstrained -> highest level (why the entry needs a school)
            });
            Check("/nbgen tags the Healing Mastery entry with its Creature school", () =>
            {
                var tr = new Dictionary<int, int> { {31,3},{33,3},{21,3} }; // Creature+Life castable; Healing (21) trained
                var r = ProfileGenerator.Generate(GenCatalog(), id => tr.TryGetValue(id, out var v) ? v : 0);
                var hm = r.Profile.Buffs.FirstOrDefault(b => b.DisplayName == "Healing Mastery Self");
                True(hm != null, "Healing Mastery generated (Healing trained)");
                Eq("Creature", hm.School);
            });

            Console.WriteLine("\nEoR spell table (authoritative dump: Family / isUntargeted / Duration / School.Id):");
            Check("parses the EoR format — target from isUntargeted, school from School.Id, Duration", () =>
            {
                var c = SpellCatalog.Load(Fx("spell-table-eor.tsv"));
                var heal = c.ById(6);                          // Heal Self I
                Eq("Heal Self I", heal.Name);
                Eq(67, heal.Category);
                Eq(SpellTarget.Self, heal.Target);             // isUntargeted=True -> Self
                Eq("Life", heal.School);                       // School.Id 2 -> Life
                True(heal.IsInstantaneous, "Heal Self is a burst (Duration -1)");
                var hm = c.ById(874);                          // Healing Mastery Self I
                Eq(67, hm.Category); Eq("Creature", hm.School); // School.Id 4 -> Creature
                True(!hm.IsInstantaneous, "Healing Mastery is a persistent buff (Duration 1800)");
            });
            Check("banes classify as targeted Item (isUntargeted=False), not Self", () =>
            {
                var c = SpellCatalog.Load(Fx("spell-table-eor.tsv"));
                var bane = c.InGroup(170).First(s => s.Name == "Flame Bane I"); // group 170 = Flame Bane
                Eq(SpellTarget.Item, bane.Target);             // isUntargeted=False + School 3 -> Item (reached via the bane bridge)
                Eq("Item", bane.School);
            });
            Check("weapon-aura Self is untargeted (Self); Other is targeted (not Self)", () =>
            {
                var c = SpellCatalog.Load(Fx("spell-table-eor.tsv"));
                Eq(SpellTarget.Self, c.ById(35).Target);       // Aura of Blood Drinker Self I (untargeted=True)
                True(c.ById(5990).Target != SpellTarget.Self); // Aura of Blood Drinker Other I (untargeted=False)
            });
            Check("EoR: group 67 resolves to the Healing Mastery buff, never a Heal Self burst", () =>
            {
                var c = SpellCatalog.Load(Fx("spell-table-eor.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = 67, Target = SpellTarget.Self, School = "Creature", DisplayName = "Healing Mastery Self" });
                var plan = new ModernBuffPlanner(c).Plan(prof, AllKnown());
                var picked = c.ById(plan.Actions.Single(a => a.Kind == CastKind.CastSelf).SpellId);
                True(picked.Name.IndexOf("Healing Mastery", StringComparison.OrdinalIgnoreCase) >= 0, $"picked {picked.Name}");
                True(!picked.IsInstantaneous, "not a burst");
            });
            Check("Duration filter alone drops Heal Self even with NO school constraint (EoR)", () =>
            {
                var c = SpellCatalog.Load(Fx("spell-table-eor.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = 67, Target = SpellTarget.Self }); // no school
                var plan = new ModernBuffPlanner(c).Plan(prof, AllKnown());
                var picked = c.ById(plan.Actions.Single(a => a.Kind == CastKind.CastSelf).SpellId);
                True(!picked.IsInstantaneous, "burst (Heal Self) excluded by Duration");
                True(picked.Name.IndexOf("Healing Mastery", StringComparison.OrdinalIgnoreCase) >= 0, "-> the persistent buff");
            });
            Check("EoR fallback overlays mana from the 2012 dump by id", () =>
            {
                var eor = SpellCatalog.Load(Fx("spell-table-eor.tsv"));
                var m2012 = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                Eq(0, eor.ById(6).Mana);                       // EoR has no mana column
                var merged = eor.WithManaFrom(m2012);
                Eq(m2012.ById(6).Mana, merged.ById(6).Mana);   // overlaid from 2012 (15)
                True(!merged.ById(874).IsInstantaneous, "EoR Duration preserved through the overlay");
            });

            Console.WriteLine("\nProfileEditor (the editor view's operations):");
            Check("add / remove / reorder nodes", () =>
            {
                var ed = new ProfileEditor(new Profile { Name = "p" });
                ed.AddEquip("Focusing Stone");
                var a = ed.AddSelfSpell(0x0531);
                var b = ed.AddSelfSpell(0x0560);
                Eq(3, ed.Count);
                True(ed.MoveUp(2));                       // move b above a
                Eq(b, ((SpellNode)ed.Profile.Nodes[1]));
                True(ed.RemoveAt(0));                      // drop the equip
                Eq(2, ed.Count);
                True(!ed.MoveDown(1));                     // last item can't move down
            });
            Check("add item spellgroup with cover + spells", () =>
            {
                var ed = new ProfileEditor(new Profile { Name = "p" });
                var g = ed.AddItemSpellGroup(0x0000DE, new[] { 0x05CA, 0x05E8 });
                Eq(0x0000DE, g.TargetCover);
                Eq(2, g.SpellIds.Count);
            });
            Check("clear empties the profile", () =>
            {
                var ed = new ProfileEditor(Sample());
                True(ed.Count > 0); ed.Clear(); Eq(0, ed.Count);
            });

            Console.WriteLine("\nProfileStore (named-profile file management):");
            Check("create, list, load, duplicate, revert, delete", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "nb3store_" + System.Guid.NewGuid().ToString("N"));
                var store = new ProfileStore(dir);

                // seed from the real recovered sample
                var sample = Sample(); sample.Name = "MyBuffs"; store.Save(sample);
                True(store.Exists("MyBuffs"));
                True(store.List().Contains("MyBuffs"));

                // load round-trips node count
                Eq(sample.Nodes.Count, store.Load("MyBuffs").Nodes.Count);

                // duplicate (Copy Profile)
                True(store.Duplicate("MyBuffs", "MyBuffs2"));
                True(store.Exists("MyBuffs2"));
                Eq("MyBuffs2", store.Load("MyBuffs2").Name);
                True(!store.Duplicate("MyBuffs", "MyBuffs2")); // no clobber

                // new empty (New Profile)
                store.Create("Empty");
                Eq(0, store.Load("Empty").Nodes.Count);

                // non-permanent delete goes to trash; permanent removes
                True(store.Delete("Empty", permanent: false));
                True(!store.Exists("Empty"));
                True(File.Exists(Path.Combine(dir, "_deleted", "Empty.xml")));
                True(store.Delete("MyBuffs2", permanent: true));
                True(!File.Exists(Path.Combine(dir, "_deleted", "MyBuffs2.xml")));

                Directory.Delete(dir, true);
            });

            Console.WriteLine("\nCoverageMask (recovered by disassembly, validated vs sample):");
            Check("all-armor checkboxes reproduce the sample's 0x7F21 group", () =>
            {
                uint m = CoverageCheckboxes.FromCheckboxes(new[] { "Coat", "Legs", "Girth", "Head", "Feet", "Hands" });
                Eq(0x00007F21u, m);
            });
            Check("weapon checkbox includes the melee bit used by the 0x100000 group", () =>
            {
                True((CoverageCheckboxes.Weapon & (uint)CoverageBits.MeleeWeapon) != 0);
                True(((uint)CoverageBits.MeleeWeapon) == 0x00100000u);
            });
            Check("individual checkbox masks match the recovered table", () =>
            {
                Eq(0x00001A00u, CoverageCheckboxes.Coat);
                Eq(0x00006000u, CoverageCheckboxes.Legs);
                Eq(0x00200000u, CoverageCheckboxes.Shield);
                Eq(0x01000000u, CoverageCheckboxes.Wand);
            });
            Check("Coat covers chest + both arm segments", () =>
            {
                uint c = CoverageCheckboxes.Coat;
                True((c & (uint)CoverageBits.ChestArmor) != 0);
                True((c & (uint)CoverageBits.UpperArms) != 0);
                True((c & (uint)CoverageBits.LowerArms) != 0);
            });

            Console.WriteLine("\nEoRSpellCatalog (cantrip classifier, validated vs doc 16):");
            Check("classifier reproduces doc-16 counts exactly", () =>
            {
                var cat = EoRSpellCatalog.Load(Fx("eor-spells.csv"));
                Eq(3250, cat.Count);
                Eq(86, cat.TierCount(CantripTier.Minor));
                Eq(76, cat.TierCount(CantripTier.Major));
                Eq(79, cat.TierCount(CantripTier.Epic));
                Eq(69, cat.TierCount(CantripTier.Legendary));
                Eq(79, cat.CantripFamilies.Count);
                Eq(69, cat.FamiliesAtAllTiers().Count);
            });
            Check("cantrips are separated from castable spells", () =>
            {
                var cat = EoRSpellCatalog.Load(Fx("eor-spells.csv"));
                int cantrips = cat.Cantrips.Count();
                Eq(310, cantrips);                    // 86+76+79+69
                Eq(2940, cat.Count - cantrips);       // the castable/other remainder
                True(cat.FindCantrip(CantripTier.Legendary, "Impenetrability") != null);
            });
            Check("Bane and Ward are distinct families", () =>
            {
                var cat = EoRSpellCatalog.Load(Fx("eor-spells.csv"));
                True(cat.CantripFamilies.Contains("Acid Bane"));
                True(cat.CantripFamilies.Contains("Acid Ward"));
            });

            Console.WriteLine("\nModernBuffSelector (category-stacking, EoR/ACE):");
            Check("picks highest known level per category", () =>
            {
                var t = new FakeSpellTable();
                t.Add(101, "Strength Self I", cat: 50, level: 1);
                t.Add(106, "Strength Self VI", cat: 50, level: 6);
                t.Add(1200, "Incantation of Strength Self", cat: 50, level: 8);
                var sel = new ModernBuffSelector(t);
                var picks = sel.Select(new[] { new DesiredBuff(50) }, id => id != 1200 /*don't know L8*/, Array.Empty<int>());
                Eq(1, picks.Count); Eq(106, picks[0].SpellId); Eq(6, picks[0].Level);
            });
            Check("skips a category already covered by an equal/higher active enchantment", () =>
            {
                var t = new FakeSpellTable();
                t.Add(106, "Strength Self VI", 50, 6);
                t.Add(1200, "Incantation of Strength Self", 50, 8);
                var sel = new ModernBuffSelector(t);
                // L8 already active on that category -> skip re-cast
                var picks = sel.Select(new[] { new DesiredBuff(50) }, id => true, new[] { 1200 });
                Eq(0, picks.Count);
            });
            Check("re-casts when the active enchant is a lower level than best known", () =>
            {
                var t = new FakeSpellTable();
                t.Add(101, "Strength Self I", 50, 1);
                t.Add(106, "Strength Self VI", 50, 6);
                var sel = new ModernBuffSelector(t);
                var picks = sel.Select(new[] { new DesiredBuff(50) }, id => true, new[] { 101 /*only L1 up*/ });
                Eq(1, picks.Count); Eq(106, picks[0].SpellId);
            });
            Check("different categories stack (both selected)", () =>
            {
                var t = new FakeSpellTable();
                t.Add(106, "Strength Self VI", 50, 6);
                t.Add(206, "Flame Bane", 77, 6);      // modern bane: self-cast, its own category
                var sel = new ModernBuffSelector(t);
                var picks = sel.Select(new[] { new DesiredBuff(50), new DesiredBuff(77) }, id => true, Array.Empty<int>());
                Eq(2, picks.Count);
            });
            Check("max-level cap limits the chosen spell", () =>
            {
                var t = new FakeSpellTable();
                t.Add(104, "Strength Self IV", 50, 4);
                t.Add(1200, "Incantation of Strength Self", 50, 8);
                var sel = new ModernBuffSelector(t);
                var picks = sel.Select(new[] { new DesiredBuff(50, maxLevel: 4) }, id => true, Array.Empty<int>());
                Eq(1, picks.Count); Eq(104, picks[0].SpellId);
            });

            Console.WriteLine("\nSpellCatalog (real 2012 retail dump: group=category, difficulty=level):");
            Check("loads the full dump", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                Eq(5891, c.Count);
                True(c.ById(1157) != null && c.ById(1157).Name == "Heal Self II"); // classic ids intact
            });
            Check("Strength group stacks by real difficulty ladder", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var strengthSelf6 = c.ById(1332); // Strength Self VI
                Eq("Strength Self VI", strengthSelf6.Name);
                Eq(250, strengthSelf6.Level);      // difficulty 250 = top of the ladder
                var group = c.InGroup(strengthSelf6.Category).Select(s => s.Level).Distinct().OrderBy(x => x).ToList();
                True(group.Contains(1) && group.Contains(250)); // real power ladder present
            });
            Check("selector picks the highest known real Strength spell", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(1332).Category;
                var known = new HashSet<int> { 2, 1328, 1329 }; // Strength Self I, II, III only
                var picks = new ModernBuffSelector(c).Select(new[] { new DesiredBuff(grp) }, known.Contains, Array.Empty<int>());
                Eq(1, picks.Count);
                Eq(1329, picks[0].SpellId);        // Strength Self III is the best known
            });
            Check("selector skips when the best-known spell is already active", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(1332).Category;
                // player only knows Strength Self VI; it's already up -> nothing to do
                var picks = new ModernBuffSelector(c).Select(new[] { new DesiredBuff(grp) }, id => id == 1332, new[] { 1332 });
                Eq(0, picks.Count);
            });
            Check("real mana + school populate on records", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var s = c.ById(1332);
                Eq("Creature", s.School);
                Eq(70, s.Mana);
            });

            Console.WriteLine("\nEmpty-plan honesty (why nothing cast: active vs unresolved):");
            Check("unresolved buff (unknown category) is reported, not called 'active'", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = 999999, Target = SpellTarget.Self, DisplayName = "Bogus Buff" });
                var st = AllKnown();                                  // knows everything, but category 999999 has no spell
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                Eq(0, plan.Actions.Count);
                Eq(0, plan.SkippedAlreadyActive);
                Eq(1, plan.Unresolved.Count);
                True(plan.Unresolved[0] == "Bogus Buff");
            });
            Check("unknown-to-spellbook buff is unresolved, not active", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self, DisplayName = "Strength Self" });
                var st = AllKnown(); st.KnowOnly = new HashSet<int>();  // knows NOTHING
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                Eq(0, plan.Actions.Count);
                Eq(1, plan.Unresolved.Count);
                Eq(0, plan.SkippedAlreadyActive);
            });
            Check("skip-active (recast off) is counted as active, not unresolved", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var top = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self).OrderByDescending(s => s.Level).First();
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self, DisplayName = "Strength Self" });
                var st = AllKnown(); st.ActiveEnchants.Add(top.Id);
                var plan = new ModernBuffPlanner(c).Plan(prof, st, null, new RebuffPolicy());  // skip active
                Eq(0, plan.Actions.Count);
                Eq(1, plan.SkippedAlreadyActive);
                Eq(0, plan.Unresolved.Count);
            });
            Check("recast-all (force) casts through active — no skip, no unresolved", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var top = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self).OrderByDescending(s => s.Level).First();
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self, DisplayName = "Strength Self" });
                var st = AllKnown(); st.ActiveEnchants.Add(top.Id);
                var plan = new ModernBuffPlanner(c).Plan(prof, st, null, new RebuffPolicy { ForceAll = true });
                Eq(1, plan.Actions.Count(a => a.Kind == CastKind.CastSelf));
                Eq(0, plan.SkippedAlreadyActive);
                Eq(0, plan.Unresolved.Count);
            });

            Console.WriteLine("\nRebuff policy (the 'after casting, re-run says 0 spells' report):");
            Check("re-run with everything active plans nothing (the reported symptom)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var top = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self).OrderByDescending(s => s.Level).First();
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self });
                var st = AllKnown();
                st.ActiveEnchants.Add(top.Id);                       // already buffed at the top level
                var plan = new ModernBuffPlanner(c).Plan(prof, st);  // no rebuff policy = skip active
                Eq(0, plan.Actions.Count);                           // <- the "0 spells" the user saw
            });
            Check("force recasts even when the buff is active", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var top = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self).OrderByDescending(s => s.Level).First();
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self });
                var st = AllKnown(); st.ActiveEnchants.Add(top.Id);
                var plan = new ModernBuffPlanner(c).Plan(prof, st, null,
                    new RebuffPolicy { ForceAll = true });
                Eq(1, plan.Actions.Count(a => a.Kind == CastKind.CastSelf));
            });
            Check("rebuff window recasts a buff expiring soon, keeps a fresh one", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var top = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self).OrderByDescending(s => s.Level).First();
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self });
                // expiring in 2 min, threshold 5 min -> recast
                var st1 = AllKnown(); st1.ActiveEnchants.Add(top.Id); st1.EnchantSeconds[top.Id] = 120;
                var expiring = new ModernBuffPlanner(c).Plan(prof, st1, null, new RebuffPolicy { MinSecondsRemaining = 300 });
                Eq(1, expiring.Actions.Count(a => a.Kind == CastKind.CastSelf));
                // fresh (30 min left) -> skip
                var st2 = AllKnown(); st2.ActiveEnchants.Add(top.Id); st2.EnchantSeconds[top.Id] = 1800;
                var fresh = new ModernBuffPlanner(c).Plan(prof, st2, null, new RebuffPolicy { MinSecondsRemaining = 300 });
                Eq(0, fresh.Actions.Count(a => a.Kind == CastKind.CastSelf));
            });
            Check("profile keeps its buffs across plan calls (not mutated by casting)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self });
                var planner = new ModernBuffPlanner(c);
                planner.Plan(prof, AllKnown());
                planner.Plan(prof, AllKnown());
                Eq(1, prof.Buffs.Count);                             // planning never edits the profile
            });

            Console.WriteLine("\nCastChance (ACE fizzle model: factor 0.07, Power-50 attempt floor):");
            Check("below the Power-50 floor cannot be attempted (auto-fail)", () =>
            {
                True(!CastChance.CanAttempt(349, 400));   // skill 349 vs Power 400 -> gated out
                True(CastChance.CanAttempt(350, 400));    // exactly Power-50 -> allowed
                Eq(0.0, CastChance.SuccessChance(349, 400));
            });
            Check("chance climbs the 0.07 sigmoid; skill==Power is 50%", () =>
            {
                True(Math.Abs(CastChance.SuccessChance(300, 300) - 0.5) < 1e-9);
                // ACE: 1 - 1/(1+e^(0.07*(skill-power)))
                double p410 = CastChance.SuccessChance(410, 400);   // +10 over Power 400
                True(p410 > 0.66 && p410 < 0.68, $"expected ~0.67, got {p410:F3}");
                double p450 = CastChance.SuccessChance(450, 400);   // +50 -> ~0.97
                True(p450 > 0.96, $"expected >0.96, got {p450:F3}");
            });
            Check("higher level (Power) is strictly harder at fixed skill", () =>
            {
                int skill = 320;
                True(CastChance.SuccessChance(skill, 250) > CastChance.SuccessChance(skill, 300));
                True(CastChance.SuccessChance(skill, 300) > CastChance.SuccessChance(skill, 400));
            });

            Console.WriteLine("\nSkill-capped selection (cast the highest level you can LAND, not just know):");
            Check("low skill downgrades a known level-8 to the reliable level", () =>
            {
                // Strength Self group from the real 2012 catalog: VI=250, L7=300, L8=400.
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;                       // Strength Self group (id 2 = Self I)
                var sel = new ModernBuffSelector(c);
                var desired = new[] { new DesiredBuff(grp, SpellTarget.Self) };
                // Skill 300, min 90%: Power-400 (L8) is auto-gated (<350), Power-300 is only 50%,
                // Power-250 (VI) at skill 300 -> ~0.97 -> the reliable pick.
                var policy = new SkillPolicy { Enabled = true, MinChancePercent = 90,
                    SkillOfSchool = _ => 300 };
                var pick = sel.Select(desired, _ => true, Array.Empty<int>(), policy).Single();
                True(pick.SkillCapped, "expected a skill cap");
                Eq(250, pick.Level);                                // capped down to Strength Self VI
                True(pick.UncappedLevel >= 400, "uncapped would have been the L8 (400)");
                Eq("self", c.ById(pick.SpellId).Target.ToString().ToLowerInvariant());
            });
            Check("high skill casts the top level, no cap flag", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var sel = new ModernBuffSelector(c);
                var policy = new SkillPolicy { Enabled = true, MinChancePercent = 90, SkillOfSchool = _ => 500 };
                var pick = sel.Select(new[] { new DesiredBuff(grp, SpellTarget.Self) }, _ => true, Array.Empty<int>(), policy).Single();
                True(!pick.SkillCapped, "no cap at skill 500");
                // Top of the real LADDER (numbered <=300 + the level-8 Incantation @400); the group
                // also holds an off-ladder special (Zongo's Fist, 420) that selection now excludes.
                var top = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self
                    && (s.Level <= 300 || s.Name.IndexOf("Incantation", StringComparison.OrdinalIgnoreCase) >= 0)).Max(s => s.Level);
                Eq(top, pick.Level);
            });
            Check("skill 0 (unreadable) fails open to highest known", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var sel = new ModernBuffSelector(c);
                var policy = new SkillPolicy { Enabled = true, MinChancePercent = 90, SkillOfSchool = _ => 0 };
                var pick = sel.Select(new[] { new DesiredBuff(grp, SpellTarget.Self) }, _ => true, Array.Empty<int>(), policy).Single();
                True(!pick.SkillCapped);
                // Top of the real ladder — off-ladder specials (power >300, not an Incantation) excluded.
                var top = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self
                    && (s.Level <= 300 || s.Name.IndexOf("Incantation", StringComparison.OrdinalIgnoreCase) >= 0)).Max(s => s.Level);
                Eq(top, pick.Level);
            });
            Check("disabled policy == historic highest-known behaviour", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var sel = new ModernBuffSelector(c);
                var off = new SkillPolicy { Enabled = false, SkillOfSchool = _ => 50 };
                var pick = sel.Select(new[] { new DesiredBuff(grp, SpellTarget.Self) }, _ => true, Array.Empty<int>(), off).Single();
                True(!pick.SkillCapped);
            });
            Check("planner emits a skill-cap warning and casts the capped spell", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self, DisplayName = "Strength Self" });
                var st = AllKnown(); st.SkillBySchool["Creature"] = 300;
                var plan = new ModernBuffPlanner(c).Plan(prof, st, new SkillPolicy { Enabled = true, MinChancePercent = 90 });
                var cast = plan.Actions.Single(a => a.Kind == CastKind.CastSelf);
                Eq(250, c.ById(cast.SpellId).Level);                // Strength Self VI, not the L8
                True(plan.Warnings.Any(w => w.Message.IndexOf("skill-capped", StringComparison.OrdinalIgnoreCase) >= 0));
            });

            Console.WriteLine("\nLevel bootstrap (re-plan after skill rises casts the higher level):");
            Check("re-plan after skill rises recasts the buff at a HIGHER level (Focus 6 -> 7)", () =>
            {
                // The mechanism the owner described: a buff lands at a low level because skill was too
                // low; once the casting-stat buffs raise the skill, a re-plan (ForceAll off) casts it
                // higher — the active lower level does NOT block it (stacking: higher surpasses lower).
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;                       // a real self ladder (Strength: VI=250, L7=300, L8=400)
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self, DisplayName = "Strength Self" });
                var st = AllKnown();

                st.SkillBySchool["Creature"] = 300;                 // low skill -> capped to VI (250)
                var plan1 = new ModernBuffPlanner(c).Plan(prof, st, new SkillPolicy { Enabled = true, MinChancePercent = 90 });
                var cast1 = plan1.Actions.Single(a => a.Kind == CastKind.CastSelf);
                Eq(250, c.ById(cast1.SpellId).Level);

                st.ActiveEnchants.Add(cast1.SpellId);               // that level is now active
                st.SkillBySchool["Creature"] = 500;                 // the casting-stat buffs raised the skill

                var plan2 = new ModernBuffPlanner(c).Plan(prof, st,
                    new SkillPolicy { Enabled = true, MinChancePercent = 90 },
                    new RebuffPolicy { ForceAll = false });         // the bootstrap upgrade pass
                var cast2 = plan2.Actions.Single(a => a.Kind == CastKind.CastSelf);
                True(c.ById(cast2.SpellId).Level > 250, "recast at a higher level once skill rose");
            });
            Check("the upgrade loop terminates: nothing to recast once at the skill's max", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(2).Category;
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self, DisplayName = "Strength Self" });
                var st = AllKnown(); st.SkillBySchool["Creature"] = 500;         // already high
                var plan1 = new ModernBuffPlanner(c).Plan(prof, st, new SkillPolicy { Enabled = true, MinChancePercent = 90 });
                st.ActiveEnchants.Add(plan1.Actions.Single(a => a.Kind == CastKind.CastSelf).SpellId); // top level active
                var plan2 = new ModernBuffPlanner(c).Plan(prof, st,
                    new SkillPolicy { Enabled = true, MinChancePercent = 90 }, new RebuffPolicy { ForceAll = false });
                Eq(0, plan2.Actions.Count(a => a.Kind != CastKind.Equip));       // stable -> loop ends
            });
            Check("spellbook-sourced selection can't pick a monster/boss spell sharing the group", () =>
            {
                // Category 37 (Invulnerability / Melee Defense) in the client spell table also holds
                // the boss enchant "Aerbax's Melee Shield" / "Aerbax Melee Shield Down" (Power 800).
                // Those are NOT in a player's spellbook. Sourcing candidates from the book (this
                // known-set) means the pick is the real top — the level-8 Incantation — never the
                // 800-power boss spell. This is the "Invulnerability got messed up" fix at the root.
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var book = new HashSet<int> { 18, 245, 246, 247, 248, 249, 2245, 4560 }; // Inv I-VI + Aura of Defense + Incantation
                True(!book.Contains(4242) && !book.Contains(4243), "the Aerbax boss-shield ids are not in the spellbook");
                var pick = new ModernBuffSelector(c)
                    .Select(new[] { new DesiredBuff(37, SpellTarget.Self) }, book.Contains, Array.Empty<int>()).Single();
                Eq("Incantation of Invulnerability Self", c.ById(pick.SpellId).Name);
            });
            Check("Invulnerability resolves to the level-7 Aura of Defense when level 8 isn't in the book", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                // Spellbook holds the Invulnerability ladder up to the bespoke level-7 (Aura of
                // Defense, id 2245), NOT the level-8 Incantation (4560): highest known = Aura of Defense.
                var book = new HashSet<int> { 18, 245, 246, 247, 248, 249, 2245 };
                var pick = new ModernBuffSelector(c)
                    .Select(new[] { new DesiredBuff(37, SpellTarget.Self) }, book.Contains, Array.Empty<int>()).Single();
                Eq("Aura of Defense", c.ById(pick.SpellId).Name);
                Eq(300, pick.Level);
            });
            Check("a LEARNED off-ladder Self special (Harbinger, power 400) is not picked over the real ladder", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                // Cat 37 also holds "Harbinger Melee Defense" (id 4195, power 400, target=SELF) and
                // (on live) self-classified fellowship buffs like "Potent Guardian of the Clutch"
                // (325). Those get LEARNED, so the spellbook filter can't drop them, and they outrank
                // the real level-7 — casting them on self hangs. The ladder bound (power>300 and not
                // an "Incantation of …") drops them, leaving the real level-8 Incantation.
                var book = new HashSet<int> { 18, 245, 246, 247, 248, 249, 2245, 4560, 4195 }; // + Incantation + Harbinger
                var pick = new ModernBuffSelector(c)
                    .Select(new[] { new DesiredBuff(37, SpellTarget.Self) }, book.Contains, Array.Empty<int>()).Single();
                Eq("Incantation of Invulnerability Self", c.ById(pick.SpellId).Name);   // never Harbinger
            });

            Console.WriteLine("\nModernProfile (era-proof, category-identified):");
            Check("round-trips category/target/maxlevel", () =>
            {
                var p = new ModernProfile { Name = "Modern Self" };
                p.EquipItems.Add("Focusing Stone");
                p.Buffs.Add(new ModernBuffEntry { Category = 1, Target = SpellTarget.Self, MaxLevel = 8, DisplayName = "Strength" });
                p.Buffs.Add(new ModernBuffEntry { Category = 67, Target = SpellTarget.Self, DisplayName = "Heal" });
                var p2 = ModernProfile.Parse(p.ToXml());
                Eq("Modern Self", p2.Name);
                Eq(1, p2.EquipItems.Count);
                Eq(2, p2.Buffs.Count);
                Eq(1, p2.Buffs[0].Category);
                Eq(SpellTarget.Self, p2.Buffs[0].Target);
                Eq(8, p2.Buffs[0].MaxLevel);
            });

            Console.WriteLine("\nModernBuffPlanner (profile -> live-resolved BuffPlan, on real 2012 data):");
            Check("resolves a self-buff profile to the highest known real spells", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int strengthGrp = c.ById(1332).Category;
                // Top of the real ladder (off-ladder specials like Zongo's Fist, 420, excluded).
                var topStrengthSelf = c.InGroup(strengthGrp)
                    .Where(s => s.Target == SpellTarget.Self
                        && (s.Level <= 300 || s.Name.IndexOf("Incantation", StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderByDescending(s => s.Level).First();
                var prof = new ModernProfile { Name = "p" };
                prof.EquipItems.Add("Focusing Stone");
                prof.Buffs.Add(new ModernBuffEntry { Category = strengthGrp, Target = SpellTarget.Self });
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1161).Category, Target = SpellTarget.Self }); // Heal Self
                var st = AllKnown(); st.SelfId = 0x1234; st.ItemsByName["Focusing Stone"] = 0xAAAA;
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                Eq(CastKind.Equip, plan.Actions[0].Kind);
                var casts = plan.Actions.Where(a => a.Kind == CastKind.CastSelf).ToList();
                Eq(2, casts.Count);
                True(casts.All(a => a.TargetGuid == 0x1234));
                True(casts.Any(a => a.SpellId == topStrengthSelf.Id)); // the highest self variant, whatever level
            });
            Check("picks the Self variant, never the Other, for a self buff", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                var cast = plan.Actions.Single(a => a.Kind == CastKind.CastSelf);
                Eq(SpellTarget.Self, c.ById(cast.SpellId).Target);   // never an "Other" spell
            });
            Check("skips a buff already active at equal/higher level (stacking)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(1332).Category;
                var topSelf = c.InGroup(grp).Where(s => s.Target == SpellTarget.Self)
                    .OrderByDescending(s => s.Level).First();
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Self });
                var st = AllKnown();
                st.ActiveEnchants.Add(topSelf.Id);   // the actual best is already up -> nothing to cast
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                Eq(0, plan.Actions.Count(a => a.Kind == CastKind.CastSelf));
            });
            Check("max-level cap flows through the planner", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self, MaxLevel = 100 }); // <= Strength III (diff 100)
                var st = AllKnown();
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                var cast = plan.Actions.Single(a => a.Kind == CastKind.CastSelf);
                True(c.ById(cast.SpellId).Level <= 100);
            });
            Check("planner output drives BuffCycle end-to-end", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1161).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                var cyc = new BuffCycle(plan); cyc.Start();
                int guard = 0;
                while (cyc.State == CycleState.Running && guard++ < 100)
                {
                    var step = cyc.Tick(st);
                    if (step.Kind == StepKind.Cast || step.Kind == StepKind.Equip) cyc.ReportCastResult(true);
                    else if (step.Kind == StepKind.Done) break;
                }
                Eq(CycleState.Done, cyc.State);
                Eq(2, cyc.SpellsCast);
            });

            Console.WriteLine("\nModernProfile targeting + includes (recovered editor semantics):");
            Check("round-trips per-entry target detail and includes", () =>
            {
                var p = new ModernProfile { Name = "T" };
                p.Includes.Add("base");
                p.Buffs.Add(new ModernBuffEntry { Category = 1, Target = SpellTarget.Other, TargetName = "Bob", DisplayName = "Strength Other" });
                p.Buffs.Add(new ModernBuffEntry { Category = 2, Target = SpellTarget.Other, TargetGuid = 0x50001234, DisplayName = "Focus Other" });
                p.Buffs.Add(new ModernBuffEntry { Category = 3, Target = SpellTarget.Item, ItemName = "Sword", DisplayName = "Blood Drinker" });
                p.Buffs.Add(new ModernBuffEntry { Category = 4, Target = SpellTarget.Item, CoverMask = 0x7F21, DisplayName = "Impenetrability" });
                p.Buffs.Add(new ModernBuffEntry { Category = 5, Target = SpellTarget.Item, ItemGuid = unchecked((int)0x80001111), DisplayName = "Defender" });
                var p2 = ModernProfile.Parse(p.ToXml());
                Eq(1, p2.Includes.Count); Eq("base", p2.Includes[0]);
                Eq("Bob", p2.Buffs[0].TargetName);
                Eq(0x50001234, p2.Buffs[1].TargetGuid);
                Eq("Sword", p2.Buffs[2].ItemName);
                Eq(0x7F21, p2.Buffs[3].CoverMask);
                Eq(unchecked((int)0x80001111), p2.Buffs[4].ItemGuid);
            });
            Check("ResolveIncludes flattens, dedups, and guards recursion", () =>
            {
                var a = new ModernProfile { Name = "a" };
                a.EquipItems.Add("Focusing Stone");
                a.Buffs.Add(new ModernBuffEntry { Category = 1, DisplayName = "S" });
                a.Includes.Add("b");
                var b = new ModernProfile { Name = "b" };
                b.EquipItems.Add("Focusing Stone");                       // duplicate equip -> once
                b.Buffs.Add(new ModernBuffEntry { Category = 1, DisplayName = "S" });  // duplicate buff -> once
                b.Buffs.Add(new ModernBuffEntry { Category = 2, DisplayName = "E" });
                b.Includes.Add("a");                                      // cycle -> guarded
                var lookup = new Dictionary<string, ModernProfile> { { "a", a }, { "b", b } };
                var warnings = new List<string>();
                var m = ModernProfile.ResolveIncludes(a, n => lookup.TryGetValue(n, out var x) ? x : null, warnings);
                Eq(1, m.EquipItems.Count);
                Eq(2, m.Buffs.Count);
                True(warnings.Any(w => w.Contains("recurse")), "cycle warning expected");
            });
            Check(".xml suffix is accepted on include names", () =>
            {
                var a = new ModernProfile { Name = "a" };
                a.Includes.Add("b.xml");
                var b = new ModernProfile { Name = "b" };
                b.Buffs.Add(new ModernBuffEntry { Category = 9, DisplayName = "X" });
                var m = ModernProfile.ResolveIncludes(a,
                    n => n == "b" || n == "b.xml" ? b : null, null);
                Eq(1, m.Buffs.Count);
            });
            Check("planner: Other by name resolves through FindWorldByName", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                int grp = c.ById(1).Category;                             // Strength Other I -> its group
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = grp, Target = SpellTarget.Other, TargetName = "Bob" });
                var st = AllKnown(); st.WorldByName["Bob"] = 0x51230000;
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                var cast = plan.Actions.Single(a2 => a2.Kind == CastKind.CastTarget);
                Eq(0x51230000, cast.TargetGuid);
                Eq(SpellTarget.Other, c.ById(cast.SpellId).Target);
            });
            Check("planner: Other by missing name warns and skips", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1).Category, Target = SpellTarget.Other, TargetName = "Ghost" });
                var st = AllKnown();
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                Eq(0, plan.Actions.Count);
                Eq(1, plan.Warnings.Count);
            });
            Check("planner: Other by explicit GUID casts on it", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1).Category, Target = SpellTarget.Other, TargetGuid = 0x50009999 });
                var st = AllKnown(); st.SelectedTargetId = 0;             // no selection needed
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                Eq(0x50009999, plan.Actions.Single(a2 => a2.Kind == CastKind.CastTarget).TargetGuid);
            });
            Check("planner: Item cover mask filters worn items (0x7F21 vs a shield)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var item = c.All.First(s2 => s2.Target == SpellTarget.Item);
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = item.Category, Target = SpellTarget.Item, CoverMask = 0x7F21 });
                var st = AllKnown();
                st.Worn.Add(new WornItem(unchecked((int)0x8000AAAAu), "Helm", unchecked((int)(uint)CoverageBits.Head)));
                st.Worn.Add(new WornItem(unchecked((int)0x8000BBBBu), "Shield", unchecked((int)(uint)CoverageBits.Shield)));
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                var items = plan.Actions.Where(a2 => a2.Kind == CastKind.CastItem).ToList();
                Eq(1, items.Count);
                Eq(unchecked((int)0x8000AAAAu), items[0].TargetGuid);                      // helm matches 0x7F21, shield doesn't
            });
            Check("planner: Item by name casts once on the named item", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var item = c.All.First(s2 => s2.Target == SpellTarget.Item);
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = item.Category, Target = SpellTarget.Item, ItemName = "Fine Sword" });
                var st = AllKnown(); st.ItemsByName["Fine Sword"] = unchecked((int)0x8000CCCCu);
                st.Worn.Add(new WornItem(unchecked((int)0x8000AAAAu), "Helm", unchecked((int)(uint)CoverageBits.Head)));
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                var items = plan.Actions.Where(a2 => a2.Kind == CastKind.CastItem).ToList();
                Eq(1, items.Count);
                Eq(unchecked((int)0x8000CCCCu), items[0].TargetGuid);
            });

            Console.WriteLine("\nModernProfileStore + EditorCatalog (the recovered editor's model):");
            Check("store: create, list, save, duplicate, delete(trash)", () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "nb3test_" + Guid.NewGuid().ToString("N"));
                try
                {
                    var store = new ModernProfileStore(dir);
                    True(store.Create("alpha") != null);
                    True(store.Create("alpha") == null, "duplicate create must fail");
                    True(store.Create("bad:name") == null, "invalid name must fail");
                    var p = store.Load("alpha");
                    p.Buffs.Add(new ModernBuffEntry { Category = 1, DisplayName = "S" });
                    store.Save(p);
                    True(store.Duplicate("alpha", "beta"));
                    Eq(1, store.Load("beta").Buffs.Count);
                    // config_* files are hidden from listings
                    File.WriteAllText(Path.Combine(dir, "config_00000001.xml"), "<x/>");
                    var names = store.List();
                    Eq(2, names.Count);
                    True(store.Delete("beta", permanent: false));
                    True(File.Exists(Path.Combine(dir, "_deleted", "beta.xml")), "trash copy expected");
                    Eq(1, store.List().Count);
                    True(ModernProfileStore.Canon("x.XML") == "x");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });
            Check("EditorCatalog resolves classic families to live categories + schools", () =>
            {
                var live = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var classic = Table();
                var unresolved = new List<string>();
                var cat = EditorCatalog.Build(classic, live, unresolved);
                True(cat.Count > 200, $"expected most of the 275 families to resolve, got {cat.Count}");
                var strength = cat.FirstOrDefault(f => f.DisplayName == "Strength Self");
                True(strength != null, "Strength Self family expected");
                Eq(live.ById(2).Category, strength.Category);             // Strength Self I
                True(strength.School.IndexOf("Creature", StringComparison.OrdinalIgnoreCase) >= 0);
                Eq(TargetType.Self, strength.ClassicTarget);
            });
            Check("EditorCatalog tab split matches the original's five tabs", () =>
            {
                var live = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var cat = EditorCatalog.Build(Table(), live, null);
                var creatureSelf = EditorCatalog.ForTab(cat, "Creature", TargetType.Self);
                var lifeSelf = EditorCatalog.ForTab(cat, "Life", TargetType.Self);
                var item = EditorCatalog.ForTab(cat, "", TargetType.Item);
                True(creatureSelf.Count > 0 && lifeSelf.Count > 0 && item.Count > 0);
                True(creatureSelf.All(f => f.ClassicTarget == TargetType.Self));
                True(item.All(f => f.ClassicTarget == TargetType.Item));
                // no family appears on both a Creature and a Life tab
                True(!creatureSelf.Any(f => lifeSelf.Contains(f)));
            });
            Check("era break: classic families resolve against the 2012 dump, NOT a renumbered live table", () =>
            {
                var classic = Table();
                // A "live" table that renumbered every id (the modern/ACE case the bug hit):
                // shift the 2012 dump's ids by a big constant so NONE match the classic table.
                var dump = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var renumbered = new FakeSpellTable();
                foreach (var s in dump.All)
                    renumbered.Add(new SpellInfo(s.Id + 900000, s.Name, s.Category, s.Level, s.School, s.Mana, s.Target));
                var live = EditorCatalog.Build(classic, renumbered, null);
                Eq(0, live.Count);                       // resolving classic ids against renumbered live -> empty (the symptom)
                var fromDump = EditorCatalog.Build(classic, dump, null);
                True(fromDump.Count > 200, $"dump must resolve the families the live table can't ({fromDump.Count})");
            });
            Check("EditorCatalog.BaseName strips trailing roman levels only", () =>
            {
                Eq("Strength Self", EditorCatalog.BaseName("Strength Self I"));
                Eq("Strength Self", EditorCatalog.BaseName("Strength Self VI"));
                Eq("Impenetrability", EditorCatalog.BaseName("Impenetrability"));
            });

            Console.WriteLine("\nNB3Coverage (game masks -> NB3 scheme; docs 16 §3.1–3.2 + COVER_MASK_RECOVERY):");
            Check("held-slot bits pass through by value (they ARE the game's LOCATIONS bits)", () =>
            {
                Eq((uint)CoverageBits.MeleeWeapon, NB3Coverage.FromLocations(1048576));
                Eq((uint)CoverageBits.Shield, NB3Coverage.FromLocations(2097152));
                Eq((uint)CoverageBits.MissileWeapon, NB3Coverage.FromLocations(4194304));
                Eq((uint)CoverageBits.Caster, NB3Coverage.FromLocations(16777216));
            });
            Check("armor-layer coverage translates (game bits differ from NB3's)", () =>
            {
                // a helm: game CLOTHING_PRIORITY Head=16384 -> NB3 Head=0x01
                Eq((uint)CoverageBits.Head, NB3Coverage.FromClothingPriority(16384));
                // gauntlets / boots
                Eq((uint)CoverageBits.Hands, NB3Coverage.FromClothingPriority(32768));
                Eq((uint)CoverageBits.Feet, NB3Coverage.FromClothingPriority(65536));
                // the workbook's real breastplate mask: chest+abdomen+upper arms = 7168
                Eq((uint)(CoverageBits.ChestArmor | CoverageBits.Girth | CoverageBits.UpperArms),
                   NB3Coverage.FromClothingPriority(7168));
            });
            Check("under-layer coverage translates", () =>
            {
                // a shirt covering chest+arms (under): 8|32|64 -> ChestUnder|UAUnder|LAUnder
                Eq((uint)(CoverageBits.ChestUnder | CoverageBits.UpperArmsUnder | CoverageBits.LowerArmsUnder),
                   NB3Coverage.FromClothingPriority(8 | 32 | 64));
                // pants: upper+lower legs under
                Eq((uint)(CoverageBits.UpperLegsUnder | CoverageBits.LowerLegsUnder),
                   NB3Coverage.FromClothingPriority(2 | 4));
            });
            Check("a full suit's translated coverage intersects the sample's 0x7F21 spellgroup", () =>
            {
                // full body armor incl. head/hands/feet: every armor-layer game bit set
                int game = 256 | 512 | 1024 | 2048 | 4096 | 8192 | 16384 | 32768 | 65536;
                uint nb3 = NB3Coverage.FromClothingPriority(game);
                True((nb3 & 0x7F21u) != 0, "suit must match the all-armor group");
                // and pure under-clothing must NOT match the armor-only 0x7F21 group
                uint shirt = NB3Coverage.FromClothingPriority(8 | 32 | 64);
                True((shirt & 0x7F21u) == 0, "underwear must not match the armor group");
            });
            Check("cloak bit (131072) has no NB3 equivalent and is dropped", () =>
                Eq(0u, NB3Coverage.FromClothingPriority(131072)));
            Check("FromGame combines clothing coverage with the wielded slot", () =>
            {
                // a wand in the caster slot with no clothing coverage
                Eq((uint)CoverageBits.Caster, NB3Coverage.FromGame(0, 16777216));
                // a helm being worn (clothing mask + its armor equip slot has no held bits)
                Eq((uint)CoverageBits.Head, NB3Coverage.FromGame(16384, 1));
            });

            Console.WriteLine("\nEquip skip (already-wielded items must never be re-used = unequipped):");
            Check("modern planner skips an already-wielded equip item", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.EquipItems.Add("Focusing Stone");
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                var st = AllKnown(); st.ItemsByName["Focusing Stone"] = 0xAAAA;
                st.WieldedGuids.Add(0xAAAA);                      // already in hand
                var plan = new ModernBuffPlanner(c).Plan(prof, st);
                Eq(0, plan.Actions.Count(a => a.Kind == CastKind.Equip));
                Eq(0, plan.Warnings.Count);                       // silent success, not a warning
                st.WieldedGuids.Clear();                          // in the pack -> equip planned
                Eq(1, new ModernBuffPlanner(c).Plan(prof, st).Actions.Count(a => a.Kind == CastKind.Equip));
            });
            Check("classic engine skips an already-wielded equip item", () =>
            {
                var t = Table(); var s = AllKnown(); s.ItemsByName["Focusing Stone"] = 0xAAAA;
                s.WieldedGuids.Add(0xAAAA);
                var plan = new BuffEngine(t).BuildPlan(Sample(), s);
                Eq(0, plan.Actions.Count(a => a.Kind == CastKind.Equip));
            });

            Console.WriteLine("\nAuto-wield a caster at Start (owner: wield a wand if the casting hand is empty):");
            // A profile with one real self-buff, so a plan has "something to cast" (1332 is a self
            // category used above; AllKnown knows every spell, so it resolves to a CastSelf).
            System.Func<SpellCatalog, ModernProfile> oneBuff = cat =>
            {
                var p = new ModernProfile { Name = "aw" };
                p.Buffs.Add(new ModernBuffEntry { Category = cat.ById(1332).Category, Target = SpellTarget.Self });
                return p;
            };
            Check("empty hand + a wand in the pack -> plan wields it FIRST, ahead of the casts", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var st = AllKnown();                          // WieldedCaster = 0 (empty hand)
                st.PackCasters.Add(0xCA57E1);                 // one caster carried
                var plan = new ModernBuffPlanner(c).Plan(oneBuff(c), st, null, null, autoWieldCaster: true);
                Eq(CastKind.Equip, plan.Actions[0].Kind);
                Eq(0xCA57E1, plan.Actions[0].TargetGuid);
                True(plan.Actions.Skip(1).Any(a => a.Kind == CastKind.CastSelf), "casts follow the wield");
            });
            Check("already holding a caster -> no auto-wield", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var st = AllKnown(); st.WieldedCaster = 0xBEEF; st.PackCasters.Add(0xCA57E1);
                var plan = new ModernBuffPlanner(c).Plan(oneBuff(c), st, null, null, autoWieldCaster: true);
                Eq(0, plan.Actions.Count(a => a.Kind == CastKind.Equip));
            });
            Check("empty hand + NO caster carried -> a warning, no equip", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var st = AllKnown();                          // hand empty, nothing to wield
                var plan = new ModernBuffPlanner(c).Plan(oneBuff(c), st, null, null, autoWieldCaster: true);
                Eq(0, plan.Actions.Count(a => a.Kind == CastKind.Equip));
                True(plan.Warnings.Any(w => (w.Message ?? "").IndexOf("caster", StringComparison.OrdinalIgnoreCase) >= 0),
                     "warns that no caster is available");
            });
            Check("nothing to cast -> no pointless auto-wield", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var st = AllKnown(); st.PackCasters.Add(0xCA57E1);
                var empty = new ModernProfile { Name = "empty" };   // no buffs -> no casts
                var plan = new ModernBuffPlanner(c).Plan(empty, st, null, null, autoWieldCaster: true);
                Eq(0, plan.Actions.Count);
            });
            Check("auto-wield is OFF by default (unchanged planner API)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var st = AllKnown(); st.PackCasters.Add(0xCA57E1);
                var plan = new ModernBuffPlanner(c).Plan(oneBuff(c), st);   // no flag
                Eq(0, plan.Actions.Count(a => a.Kind == CastKind.Equip));
            });
            Check("no duplicate wield when the profile already equips that caster", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var p = oneBuff(c); p.EquipItems.Add("My Wand");
                var st = AllKnown();
                st.ItemsByName["My Wand"] = 0xCA57E1;         // the profile's equip resolves to the caster
                st.PackCasters.Add(0xCA57E1);                 // and it's the auto-pick too
                var plan = new ModernBuffPlanner(c).Plan(p, st, null, null, autoWieldCaster: true);
                Eq(1, plan.Actions.Count(a => a.Kind == CastKind.Equip && a.TargetGuid == 0xCA57E1));
            });

            Console.WriteLine("\nModernProfileFactory (the /nbnew starter profile):");
            Check("default self set resolves fully against the real 2012 catalog", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var p = ModernProfileFactory.CreateDefaultSelf(c, out var unresolved);
                Eq(0, unresolved.Count);
                Eq(17, p.Buffs.Count);                            // 6 attributes + 4 life + 7 prots
                True(p.Buffs.All(b => b.Target == SpellTarget.Self));
                True(p.Buffs.Select(b => b.Category).Distinct().Count() == p.Buffs.Count,
                     "one entry per stacking category");
                // categories are real: Strength Self I (id 2) lands in its live group
                True(p.Buffs.Any(b => b.Category == c.ById(2).Category));
            });
            Check("factory profile plans and cycles end-to-end", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var p = ModernProfileFactory.CreateDefaultSelf(c, out _);
                var st = AllKnown();
                var plan = new ModernBuffPlanner(c).Plan(p, st);
                True(plan.Actions.Count(a => a.Kind == CastKind.CastSelf) >= 15,
                     $"expected >=15 self casts, got {plan.Actions.Count}");
                var cyc = new BuffCycle(plan); cyc.Start();
                int guard = 0;
                while (cyc.State == CycleState.Running && guard++ < 200)
                {
                    var step = cyc.Tick(st);
                    if (step.Kind == StepKind.Cast || step.Kind == StepKind.Equip) cyc.ReportCastResult(true);
                    else if (step.Kind == StepKind.Done) break;
                }
                Eq(CycleState.Done, cyc.State);
            });
            Check("round-trips through XML (what /nbnew writes, /nbuff reads)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var p = ModernProfileFactory.CreateDefaultSelf(c, out _);
                p.Name = "default";
                var p2 = ModernProfile.Parse(p.ToXml());
                Eq(p.Buffs.Count, p2.Buffs.Count);
                Eq(p.Buffs[0].Category, p2.Buffs[0].Category);
            });

            Console.WriteLine("\nCastChat (doc-18 §3 outcome catalog, UB-verbatim patterns):");
            Check("'You cast …' is Success (plain, recovery and self-heal variants)", () =>
            {
                Eq(CastOutcome.Success, CastChat.ClassifyOutcome("You cast Strength Self VI on yourself"));
                Eq(CastOutcome.Success, CastChat.ClassifyOutcome(
                    "You cast Stamina to Mana Self VI on yourself and lose 113 points of stamina and also gain 96 points of mana"));
                Eq(CastOutcome.Success, CastChat.ClassifyOutcome(
                    "You cast Heal Self VI and restore 84 points of your Health."));
            });
            Check("fizzle is the exact UB pattern", () =>
            {
                Eq(CastOutcome.Fizzle, CastChat.ClassifyOutcome("Your spell fizzled.\n")); // Decal lines carry \n
                Eq(CastOutcome.None, CastChat.ClassifyOutcome("His spell fizzled."));
            });
            Check("failed-to-affect and target-resists classify", () =>
            {
                Eq(CastOutcome.FailedToAffect,
                   CastChat.ClassifyOutcome("You failed to affect Drudge Prowler with Imperil Other VI"));
                Eq(CastOutcome.Resisted, CastChat.ClassifyOutcome("Drudge Prowler resists your spell"));
            });
            Check("fail-to-affect accepts the ACE present-tense variant too", () =>
                Eq(CastOutcome.FailedToAffect,
                   CastChat.ClassifyOutcome("You fail to affect Drudge Prowler with Imperil Other VI")));
            Check("YOU resisting THEIR spell resolves nothing", () =>
                Eq(CastOutcome.None, CastChat.ClassifyOutcome("You resist the spell cast by Drudge Prowler")));
            Check("components-consumed and expiry are informational, not outcomes", () =>
            {
                var burned = "The spell consumed the following components: Lead Scarab, Prismatic Taper";
                Eq(CastOutcome.None, CastChat.ClassifyOutcome(burned));
                True(CastChat.IsComponentsConsumed(burned));
                var expired = "Strength Self VI has expired.";
                Eq(CastOutcome.None, CastChat.ClassifyOutcome(expired));
                True(CastChat.IsEnchantmentExpired(expired));
            });
            Check("missing-components line skips (confirm-by-test string)", () =>
                Eq(CastOutcome.MissingComponents,
                   CastChat.ClassifyOutcome("You don't have all the components required to cast this spell.")));
            Check("chatter classifies as None", () =>
            {
                Eq(CastOutcome.None, CastChat.ClassifyOutcome("Hail, Nerfus!"));
                Eq(CastOutcome.None, CastChat.ClassifyOutcome(""));
                Eq(CastOutcome.None, CastChat.ClassifyOutcome(null));
            });

            Console.WriteLine("\nCastResultMonitor (one cast in flight; chat + enchant-add + watchdog):");
            Check("outcome line resolves the pending cast and clears in-flight", () =>
            {
                var m = new CastResultMonitor();
                m.BeginCast(1332, nowTick: 1000);
                True(m.CastInFlight);
                Eq(CastOutcome.Fizzle, m.ObserveChat("Your spell fizzled."));
                True(!m.CastInFlight);
            });
            Check("chat while idle resolves nothing", () =>
            {
                var m = new CastResultMonitor();
                Eq(CastOutcome.None, m.ObserveChat("You cast Strength Self VI on yourself"));
            });
            Check("non-terminal lines don't resolve the pending cast", () =>
            {
                var m = new CastResultMonitor();
                m.BeginCast(1332, 0);
                Eq(CastOutcome.None, m.ObserveChat("The spell consumed the following components: Lead Scarab"));
                Eq(CastOutcome.None, m.ObserveChat("You resist the spell cast by Drudge Prowler"));
                True(m.CastInFlight);
                Eq(CastOutcome.Success, m.ObserveChat("You cast Strength Self VI on yourself"));
            });
            Check("enchantment ADD resolves only the matching spell id", () =>
            {
                var m = new CastResultMonitor();
                m.BeginCast(1332, 0);
                True(!m.ObserveEnchantmentAdded(9999));
                True(m.CastInFlight);
                True(m.ObserveEnchantmentAdded(1332));
                True(!m.CastInFlight);
            });
            Check("watchdog times out once, after TimeoutMs", () =>
            {
                var m = new CastResultMonitor(); // default 10000 ms
                m.BeginCast(1332, 1000);
                True(!m.CheckTimeout(5000));
                True(!m.CheckTimeout(11000));   // exactly 10000 elapsed — not yet past
                True(m.CheckTimeout(11001));
                True(!m.CastInFlight);
                True(!m.CheckTimeout(999999));  // resolved; never fires twice
            });
            Check("watchdog arithmetic survives TickCount wrap-around", () =>
            {
                var m = new CastResultMonitor();
                m.BeginCast(1332, int.MaxValue - 100);
                True(!m.CheckTimeout(int.MinValue + 100)); // ~201 ms elapsed across the wrap
                True(m.CheckTimeout(int.MinValue + 20000)); // ~20.1 s elapsed
            });
            Check("partial feedback shortens the watchdog (the original's 'probably a dud')", () =>
            {
                var m = new CastResultMonitor(); // TimeoutMs 10000, PartialTimeoutMs 5000
                m.BeginCast(1332, 1000);
                m.NotePartialFeedback();        // saw components-consumed etc., no terminal line
                True(!m.CheckTimeout(6000));    // 5000 elapsed — not yet past the partial budget
                True(m.CheckTimeout(6001));     // past 5000 -> times out early, not at 10000
                True(!m.CastInFlight);
            });
            Check("partial feedback resets per cast (a fresh cast gets the full budget)", () =>
            {
                var m = new CastResultMonitor();
                m.BeginCast(1, 0); m.NotePartialFeedback(); m.Reset();
                m.BeginCast(2, 0);              // new cast, no partial yet
                True(!m.CheckTimeout(5001));    // still within the FULL 10000, so no early timeout
                True(m.CheckTimeout(10001));
            });

            Console.WriteLine("\nBuffCycle outcome policy (doc-18 §3: advance / retry / count+advance / skip):");
            Check("resist counts and advances (never recast into a resister)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1161).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st)); cyc.Start();
                var a1 = cyc.Tick(st).Action;
                cyc.ReportCastOutcome(CastOutcome.Resisted);
                Eq(1, cyc.Resists);
                Eq(0, cyc.SpellsCast);
                True(cyc.Tick(st).Action.SpellId != a1.SpellId, "must advance past the resisted spell");
            });
            Check("missing components skips and advances", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st)); cyc.Start();
                cyc.Tick(st);
                cyc.ReportCastOutcome(CastOutcome.MissingComponents);
                Eq(1, cyc.Skipped);
                Eq(CycleState.Done, cyc.State);
            });
            Check("timeout retries the same spell and counts", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st)); cyc.Start();
                var a1 = cyc.Tick(st).Action;
                cyc.ReportCastOutcome(CastOutcome.Timeout);
                Eq(1, cyc.Timeouts);
                Eq(a1.SpellId, cyc.Tick(st).Action.SpellId);
            });
            Check("attempt cap skips a hopeless spell instead of wedging", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1161).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st),
                    new CycleOptions { MaxAttemptsPerAction = 3 });
                cyc.Start();
                var a1 = cyc.Tick(st).Action;
                for (int i = 0; i < 3; i++) { cyc.Tick(st); cyc.ReportCastOutcome(CastOutcome.Fizzle); }
                Eq(3, cyc.Fizzles);
                Eq(1, cyc.Skipped);
                True(cyc.Tick(st).Action.SpellId != a1.SpellId, "must have moved on");
            });
            Check("Busy counts episodes, not 300ms polls", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1161).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st)); cyc.Start();
                st.IsCasting = true;                      // one cast's whole windup...
                for (int i = 0; i < 6; i++) Eq(StepKind.Busy, cyc.Tick(st).Kind);
                Eq(1, cyc.BusyHits);                      // ...is ONE busy episode
                st.IsCasting = false;
                Eq(StepKind.Cast, cyc.Tick(st).Kind);     // run broken
                st.IsCasting = true;
                cyc.Tick(st);
                Eq(2, cyc.BusyHits);                      // a second episode counts again
            });
            Check("mana gate clamps to the pool (never an unreachable requirement)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                var st = AllKnown();                       // MaxMana = 1000 in the fake
                st.DefaultManaCost = 5000;                 // costs more than the whole pool
                var opts = new CycleOptions { ManaRegenMode = ManaRegenMode.TradeManaElixir };

                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st), opts); cyc.Start();
                st.CurMana = 500;                          // below the clamped gate -> regen
                var step = cyc.Tick(st);
                Eq(StepKind.RegenMana, step.Kind);
                Eq(1000, step.RequiredMana);               // clamped to MaxMana, not 5000
                st.CurMana = 1000;                         // full pool -> must attempt the cast
                Eq(StepKind.Cast, cyc.Tick(st).Kind);
            });
            Check("cap 0 = retry forever (the original's behaviour)", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st),
                    new CycleOptions { MaxAttemptsPerAction = 0 });
                cyc.Start();
                var a1 = cyc.Tick(st).Action;
                for (int i = 0; i < 50; i++) { cyc.Tick(st); cyc.ReportCastOutcome(CastOutcome.Fizzle); }
                Eq(50, cyc.Fizzles);
                Eq(0, cyc.Skipped);
                Eq(a1.SpellId, cyc.Tick(st).Action.SpellId);
            });

            Console.WriteLine("\nRegenItems (consumable lookup for the regen modes; doc-19 ranking):");
            Check("mana elixir matches, preferring the Trade variant (70 pts, doc 19 §5)", () =>
            {
                var s = AllKnown();
                s.ItemsByName["Mana Elixir"] = 0xC000;
                s.ItemsByName["Trade Mana Elixir"] = 0xC001;
                Eq(0xC001, RegenItems.FindManaElixir(s));
                s.ItemsByName.Remove("Trade Mana Elixir");
                Eq(0xC000, RegenItems.FindManaElixir(s));   // plain variant as fallback
                Eq(0, RegenItems.FindStaminaElixir(s));
            });
            Check("healing kit prefers Plentiful > Treated > Peerless (doc 19 §4)", () =>
            {
                var s = AllKnown();
                s.ItemsByName["Peerless Healing Kit"] = 0xC002;
                s.ItemsByName["Treated Healing Kit"] = 0xC003;
                s.ItemsByName["Plentiful Healing Kit"] = 0xC004;
                var all = HealingKitTiers.Plentiful | HealingKitTiers.Treated | HealingKitTiers.Peerless;
                Eq(0xC004, RegenItems.FindHealingKit(s, all));                              // +100 boost wins
                Eq(0xC003, RegenItems.FindHealingKit(s, HealingKitTiers.Treated | HealingKitTiers.Peerless)); // Treated dominates Peerless
                Eq(0xC002, RegenItems.FindHealingKit(s, HealingKitTiers.Peerless));        // only enabled tier
                s.ItemsByName.Remove("Plentiful Healing Kit");
                Eq(0xC003, RegenItems.FindHealingKit(s, all));                              // enabled but not carried -> next
                Eq(0, RegenItems.FindHealingKit(s, HealingKitTiers.None));
            });

            Console.WriteLine("\nCasting loop end-to-end (cycle + monitor driven by chat lines):");
            Check("fizzle-retry, success-advance, resist-advance reach Done with true counters", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1161).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st)); cyc.Start();
                var mon = new CastResultMonitor();

                // Scripted chat truth per attempt: first spell fizzles once then lands,
                // second is resisted — exactly the §3 sequential-runner recipe.
                var script = new Queue<string>(new[]
                {
                    "Your spell fizzled.",
                    "You cast a buff on yourself",
                    "Something resists your spell",
                });
                int tick = 0, guard = 0;
                while (cyc.State == CycleState.Running && guard++ < 100)
                {
                    if (mon.CheckTimeout(tick)) { cyc.ReportCastOutcome(CastOutcome.Timeout); st.IsCasting = false; }
                    var step = cyc.Tick(st);
                    if (step.Kind == StepKind.Cast)
                    {
                        mon.BeginCast(step.Action.SpellId, tick);
                        st.IsCasting = true;
                        // ...windup... then the outcome line arrives:
                        var o = mon.ObserveChat(script.Count > 0 ? script.Dequeue() : "You cast a buff on yourself");
                        True(o != CastOutcome.None, "scripted line must resolve the cast");
                        cyc.ReportCastOutcome(o);
                        st.IsCasting = false;
                    }
                    else if (step.Kind == StepKind.Done) break;
                    tick += 300;
                }
                Eq(CycleState.Done, cyc.State);
                Eq(1, cyc.SpellsCast);   // first spell (after one fizzle)
                Eq(1, cyc.Fizzles);
                Eq(1, cyc.Resists);      // second spell resisted -> counted, advanced
                Eq(0, cyc.SpellsLeft);
            });
            Check("silence resolves through the watchdog, then the retry lands", () =>
            {
                var c = SpellCatalog.Load(Fx("spellcat-2012.tsv"));
                var prof = new ModernProfile { Name = "p" };
                prof.Buffs.Add(new ModernBuffEntry { Category = c.ById(1332).Category, Target = SpellTarget.Self });
                var st = AllKnown();
                var cyc = new BuffCycle(new ModernBuffPlanner(c).Plan(prof, st)); cyc.Start();
                var mon = new CastResultMonitor();

                var step = cyc.Tick(st);
                mon.BeginCast(step.Action.SpellId, 0); st.IsCasting = true;
                True(!mon.CheckTimeout(9000));                     // still waiting
                True(mon.CheckTimeout(10001));                     // watchdog fires
                cyc.ReportCastOutcome(CastOutcome.Timeout); st.IsCasting = false;
                Eq(1, cyc.Timeouts);

                step = cyc.Tick(st);                               // same spell, retried
                mon.BeginCast(step.Action.SpellId, 10300);
                // this time the enchantment-add delta resolves it (chat line eaten)
                True(mon.ObserveEnchantmentAdded(step.Action.SpellId));
                cyc.ReportCastOutcome(CastOutcome.Success); st.IsCasting = false;
                Eq(CycleState.Done, cyc.State);
                Eq(1, cyc.SpellsCast);
            });

            Console.WriteLine($"\n{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        static Fake AllKnown() => new Fake();

        static EditorFamily Fam(string name, int cat) =>
            new EditorFamily { DisplayName = name, Category = cat, School = "", LiveTarget = SpellTarget.Self, ClassicTarget = TargetType.Self };

        /// <summary>A minimal family catalog for the generator tests — every family the curated
        /// set references, each with a distinct category. Blood Drinker = 154 (its real stacking
        /// group, shared with Spirit Drinker, which the caster path resolves through it).</summary>
        static List<EditorFamily> GenCatalog() => new List<EditorFamily>
        {
            Fam("Focus Self",1), Fam("Willpower Self",2), Fam("Creature Magic Self",3),
            Fam("Mana Conversion Self",4), Fam("Life Magic Mastery Self",5),
            Fam("Strength Self",6), Fam("Endurance Self",7), Fam("Coordination Self",8), Fam("Quickness Self",9),
            Fam("Item Magic Self",10), Fam("War Magic Mastery Self",11),
            Fam("Invulnerability Self",12), Fam("Impregnability Self",13), Fam("Magic Resistance Self",14),
            Fam("Sword Mastery Self",15), Fam("Bow Mastery Self",16), Fam("Healing Mastery Self",67),
            Fam("Armor Self",20), Fam("Regeneration Self",21), Fam("Rejuvenation Self",22),
            Fam("Mana Renewal Self",23), Fam("Revitalize Self",24),
            Fam("Fire Protection Self",30), Fam("Cold Protection Self",31), Fam("Acid Protection Self",32),
            Fam("Lightning Protection Self",33), Fam("Blade Protection Self",34),
            Fam("Piercing Protection Self",35), Fam("Bludgeon Protection Self",36),
            Fam("Flame Bane",40), Fam("Frost Bane",41), Fam("Acid Bane",42), Fam("Lightning Bane",43),
            Fam("Blade Bane",44), Fam("Piercing Bane",45), Fam("Bludgeon Bane",46), Fam("Impenetrability",47),
            Fam("Blood Drinker",154), Fam("Heart Seeker",50), Fam("Defender",51),
            Fam("Swift Killer",52), Fam("Hermetic Link",53),
        };

        /// <summary>In-memory live spell table for the modern selector tests.</summary>
        sealed class FakeSpellTable : ILiveSpellTable
        {
            private readonly Dictionary<int, SpellInfo> _byId = new Dictionary<int, SpellInfo>();
            public void Add(int id, string name, int cat, int level) => _byId[id] = new SpellInfo(id, name, cat, level);
            public void Add(SpellInfo s) => _byId[s.Id] = s;
            public SpellInfo ById(int id) => _byId.TryGetValue(id, out var s) ? s : null;
            public IReadOnlyCollection<SpellInfo> All => _byId.Values;
        }

        sealed class Fake : IGameState
        {
            public int SelfId { get; set; } = 0x50000001;
            public int SelectedTargetId { get; set; } = 0x50000099;
            public Dictionary<string, int> ItemsByName { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<WornItem> Worn { get; } = new List<WornItem>();
            /// <summary>If set, only these ids are "known"; otherwise everything is known.</summary>
            public HashSet<int> KnowOnly;

            public bool SpellKnown(int id) => KnowOnly == null || KnowOnly.Contains(id);
            public int FindItemByName(string n) => ItemsByName.TryGetValue(n ?? "", out var g) ? g : 0;
            public int FindItemBySubstring(string frag)
            {
                foreach (var kv in ItemsByName)
                    if (kv.Key.IndexOf(frag ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                        return kv.Value;
                return 0;
            }
            public IEnumerable<WornItem> WornItems => Worn;
            /// <summary>World objects (players etc.) findable by exact name (FindWorldByName).</summary>
            public Dictionary<string, int> WorldByName { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int FindWorldByName(string n) => WorldByName.TryGetValue(n ?? "", out var g) ? g : 0;
            public List<int> ActiveEnchants { get; } = new List<int>();
            public IEnumerable<int> ActiveEnchantmentSpellIds => ActiveEnchants;
            /// <summary>Per-enchant remaining seconds (default int.MaxValue = permanent).</summary>
            public Dictionary<int, int> EnchantSeconds { get; } = new Dictionary<int, int>();
            public IEnumerable<NB3.Core.Modern.ActiveEnchant> ActiveEnchantments =>
                ActiveEnchants.Select(id => new NB3.Core.Modern.ActiveEnchant(
                    id, EnchantSeconds.TryGetValue(id, out var s) ? s : int.MaxValue)).ToList();
            /// <summary>Guids currently wielded (for the equip-skip check).</summary>
            public HashSet<int> WieldedGuids { get; } = new HashSet<int>();
            public bool IsWielded(int guid) => WieldedGuids.Contains(guid);

            /// <summary>Caster model: the guid in the casting hand (0 = empty), and the unwielded
            /// casters carried in the pack (FindWieldableCaster returns the first). Kept separate so a
            /// test can model "hand empty but a wand in the pack" vs "already holding one".</summary>
            public int WieldedCaster;
            public List<int> PackCasters { get; } = new List<int>();
            public int WieldedCasterId => WieldedCaster;
            public int FindWieldableCaster() => PackCasters.Count > 0 ? PackCasters[0] : 0;

            // cycle seam
            public bool IsCasting { get; set; } = false;
            public bool InMagicCombatMode { get; set; } = true;
            public Dictionary<int, int> ManaCosts { get; } = new Dictionary<int, int>();
            public int DefaultManaCost = 50;
            public int SpellManaCost(int id) => ManaCosts.TryGetValue(id, out var c) ? c : DefaultManaCost;
            /// <summary>Effective magic skill per school ("Creature"/"Life"/"Item"/...); 0 = unknown.</summary>
            public Dictionary<string, int> SkillBySchool { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int EffectiveMagicSkill(string school) =>
                SkillBySchool.TryGetValue(school ?? "", out var v) ? v : 0;

            public int CurMana = 1000, CurHealth = 200, CurStam = 300;
            public int CurrentMana => CurMana; public int MaxMana => 1000;
            public int CurrentStamina => CurStam; public int MaxStamina => 300;
            public int CurrentHealth => CurHealth; public int MaxHealth => 200;

            /// <summary>Skill training ranks by CharFilterSkillType id (0-3); default 0 = Unusable.</summary>
            public Dictionary<int, int> Training { get; } = new Dictionary<int, int>();
            public int SkillTrainingLevel(int skillType) =>
                Training.TryGetValue(skillType, out var v) ? v : 0;

            // Auto-scan model: drinkables (per vital) and healing kits, each with a rank score.
            // FindBest* return the highest-scoring guid, mirroring the adapter's property scan.
            public List<(Vital vital, int guid, int boost)> Potions { get; } = new List<(Vital, int, int)>();
            public List<(int guid, double score)> Kits { get; } = new List<(int, double)>();
            public int FindBestPotion(Vital vital)
            {
                int best = 0, bestBoost = 0;
                foreach (var p in Potions) if (p.vital == vital && p.boost > bestBoost) { bestBoost = p.boost; best = p.guid; }
                return best;
            }
            public int FindBestHealingKit()
            {
                int best = 0; double bestScore = -1;
                foreach (var k in Kits) if (k.score > bestScore) { bestScore = k.score; best = k.guid; }
                return best;
            }
        }
    }
}
