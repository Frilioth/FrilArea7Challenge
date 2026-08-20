// ============================================================================
//  Fril Area 7 Menu Music
//  Plays Fril's original track "Area 7 Ain't Home" on the main menu, replacing
//  the vanilla menu theme. Written from scratch against the game's own
//  BackgroundMusicMono class. No third-party code used.
//
//  How it works: the game plays menu music through BackgroundMusicMono, a
//  singleton whose Start() populates musicTrackStates with a BackgroundMusic
//  entry (its own AudioSource) and plays it. We Postfix Start(), reach into the
//  EXISTING BackgroundMusic track state, swap its AudioSource.clip to our own
//  clip (loaded from our bundle via the game's DataLoader), and call Play so the
//  same audio source now plays our track at the correct menu-music volume.
//
//  PLAY ONCE (v1.0.3): setting AudioSource.loop = false is NOT enough on its
//  own. The vanilla menu music does not rely on the AudioSource loop flag - the
//  singleton's own Update() calls UpdateTrack() every frame, and UpdateTrack
//  re-calls AudioSource.Play() the moment it sees the source has stopped. That
//  re-play IS the loop. So to play the song exactly once we also Prefix
//  UpdateTrack: once our clip has played its single pass and stopped, we skip
//  UpdateTrack for the BackgroundMusic track so the engine can't restart it.
//  We let the FIRST pass run normally (the flag only trips after we have both
//  seen it playing AND then seen it stop), so nothing suppresses the initial
//  play, and only the BackgroundMusic track is ever touched.
//
//  Version 1.0.3
// ============================================================================
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class FrilMenuMusicInit : IModApi
{
    public const string ModVersion = "1.0.3";

    public void InitMod(Mod _modInstance)
    {
        try
        {
            var harmony = new Harmony("com.frilioth.area7menumusic");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            UnityEngine.Debug.Log("[Fril Menu Music] Initialised v" + ModVersion);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Fril Menu Music] Init failed: " + e);
        }
    }
}

// Shared state between the two patches.
public static class FrilMenuMusicState
{
    // Set true once we have swapped in our clip and started it.
    public static bool applied = false;
    // Set true the first frame we actually observe our track playing. Only after
    // this becomes true can the "stopped => suppress" logic trip, so the very
    // first pass is never suppressed.
    public static bool sawPlaying = false;
    // Once true, the song has completed its single pass and we suppress the
    // engine's auto-restart for the BackgroundMusic track from then on.
    public static bool finishedOnce = false;
}

// Postfix BackgroundMusicMono.Start(): the vanilla BackgroundMusic track and its
// AudioSource already exist by now, so swap in our clip and replay.
[HarmonyPatch(typeof(BackgroundMusicMono), "Start")]
public static class FrilMenuMusic_StartPatch
{
    public static void Postfix(BackgroundMusicMono __instance)
    {
        if (FrilMenuMusicState.applied) return;

        try
        {
            AudioClip clip = DataLoader.LoadAsset<AudioClip>(
                "#@modfolder(FrilArea7MenuMusic):Resources/area7music.unity3d?area7song",
                false);

            if (clip == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[Fril Menu Music] Could not load area7song clip; leaving vanilla menu music.");
                return;
            }

            var track = BackgroundMusicMono.MusicTrack.BackgroundMusic;

            BackgroundMusicMono.MusicTrackState state;
            if (__instance.musicTrackStates != null
                && __instance.musicTrackStates.TryGetValue(track, out state)
                && state != null && state.AudioSource != null)
            {
                state.AudioSource.clip = clip;
                // Belt-and-braces: also turn off the AudioSource's own loop flag.
                // (Not sufficient alone - the UpdateTrack prefix below does the real work.)
                state.AudioSource.loop = false;
                __instance.Play(track);
                FrilMenuMusicState.applied = true;
                UnityEngine.Debug.Log("[Fril Menu Music] Area 7 menu track playing (once).");
            }
            else
            {
                UnityEngine.Debug.LogWarning(
                    "[Fril Menu Music] BackgroundMusic track state or its AudioSource was not available; left vanilla music.");
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Fril Menu Music] Failed to set menu music: " + e.Message);
        }
    }
}

// Prefix BackgroundMusicMono.UpdateTrack(activeTrack): UpdateTrack is what the
// singleton's Update() calls every frame, and it re-calls AudioSource.Play() when
// it sees the track has stopped - that re-play is the loop. Once our song has
// played its single pass and stopped, skip UpdateTrack for the BackgroundMusic
// track so the engine cannot restart it. Every other track, and the first pass of
// ours, run untouched.
[HarmonyPatch(typeof(BackgroundMusicMono), "UpdateTrack")]
public static class FrilMenuMusic_UpdateTrackPatch
{
    // Returning false skips the original method; true runs it normally.
    public static bool Prefix(BackgroundMusicMono __instance,
                              BackgroundMusicMono.MusicTrack activeTrack, ref bool __result)
    {
        try
        {
            // Only ever touch the BackgroundMusic track, and only after our clip is in.
            if (!FrilMenuMusicState.applied
                || activeTrack != BackgroundMusicMono.MusicTrack.BackgroundMusic)
            {
                return true; // run vanilla
            }

            BackgroundMusicMono.MusicTrackState state;
            if (__instance.musicTrackStates == null
                || !__instance.musicTrackStates.TryGetValue(activeTrack, out state)
                || state == null || state.AudioSource == null)
            {
                return true; // can't reason about it; let vanilla handle it
            }

            // If we've already decided the song is done, keep suppressing so the
            // engine never restarts it.
            if (FrilMenuMusicState.finishedOnce)
            {
                __result = false;
                return false; // skip vanilla UpdateTrack (no re-Play)
            }

            bool isPlaying = state.AudioSource.isPlaying;

            if (isPlaying)
            {
                // First (and only) pass in progress - let it play, note that we saw it.
                FrilMenuMusicState.sawPlaying = true;
                return true; // run vanilla (volume handling etc.)
            }

            // Not playing. If we had previously seen it playing, the single pass has
            // finished: latch finished and suppress from here on. If we have NOT yet
            // seen it play, this is the pre-start frame(s) - let vanilla run so the
            // track actually starts the first time.
            if (FrilMenuMusicState.sawPlaying)
            {
                FrilMenuMusicState.finishedOnce = true;
                __result = false;
                return false; // skip the re-Play that would loop it
            }

            return true; // run vanilla (still waiting for first play)
        }
        catch
        {
            return true; // on any error, never break vanilla music
        }
    }
}
