using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: AssemblyInformationalVersion("1.0.2")]

namespace DroneCoverage
{
    [BepInPlugin(Guid, "BUG_DroneCoverage", "1.0.2")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "bug.dsp.dronecoverage";
        static GameData appliedFor;
        static int frame;
        ConfigEntry<bool> reset;
        DateTime cfgTime;

        void Awake()
        {
            Coverage.Log = Logger;
            reset = Config.Bind("Reset", "ResetResearch", false,
                "Set to true to reset the Drone Coverage upgrades of the running (or next loaded) game to stage 0, so they can be researched again. Spent cubes are not refunded. Turns itself back off.");
            cfgTime = CfgTime();
            new Harmony(Guid).PatchAll(typeof(Coverage));
        }

        DateTime CfgTime()
        {
            try { return File.GetLastWriteTimeUtc(Config.ConfigFilePath); } catch { return default; }
        }

        void Update()
        {
            if (!GameMain.isRunning || GameMain.data == null) return;
            // Every second also catches other mods (Multifunction's build area field) and newly built battle bases.
            if (!Coverage.Dirty && appliedFor == GameMain.data && ++frame % 60 != 0) return;
            var t = CfgTime();
            if (t != cfgTime)  // edited in the mod manager while the game runs
            {
                cfgTime = t;
                Config.Reload();
            }
            if (reset.Value && GameMain.history != null)
            {
                Coverage.ResetResearch(GameMain.history);
                reset.Value = false;  // saves the file
                cfgTime = CfgTime();
            }
            Coverage.Dirty = false;
            appliedFor = GameMain.data;
            Coverage.Apply();
        }
    }

    // Ten upgrade stages on the upgrade page; each widens the area the construction and repair drones of the
    // mecha and of battlefield analysis bases cover. Stage 10 covers the whole planet.
    // Whatever range is set when the mod first sees it (vanilla 80 m, or another mod's value) is the base it scales;
    // saves always get that base back, so removing the mod leaves the game's own ranges.
    public static class Coverage
    {
        public const int MaxStage = 10, Base = 9350;  // tech ids Base+1.. (DeliveryItemChain uses 9311-9336)
        public const float Step = 0.25f, PlanetWide = 10000f;  // stage 9 = x3.25; 10000 m is past any planet's diameter

        const string Name = "Drone Coverage";
        const string Desc = "Widens the area the construction and repair drones of the mecha and of battlefield analysis bases cover, by 25% of the original range per stage. Stage 10 lets them work across the whole planet.";
        const string Conclusion = "Construction and repair drones cover a wider area.";
        const string LastName = "Drone Coverage: Planet-wide";
        const string LastConclusion = "Construction and repair drones now cover the whole planet.";

        internal static ManualLogSource Log;
        public static TechProto[] Tiers;  // null until the tech table is built
        public static volatile bool Dirty;  // labs unlock on worker threads; ranges are applied on the main thread

        public static float Range(float baseRange, int stage)
        {
            if (baseRange <= 0f || stage <= 0) return baseRange;
            return Math.Max(baseRange, stage >= MaxStage ? PlanetWide : baseRange * (1f + Step * stage));
        }

        // Blue cubes first, one more cube kind every two stages, all six on the last stage (as DeliveryItemChain).
        public static int Cubes(int stage) => stage >= MaxStage ? 6 : stage == MaxStage - 1 ? 5 : (stage + 1) / 2;

        public static List<int[]> TierRanges()
        {
            var tiers = new List<int[]>();
            for (int s = 1; s <= MaxStage; s++)
            {
                if (tiers.Count > 0 && Cubes(tiers[tiers.Count - 1][0]) == Cubes(s)) tiers[tiers.Count - 1][1] = s;
                else tiers.Add(new[] { s, s });
            }
            return tiers;
        }

        public static int Done(Func<int, TechState?> state)
        {
            int done = 0;
            if (Tiers == null) return done;
            foreach (var p in Tiers)
            {
                var s = state(p.ID);
                if (s == null) continue;
                if (s.Value.unlocked) done = Math.Max(done, p.MaxLevel);
                else if (s.Value.curLevel > p.Level) done = Math.Max(done, s.Value.curLevel - 1);  // a fresh tier sits at its first level
            }
            return done;
        }

        public static int Stage
        {
            get
            {
                var states = GameMain.history?.techStates;
                return Done(id => states != null && states.TryGetValue(id, out var s) ? s : (TechState?)null);
            }
        }

        // Base (first seen or set by someone else) and the value we last wrote, per mecha / construction module.
        public class Scaled { public float Base = -1f, Set = -1f; }
        static readonly ConditionalWeakTable<object, Scaled> scaled = new ConditionalWeakTable<object, Scaled>();

        public static float Raise(object owner, float current, int stage)
        {
            var s = scaled.GetOrCreateValue(owner);
            if (s.Set < 0f || current != s.Set) s.Base = current;  // first sight, or changed outside this mod
            s.Set = Range(s.Base, stage);
            return s.Set;
        }

        // The base to write into a save, when the live value is ours.
        public static bool SavedBase(object owner, float current, out float value)
        {
            value = current;
            if (!scaled.TryGetValue(owner, out var s) || s.Set < 0f || current != s.Set || s.Base == current) return false;
            value = s.Base;
            return true;
        }

        // Back to unresearched, as the game creates tech states for a new game; the ranges follow on the next Apply.
        public static void ResetResearch(GameHistoryData history)
        {
            if (Tiers == null || Tiers.Length == 0) { Log?.LogWarning("Reset skipped: upgrade techs are not registered"); return; }
            int before = Stage;
            for (int i = history.techQueue.Length - 1; i >= 0; i--)
            {
                int id = history.techQueue[i];
                if (id > Base && id <= Base + Tiers.Length) history.RemoveTechInQueue(i);
            }
            foreach (var p in Tiers)
                history.techStates[p.ID] = new TechState(false, p.Level, p.MaxLevel, 0L, p.GetHashNeeded(p.Level), 0);
            Dirty = true;
            Log?.LogInfo("Drone Coverage research reset: stage " + before + " -> " + Stage);
        }

        public static void Apply()
        {
            int stage = Stage;
            var player = GameMain.mainPlayer;
            var mecha = player?.mecha;
            if (mecha != null)
            {
                float want = Raise(mecha, mecha.buildArea, stage);
                if (want != mecha.buildArea)
                {
                    mecha.buildArea = want;
                    if (mecha.constructionModule != null) mecha.constructionModule.prebuildDistribution = null;  // sized by range on first use
                    mecha.buildLastSearchAstroId = 0;  // the drones search again at once with the new range
                    mecha.repairLastSearchAstroId = 0;
                    mecha.autoReconstructLastSearchAstroId = 0;
                }
            }

            var data = GameMain.data;
            if (data?.factories == null) return;
            for (int f = 0; f < data.factoryCount; f++)
            {
                var cs = data.factories[f]?.constructionSystem;
                var pool = cs?.constructionModules;
                if (pool?.buffer == null) continue;
                for (int i = 1; i < pool.cursor; i++)
                {
                    var m = pool.buffer[i];
                    if (m == null || m.id != i || m.entityId <= 0) continue;
                    float want = Raise(m, m.baseBuildRange, stage);
                    if (want == m.baseBuildRange) continue;
                    m.baseBuildRange = want;
                    m.prebuildDistribution = null;
                }
            }
        }

        // ---- Saves keep the base range ----

        [HarmonyPrefix, HarmonyPatch(typeof(Mecha), nameof(Mecha.Export))]
        static void MechaExportPrefix(Mecha __instance, out float __state)
        {
            __state = -1f;
            if (!SavedBase(__instance, __instance.buildArea, out float b)) return;
            __state = __instance.buildArea;
            __instance.buildArea = b;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(Mecha), nameof(Mecha.Export))]
        static void MechaExportRestore(Mecha __instance, float __state)
        {
            if (__state >= 0f) __instance.buildArea = __state;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.Export))]
        static void ModuleExportPrefix(ConstructionModuleComponent __instance, out float __state)
        {
            __state = -1f;
            if (!SavedBase(__instance, __instance.baseBuildRange, out float b)) return;
            __state = __instance.baseBuildRange;
            __instance.baseBuildRange = b;
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.Export))]
        static void ModuleExportRestore(ConstructionModuleComponent __instance, float __state)
        {
            if (__state >= 0f) __instance.baseBuildRange = __state;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(GameHistoryData), nameof(GameHistoryData.NotifyTechUnlock))]
        static void Unlocked() => Dirty = true;

        // ---- Searches past the hash's 32 nearest buckets ----
        // The drones find targets through HashSystem, which hands out at most the 32 buckets nearest to the centre.
        // A wider range would be cut there, so inside the drone searches any range those 32 cannot cover walks every bucket.

        [ThreadStatic] static int searching;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.SearchBuildTargets))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.SearchRepairTargets))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.SearchAutoReconstructTargets))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.AutoReconstructAllInstantly))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), "PlaceItems")]
        static void SearchEnter() => searching++;

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.SearchBuildTargets))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.SearchRepairTargets))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.SearchAutoReconstructTargets))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), nameof(ConstructionModuleComponent.AutoReconstructAllInstantly))]
        [HarmonyPatch(typeof(ConstructionModuleComponent), "PlaceItems")]
        static void SearchLeave() => searching--;

        [HarmonyPrefix, HarmonyPatch(typeof(HashSystem), nameof(HashSystem.GetBucketIdxesInArea), typeof(Vector3), typeof(float))]
        static bool AllBuckets(HashSystem __instance, Vector3 center, float areaRadius)
        {
            if (searching == 0 || HashSystem.bucketMap == null) return true;
            if (!NeedsAll(center, areaRadius)) return true;
            int n = HashSystem.bucketCount;
            var active = __instance.activeBuckets;
            if (active == null || active.Length < n) return true;
            for (int i = 0; i < n; i++) active[i] = i;
            __instance.activeBucketsCount = n;
            return false;
        }

        // Same cell lookup as the game; the 32 cells are sorted by distance, so if the last is still in range more may be.
        public static bool NeedsAll(Vector3 center, float areaRadius)
        {
            float size = HashSystem.cellSize;
            int x = Mathf.Clamp((int)((center.x + 270f) / size), 0, 99);
            int y = Mathf.Clamp((int)((center.y + 270f) / size), 0, 99);
            int z = Mathf.Clamp((int)((center.z + 270f) / size), 0, 99);
            int cell = (z * 10000 + y * 100 + x) << 5;
            return HashSystem.bucketMap[cell + 31].dist <= areaRadius;
        }

        // ---- Upgrade techs ----

        [HarmonyPostfix, HarmonyPatch(typeof(VFPreload), "InvokeOnLoadWorkEnded")]
        [HarmonyAfter("me.xiaoye97.plugin.Dyson.LDBTool", "dsp.common-api.CommonAPI", "org.LoShin.GenesisBook", "bug.dsp.deliveryitemchain")]
        static void Register()
        {
            if (Tiers != null) return;
            try { Build(); }
            catch (Exception e) { Log?.LogError("Upgrade techs not added: " + e); Tiers = new TechProto[0]; }
        }

        static void Build()
        {
            var set = LDB.techs;
            var ranges = TierRanges();
            for (int t = 0; t < ranges.Count; t++)
                if (set.Exist(Base + t + 1))
                {
                    Log?.LogWarning("Tech ids " + (Base + 1) + "-" + (Base + ranges.Count) + " are taken by another mod (" + (Base + t + 1) + "), upgrades not added");
                    Tiers = new TechProto[0];
                    return;
                }
            int pre = MechaDroneTech();
            Layout(set.dataArray, out float x0, out float dx, out float y0);
            var tiers = new TechProto[ranges.Count];
            for (int t = 0; t < ranges.Count; t++)
            {
                int cubes = Cubes(ranges[t][0]);
                var items = new int[cubes];
                var points = new int[cubes];
                for (int c = 0; c < cubes; c++) { items[c] = TechProto.matrixIds[c]; points[c] = 20; }
                bool last = ranges[t][1] >= MaxStage;
                tiers[t] = new TechProto
                {
                    ID = Base + t + 1,
                    Name = last ? LastName : Name,
                    Desc = Desc,
                    Conclusion = last ? LastConclusion : Conclusion,
                    Published = true,
                    IsLabTech = true,
                    IconPath = "",
                    IconTag = "",
                    PreTechs = t == 0 ? new int[0] : new[] { tiers[t - 1].ID },
                    PreTechsImplicit = t == 0 && pre > 0 ? new[] { pre } : new int[0],
                    PreItem = new int[0],
                    Items = items,
                    ItemPoints = points,
                    HashNeeded = 0,
                    LevelCoef1 = last ? 240000 : 18000,  // 18000*(L + L^2): 36k at stage 1, 1.62M at stage 9; planet-wide 26.4M
                    LevelCoef2 = last ? 240000 : 18000,
                    Level = ranges[t][0],
                    MaxLevel = ranges[t][1],
                    UnlockRecipes = new int[0],
                    UnlockFunctions = new int[0],
                    UnlockValues = new double[0],
                    AddItems = new int[0],
                    AddItemCounts = new int[0],
                    PropertyOverrideItems = new int[0],
                    PropertyItemCounts = new int[0],
                    PropertyOverrideItemArray = new IDCNT[0],
                    Position = new Vector2(x0 + t * dx, y0),
                };
            }

            var old = set.dataArray;
            set.Init(old.Length + tiers.Length);
            Array.Copy(old, set.dataArray, old.Length);
            for (int i = 0; i < tiers.Length; i++) set.dataArray[old.Length + i] = tiers[i];
            var index = new Dictionary<int, int>();  // not OnAfterDeserialize: it resets every translated name to its key
            for (int i = 0; i < set.dataArray.Length; i++) index[set.dataArray[i].ID] = i;
            Traverse.Create(set).Field("dataIndices").SetValue(index);
            if (set.Select(tiers[0].ID) != tiers[0]) throw new InvalidOperationException("tech index not rebuilt");
            var sprite = LoadIcon("tech-coverage.png");
            foreach (var p in tiers) p.Preload();
            foreach (var p in tiers) { p.Preload2(); Traverse.Create(p).Field("_iconSprite").SetValue(sprite); }
            Tiers = tiers;
            Log?.LogInfo("Added " + tiers.Length + " drone coverage upgrade techs (ids " + tiers[0].ID + "-" + tiers[tiers.Length - 1].ID + ")" + (sprite == null ? ", icon missing" : ""));
        }

        // The upgrade that raises the mecha's construction drone count (unlock function 1), so the line opens with drones.
        static int MechaDroneTech()
        {
            int best = 0;
            foreach (var t in LDB.techs.dataArray)
                if (t?.UnlockFunctions != null && Array.IndexOf(t.UnlockFunctions, 1) >= 0 && (best == 0 || t.ID < best)) best = t.ID;
            return best;
        }

        // A new row under the lowest upgrade row, on the upgrade grid's own spacing and starting column (as DeliveryItemChain).
        public static void Layout(TechProto[] techs, out float x0, out float dx, out float y0)
        {
            float minY = float.MaxValue;
            var rowStart = new Dictionary<float, float>();
            var ys = new SortedSet<float>();
            var xs = new SortedSet<float>();
            foreach (var t in techs)
            {
                if (t == null || t.page != 1) continue;
                var p = t.Position;
                minY = Math.Min(minY, p.y);
                ys.Add(p.y);
                xs.Add(p.x);
                rowStart[p.y] = rowStart.TryGetValue(p.y, out float x) ? Math.Min(x, p.x) : p.x;
            }
            dx = CommonGap(xs, 4f);
            float dy = CommonGap(ys, 4f);
            x0 = Mode(rowStart.Values, 0f);
            y0 = (minY == float.MaxValue ? 0f : minY) - dy;
        }

        static float CommonGap(SortedSet<float> values, float fallback)
        {
            var gaps = new List<float>();
            float last = float.NaN;
            foreach (float v in values) { if (!float.IsNaN(last) && v - last > 0.5f) gaps.Add(v - last); last = v; }
            return Mode(gaps, fallback);
        }

        static float Mode(IEnumerable<float> values, float fallback)
        {
            var counts = new Dictionary<float, int>();
            float best = fallback;
            int most = 0;
            foreach (float v in values)
            {
                counts[v] = counts.TryGetValue(v, out int n) ? n + 1 : 1;
                if (counts[v] > most || counts[v] == most && v < best) { most = counts[v]; best = v; }
            }
            return best;
        }

        static Sprite LoadIcon(string name)
        {
            using (var s = typeof(Coverage).Assembly.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                tex.LoadImage(bytes);
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
        }
    }
}
