// FrilRagdollFloorFix.cs
// Version: 1.8.4
// Standalone mod, do NOT merge into Area 7 or Hospital code.
// RELEASE BUILD. Per-entity diagnostic logging stripped; see 1.8.2-diag / 1.8.3-diag.
//
// Fixes zombie/entity getting up one block below the floor surface after knockdown.
//
// ROOT CAUSE (unchanged since v1.5.0, see DEVLOG_RagdollFloorFix.md)
// BlendRagdoll fires a downward raycast to find the floor and position the entity on
// get-up. If the pelvis has sunk below the floor during ragdoll (clip depths up to 0.22f
// observed), the raycast origin is already below the floor, so the ray hits the next
// surface DOWN and the entity is placed exactly one block too low. BlendRagdoll adjusts
// only X and Z of its working copy beforehand and leaves Y for the raycast to supply:
//     loc3.y = hit.point.y + 0.02   ->   PhysicsResume(loc3 + Origin.position, rotY)
// so the raycast result IS the block the zombie stands on. We are not changing how the
// zombie stands, only which surface it is told to stand on.
//
// WHAT THIS BUILD DOES: two IL edits inside EModelBase.BlendRagdoll
//   1. Insert RaiseOrigin() immediately after the origin is pushed for the raycast,
//      adding 0.5f to the value ON THE STACK, and ONLY on the first cast of a get-up.
//   2. Change the raycast distance constant from 3.0f to 3.5f.
// TFP's local is never written to and ragdollPosePelvisPos is never touched.
//
// WHY THE FIRST CAST ONLY (the v1.8.3 fix, and the whole story of 1.8.0 - 1.8.2)
// Vanilla starts the ray INSIDE the pelvis collider, and Unity does not report a hit on
// a collider the ray starts inside, so vanilla rarely self-hits. Raising the origin puts
// it outside the body, so the ray self-hits nearly every time. TFP handle that with a
// step-down, loc3.y = hit.point.y - 0.01, then recast, up to 5 attempts.
// v1.8.0 - v1.8.1 raised the origin at the point of USE, so every retry started from
// (hit - 0.01) + raise and landed on the same bone again. Diagnostic logging proved it:
// 9 get-ups, 45 hits, exactly 5 each, every one on the zombie's own Spine2 or Hips, hitY
// identical across all five, never once reaching the floor. The loop exhausted and the
// entity was placed on its own spine, roughly a metre up, then dropped. That is also why
// 1.2f and 0.5f behaved identically: the step-down is 0.01, so any raise above about
// 0.05 defeats it just as completely. It was never a tuning problem.
// Raising only the first cast lets the step-down walk the ray down through spine, hips
// and out to real geometry exactly as vanilla does. Confirmed in game: chains now end on
// CLayer01/opaqueCollider or terrainCollider, at floor level.
//
// The 5-attempt limit is left at TFP's value deliberately. One observed get-up used all
// five before reaching the floor, so the margin is thin, but matching vanilla is the
// right default and raising it is a change to game behaviour rather than a fix.
//
// Why the distance also changes: the ray length is a fixed 3.0f. Raising the origin
// without touching it would cut the downward reach below the original pelvis and CAUSE
// misses where the floor is furthest away. 3.5f restores the identical downward span.
//
// Why there is no restore helper: the visual lerp later in the same method reads the
// FIELD while the raycast reads a LOCAL. Because this never touches the field, the
// single-frame visual pop that forced v1.6.0 and v1.6.1 to be abandoned cannot occur.
//
// VERSION HISTORY (full account in the devlog)
// v1.0.0 - v1.4.0: Postfix on FrameUpdateRagdoll. ABANDONED at v1.5.0, wrong problem.
// v1.5.0 - v1.6.1: Prefix/Postfix on BlendRagdoll. Visual pop, unfixable that way.
// v1.7.0:  Transpiler around the Physics.Raycast call. Correct idea, wrong injection
//          point for the 3.x IL, where the origin is copied to a local 40 instructions
//          earlier. Did nothing at all on V3.1.0.
// v1.8.0:  Raise on the stack at the point of use, ray 3.0 -> 4.2. Fixed the floor clips
//          but caused a high-stand regression.
// v1.8.1:  Raise 1.2 -> 0.5. No change to the rate, which disproved the tuning theory.
// v1.8.2-diag / v1.8.3-diag: instrumented builds that found and fixed the real cause.
// v1.8.4:  Release. First-cast-only raise, logging stripped.

using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

public class FrilRagdollFloorFix : IModApi
{
    public const string ModVersion = "1.8.4";

    // Only has to clear the FLOOR, not the body. Max observed clip depth is 0.22f.
    private const float PelvisRaiseAmount = 0.5f;

    private const float VanillaRayDistance = 3.0f;
    private const float PatchedRayDistance = VanillaRayDistance + PelvisRaiseAmount;

    public void InitMod(Mod _modInstance)
    {
        UnityEngine.Debug.Log("[RagdollFix] v" + ModVersion + " loaded");
        new Harmony("com.fril.ragdollfloorfix").PatchAll();
    }

    // Injected immediately after the origin is pushed for the raycast.
    // Raises the stack value only, and only on the first cast of a get-up.
    public static Vector3 RaiseOrigin(Vector3 origin, EModelBase instance)
    {
        if (instance == null) return origin;

        // On the first cast loc3.y is still exactly ragdollPosePelvisPos.y, because
        // BlendRagdoll adjusts only X and Z beforehand. On a retry it holds
        // hit.point.y - 0.01. Raising a retry would re-land the ray on the same bone and
        // defeat TFP's step-down, which is precisely the v1.8.0 - v1.8.1 bug.
        if (Mathf.Abs(origin.y - instance.ragdollPosePelvisPos.y) >= 0.0001f)
            return origin;

        origin.y += PelvisRaiseAmount;
        return origin;
    }

    [HarmonyPatch(typeof(EModelBase), "BlendRagdoll")]
    public class Patch_BlendRagdoll
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);

            MethodInfo raycastMethod = null;
            foreach (var m in typeof(Physics).GetMethods())
            {
                if (m.Name != "Raycast") continue;
                var p = m.GetParameters();
                if (p.Length == 5 &&
                    p[0].ParameterType == typeof(Vector3) &&
                    p[1].ParameterType == typeof(Vector3) &&
                    p[2].ParameterType == typeof(RaycastHit).MakeByRefType() &&
                    p[3].ParameterType == typeof(float) &&
                    p[4].ParameterType == typeof(int))
                { raycastMethod = m; break; }
            }
            if (raycastMethod == null)
            {
                UnityEngine.Debug.LogError("[RagdollFix] Transpiler ABORTED: could not resolve Physics.Raycast(Vector3,Vector3,out RaycastHit,float,int). Mod is doing NOTHING.");
                return codes;
            }

            MethodInfo downGetter = typeof(Vector3).GetProperty("down", BindingFlags.Public | BindingFlags.Static).GetGetMethod();
            MethodInfo raiseMethod = typeof(FrilRagdollFloorFix).GetMethod("RaiseOrigin", BindingFlags.Public | BindingFlags.Static);

            int raycastIndex = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call && (codes[i].operand as MethodInfo) == raycastMethod)
                { raycastIndex = i; break; }
            }
            if (raycastIndex == -1)
            {
                UnityEngine.Debug.LogError("[RagdollFix] Transpiler ABORTED: no Physics.Raycast call found in BlendRagdoll. Mod is doing NOTHING.");
                return codes;
            }

            // The direction argument is Vector3.down, pushed immediately after the origin.
            // Finding it is how we locate the end of the origin load without assuming a
            // particular local index or instruction offset.
            int downIndex = -1;
            for (int i = raycastIndex - 1; i >= 0 && i > raycastIndex - 12; i--)
            {
                if (codes[i].opcode == OpCodes.Call && (codes[i].operand as MethodInfo) == downGetter)
                { downIndex = i; break; }
            }
            if (downIndex == -1)
            {
                UnityEngine.Debug.LogError("[RagdollFix] Transpiler ABORTED: could not locate the Vector3.down argument before the raycast. Mod is doing NOTHING.");
                return codes;
            }

            // The distance is the only ldc.r4 between the direction and the call.
            int distIndex = -1;
            for (int i = downIndex + 1; i < raycastIndex; i++)
            {
                if (codes[i].opcode == OpCodes.Ldc_R4) { distIndex = i; break; }
            }
            if (distIndex == -1)
            {
                UnityEngine.Debug.LogError("[RagdollFix] Transpiler ABORTED: could not locate the raycast distance constant. Mod is doing NOTHING.");
                return codes;
            }
            float foundDistance = (float)codes[distIndex].operand;
            codes[distIndex].operand = PatchedRayDistance;

            // Inserting AT downIndex places our call after the origin load and before
            // Vector3.down is pushed. The origin load is the retry loop's branch target,
            // so it must not move; inserting after it leaves its label attached.
            codes.InsertRange(downIndex, new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, raiseMethod)
            });

            UnityEngine.Debug.Log(string.Format(
                "[RagdollFix] Transpiler OK: first-cast origin raise {0:F2}, ray distance {1:F2} -> {2:F2}",
                PelvisRaiseAmount, foundDistance, PatchedRayDistance));
            return codes;
        }
    }
}
