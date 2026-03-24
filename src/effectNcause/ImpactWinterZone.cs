using Vintagestory.API.MathTools;

namespace EffectNCause
{
    /// <summary>
    /// Represents a localised climate anomaly caused by a meteorite impact.
    /// All positional data is stored as plain integers so the object serialises
    /// cleanly with Newtonsoft.Json (BlockPos is not guaranteed serialisable).
    /// </summary>
    public class ImpactWinterZone
    {
        /// <summary>X coordinate of the impact centre.</summary>
        public int CenterX { get; set; }

        /// <summary>Y coordinate of the impact centre (surface level at impact).</summary>
        public int CenterY { get; set; }

        /// <summary>Z coordinate of the impact centre.</summary>
        public int CenterZ { get; set; }

        /// <summary>
        /// Horizontal radius (in blocks) within which the winter effect is applied.
        /// Only X/Z distance is considered; Y is ignored.
        /// </summary>
        public int Radius { get; set; }

        /// <summary>
        /// The <see cref="Vintagestory.API.Common.IGameCalendar.TotalDays"/> value at
        /// which this zone expires and is removed from the active list.
        /// </summary>
        public double ExpiryTotalDays { get; set; }

        // ── convenience ──────────────────────────────────────────────────────────

        /// <summary>Returns the centre as a <see cref="BlockPos"/>.</summary>
        public BlockPos GetCenter() => new BlockPos(CenterX, CenterY, CenterZ);

        /// <summary>
        /// Returns <c>true</c> if <paramref name="pos"/> falls within the horizontal
        /// radius of this zone (Y axis is ignored).
        /// </summary>
        public bool Contains(BlockPos pos)
        {
            long dx = pos.X - CenterX;
            long dz = pos.Z - CenterZ;
            return (dx * dx + dz * dz) <= (long)Radius * Radius;
        }
    }
}
