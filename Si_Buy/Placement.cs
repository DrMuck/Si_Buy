/*
 Placement.cs — where a purchase lands.

 Units:      rings around the buyer, starting BuySpawnDistance ahead, fanning out from the facing direction up to
             BuySpawnSearchRadius. A candidate must have real ground (the surface level the buyer stands on, never a
             unit or structure), no roof or bridge above it, a gentle slope, and nothing but that floor inside a
             sphere of the prefab's size. Nothing found → the purchase is refused, credits untouched.

 Structures: the game's own placement rules (ConstructionPreview of the structure's ConstructionData: grid snap,
             rotate-to-surface, GetGroundSlopeClear, GetObstructed) on a square spiral around the point ahead of the
             buyer, up to StructureSpawnSearchRadius. Every valid candidate is scored by how much of its foundation
             hangs in the air (highest minus lowest ground under the foundation anchors); the flattest spot wins,
             even when it is further away. Structures without construction data use the unit search.

 Ground: a ray from 1500 m above with the placement mask the game uses (terrain, static map geometry, floors above
 the heightmap such as Industrial Quarter's plates), all hits collected; colliders that belong to a unit or structure
 are skipped, and the hit closest to the buyer's own height is the floor. That keeps a hidden heightmap under a
 steel floor, a roof above the buyer, or the wrong terrain tile from being taken for the ground.
*/

using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_Buy
{
    public partial class Buy
    {
        struct GroundHit
        {
            public Vector3 Point;
            public Vector3 Normal;
            public Collider? Col;
            public bool Covered;     // static geometry within CEILING metres above the floor (roof, bridge, gantry)
        }

        const float CEILING = 25f;

        // ----- ground -----

        /// <summary>
        /// The floor under x/z at the buyer's level: all non-trigger hits of a downward ray, units and structures
        /// skipped, closest to anchorY wins. False when the column holds no map surface at all.
        /// </summary>
        static bool GroundAt(float x, float z, float anchorY, out GroundHit best)
        {
            best = default;
            RaycastHit[] hits;
            try
            {
                hits = Physics.RaycastAll(new Vector3(x, anchorY + 1500f, z), Vector3.down, 3000f,
                                          GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC, QueryTriggerInteraction.Ignore);
            }
            catch { return false; }
            if (hits == null || hits.Length == 0) return false;

            int bi = -1; float bd = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i].collider;
                if (c == null || IsGameObjectCollider(c)) continue;
                float d = Mathf.Abs(hits[i].point.y - anchorY);
                if (d < bd) { bd = d; bi = i; }
            }
            if (bi < 0) return false;

            var b = hits[bi];
            bool covered = false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (i == bi) continue;
                var c = hits[i].collider;
                if (c == null || IsGameObjectCollider(c)) continue;
                float above = hits[i].point.y - b.point.y;
                if (above > 0.5f && above < CEILING) { covered = true; break; }
            }
            best = new GroundHit { Point = b.point, Normal = b.normal, Col = b.collider, Covered = covered };
            return true;
        }

        static bool IsGameObjectCollider(Collider c)
        {
            try { return c.GetComponentInParent<BaseGameObject>() != null; }
            catch { return false; }
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 0.0001f ? Vector3.forward : v.normalized; }

        static float RadiusOf(GameObject? go)
        {
            try
            {
                var bgo = go != null ? go.GetComponent<BaseGameObject>() : null;
                if (bgo != null && bgo.PhysicalRadius > 0.1f) return bgo.PhysicalRadius;
            }
            catch { }
            return 1f;
        }

        static float RadiusOf(BaseGameObject? o)
        {
            try { if (o != null && o.PhysicalRadius > 0.1f) return o.PhysicalRadius; } catch { }
            return 1f;
        }

        // Everything a spawn must not sit in: units, vehicles, structures, props. Terrain, triggers, UI, water are fine.
        static int _overlapMask;
        static int OverlapMask()
        {
            if (_overlapMask != 0) return _overlapMask;
            int exclude = (int)GamePhysics.TRACELAYERMASK_TERRAIN | (int)GamePhysics.TRACELAYERMASK_TRIGGER | (int)GamePhysics.TRACELAYERMASK_UI;
            try { exclude |= LayerMask.GetMask("Water"); } catch { }
            try { exclude |= LayerMask.GetMask("Ignore Raycast"); } catch { }
            _overlapMask = ~exclude;
            return _overlapMask;
        }

        /// <summary>Anything but the floor itself and flat static geometry at floor level inside the prefab's sphere.</summary>
        static bool Overlaps(GroundHit g, float r, int mask)
        {
            Collider[] hits;
            try { hits = Physics.OverlapSphere(g.Point + Vector3.up * (r * 0.6f + 0.3f), r + 0.5f, mask, QueryTriggerInteraction.Ignore); }
            catch { return false; }
            if (hits == null) return false;
            foreach (var c in hits)
            {
                if (c == null || c == g.Col) continue;
                // neighbouring floor plates, roads, pads: static geometry whose top is at floor level does not block
                if (!IsGameObjectCollider(c) && c.bounds.max.y <= g.Point.y + 0.6f) continue;
                return true;
            }
            return false;
        }

        // ----- units -----

        /// <summary>
        /// A free floor spot for a unit of prefabRadius near the buyer: rings from BuySpawnDistance ahead out to
        /// BuySpawnSearchRadius, angles fanning out from the facing direction. `why` summarises the rejected
        /// candidates when nothing is found.
        /// </summary>
        static bool FindUnitSpot(Vector3 origin, Vector3 forward, float buyerRadius, float prefabRadius,
                                 out Vector3 spot, out float distance, out string why)
        {
            float r = Mathf.Max(0.5f, prefabRadius);
            float first = Mathf.Max(Mathf.Max(2f, Cfg.BuySpawnDistance), buyerRadius + r + 1.5f);
            float maxD = Mathf.Max(first, Cfg.BuySpawnSearchRadius);
            float step = Mathf.Max(2.5f, r * 1.2f);
            Vector3 dir0 = Flat(forward);
            int mask = OverlapMask();
            int blocked = 0, covered = 0, steep = 0, level = 0, noGround = 0;
            for (float d = first; d <= maxD + 0.01f; d += step)
            {
                for (int k = 0; k < 12; k++)
                {
                    float angle = k == 0 ? 0f : (k + 1) / 2 * 30f * ((k % 2 == 1) ? 1f : -1f);
                    Vector3 p = origin + Quaternion.Euler(0f, angle, 0f) * dir0 * d;
                    if (!GroundAt(p.x, p.z, origin.y, out GroundHit g)) { noGround++; continue; }
                    if (g.Covered) { covered++; continue; }
                    if (Mathf.Abs(g.Point.y - origin.y) > 6f + 0.35f * d) { level++; continue; }   // a roof or ledge, not the buyer's level
                    if (g.Normal.y < 0.75f) { steep++; continue; }                                  // steeper than ~40 degrees
                    if (Overlaps(g, r, mask)) { blocked++; continue; }
                    spot = g.Point; distance = d; why = "";
                    return true;
                }
            }
            spot = origin + dir0 * first; distance = first;
            why = $"{blocked} on units or buildings, {covered} under a roof, {steep} too steep, {level} on another level, {noGround} without ground";
            return false;
        }

        // ----- structures -----

        /// <summary>
        /// The game's own placement rules for a structure prefab around the point ahead of the buyer. Returns the
        /// valid candidate whose foundation hangs least above the ground (ties: nearest). False with
        /// hasRules=false when the prefab carries no construction data (caller uses the unit search).
        /// </summary>
        static bool FindStructureSpot(GameObject prefab, Vector3 origin, Vector3 forward, out Vector3 pos, out Quaternion rot,
                                      out float hang, out float distance, out bool hasRules, out string why)
        {
            pos = origin; rot = Quaternion.identity; hang = 0f; distance = 0f; hasRules = false; why = "";
            ConstructionData? cd = null; ConstructionPreview? preview = null;
            float radius = 1f;
            try
            {
                var bgo = prefab.GetComponent<BaseGameObject>();
                radius = RadiusOf(bgo);
                cd = bgo != null && bgo.ObjectInfo != null ? bgo.ObjectInfo.ConstructionData : null;
                preview = cd != null ? cd.ObjectPreviewSetup : null;
            }
            catch (Exception ex) { MelonLogger.Warning("[Buy] construction data of " + prefab.name + ": " + ex.Message); }
            if (cd == null || preview == null) { why = "no construction data"; return false; }
            hasRules = true;

            float gridXZ = Mathf.Max(1f, cd.GridSnapXZ);
            float gridY = Mathf.Max(0.5f, cd.GridSnapY);
            Vector3 dir0 = Flat(forward);
            float yaw = Mathf.Round(Quaternion.LookRotation(dir0).eulerAngles.y / 90f) * 90f;   // the 90-degree steps commanders get
            Quaternion input = Quaternion.Euler(0f, yaw, 0f);
            Vector3 center = origin + dir0 * (Mathf.Max(2f, Cfg.BuySpawnDistance) + radius);
            float range = Mathf.Max(gridXZ, Cfg.StructureSpawnSearchRadius);

            bool found = false; float bestHang = float.MaxValue, bestDist = float.MaxValue;
            int tried = 0, slope = 0, obstructed = 0, noGround = 0;
            var anchors = preview.AboveGroundPoints;
            // square spiral in grid steps, the pattern of ConstructionPlacement.GetFirstValidPlacementAroundPoint
            int rings = Mathf.CeilToInt(range / gridXZ);
            for (int i = 0; i <= rings && !(found && bestHang <= 0.5f); i++)
            {
                int side = i * 2;
                float half = side * gridXZ * 0.5f;
                int count = i == 0 ? 1 : side * 4;
                for (int j = 0; j < count; j++)
                {
                    float x = 0f, z = 0f;
                    if (i > 0)
                    {
                        int edge = j / side, k = j - edge * side;
                        switch (edge)
                        {
                            case 0: x = k * gridXZ - half; z = half; break;
                            case 1: x = half; z = k * gridXZ - half; break;
                            case 2: x = k * gridXZ - half; z = -half; break;
                            default: x = -half; z = k * gridXZ - half; break;
                        }
                    }
                    Vector3 p = center + new Vector3(x, 0f, z);
                    tried++;
                    if (!GroundAt(p.x, p.z, origin.y, out GroundHit g)) { noGround++; continue; }
                    Vector3 snapped = GameMath.SnapToGrid(g.Point, gridXZ, gridY);
                    Quaternion r;
                    float adjustY;
                    try
                    {
                        r = preview.GetSnappedRotation(snapped, input, gridY);
                        if (!preview.GetGroundSlopeClear(snapped, r, out adjustY)) { slope++; continue; }
                        snapped.y += adjustY;
                        if (preview.GetObstructed(snapped, r, out _) != EObstructedType.None) { obstructed++; continue; }
                    }
                    catch (Exception ex) { why = "placement check threw: " + ex.Message; MelonLogger.Warning("[Buy] " + why); return false; }
                    float h = FoundationHang(anchors, snapped, r);
                    float d = Vector3.Distance(new Vector3(origin.x, 0f, origin.z), new Vector3(snapped.x, 0f, snapped.z));
                    if (h < bestHang - 0.05f || (Mathf.Abs(h - bestHang) <= 0.05f && d < bestDist))
                    {
                        found = true; bestHang = h; bestDist = d; pos = snapped; rot = r;
                    }
                }
            }
            if (found) { hang = bestHang; distance = bestDist; return true; }
            why = $"{tried} spots tried: {obstructed} obstructed, {slope} too steep or uneven, {noGround} without ground";
            return false;
        }

        /// <summary>Highest minus lowest ground under the foundation anchors at a candidate pose (0 when none are defined).</summary>
        static float FoundationHang(List<Transform> anchors, Vector3 pos, Quaternion rot)
        {
            if (anchors == null || anchors.Count == 0) return 0f;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var a in anchors)
            {
                if (a == null) continue;
                Vector3 w = GameMath.TransformPointUnscaled(pos, rot, a.localPosition);
                if (!Physics.Raycast(w + Vector3.up * 100f, Vector3.down, out RaycastHit hit, 200f,
                                     GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC, QueryTriggerInteraction.Ignore)) continue;
                if (hit.point.y < lo) lo = hit.point.y;
                if (hit.point.y > hi) hi = hit.point.y;
            }
            return hi > lo ? hi - lo : 0f;
        }
    }
}
