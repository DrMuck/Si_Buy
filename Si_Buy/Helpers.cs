/*
 Helpers.cs — spawn-height sampling and team naming shared with the KoH mod
 (copied at the split so Si_Buy has no dependency on Si_KingOfTheHill).
*/

using System;
using UnityEngine;

namespace Si_Buy
{
    public partial class Buy
    {
        /// <summary>
        /// Surface Y at (x, z) suitable for spawning a vehicle/unit near an existing
        /// anchor (the buyer's controlled unit). The generic SampleSurfaceY raycasts
        /// from terrain+200m down 400m and accepts ANY collider, so a Bomber / mid-air
        /// projectile crossing the spawn ray returns a 150-200m "ground" — the spawn
        /// drops from the sky and explodes. We bound the ray to ±50m around the anchor
        /// so only nearby static surfaces (terrain, structure floors next to the player)
        /// can be picked. Falls back to terrain Y, then anchor Y, then 0.
        /// </summary>
        static float SampleSurfaceY_NearAnchor(Terrain terrain, float x, float z, float anchorY)
        {
            float rayStart = anchorY + 10f;
            try
            {
                if (Physics.Raycast(new Vector3(x, rayStart, z), Vector3.down,
                                    out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore))
                {
                    return hit.point.y;
                }
            }
            catch { }
            float terrainY = SampleTerrainYLocal(terrain, x, z);
            // If terrain sample is wildly off vs anchor (e.g. tank on a building roof),
            // prefer the anchor — never let the caller spawn far above the ground.
            if (Mathf.Abs(terrainY - anchorY) > 50f) return anchorY;
            return terrainY;
        }

        static float SampleTerrainYLocal(Terrain terrain, float x, float z)
        {
            if (terrain == null) return 0f;
            try
            {
                return terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
            }
            catch { return 0f; }
        }

        static string SafeTeamName(Team t)
        {
            // Prefer ShortName — it's clean ("Sol" / "Cent" / "Alien") whereas TeamName
            // returns "Human Sol" / "Human Centauri" / etc.
            try
            {
                var s = t.GetTeamShortName();
                if (!string.IsNullOrEmpty(s)) return s;
                var n = t.GetTeamName();
                if (!string.IsNullOrEmpty(n)) return n;
                return t.name ?? "?";
            }
            catch { return "?"; }
        }
    }
}
