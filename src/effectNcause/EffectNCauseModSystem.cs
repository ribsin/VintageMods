using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace EffectNCause
{
    /// <summary>
    /// Main entry point for the effectNcause mod.
    ///
    /// Subsystems
    /// ──────────
    ///  1. Save / Load      – persists state between server restarts.
    ///  2. Climate Engine   – modifies temperature and rainfall in real-time.
    ///  3. Player Trackers  – reacts to block-break / block-place actions.
    ///  4. Meteorite System – random daily chance of an impact winter event.
    ///  5. Deserts / Tundras – bimodal rainfall logic driven by warming or cooling.
    ///  6. Wind &amp; Clouds    – adjusts WeatherSystemServer wind strength.
    ///  7. Sea Level         – slow block-replacement tick for rising/falling ocean.
    /// </summary>
    public class EffectNCauseModSystem : ModSystem
    {
        // ── save-game key ─────────────────────────────────────────────────────────
        private const string SaveKey = "effectncause_state";

        // ── climate constants ─────────────────────────────────────────────────────
        /// <summary>Degrees Celsius added to climate.Temperature per unit of WorldClimateImpact.</summary>
        private const float ImpactToDegreeRatio = 1f;

        /// <summary>
        /// Rainfall reduction per unit of WorldClimateImpact when warming.
        /// E.g. at impact = 20 the rainfall is reduced by 0.20 (desert expansion).
        /// </summary>
        private const double WarmingRainfallReductionRatio = 1.0 / 100.0;

        /// <summary>
        /// Rainfall reduction per unit of cooling magnitude.
        /// Cold air holds less moisture; halved vs warming to avoid over-drying.
        /// Remaining precipitation falls as snow due to low temperature, expanding tundras.
        /// </summary>
        private const double CoolingRainfallReductionRatio = 0.5 / 100.0;

        /// <summary>Additional temperature drop inside an impact-winter zone (°C).</summary>
        private const float ImpactWinterTempDrop = 20f;

        // ── player-action constants ───────────────────────────────────────────────
        /// <summary>Climate impact increase when a log/wood block is broken.</summary>
        private const double DeforestationImpact = 0.01;

        /// <summary>Climate impact decrease when a sapling is planted.</summary>
        private const double PlantingSaplingReduction = 0.05;

        /// <summary>Climate impact increase when coal is consumed as fuel.</summary>
        private const double CoalBurnImpact = 0.05;

        // ── meteorite constants ───────────────────────────────────────────────────
        /// <summary>Inverse probability of a meteorite per in-game day (1 in N).</summary>
        private const int MeteoriteChanceOneInN = 100;

        /// <summary>Horizontal explosion destruction radius (blocks).</summary>
        private const double MeteoriteDestructionRadius = 15.0;

        /// <summary>Explosion damage/injure radius (blocks).</summary>
        private const double MeteoriteInjureRadius = 30.0;

        /// <summary>Maximum random X or Z offset from the target player (blocks).</summary>
        private const int MeteoriteMaxOffset = 200;

        /// <summary>Radius of the impact winter zone (blocks, horizontal only).</summary>
        private const int ImpactWinterRadius = 500;

        /// <summary>Duration of an impact winter in in-game days.</summary>
        private const double ImpactWinterDurationDays = 14.0;

        // ── tick interval ─────────────────────────────────────────────────────────
        /// <summary>
        /// How often (in real milliseconds) to run the daily-event check.
        /// One real minute is enough resolution; the code guards against running
        /// more than once per in-game day.
        /// </summary>
        private const int DailyCheckIntervalMs = 60_000;

        // ── sea level constants ───────────────────────────────────────────────────
        /// <summary>
        /// Impact threshold (in WorldClimateImpact units) that must pass before
        /// GlobalSeaLevelOffset steps by 1.
        /// E.g. impact = 15 → offset = 1 (1 block rise); impact = -10 → offset = -1.
        /// </summary>
        private const double SeaLevelImpactThreshold = 10.0;

        /// <summary>Maximum number of XZ columns to process during one sea-level tick.</summary>
        private const int SeaLevelColumnsPerTick = 16;

        /// <summary>How often (ms) to run the sea-level block-replacement tick.</summary>
        private const int SeaLevelTickIntervalMs = 2_000;

        // ── weather constants ─────────────────────────────────────────────────────
        /// <summary>How often (ms) to update the weather system wind strength.</summary>
        private const int WeatherTickIntervalMs = 10_000;

        /// <summary>WorldClimateImpact above this threshold triggers aggressive storm patterns.</summary>
        private const double WeatherHighImpactThreshold = 20.0;

        /// <summary>WorldClimateImpact below this threshold triggers stagnant freezing air.</summary>
        private const double WeatherLowImpactThreshold = -20.0;

        /// <summary>Wind strength for calm, freezing conditions (VS wind scale 0–2).</summary>
        private const float MinCalmWindStrength = 0.05f;

        /// <summary>Baseline wind strength at zero climate impact.</summary>
        private const float NormalWindStrength = 0.5f;

        /// <summary>Maximum wind strength during severe warming / storm patterns.</summary>
        private const float MaxStormWindStrength = 1.8f;

        // ── state ─────────────────────────────────────────────────────────────────
        // ShouldLoad() ensures this ModSystem only runs server-side, so
        // StartServerSide() is always called before any event handler.
        // null! is safe here because the field is always assigned before first use.
        private ICoreServerAPI api = null!;

        /// <summary>
        /// Global accumulated climate impact in degrees Celsius.
        /// Increased by deforestation / coal burning; decreased by planting.
        /// </summary>
        public double WorldClimateImpact { get; private set; } = 0.0;

        /// <summary>Currently active meteorite impact-winter zones.</summary>
        public List<ImpactWinterZone> ActiveImpactWinters { get; private set; } = new List<ImpactWinterZone>();

        /// <summary>
        /// The last whole in-game day on which the daily check ran.
        /// Initialised to -1 so the check fires on the first tick after load.
        /// </summary>
        private double _lastCheckedDay = -1.0;

        /// <summary>
        /// How many blocks above (positive) or below (negative) the base sea level
        /// the ocean has risen/fallen.  Derived from <see cref="WorldClimateImpact"/>;
        /// any call to <see cref="ChangeClimateImpact"/> keeps this in sync.
        /// </summary>
        public int GlobalSeaLevelOffset { get; private set; } = 0;

        /// <summary>Block ID for still water at full level. Cached at startup.</summary>
        private int _waterBlockId;

        /// <summary>Block ID for lake/surface ice. Cached at startup.</summary>
        private int _iceBlockId;

        // ─────────────────────────────────────────────────────────────────────────
        // 1. ModSystem lifecycle
        // ─────────────────────────────────────────────────────────────────────────

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.api = api;

            // Save / Load
            api.Event.SaveGameLoaded += OnSaveGameLoaded;
            api.Event.GameWorldSave   += OnGameWorldSave;

            // Climate engine
            api.Event.OnGetClimate += OnGetClimate;

            // Player trackers
            api.Event.BreakBlock    += OnBreakBlock;
            api.Event.DidPlaceBlock += OnDidPlaceBlock;

            // Meteorite / daily timer
            api.Event.RegisterGameTickListener(OnDailyTick, DailyCheckIntervalMs);

            // Wind & cloud manipulation (every 10 s)
            api.Event.RegisterGameTickListener(OnWeatherTick, WeatherTickIntervalMs);

            // Sea-level block replacement (every 2 s)
            api.Event.RegisterGameTickListener(OnSeaLevelTick, SeaLevelTickIntervalMs);

            // Cache frequently-used block IDs.
            _waterBlockId = api.World.GetBlock(new AssetLocation("game:water-still-7"))?.BlockId
                         ?? api.World.GetBlock(new AssetLocation("game:water-still"))?.BlockId
                         ?? 0;
            _iceBlockId   = api.World.GetBlock(new AssetLocation("game:lakeice"))?.BlockId ?? 0;

            if (_waterBlockId == 0)
                Mod.Logger.Warning("[effectNcause] Could not resolve water block ID — sea-level rising will be disabled.");

            Mod.Logger.Notification("[effectNcause] Mod loaded successfully.");
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 2. Save / Load
        // ─────────────────────────────────────────────────────────────────────────

        private void OnSaveGameLoaded()
        {
            try
            {
                byte[]? raw = api.WorldManager.SaveGame.GetData(SaveKey);
                if (raw == null || raw.Length == 0)
                {
                    Mod.Logger.Notification("[effectNcause] No existing save data found, starting fresh.");
                    return;
                }

                string json = Encoding.UTF8.GetString(raw);
                EffectNCauseSaveState? state = JsonConvert.DeserializeObject<EffectNCauseSaveState>(json);
                if (state == null) return;

                WorldClimateImpact  = state.WorldClimateImpact;
                ActiveImpactWinters = state.ActiveImpactWinters ?? new List<ImpactWinterZone>();

                // GlobalSeaLevelOffset is derived from WorldClimateImpact; recompute to
                // ensure the two values are always consistent after load.
                GlobalSeaLevelOffset = (int)(WorldClimateImpact / SeaLevelImpactThreshold);

                Mod.Logger.Notification(
                    $"[effectNcause] Loaded state — Impact: {WorldClimateImpact:F4}, " +
                    $"SeaOffset: {GlobalSeaLevelOffset}, " +
                    $"ActiveWinters: {ActiveImpactWinters.Count}");
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"[effectNcause] Failed to load save data: {ex}");
            }
        }

        private void OnGameWorldSave()
        {
            try
            {
                var state = new EffectNCauseSaveState
                {
                    WorldClimateImpact   = WorldClimateImpact,
                    GlobalSeaLevelOffset = GlobalSeaLevelOffset,
                    ActiveImpactWinters  = ActiveImpactWinters
                };

                string json = JsonConvert.SerializeObject(state);
                api.WorldManager.SaveGame.StoreData(SaveKey, Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex)
            {
                Mod.Logger.Error($"[effectNcause] Failed to save state: {ex}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 3. Climate Modification Engine
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by the game engine whenever climate values are requested for a
        /// given world position.  World-generation queries are intentionally skipped
        /// so that the generated terrain layout is never affected.
        /// </summary>
        private void OnGetClimate(ref ClimateCondition climate, BlockPos pos,
                                  EnumGetClimateMode mode, double totalDays)
        {
            // Never alter world-generation climate lookups.
            if (mode == EnumGetClimateMode.WorldGenValues) return;

            // ── Global temperature shift ──────────────────────────────────────────
            climate.Temperature += (float)(WorldClimateImpact * ImpactToDegreeRatio);

            // ── Desert / Tundra expansion ─────────────────────────────────────────
            // ClimateCondition.Rainfall is normalised to [0, 1].
            if (WorldClimateImpact > 0)
            {
                // Warming → drier world → expanding deserts.
                float reduction = (float)(WorldClimateImpact * WarmingRainfallReductionRatio);
                climate.Rainfall = GameMath.Clamp(climate.Rainfall - reduction, 0f, 1f);
            }
            else if (WorldClimateImpact < 0)
            {
                // Cooling → cold air holds less moisture → slight drying.
                // The already-reduced temperature causes remaining precipitation to
                // fall as snow, which the game's engine naturally converts into
                // tundra biome characteristics.
                float reduction = (float)(-WorldClimateImpact * CoolingRainfallReductionRatio);
                climate.Rainfall = GameMath.Clamp(climate.Rainfall - reduction, 0f, 1f);
            }

            // ── Impact winter zones ───────────────────────────────────────────────
            // Iterate a snapshot; mutation happens only on the server tick thread.
            foreach (ImpactWinterZone zone in ActiveImpactWinters)
            {
                if (!zone.Contains(pos)) continue;

                climate.Temperature -= ImpactWinterTempDrop;
                climate.Rainfall     = 0f;
                break; // Apply at most one zone; further zones are ignored to avoid double-applying effects.
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 4. Player Action Trackers
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fires just before a block is broken.  Checks whether the block is made of
        /// wood/log material and, if so, increases WorldClimateImpact slightly to
        /// simulate deforestation pollution.
        /// </summary>
        private void OnBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel,
                                  ref EnumHandling handling)
        {
            if (byPlayer == null || blockSel?.Position == null) return;

            Block block = api.World.BlockAccessor.GetBlock(blockSel.Position);
            if (block == null) return;

            if (IsWoodOrLog(block))
            {
                ChangeClimateImpact(DeforestationImpact);
                Mod.Logger.VerboseDebug(
                    $"[effectNcause] {byPlayer.PlayerName} broke a log/wood block. " +
                    $"Impact → {WorldClimateImpact:F4}");
            }
        }

        /// <summary>
        /// Fires after a block has been placed.  Checks whether the placed block is a
        /// sapling; if so, decreases WorldClimateImpact to simulate reforestation.
        /// </summary>
        private void OnDidPlaceBlock(IServerPlayer byPlayer, int oldBlockId,
                                     BlockSelection blockSel, ItemStack withItemStack)
        {
            if (byPlayer == null || blockSel?.Position == null) return;

            Block placedBlock = api.World.BlockAccessor.GetBlock(blockSel.Position);
            if (placedBlock == null) return;

            if (IsSapling(placedBlock))
            {
                ChangeClimateImpact(-PlantingSaplingReduction);
                Mod.Logger.VerboseDebug(
                    $"[effectNcause] {byPlayer.PlayerName} planted a sapling. " +
                    $"Impact → {WorldClimateImpact:F4}");
            }
        }

        // ── Fuel/smelting hook (optional) ──────────────────────────────────────────
        //
        // The Vintage Story API does not expose a dedicated "fuel consumed" event.
        // Hooking into coal burning would require either:
        //   a) patching IFuelBurnTimeMultiplier via Harmony, or
        //   b) listening to BlockEntityFirepit / BlockEntityForge tick events.
        //
        // A minimal approach: call AddCoalBurnImpact() from a Harmony postfix on
        // BlockEntityFirepit.OnFueled() if that integration is desired.
        //
        // The public helper below is provided so external code (e.g. a Harmony patch)
        // can feed coal-burn events into this system without exposing mutable fields.

        /// <summary>
        /// Call this whenever coal (or another high-impact fuel) is consumed.
        /// Each call represents one unit of coal, adding <see cref="CoalBurnImpact"/>
        /// to <see cref="WorldClimateImpact"/>.
        /// </summary>
        public void AddCoalBurnImpact()
        {
            ChangeClimateImpact(CoalBurnImpact);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 5. Meteorite & Impact Winter System
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Server tick handler.  Runs every <see cref="DailyCheckIntervalMs"/>
        /// milliseconds.  Advances no more than once per in-game day to keep the
        /// meteorite probability correct regardless of tick frequency.
        /// </summary>
        private void OnDailyTick(float deltaTime)
        {
            double currentDay = Math.Floor(api.World.Calendar.TotalDays);

            // Guard: run at most once per in-game day.
            if (currentDay <= _lastCheckedDay) return;
            _lastCheckedDay = currentDay;

            // ── Expire old impact winters ─────────────────────────────────────────
            int removed = ActiveImpactWinters.RemoveAll(
                z => api.World.Calendar.TotalDays >= z.ExpiryTotalDays);

            if (removed > 0)
                Mod.Logger.Notification($"[effectNcause] {removed} impact winter(s) expired.");

            // ── Meteorite random chance ───────────────────────────────────────────
            if (api.World.Rand.Next(MeteoriteChanceOneInN) == 0)
                TrySpawnMeteorite();
        }

        /// <summary>
        /// Attempts to spawn a meteorite explosion near a random online player and
        /// registers a new impact-winter zone centred on the impact site.
        /// Does nothing if there are no online players to anchor the position to.
        /// </summary>
        private void TrySpawnMeteorite()
        {
            IPlayer[] onlinePlayers = api.World.AllOnlinePlayers;
            if (onlinePlayers.Length == 0)
            {
                Mod.Logger.Notification("[effectNcause] Meteorite roll succeeded but no players are online — skipping.");
                return;
            }

            // Pick a random online player as the anchor point.
            IPlayer anchor = onlinePlayers[api.World.Rand.Next(onlinePlayers.Length)];
            if (anchor.Entity?.Pos == null) return;

            // Offset the impact randomly within ±MeteoriteMaxOffset blocks horizontally.
            int impactX = (int)anchor.Entity.Pos.X
                          + api.World.Rand.Next(-MeteoriteMaxOffset, MeteoriteMaxOffset + 1);
            int impactZ = (int)anchor.Entity.Pos.Z
                          + api.World.Rand.Next(-MeteoriteMaxOffset, MeteoriteMaxOffset + 1);

            // Find the surface Y at the impact position for a realistic impact point.
            int impactY = api.World.BlockAccessor.GetTerrainMapheightAt(
                              new BlockPos(impactX, 1, impactZ));
            // GetTerrainMapheightAt returns 0 when the column is not loaded.
            // Fall back to world mid-height; the explosion will still be visible
            // though it may be above or below the surface in unloaded terrain.
            if (impactY <= 0) impactY = api.WorldManager.MapSizeY / 2;

            BlockPos impactPos = new BlockPos(impactX, impactY, impactZ);

            // ── Explosion ─────────────────────────────────────────────────────────
            api.World.CreateExplosion(impactPos, EnumBlastType.RockBlast,
                                      MeteoriteDestructionRadius, MeteoriteInjureRadius);

            // ── Impact winter zone ────────────────────────────────────────────────
            double expiryDay = api.World.Calendar.TotalDays + ImpactWinterDurationDays;
            ActiveImpactWinters.Add(new ImpactWinterZone
            {
                CenterX          = impactX,
                CenterY          = impactY,
                CenterZ          = impactZ,
                Radius           = ImpactWinterRadius,
                ExpiryTotalDays  = expiryDay
            });

            // ── Broadcast announcement ────────────────────────────────────────────
            string message =
                $"A meteorite has struck the world near ({impactX}, {impactY}, {impactZ})! " +
                $"Expect a long winter within {ImpactWinterRadius} blocks for ~{ImpactWinterDurationDays} days.";

            foreach (IPlayer player in api.World.AllOnlinePlayers)
                (player as IServerPlayer)?.SendMessage(
                    GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);

            Mod.Logger.Notification($"[effectNcause] Meteorite impact at {impactPos}. {message}");
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 6. Wind & Cloud Manipulation
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs every <see cref="WeatherTickIntervalMs"/> ms.
        /// Adjusts the global wind strength in <see cref="WeatherSystemServer"/>:
        /// <list type="bullet">
        ///   <item>High positive impact → stormy, energetic patterns (high wind).</item>
        ///   <item>High negative impact → stagnant, freezing high-pressure (low wind).</item>
        /// </list>
        /// Wind strength is smoothly interpolated to avoid jarring transitions.
        /// </summary>
        private void OnWeatherTick(float deltaTime)
        {
            WeatherSystemServer? wss = api.ModLoader.GetModSystem<WeatherSystemServer>();
            if (wss == null) return;

            float target = CalculateTargetWindStrength();

            // Lerp 10 % of the way to the target each tick for a gradual transition.
            wss.windData.Strength = GameMath.Lerp(wss.windData.Strength, target, 0.1f);
        }

        /// <summary>
        /// Returns the desired wind strength based on <see cref="WorldClimateImpact"/>,
        /// clamped to [<see cref="MinCalmWindStrength"/>, <see cref="MaxStormWindStrength"/>].
        /// </summary>
        private float CalculateTargetWindStrength()
        {
            if (WorldClimateImpact >= WeatherHighImpactThreshold)
            {
                // Scale up from normal towards max storm strength.
                float excess = (float)(WorldClimateImpact - WeatherHighImpactThreshold);
                return Math.Min(NormalWindStrength + excess * 0.025f, MaxStormWindStrength);
            }

            if (WorldClimateImpact <= WeatherLowImpactThreshold)
            {
                // Scale down from normal towards minimum calm strength.
                float deficit = (float)(WeatherLowImpactThreshold - WorldClimateImpact);
                return Math.Max(NormalWindStrength - deficit * 0.015f, MinCalmWindStrength);
            }

            return NormalWindStrength;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // 7. Sea Level Rising / Lowering
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs every <see cref="SeaLevelTickIntervalMs"/> ms.
        /// Selects <see cref="SeaLevelColumnsPerTick"/> random XZ columns within a
        /// 32×32 chunk centred on a random online player and adjusts blocks to match
        /// the target sea level.  Only a small batch is processed per tick to keep
        /// TPS impact negligible.
        /// </summary>
        private void OnSeaLevelTick(float deltaTime)
        {
            if (GlobalSeaLevelOffset == 0 || _waterBlockId == 0) return;

            IPlayer[] onlinePlayers = api.World.AllOnlinePlayers;
            if (onlinePlayers.Length == 0) return;

            // Choose an anchor player to determine which chunk area to process.
            IPlayer anchor = onlinePlayers[api.World.Rand.Next(onlinePlayers.Length)];
            if (anchor.Entity?.Pos == null) return;

            int baseSeaLevel   = api.World.SeaLevel;
            int targetSeaLevel = baseSeaLevel + GlobalSeaLevelOffset;

            // Snap to chunk-aligned origin (chunks are 32 blocks wide).
            int chunkX = ((int)anchor.Entity.Pos.X >> 5) << 5;
            int chunkZ = ((int)anchor.Entity.Pos.Z >> 5) << 5;

            // Determine whether to leave ice instead of air when lowering.
            ClimateCondition? climate = api.World.BlockAccessor.GetClimateAt(
                anchor.Entity.Pos.AsBlockPos, EnumGetClimateMode.NowValues);
            bool isFreezing = climate != null && climate.Temperature < 0f;
            int  lowerReplacement = (isFreezing && _iceBlockId != 0) ? _iceBlockId : 0; // 0 = Air

            IBlockAccessor ba = api.World.BlockAccessor;

            for (int i = 0; i < SeaLevelColumnsPerTick; i++)
            {
                int x = chunkX + api.World.Rand.Next(32);
                int z = chunkZ + api.World.Rand.Next(32);

                if (GlobalSeaLevelOffset > 0)
                {
                    // Rising: fill each column from (baseSeaLevel + 1) up to targetSeaLevel.
                    for (int y = baseSeaLevel + 1; y <= targetSeaLevel; y++)
                    {
                        BlockPos pos   = new BlockPos(x, y, z);
                        Block    block = ba.GetBlock(pos);
                        if (block != null && IsReplaceableByWater(block))
                            ba.SetBlock(_waterBlockId, pos);
                    }
                }
                else
                {
                    // Lowering: drain each column from baseSeaLevel down to (targetSeaLevel + 1).
                    for (int y = baseSeaLevel; y > targetSeaLevel; y--)
                    {
                        BlockPos pos   = new BlockPos(x, y, z);
                        Block    block = ba.GetBlock(pos);
                        if (block != null && IsWaterBlock(block))
                            ba.SetBlock(lowerReplacement, pos);
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if <paramref name="block"/> represents a log or
        /// structural wood block (the kind that contributes to deforestation when
        /// removed from a forest).
        ///
        /// The check uses both the block material enum and the code path as a
        /// fall-back so that modded wood blocks are also captured.
        /// </summary>
        private static bool IsWoodOrLog(Block block)
        {
            if (block.BlockMaterial == EnumBlockMaterial.Wood) return true;

            string path = block.Code?.Path ?? string.Empty;
            return path.Contains("log") || path.Contains("-wood");
        }

        /// <summary>
        /// Returns <c>true</c> if <paramref name="block"/> is a sapling (any variant,
        /// including modded ones whose code path contains "sapling").
        /// </summary>
        private static bool IsSapling(Block block)
        {
            string path = block.Code?.Path ?? string.Empty;
            return path.Contains("sapling");
        }

        /// <summary>
        /// The single authoritative mutator for <see cref="WorldClimateImpact"/>.
        /// Applies <paramref name="delta"/> and keeps <see cref="GlobalSeaLevelOffset"/>
        /// in sync via integer truncation: one sea-level step per
        /// <see cref="SeaLevelImpactThreshold"/> units of impact.
        /// </summary>
        private void ChangeClimateImpact(double delta)
        {
            WorldClimateImpact   += delta;
            GlobalSeaLevelOffset  = (int)(WorldClimateImpact / SeaLevelImpactThreshold);
        }

        /// <summary>
        /// Returns <c>true</c> if a rising sea can replace <paramref name="block"/>
        /// (e.g. air, tall grass, flowers — anything with a VS Replaceable score ≥ 5000).
        /// </summary>
        private static bool IsReplaceableByWater(Block block) => block.Replaceable >= 5000;

        /// <summary>
        /// Returns <c>true</c> if <paramref name="block"/> is a liquid water block
        /// that should be drained when the sea level falls.
        /// </summary>
        private static bool IsWaterBlock(Block block)
            => block.BlockMaterial == EnumBlockMaterial.Liquid
            && (block.Code?.Path.StartsWith("water") ?? false);
    }
}
