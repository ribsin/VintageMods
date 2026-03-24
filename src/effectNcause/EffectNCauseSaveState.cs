using System.Collections.Generic;

namespace EffectNCause
{
    /// <summary>
    /// Plain data container that is serialised to/from the save-game data store.
    /// Newtonsoft.Json handles all serialisation, so no special attributes are needed.
    /// </summary>
    public class EffectNCauseSaveState
    {
        /// <summary>
        /// The cumulative global climate impact in degrees Celsius.
        /// Positive values warm the world; negative values cool it.
        /// </summary>
        public double WorldClimateImpact { get; set; }

        /// <summary>
        /// How many blocks the ocean has risen (positive) or fallen (negative)
        /// from the base sea level.  Steps by 1 for every
        /// <c>SeaLevelImpactThreshold</c> units of <c>WorldClimateImpact</c>.
        /// Stored here for save-game transparency; always recomputed from
        /// <c>WorldClimateImpact</c> on load to keep the two values consistent.
        /// </summary>
        public int GlobalSeaLevelOffset { get; set; }

        /// <summary>Active meteorite impact winter zones.</summary>
        public List<ImpactWinterZone> ActiveImpactWinters { get; set; } = new List<ImpactWinterZone>();
    }
}
