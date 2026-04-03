// FrilRagdollFloorFix.cs
// Version: 1.3.3
// Standalone mod — do NOT merge into Area 7 or Hospital code.
//
// Fixes zombie/entity lower-body clipping through floor surfaces during ragdoll.
// Root cause: pelvisRB (the Rigidbody driving entity position during IsRagdollMovement)
// can tunnel through floor geometry.
//
// Fix: Harmony Postfix on EModelBase.FrameUpdateRagdoll. Fires every frame on every
// entity already in ragdoll — no entity list iteration needed. Raycasts downward from
// above the pelvis using TFP's own layer mask (-538750981) and the same self-hit-aware
// loop from EModelBase.BlendRagdoll. Corrects pelvisRB.position if below floor surface.
//
// v1.3.0: Reject raycast hits above the pelvis position (ceiling/overhang geometry).
//         Previously caused entities to launch upward when under a ledge or platform.
// v1.3.1: Removed IsDead() early-exit. Death ragdoll is the primary case that needs
//         correction — entity.IsDead() is true during that state, so the previous check
//         was bailing out exactly when the fix was needed.
// v1.3.2: Clear _lastLogTime dictionary on GameStartDone to prevent unbounded growth
//         over long sessions with many zombie kills.
// v1.3.3: Removed all debug logging. Removed _lastLogTime, LogCooldown, ClearLogTimes,
//         and GameStartDone handler — all were only needed to support logging.

using HarmonyLib;
using UnityEngine;

public class FrilRagdollFloorFix : IModApi
{
    // Layer mask confirmed from EModelBase.BlendRagdoll decompile
    private const int FloorLayerMask = -538750981;

    // Start raycast this far above the pelvis to avoid starting inside geometry
    private const float CastRaise = 1.5f;

    // Total downward cast distance from the raised origin
    private const float CastDistance = 3.0f;

    // Only correct if pelvis is at or below floor surface (small tolerance)
    private const float ClipThreshold = 0.02f;

    // Place the corrected pelvis this far above the floor surface
    private const float CorrectOffset = 0.08f;

    public void InitMod(Mod _modInstance)
    {
        new Harmony("com.fril.ragdollfloorfix").PatchAll();
    }

    [HarmonyPatch(typeof(EModelBase), "FrameUpdateRagdoll")]
    public class Patch_FrameUpdateRagdoll
    {
        public static void Postfix(EModelBase __instance)
        {
            // Only act when the ragdoll is actively driving entity position
            if (!__instance.IsRagdollMovement)
                return;

            Rigidbody pelvisRB = __instance.pelvisRB;
            if (pelvisRB == null)
                return;

            Entity entity = __instance.entity;
            if (entity == null)
                return;

            Vector3 pelvisPos = pelvisRB.position;

            // Cast downward from above the pelvis.
            // Loop mirrors TFP's BlendRagdoll self-hit handling exactly:
            // if we hit our own entity (RootTransformRefEntity matches), step past it.
            Vector3 castOrigin = pelvisPos;
            castOrigin.y += CastRaise;

            bool foundFloor = false;
            float floorY = 0f;
            int attempts = 0;

            RaycastHit hit;
            while (attempts < 5 && Physics.Raycast(castOrigin, Vector3.down, out hit, CastDistance, FloorLayerMask))
            {
                // Reject hits above the pelvis — that is ceiling or overhang geometry,
                // not the floor. Without this check, zombies under ledges get launched upward.
                if (hit.point.y > pelvisPos.y)
                    break;

                RootTransformRefEntity rtRef = hit.transform.GetComponent<RootTransformRefEntity>();
                if (rtRef == null || rtRef.RootTransform != entity.transform)
                {
                    floorY = hit.point.y;
                    foundFloor = true;
                    break;
                }

                // Self-hit — step cast origin past it and retry
                castOrigin.y = hit.point.y - 0.01f;
                attempts++;
            }

            if (!foundFloor)
                return;

            // Pelvis is at or below the floor surface — correct it
            if (pelvisPos.y >= floorY + ClipThreshold)
                return;

            pelvisPos.y = floorY + CorrectOffset;
            pelvisRB.position = pelvisPos;

            // Kill downward velocity so physics doesn't immediately re-clip it
            Vector3 vel = pelvisRB.velocity;
            if (vel.y < 0f)
            {
                vel.y = 0f;
                pelvisRB.velocity = vel;
            }
        }
    }
}