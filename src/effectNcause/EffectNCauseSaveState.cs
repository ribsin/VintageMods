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

        /// <summary>Active meteorite impact winter zones.</summary>
        public List<ImpactWinterZone> ActiveImpactWinters { get; set; } = new List<ImpactWinterZone>();
    }
}
