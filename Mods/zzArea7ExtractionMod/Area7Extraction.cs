// File: Area7ExtractionMod.cs
// Area 7 Extraction Mod - Version 1.4.8
// Author: Frilioth
// Requires: FrilArea7Challenge mod, BDubs Vehicles mod (vehicleUH60)
//
// Hooks into the escapeArea7 challenge redeem to trigger a UH-60 extraction
// sequence. The chopper flies in, lands near the player, and departs with
// the player aboard. Load order: after FrilArea7Challenge (use zzz_ prefix).

using System;
using System.Collections;
using Challenges;
using HarmonyLib;
using UnityEngine;

public class Area7ExtractionMod : IModApi
{
    public const string ModVersion = "1.4.8";

    public void InitMod(Mod _modInstance)
    {
        UnityEngine.Debug.Log("[Area7Extraction] Mod initializing v" + ModVersion);
        var harmony = new Harmony("com.frilioth.area7extraction");
        harmony.PatchAll();
        UnityEngine.Debug.Log("[Area7Extraction] Patches applied.");
    }
}

// ---------------------------------------------------------------
// EXTRACTION SEQUENCE CONTROLLER
// ---------------------------------------------------------------
public static class Area7ExtractionSequence
{
    private static bool extractionStarted = false;
    private static EntityVehicle uh60Entity = null;
    // Parallel to crewMembers: the slot each one actually ended up in. The seated pose is
    // per-SEAT (Vehicle.GetSeatPose), and a failed attach used to leave an entity in
    // crewMembers at an index that no longer matched any seat.
    private static readonly System.Collections.Generic.List<int> crewSlots =
        new System.Collections.Generic.List<int>();

    // Vertical nudge applied to each crew member's MODEL after attaching, in metres.
    // The crew stand rather than sit (see the SEATED POSE note below), which leaves their feet
    // poking through the cockpit floor. Entity.AttachToEntity sets
    //     ModelTransform.localPosition = info.enterPosition
    // and that line is the ONLY write to ModelTransform.localPosition anywhere in
    // Assembly-CSharp, so a one-shot offset applied straight after the attach stays put. No
    // per-frame correction, nothing to fight.
    // THIS IS THE NUMBER TO TUNE if they sit too high or too low.
    // 0.35f rode too high on test (boots hanging below the fuselage). 0.2f at Fril's call.
    private const float CREW_MODEL_Y_OFFSET = 0.2f;

    private static readonly System.Collections.Generic.List<EntityAlive> crewMembers =
        new System.Collections.Generic.List<EntityAlive>();

    private const string UH60_CLASS = "vehicleUH60";
    // v1.4.6: was "buffArea7Victory", which is not defined in FrilArea7Challenge/Config/buffs.xml
    // and never has been. AddBuff on an unknown name fails SILENTLY, with nothing in the log, so
    // "Step 1: Protect the player" has been doing nothing at all. The real buff now exists.
    private const string INVULN_BUFF = "buffArea7ExtractionShield";
    private const float SPAWN_ALTITUDE = 150f;
    private const float APPROACH_SPEED = 18f;
    private const float DESCENT_SPEED = 8f;
    private const float LAND_THRESHOLD = 4f;

    // Fly-in: start this far out horizontally, this high above Hugh's ground level.
    // 520m at APPROACH_SPEED 18 m/s is about a 29s run-in (was 250m / ~14s). Lengthened
    // 6 Aug at Fril's request so the player has time to get outside and watch it come in.
    private const float APPROACH_DISTANCE = 520f;
    private const float APPROACH_HEIGHT   = 100f;
    private const float ARRIVE_RADIUS     = 6f;    // close enough to stop and hover
    private const float APPROACH_TIMEOUT  = 60f;   // never let the fly-in stall the extraction (raised with APPROACH_DISTANCE)

    // Departure climb ceiling. Raised from 120 to 150 - at 120 the chopper clipped
    // mountains on some outbound headings.
    private const float DEPART_CEILING = 150f;

    // Cockpit crew. Attached to seats 0 and 1 so the player boards into seat 2 as a
    // passenger instead of becoming the pilot.
    // npcSurvivorRanged is a stock vanilla human NPC (player mesh, whiteriver faction,
    // carries a pistol). If the crew climb out mid-flight, the fix is a custom passive
    // class with the AI packages stripped - change this one constant to point at it.
    // Tried in order; the first one actually REGISTERED in this install is used.
    // npcSurvivorRanged is defined in vanilla entityclasses.xml but is NOT registered at
    // runtime in 3.0.1 (EntityFactory rejected it with "unknown type"), so the trader
    // classes are here as proven fallbacks - Hugh is an npcTrader and spawns fine every run.
    // Hugh himself is deliberately excluded: he is standing at the camp.
    private static readonly string[] PILOT_CLASS_CANDIDATES =
    {
        "npcSurvivorRanged",
        "npcSurvivorTemplate",
        "npcTraderJoel",
        "npcTraderBob",
        "npcTraderJen",
        "npcTraderRekt",
    };
    private const int PILOT_COUNT = 2;

    // NOTE (v1.4.0): CREW_SEAT_OFFSETS used to live here, hardcoded out of the UH-60's
    // vehicles.xml because Vehicle exposes no runtime seat-position getter. It is gone.
    // EntityVehicle.GetAttachedToInfo(slot) already reads position/rotation straight off the
    // vehicle's own seat<N> DynamicProperties and Entity.AttachToEntity applies them, so the
    // engine now does this for us. See the long comment in SeatCrew below.

    // True only while the chopper is still a live, usable object. On shutdown Unity DESTROYS
    // the GameObject while this coroutine is still mid-flight, and touching `.transform` on a
    // destroyed Component throws a NullReferenceException — which is exactly what Fril saw when
    // he quit during an extraction. Unity's == null override reports destroyed objects as null,
    // so this catches it; the transform is then checked directly for belt and braces.
    private static bool VehicleUsable(EntityVehicle uh60)
    {
        if (uh60 == null) return false;
        try
        {
            if (uh60.IsDead()) return false;
            return uh60.transform != null;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------
    // COMPLETION TOOLTIPS
    //
    // GameManager.ShowTooltip has two relevant overloads. The one this mod used to call,
    //     ShowTooltip(player, text, string _arg, alertSound, handler, showImmediately, pinTooltip, timeout)
    // forwards to the string[] overload but pushes a HARDCODED false in the pinTooltip
    // position, so the `true` we were passing was silently thrown away. Read from the IL.
    // Calling the string[] overload directly is the only way to actually pin anything.
    //
    // What pinning does, from XUiC_PopupToolTip: a pinned Tooltip is re-enqueued onto
    // tooltipQueue every time it is dequeued for display, so it cycles indefinitely, and
    // XUiC_PopupToolTip.Update re-displays whenever the current tip has faded out. Two
    // pinned tips therefore alternate on their own with no timer of ours. GameManager
    // .RemovePinnedTooltip(player, key) flags one RemoveOnDequeue so it drops out on its
    // next cycle; `key` is the RAW string passed in, before localisation.
    //
    // NOTE Tooltip.Equals compares TEXT, and QueueTooltip skips enqueuing anything the
    // queue already Contains. Two pinned messages must therefore differ in wording, and
    // the old unpinned copy of the completion line had to go rather than sit alongside
    // this one, or the pinned version would have been dropped as a duplicate.
    // ---------------------------------------------------------------
    private const string CompletionTipText =
        "MISSION COMPLETE — You escaped Area 7. Well done, survivor.";

    // Fallback only. The real path is read from the challenge mod at runtime.
    private const string StatsPathFallback =
        @"Steam\steamapps\common\7 Days To Die\Mods\FrilArea7Challenge\stats\Area7_Debrief.html";

    private static string cachedStatsTip = null;

    // Area7ChallengeMod.GetModPath() is public static and the debrief is written to
    // <modpath>/stats/Area7_Debrief.html. Reached by reflection rather than a compile-time
    // reference so this mod keeps building and running on its own if the challenge mod is
    // absent or a different version.
    private static string StatsTipText()
    {
        if (cachedStatsTip != null) return cachedStatsTip;

        string path = null;
        try
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType("Area7ChallengeMod"); }
                catch { continue; }
                if (t == null) continue;

                System.Reflection.MethodInfo mi = t.GetMethod("GetModPath",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (mi == null) continue;

                string modPath = mi.Invoke(null, null) as string;
                if (string.IsNullOrEmpty(modPath)) continue;

                path = System.IO.Path.Combine(System.IO.Path.Combine(modPath, "stats"),
                                              "Area7_Debrief.html");
                break;
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area7Extraction] Could not read the stats path from the challenge mod, using the fallback: " + e.Message);
        }

        if (string.IsNullOrEmpty(path)) path = StatsPathFallback;

        // The full absolute path ran off both edges of the screen: the tooltip label does not
        // wrap. Fril's resolved to
        //   D:/Program Files (x86)/Steam/steamapps/common/7 Days To Die/7DaysToDie_Data/../Mods\...
        // so show only the tail, from the Mods folder down, and put the full normalised path in
        // the log for anyone who needs it.
        string shortPath = path;
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            UnityEngine.Debug.Log("[Area7Extraction] Debrief written to: " + full);

            int idx = full.LastIndexOf("Mods", StringComparison.OrdinalIgnoreCase);
            shortPath = idx >= 0 ? full.Substring(idx) : full;
        }
        catch { }

        cachedStatsTip = "Run stats: " + shortPath;
        return cachedStatsTip;
    }

    private static void ShowPinnedTip(EntityPlayerLocal player, string text)
    {
        if (player == null || string.IsNullOrEmpty(text)) return;
        try
        {
            // string[] overload, NOT the single-string one - see the note above.
            // _args is null so QueueTooltipInternal skips string.Format, which would
            // otherwise choke on any brace in a path.
            GameManager.ShowTooltip(player, text, (string[])null, null, null, false, true, 0f);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area7Extraction] ShowPinnedTip failed: " + e.Message);
        }
    }

    private static void ClearPinnedTips(EntityPlayerLocal player)
    {
        if (player == null) return;
        try { GameManager.RemovePinnedTooltip(player, CompletionTipText); } catch { }
        try { GameManager.RemovePinnedTooltip(player, StatsTipText()); } catch { }
    }

    // ---------------------------------------------------------------
    // SEATED POSE - SETTLED 24 Jul, THE CREW CANNOT SIT
    //
    // v1.4.3 added a ReassertCrewPose that re-applied SetVehiclePoseMode every frame. It is
    // gone again, because Fril's Player.log proved it can never work:
    //     Pose diag (after seating) slot 0 want=50 anim=0 mode=50 emodel=yes avatar=AvatarNpcController
    //     Pose diag (departure)     slot 0 want=50 anim=0 mode=50 emodel=yes avatar=AvatarNpcController
    // emodel and the avatar controller are both up, so this was never the timing race we
    // suspected. vehiclePoseMode stores 50 correctly. But GetVehicleAnimation, which is
    // literally anim.GetInteger(vehiclePoseHash) via TryGetInt, keeps reading back 0 after we
    // set it to 50 - and it read 0 at BOTH sample points, minutes apart, with the re-assert
    // running every frame in between. The trader animator simply does not honour that
    // parameter; there is no seated state on that rig. No amount of code fixes that, and
    // leaving the re-assert in meant hammering _setInt plus Crouching=false every single frame
    // for the whole flight, forever, because the condition could never become false.
    //
    // ACCEPTED WORKAROUND (Fril's own suggestion): leave them standing and raise the MODEL a
    // little so their feet stop poking through the cockpit floor. See CREW_MODEL_Y_OFFSET.
    // ---------------------------------------------------------------

    // Diagnostic, kept as a one-line record per run. This is what settled the standing crew:
    //   avatar=none / anim=-1  -> the avatar controller never came up. RULED OUT; both samples
    //                             reported emodel=yes and a live AvatarNpcController.
    //   anim=0 while want=50   -> CONFIRMED. The trader animator ignores the pose parameter.
    // Retained in case a game patch or a different NPC rig ever changes that.
    private static void LogPoseDiag(string when, EntityVehicle uh60)
    {
        try
        {
            if (uh60 == null || uh60.vehicle == null) return;
            for (int i = 0; i < crewMembers.Count && i < crewSlots.Count; i++)
            {
                EntityAlive crew = crewMembers[i];
                if (crew == null) continue;

                string avatar = "none";
                if (crew.emodel != null && crew.emodel.avatarController != null)
                    avatar = crew.emodel.avatarController.GetType().Name;

                UnityEngine.Debug.Log("[Area7Extraction] Pose diag (" + when + ") slot "
                                      + crewSlots[i]
                                      + " want=" + uh60.vehicle.GetSeatPose(crewSlots[i])
                                      + " anim=" + crew.GetVehicleAnimation()
                                      + " mode=" + crew.vehiclePoseMode
                                      + " emodel=" + (crew.emodel != null ? "yes" : "no")
                                      + " avatar=" + avatar);
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area7Extraction] Pose diag failed: " + e.Message);
        }
    }

    // EntityClass.FromString is JUST name.GetHashCode() - it never returns -1 and never
    // validates. EntityClass.GetEntityClass(id) does list.TryGetValue and returns null for
    // an unknown id, so that is the only reliable "does this class exist" test.
    private static int ResolveEntityClass(string name)
    {
        int id = EntityClass.FromString(name);
        return EntityClass.GetEntityClass(id) != null ? id : -1;
    }

    // Landing spots matched to Hugh spawn locations (same index as Area7.cs hughSpawnLocations)
    private static readonly Vector3[] landingSpots = new Vector3[]
    {
        new Vector3(-442f, 150f,  973f),
        new Vector3( 838f, 150f, 1275f),
        new Vector3(-945f, 150f,   32f),
        new Vector3( 751f, 150f, -355f),
        new Vector3(-636f, 150f,-1392f),
        new Vector3( 770f, 150f, -920f),
    };

    // Outbound yaw per landing spot
    private static readonly float[] landingYaws = new float[]
    {
          90f,  // E
          90f,  // E
        180f,  // S
        270f,  // W
        315f,  // NW
          0f,  // N
    };

    // Hugh spawn locations from Area7.cs (same order, used to find closest landing spot)
    private static readonly Vector3[] hughSpawns = new Vector3[]
    {
        new Vector3(-449.5f, 42f,  1000.5f),
        new Vector3( 870.5f, 56f,  1305.5f),
        new Vector3(-941f,   43f,    -15f),
        new Vector3( 738f,   57f,   -317f),
        new Vector3(-632f,   53f,  -1359f),
        new Vector3( 744f,   49f,   -930f),
    };

    // A driverless vehicle whose velocity drops below a threshold accumulates
    // RBNoDriverSleepTime until RBActive flips false, at which point the game makes the
    // rigidbody KINEMATIC - and a kinematic body ignores every velocity assignment.
    // That is almost certainly why the original fly-in attempt never moved. Calling this
    // each FixedUpdate keeps the chopper simulated for the whole flight.
    // NOTE: deliberately does NOT touch hasDriver. Spawn sets it true so the rotors spin,
    // but EntityVehicle.HasDriver simply returns that field and the boarding wait checks it,
    // so forcing it true here would make the sequence think the player had already boarded.
    private static void KeepFlying(EntityVehicle uh60)
    {
        if (uh60 == null) return;

        uh60.RBActive = true;
        uh60.RBNoDriverSleepTime = 0f;
        uh60.RBNoDriverGndTime = 0f;

        if (uh60.vehicleRB != null && uh60.vehicleRB.isKinematic)
            uh60.vehicleRB.isKinematic = false;

        // Rotors: motors use trigger="relative" so rpm only builds with player input.
        // Setting rpm directly each frame is the only way to spin them with no driver.
        if (uh60.motors != null)
            for (int i = 0; i < uh60.motors.Length; i++)
                uh60.motors[i].rpm = uh60.motors[i].rpmMax;

        // Pose only, never position. See the SEATED POSE note above for why this is free
        // once the pose has taken, and why it is not a return to HoldCrewInSeats.
    }

    // Spawn the cockpit crew and attach them to seats 0 and 1.
    // Both halves of the attach path are typed against EntityAlive rather than EntityPlayer
    // (AttachToEntity, AttachEntityToSelf and SetVehiclePoseMode alike), so an NPC is seated
    // exactly the way a player is. Entirely non-fatal: if any of this fails the extraction
    // carries on with an empty cockpit.
    private static void SeatCrew(World world, EntityVehicle uh60, Vector3 nearPos)
    {
        try
        {
            // v1.4.1: resolve EVERY candidate that is actually registered, not just the first,
            // so each seat can get a DIFFERENT trader. Two identical Joels read as a glitch.
            // Order of PILOT_CLASS_CANDIDATES decides who sits where: index 0 is the pilot in
            // seat 0, index 1 the co-pilot in seat 1. Reorder that array to change the pairing.
            var usableIds = new System.Collections.Generic.List<int>();
            var usableNames = new System.Collections.Generic.List<string>();
            for (int i = 0; i < PILOT_CLASS_CANDIDATES.Length; i++)
            {
                int id = ResolveEntityClass(PILOT_CLASS_CANDIDATES[i]);
                if (id == -1)
                {
                    UnityEngine.Debug.Log("[Area7Extraction] Pilot class '" + PILOT_CLASS_CANDIDATES[i] + "' not registered, skipping.");
                    continue;
                }
                if (usableIds.Contains(id)) continue;   // guard against duplicate names in the array
                usableIds.Add(id);
                usableNames.Add(PILOT_CLASS_CANDIDATES[i]);
                if (usableIds.Count >= PILOT_COUNT) break;
            }

            if (usableIds.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[Area7Extraction] No usable pilot class found - flying empty.");
                return;
            }

            // If only one class registers we wrap and reuse it, which is exactly the pre-1.4.1
            // behaviour - two of the same. No regression, just no variety.
            if (usableIds.Count < PILOT_COUNT)
                UnityEngine.Debug.LogWarning("[Area7Extraction] Only " + usableIds.Count
                                             + " pilot class(es) registered; seats will reuse them.");

            UnityEngine.Debug.Log("[Area7Extraction] Pilot classes: " + string.Join(", ", usableNames.ToArray()));

            for (int slot = 0; slot < PILOT_COUNT; slot++)
            {
                int classId = usableIds[slot % usableIds.Count];
                string chosen = usableNames[slot % usableNames.Count];

                Entity spawned = EntityFactory.CreateEntity(classId, nearPos);
                if (spawned == null)
                {
                    UnityEngine.Debug.LogWarning("[Area7Extraction] CreateEntity returned null for slot " + slot + ".");
                    continue;
                }

                world.SpawnEntityInWorld(spawned);

                EntityAlive crew = spawned as EntityAlive;
                if (crew == null)
                {
                    UnityEngine.Debug.LogWarning("[Area7Extraction] Pilot is not an EntityAlive - skipping.");
                    continue;
                }

                crew.Health = 9999;

                // THE v1.4.0 FIX. This used to be uh60.AttachEntityToSelf(crew, slot), which
                // is only HALF of the engine's attach path. That half sets the seated pose, the
                // IK targets, layer 24 and disables the character controller, but it positions
                // nothing and never sets crew.AttachedToEntity.
                //
                // Entity.AttachToEntity(other, slot) is the other half, and it is the call the
                // game itself uses - EntityVehicle.UpdateAttachment does e.AttachToEntity(this,
                // slot) for its own delayed attachments. It invokes other.AttachEntityToSelf
                // internally, then does the positioning we had been doing by hand:
                //     RootTransform.SetParent(info.enterParentTransform, false)  // vehicle transform
                //     RootTransform.localPosition / localEulerAngles = zero
                //     ModelTransform.localPosition    = info.enterPosition       // the seat<N> offset
                //     ModelTransform.localEulerAngles = info.enterRotation
                //     this.AttachedToEntity = other
                //
                // The payoff is that last line. Entity.updateTransform opens with
                //     if (this.AttachedToEntity != null) return;
                // and ONLY AttachToEntity sets that field. Attached this way the crew stop
                // driving their own transform altogether and simply ride the airframe as a
                // child object, so no per-frame correction is needed. That is why
                // HoldCrewInSeats, CREW_SEAT_OFFSETS and both crew-freeze Harmony patches
                // are deleted in v1.4.0.
                //
                // EntityAlive.AttachToEntity also sets CurrentMovementTag to idle and clears
                // Crouching. Its inventory-swap branch is gated on (other is EntityAlive) and
                // EntityVehicle is not one, so there are no inventory side effects here.
                int actualSlot = crew.AttachToEntity(uh60, slot);
                if (actualSlot < 0)
                {
                    UnityEngine.Debug.LogWarning("[Area7Extraction] AttachToEntity refused slot "
                                                 + slot + " for entity " + crew.entityId + ".");
                    continue;
                }

                UnityEngine.Debug.Log("[Area7Extraction] Crew seated in slot " + actualSlot
                                      + " as '" + chosen + "' (entity " + crew.entityId + ")");

                // Tracked only AFTER a successful attach, so crewMembers[i] and crewSlots[i]
                // always describe an entity that really is in a seat.
                crewMembers.Add(crew);
                crewSlots.Add(actualSlot);

                // Raise the model so the feet are not through the floor. Applied AFTER the
                // attach, because AttachToEntity is what sets localPosition in the first place.
                try
                {
                    Transform mt = crew.ModelTransform;
                    if (mt != null)
                    {
                        Vector3 lp = mt.localPosition;
                        mt.localPosition = new Vector3(lp.x, lp.y + CREW_MODEL_Y_OFFSET, lp.z);
                        UnityEngine.Debug.Log("[Area7Extraction] Crew model raised " + CREW_MODEL_Y_OFFSET
                                              + "m: " + lp + " -> " + mt.localPosition);
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[Area7Extraction] Could not raise crew model: " + ex.Message);
                }
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area7Extraction] Seating crew failed (non-fatal): " + e.Message);
        }
    }

    public static void Reset()
    {
        extractionStarted = false;
        uh60Entity = null;
        crewMembers.Clear();
        crewSlots.Clear();

        // A pinned tooltip lives in the UI, not in this class, so a sequence that ended
        // abnormally could otherwise leave one cycling into the next session.
        try
        {
            EntityPlayerLocal p = GameManager.Instance != null && GameManager.Instance.World != null
                                ? GameManager.Instance.World.GetPrimaryPlayer() : null;
            if (p != null) ClearPinnedTips(p);
        }
        catch { }
    }

    public static void TriggerExtraction(EntityPlayerLocal player)
    {
        if (extractionStarted) return;
        extractionStarted = true;

        UnityEngine.Debug.Log("[Area7Extraction] Extraction sequence triggered.");

        GameManager.Instance.StartCoroutine(ExtractionCoroutine(player));
    }

    private static IEnumerator ExtractionCoroutine(EntityPlayerLocal player)
    {
        World world = GameManager.Instance.World;
        if (world == null || player == null) yield break;

        // --- Step 1: Protect the player ---
        try
        {
            if (player.Buffs != null && !player.Buffs.HasBuff(INVULN_BUFF))
                player.Buffs.AddBuff(INVULN_BUFF);

            // Verify rather than assume. The previous version of this call referenced a buff
            // that did not exist and failed without a single line in the log, which is why it
            // went unnoticed. If the buff ever goes missing again, this will say so.
            if (player.Buffs != null && player.Buffs.HasBuff(INVULN_BUFF))
                UnityEngine.Debug.Log("[Area7Extraction] Protection buff '" + INVULN_BUFF + "' applied.");
            else
                UnityEngine.Debug.LogWarning("[Area7Extraction] Protection buff '" + INVULN_BUFF
                                             + "' did NOT apply - is it defined in FrilArea7Challenge/Config/buffs.xml?");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area7Extraction] Protection buff failed: " + e.Message);
        }

        // --- Step 2: Kill all zombies ---
        try { SdtdConsole.Instance.ExecuteSync("killall", null); } catch { }

        // --- Step 3: Radio comms message ---
        GameManager.ShowTooltip(player,
            "NORTHERN AREA COMMAND: Extraction inbound. Hold position.",
            (string)null, null, null, false, true, 12f);


        // --- Step 4: Find landing spot closest to player's current position ---
        // (Hugh is already despawned by Area7 cleanup by this point)
        int landingIndex = 0;
        float closestDist = float.MaxValue;
        for (int i = 0; i < hughSpawns.Length; i++)
        {
            float dist = Vector3.Distance(player.position, hughSpawns[i]);
            if (dist < closestDist) { closestDist = dist; landingIndex = i; }
        }

        Vector3 landingPos = landingSpots[landingIndex];
        float outboundYaw = landingYaws[landingIndex];

        // landingSpots carries a placeholder Y of 150; hughSpawns holds the real ground
        // heights, so take the approach altitude from there.
        float groundY = hughSpawns[landingIndex].y;
        float approachAltitude = groundY + APPROACH_HEIGHT;

        // Spawn directly over the landing spot at approach altitude. The chopper is then
        // stepped back along its OWN forward vector below, so the fly-in direction always
        // matches where the model is actually pointing.
        Vector3 hoverTarget = new Vector3(landingPos.x, approachAltitude, landingPos.z);
        Vector3 spawnPos = hoverTarget;

        // --- Step 5: Spawn UH-60 ---
        EntityVehicle uh60 = null;
        try
        {
            int classId = ResolveEntityClass(UH60_CLASS);
            if (classId == -1)
            {
                UnityEngine.Debug.LogError("[Area7Extraction] vehicleUH60 entity class not found! Is BDubs Vehicles installed?");
                yield break;
            }

            Entity spawned = EntityFactory.CreateEntity(classId, spawnPos);
            world.SpawnEntityInWorld(spawned);
            uh60 = spawned as EntityVehicle;

            if (uh60 == null)
            {
                UnityEngine.Debug.LogError("[Area7Extraction] Spawned entity is not EntityVehicle!");
                yield break;
            }

            uh60Entity = uh60;

            // Face outbound direction
            uh60.rotation = new Vector3(0f, outboundYaw, 0f);

            // Set hasDriver so rotors spin
            uh60.hasDriver = true;

            // Make the chopper its OWN chunk observer for the unmanned fly-in.
            // Entity.Update does: if (bIsChunkObserver) { create movableChunkObserver if null;
            // observer.SetPosition(this.position); } — so simply setting this flag keeps chunks
            // loaded around the chopper as it flies, and the observer is disposed automatically
            // when the flag clears or the entity unloads.
            // Without it the chopper only simulates while inside the PLAYER's loaded chunks:
            // a 100m run-in worked, 250m stopped it dead. (The departure flies ~800m fine only
            // because the player is aboard by then, so the player's own observer travels with it.)
            uh60.bIsChunkObserver = true;

            // Wake up physics
            uh60.RBActive = true;
            uh60.IsEngineRunning = true;
            if (uh60.vehicleRB != null)
            {
                uh60.vehicleRB.isKinematic = false;
                uh60.vehicleRB.WakeUp();
            }

            // Max health so it survives any collisions
            uh60.Health = 9999;

            // Fuel it
            uh60.vehicle.AddFuel(uh60.vehicle.GetMaxFuelLevel());

            // Fire engine start sound
            uh60.vehicle.FireEvent(Vehicle.Event.Start);

            UnityEngine.Debug.Log("[Area7Extraction] UH-60 spawned at " + spawnPos + " landing target " + landingPos);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area7Extraction] Failed to spawn UH-60: " + e.Message);
            yield break;
        }

        // --- Step 5b: Fly in from APPROACH_DISTANCE out at approach altitude ---
        // Step the chopper back along its own forward vector, then fly it in. Using
        // transform.forward (not yaw maths) because the model's forward does not match
        // Unity's Z axis - the departure flight already relies on this.
        Vector3 approachDir = Vector3.forward;
        try
        {
            approachDir = uh60.transform.forward;
            approachDir.y = 0f;
            if (approachDir.sqrMagnitude < 0.01f) approachDir = Vector3.forward;
            approachDir.Normalize();

            Vector3 startPos = hoverTarget - approachDir * APPROACH_DISTANCE;
            uh60.SetPosition(startPos, true);

            UnityEngine.Debug.Log("[Area7Extraction] Fly-in from " + startPos
                                  + " to hover " + hoverTarget
                                  + " (ground " + groundY + ")");

            // Crew go in AFTER the reposition so they are spawned at the chopper's actual
            // start point - no chance of them being left behind at the hover position.
            SeatCrew(world, uh60, startPos);
            LogPoseDiag("after seating", uh60);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area7Extraction] Fly-in setup failed, descending in place: " + e.Message);
        }

        float approachElapsed = 0f;
        while (approachElapsed < APPROACH_TIMEOUT)
        {
            yield return new WaitForFixedUpdate();
            approachElapsed += Time.fixedDeltaTime;

            if (!VehicleUsable(uh60)) yield break;
            if (uh60.vehicleRB == null) yield break;

            KeepFlying(uh60);

            Vector3 toTarget = hoverTarget - uh60.position;
            Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);

            if (flat.magnitude <= ARRIVE_RADIUS) break;

            // Drive horizontally, correcting altitude gently so it holds its height.
            Vector3 vel = flat.normalized * APPROACH_SPEED;
            vel.y = Mathf.Clamp(toTarget.y, -DESCENT_SPEED, DESCENT_SPEED);
            uh60.vehicleRB.velocity = vel;
        }

        // Settle into a brief hover so the descent starts from a stable position.
        if (uh60 == null || uh60.IsDead()) yield break;
        UnityEngine.Debug.Log("[Area7Extraction] Arrived overhead, hovering before descent.");

        float hoverElapsed = 0f;
        while (hoverElapsed < 1.5f)
        {
            yield return new WaitForFixedUpdate();
            hoverElapsed += Time.fixedDeltaTime;

            if (!VehicleUsable(uh60)) yield break;
            if (uh60.vehicleRB == null) yield break;

            KeepFlying(uh60);
            uh60.vehicleRB.velocity = Vector3.zero;
            uh60.vehicleRB.angularVelocity = Vector3.zero;
        }

        // --- Step 6: Descend — push down at full speed, physics stops it on ground ---
        float timeout = 60f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            yield return new WaitForFixedUpdate();
            elapsed += Time.fixedDeltaTime;

            if (!VehicleUsable(uh60)) yield break;
            if (uh60.vehicleRB == null) yield break;

            // Rotors + keep the rigidbody simulated (see KeepFlying)
            KeepFlying(uh60);

            // Stop descending once velocity is killed by ground contact
            if (elapsed > 1f && uh60.vehicleRB.velocity.magnitude < 1f) break;

            uh60.vehicleRB.velocity = Vector3.down * DESCENT_SPEED;
        }

        // --- Step 7: Landed - hold position ---
        if (uh60 == null || uh60.IsDead()) yield break;

        try
        {
            if (uh60.vehicleRB != null && !uh60.vehicleRB.isKinematic)
            {
                uh60.vehicleRB.velocity = Vector3.zero;
                uh60.vehicleRB.angularVelocity = Vector3.zero;
            }
        }
        catch { }

        UnityEngine.Debug.Log("[Area7Extraction] UH-60 landed.");

        GameManager.ShowTooltip(player,
            "Extraction vehicle on the ground. Board now!",
            (string)null, null, null, false, true, 10f);

        // --- Step 8: Wait for player to board ---
        bool boarded = false;

        while (true)
        {
            yield return new WaitForSeconds(0.5f);

            if (uh60 == null || uh60.IsDead()) yield break;

            // MUST test the PLAYER specifically. The old check was
            //     uh60.HasDriver || uh60.GetFirstAttached() != null
            // which now matches a seated CREW member the instant the chopper lands, so the
            // extraction would depart without the player aboard.
            if (player.AttachedToEntity == uh60)
            {
                boarded = true;
                break;
            }

            if (uh60.vehicleRB != null && !uh60.vehicleRB.isKinematic)
            {
                uh60.vehicleRB.velocity = Vector3.zero;
                uh60.vehicleRB.angularVelocity = Vector3.zero;
            }
        }

        UnityEngine.Debug.Log("[Area7Extraction] Player boarded. Departure sequence.");

        try
        {
            if (player.Buffs != null && !player.Buffs.HasBuff("frilArea7Victory"))
                player.Buffs.AddBuff("frilArea7Victory");
        }
        catch { }

        // --- Step 9: Fly straight up to Y=120 ---
        float targetY = DEPART_CEILING;
        float climbTimeout = 30f;
        float climbElapsed = 0f;

        while (climbElapsed < climbTimeout)
        {
            yield return new WaitForFixedUpdate();
            climbElapsed += Time.fixedDeltaTime;

            if (!VehicleUsable(uh60)) break;
            if (uh60.vehicleRB == null) break;

            float yDiff = targetY - uh60.position.y;
            if (yDiff < 2f) break;

            uh60.vehicleRB.velocity = Vector3.up * Mathf.Min(20f, yDiff);
        }

        // --- Step 10: Congratulations, PINNED so it survives the whole flight ---
        // Was a single fire-and-forget tooltip that faded after a few seconds. Players kept
        // missing it while watching themselves get airlifted out, so both lines are now
        // PINNED and cycle until the sequence ends. See ShowPinnedTip for why this cannot
        // use the overload the old call used.
        LogPoseDiag("departure", uh60);

        ShowPinnedTip(player, CompletionTipText);
        ShowPinnedTip(player, StatsTipText());

        // --- Step 11: Fly forward in facing direction for 30 seconds ---
        // Guarded: this was the ONE unprotected `.transform` in the coroutine, and quitting the
        // game mid-extraction destroyed the chopper right here, throwing on shutdown.
        if (!VehicleUsable(uh60))
        {
            UnityEngine.Debug.Log("[Area7Extraction] Chopper gone before departure - ending sequence.");
            ClearPinnedTips(player);
            yield break;
        }

        Vector3 outboundDir = uh60.transform.forward;
        outboundDir.y = 0f;
        outboundDir.Normalize();

        float departTime = 0f;
        // 30f -> 60f -> 120f -> 180f at Fril's request: the outbound leg was over too quickly, and
        // 120f still ended before the extraction song (~3:18) finished. This is
        // DURATION only, the speed is unchanged. It also doubles how long the pinned completion
        // and stats messages stay up, since ClearPinnedTips runs when this loop ends.
        float departDuration = 180f;

        while (departTime < departDuration)
        {
            yield return new WaitForFixedUpdate();
            departTime += Time.fixedDeltaTime;

            if (!VehicleUsable(uh60)) break;
            if (uh60.vehicleRB == null) break;

            uh60.vehicleRB.velocity = outboundDir * APPROACH_SPEED * 1.5f;
        }

        // Pinned tooltips re-enqueue themselves forever, so they MUST be removed explicitly
        // or they would nag for the rest of the session.
        ClearPinnedTips(player);

        UnityEngine.Debug.Log("[Area7Extraction] Extraction complete.");
    }
}

// ---------------------------------------------------------------
// HARMONY PATCH — Hook after escapeArea7 challenge redeems
// ---------------------------------------------------------------
[HarmonyPatch(typeof(Challenge), "Redeem")]
public class Area7ExtractionChallengePatch
{
    [HarmonyPriority(Priority.Low)]
    static void Postfix(Challenge __instance)
    {
        try
        {
            if (__instance?.ChallengeClass == null) return;
            string name = __instance.ChallengeClass.Name;
            if (!name.Equals("escapeArea7", StringComparison.OrdinalIgnoreCase)) return;

            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) return;

            Area7ExtractionSequence.TriggerExtraction(player);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area7Extraction] ChallengePatch error: " + e.Message);
        }
    }
}

// ---------------------------------------------------------------
// HARMONY PATCH — Reset extraction state on world load
// ---------------------------------------------------------------
[HarmonyPatch(typeof(GameManager), "StartGame")]
public class Area7ExtractionResetPatch
{
    static void Postfix()
    {
        Area7ExtractionSequence.Reset();
        UnityEngine.Debug.Log("[Area7Extraction] State reset on world load.");
    }
}


// ---------------------------------------------------------------
// CREW FREEZE - REMOVED IN v1.4.0
// Area7CrewFreezeLivePatch and Area7CrewFreezeMovePatch used to Harmony-skip
// EntityAlive.OnUpdateLive and EntityAlive.updateSpeedForwardAndStrafe for the two crew
// entities. They were tested and changed NOTHING, because neither method was moving the
// crew: the real cause was that the crew had never been parented to the vehicle at all.
// Deleted rather than left dormant, since they patched hot per-entity methods for every
// EntityAlive in the world to no benefit.
// ---------------------------------------------------------------
