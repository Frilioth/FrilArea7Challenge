// File: Area7ChallengeMod.cs
// Area 7 Challenge Mod - Version 3.0.80
// Author: Frilioth

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Challenges;
using HarmonyLib;
using Platform;
using UnityEngine;

// ---------------------------------------------------------------
// STATS TRACKING
// Tracks run statistics in memory. Generated as an HTML debrief
// page on escape (Mission Complete) or death (Mission Failed).
// Stats reset each time the world loads.
// ---------------------------------------------------------------
public static class Area7RunStats
{
    public static ulong startWorldTime;
    public static int startLevel;
    public static int zombieKills;
    public static Dictionary<string, int> killsByWeapon = new Dictionary<string, int>();
    public static Dictionary<string, int> killsByZombieType = new Dictionary<string, int>();
    public static Dictionary<string, int> bagsByType = new Dictionary<string, int>();
    public static int totalBagsDropped;
    public static string airdropTier = "None";
    public static float highestInfection;
    public static int highestGamestage;
    public static Dictionary<string, ulong> challengeTimes = new Dictionary<string, ulong>();
    public static int airdropCrateEntityId = -1;
    public static List<string> airdropContents = new List<string>();

    // v3.0.66: sticky. Set when a run's stats file went missing mid-run and fresh stats
    // were started against a character that was NOT new. startWorldTime then measures from
    // the reload rather than the real start, so every duration on the debrief, and the
    // elapsed time in the completion code, is short by however long they had already played.
    // Carried into the leaderboard code as flags bit 1 so the site can mark the run unranked.
    public static bool statsWereReset;

    private static float lastSaveTime = 0f;
    private static float saveInterval = 10f; // Save at most every 10 seconds

    public static void Reset(ulong worldTime, int playerLevel)
    {
        startWorldTime = worldTime;
        startLevel = playerLevel;
        zombieKills = 0;
        killsByWeapon.Clear();
        killsByZombieType.Clear();
        bagsByType.Clear();
        totalBagsDropped = 0;
        airdropTier = "None";
        highestInfection = 0f;
        highestGamestage = 0;
        challengeTimes.Clear();
        airdropCrateEntityId = -1;
        airdropContents.Clear();
        statsWereReset = false;
        lastSaveTime = 0f;
    }

    public static void RecordKill(string weaponName, string zombieTypeName)
    {
        zombieKills++;

        if (string.IsNullOrEmpty(weaponName)) weaponName = "Unknown";
        if (string.IsNullOrEmpty(zombieTypeName)) zombieTypeName = "Unknown";

        if (weaponName == "meleeHandPlayer") weaponName = "Fists";

        if (killsByWeapon.ContainsKey(weaponName))
            killsByWeapon[weaponName]++;
        else
            killsByWeapon[weaponName] = 1;

        if (killsByZombieType.ContainsKey(zombieTypeName))
            killsByZombieType[zombieTypeName]++;
        else
            killsByZombieType[zombieTypeName] = 1;

        ThrottledSave();
    }

    public static void RecordBagDrop(string bagType)
    {
        totalBagsDropped++;
        string friendly = GetFriendlyBagName(bagType);
        if (bagsByType.ContainsKey(friendly))
            bagsByType[friendly]++;
        else
            bagsByType[friendly] = 1;

        ThrottledSave();
    }

    public static void RecordChallengeComplete(string challengeName, ulong worldTime)
    {
        challengeTimes[challengeName.ToLower()] = worldTime;
        SaveToFile(); // Always save immediately on challenge completion
    }

    public static void UpdateInfection(float level)
    {
        if (level > highestInfection)
            highestInfection = level;
    }

    public static void UpdateGamestage(int stage)
    {
        if (stage > highestGamestage)
            highestGamestage = stage;
    }

    private static void ThrottledSave()
    {
        if (UnityEngine.Time.time - lastSaveTime >= saveInterval)
        {
            SaveToFile();
            lastSaveTime = UnityEngine.Time.time;
        }
    }

    public static string GetStatsFilePath()
    {
        string modPath = Area7ChallengeMod.GetModPath();
        if (string.IsNullOrEmpty(modPath)) return null;

        World world = GameManager.Instance?.World;
        if (world == null) return null;

        string worldName = world.ChunkCache?.Name ?? "unknown";
        string safeName = "";
        foreach (char c in worldName)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '(' || c == ')' || c == ' ' || c == '.')
                safeName += c;
        }
        return Path.Combine(modPath, "area7_stats_" + safeName + ".txt");
    }

    public static void SaveToFile()
    {
        try
        {
            string filePath = GetStatsFilePath();
            if (string.IsNullOrEmpty(filePath)) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("modVersion=" + Area7ChallengeMod.ModVersion);
            sb.AppendLine("startWorldTime=" + startWorldTime);
            sb.AppendLine("startLevel=" + startLevel);
            sb.AppendLine("zombieKills=" + zombieKills);
            sb.AppendLine("totalBagsDropped=" + totalBagsDropped);
            sb.AppendLine("airdropTier=" + airdropTier);
            sb.AppendLine("highestInfection=" + highestInfection.ToString("F4"));
            sb.AppendLine("highestGamestage=" + highestGamestage);
            sb.AppendLine("airdropCrateEntityId=" + airdropCrateEntityId);
            sb.AppendLine("statsWereReset=" + (statsWereReset ? "1" : "0"));

            foreach (var kvp in killsByWeapon)
                sb.AppendLine("weapon:" + kvp.Key + "=" + kvp.Value);

            foreach (var kvp in killsByZombieType)
                sb.AppendLine("zombie:" + kvp.Key + "=" + kvp.Value);

            foreach (var kvp in bagsByType)
                sb.AppendLine("bag:" + kvp.Key + "=" + kvp.Value);

            foreach (var kvp in challengeTimes)
                sb.AppendLine("challenge:" + kvp.Key + "=" + kvp.Value);

            foreach (string item in airdropContents)
                sb.AppendLine("airdropItem:" + item);

            File.WriteAllText(filePath, sb.ToString());
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Failed to save stats: " + e.Message);
        }
    }

    public static bool LoadFromFile()
    {
        try
        {
            string filePath = GetStatsFilePath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            string[] lines = File.ReadAllLines(filePath);

            // Clear dictionaries but don't reset startWorldTime/startLevel yet
            killsByWeapon.Clear();
            killsByZombieType.Clear();
            bagsByType.Clear();
            challengeTimes.Clear();
            airdropContents.Clear();

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Handle airdropItem lines (no = sign)
                if (trimmed.StartsWith("airdropItem:"))
                {
                    airdropContents.Add(trimmed.Substring(12));
                    continue;
                }

                int eqIndex = trimmed.IndexOf('=');
                if (eqIndex <= 0) continue;

                string key = trimmed.Substring(0, eqIndex);
                string val = trimmed.Substring(eqIndex + 1);

                if (key == "startWorldTime") { ulong.TryParse(val, out startWorldTime); }
                else if (key == "startLevel") { int.TryParse(val, out startLevel); }
                else if (key == "zombieKills") { int.TryParse(val, out zombieKills); }
                else if (key == "totalBagsDropped") { int.TryParse(val, out totalBagsDropped); }
                else if (key == "airdropTier") { airdropTier = val; }
                else if (key == "highestInfection") { float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out highestInfection); }
                else if (key == "highestGamestage") { int.TryParse(val, out highestGamestage); }
                else if (key == "airdropCrateEntityId") { int.TryParse(val, out airdropCrateEntityId); }
                else if (key == "statsWereReset") { statsWereReset = (val == "1"); }
                else if (key.StartsWith("weapon:"))
                {
                    string weaponName = key.Substring(7);
                    int count; if (int.TryParse(val, out count)) killsByWeapon[weaponName] = count;
                }
                else if (key.StartsWith("zombie:"))
                {
                    string zombieName = key.Substring(7);
                    int count; if (int.TryParse(val, out count)) killsByZombieType[zombieName] = count;
                }
                else if (key.StartsWith("bag:"))
                {
                    string bagName = key.Substring(4);
                    int count; if (int.TryParse(val, out count)) bagsByType[bagName] = count;
                }
                else if (key.StartsWith("challenge:"))
                {
                    string challengeName = key.Substring(10);
                    ulong time; if (ulong.TryParse(val, out time)) challengeTimes[challengeName] = time;
                }
            }

            UnityEngine.Debug.Log("[Area 7] Stats loaded from file: " + zombieKills + " kills, " + totalBagsDropped + " bags, " + challengeTimes.Count + " challenges.");
            return true;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Failed to load stats: " + e.Message);
            return false;
        }
    }

    public static void DeleteStatsFile()
    {
        try
        {
            string filePath = GetStatsFilePath();
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                File.Delete(filePath);
                UnityEngine.Debug.Log("[Area 7] Stats file deleted: " + filePath);
            }
        }
        catch { }
    }

    private static string GetFriendlyBagName(string entityClassName)
    {
        switch (entityClassName)
        {
            case "EntityLootContainerArea7Medical": return "Medical";
            case "EntityLootContainerArea7Biker": return "Biker";
            case "EntityLootContainerArea7Cops": return "Cops";
            case "EntityLootContainerArea7Tokens": return "Tokens";
            case "EntityLootContainerArea7Heavy": return "Heavy";
            case "EntityLootContainerArea7Bubbles": return "Hazmat";
            case "EntityLootContainerArea7Creepy": return "Researcher";
            case "EntityLootContainerArea7Books": return "Books";
            default: return entityClassName;
        }
    }

    private static string GetBagColor(string bagName)
    {
        switch (bagName)
        {
            case "Medical": return "#dc3c3c";
            case "Heavy": return "#3c8ddc";
            case "Hazmat": return "#8e44ad";
            case "Books": return "#3cb043";
            case "Cops": return "#d4a026";
            case "Researcher": return "#e74c8b";
            case "Biker": return "#e67e22";
            case "Tokens": return "#1abc9c";
            default: return "#888888";
        }
    }

    // The debrief used to list Difficulty and the three zombie speeds and loot respawn.
    // Those are read live from GamePrefs so they were accurate, but since the sandbox
    // presets landed they no longer describe a run usefully: what a reader wants is
    // "he played Soldier", not four sliders. v3.0.37 shows the preset instead.
    //
    // EnumGamePrefs.SandboxPreset holds the chosen preset's name. SandboxOptionManager
    // resolves it to the display name, and IsUserPreset marks a player's own saved
    // variant so a tweaked run is not passed off as a stock one.
    // Presets are named Area7Recruit, Area7Nightmare and so on so they cannot collide with
    // another mod's presets in the manager's dictionary. On the debrief the prefix is just
    // noise, the reader already knows which mod they are reading about, so strip it.
    // Handles "Area7Nightmare", "Area 7 Nightmare" and "Area7 Nightmare".
    // "Area7InsaneNightmare" -> "Area7 Insane Nightmare" (the prefix is stripped after).
    // Leaves single words and already-spaced names alone, and does not break on digits, so
    // "Area7" itself survives to be stripped normally.
    private static string SplitCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.IndexOf(' ') >= 0) return name;   // already spaced, leave it

        StringBuilder sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) && !char.IsDigit(name[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string StripArea7Prefix(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        string trimmed = name.TrimStart();
        if (trimmed.StartsWith("Area 7", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(6);
        else if (trimmed.StartsWith("Area7", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(5);
        else
            return name;

        trimmed = trimmed.TrimStart(' ', '_', '-');
        // Never hand back an empty string if a preset is ever named just "Area7".
        return string.IsNullOrEmpty(trimmed) ? name : trimmed;
    }

    // Difficulty colours for the debrief, matching the in-game preset colours Fril uses.
    // Keyed on the stripped name so "Area7Nightmare" and a User preset built from Nightmare
    // both land on red. Anything unrecognised falls back to the panel's amber so a Custom
    // or third-party preset still reads cleanly rather than disappearing.
    // v3.0.54: the debrief never said which biome the run was in. Area 7 ships one world
    // per biome, named "Area 7 Challenge (1. Forest)" and so on, so the biome is in the
    // world name and does not need the player's position (which would report whatever
    // block they happened to die on).
    public static string GetBiomeName()
    {
        try
        {
            string world = GamePrefs.GetString(EnumGamePrefs.GameWorld);
            if (string.IsNullOrEmpty(world)) return "Unknown";

            int open = world.LastIndexOf('(');
            int close = world.LastIndexOf(')');
            if (open < 0 || close <= open) return "Unknown";

            string inner = world.Substring(open + 1, close - open - 1).Trim();

            // Strip the ordering prefix, "1. Forest" -> "Forest".
            int dot = inner.IndexOf('.');
            if (dot >= 0 && dot + 1 < inner.Length)
            {
                string before = inner.Substring(0, dot).Trim();
                int n;
                if (int.TryParse(before, out n)) inner = inner.Substring(dot + 1).Trim();
            }

            return string.IsNullOrEmpty(inner) ? "Unknown" : inner;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] Could not read biome from world name: " + e.Message);
            return "Unknown";
        }
    }

    // Colours sampled straight out of each world's biomes.png, every one of which is a
    // single flat colour across all 512x512 pixels:
    //     Forest #004000, Burnt Forest #ba00ff, Desert #ffe477,
    //     Snow #ffffff, Wasteland #ffa800
    // NOTE Forest's true colour is very dark and barely readable on the near-black panel.
    // It is used as-is because Fril asked for the biomes.png colour; swap the Forest line
    // for something like #2e8b2e if it needs to be legible on stream.
    public static string GetBiomeColour(string biome)
    {
        if (string.IsNullOrEmpty(biome)) return "#d4a026";

        string key = biome.ToLowerInvariant();
        if (key.StartsWith("burnt"))     return "#ba00ff";  // burnt forest
        if (key.StartsWith("forest"))    return "#3cb043";  // biomes.png is #004000, but that is
                                                            // near-invisible on the dark panel, so this
                                                            // uses the MISSION COMPLETE green instead
        if (key.StartsWith("desert"))    return "#ffe477";
        if (key.StartsWith("snow"))      return "#ffffff";
        if (key.StartsWith("wasteland")) return "#ffa800";
        return "#d4a026";                                   // amber fallback
    }

    public static string GetPresetColour(string strippedLabel)
    {
        if (string.IsNullOrEmpty(strippedLabel)) return "#d4a026";

        string key = strippedLabel.ToLowerInvariant();
        // "insane" is tested BEFORE the others because the match is on the START of the
        // name: "Insane Nightmare" would otherwise fall through to the amber default,
        // since StartsWith("nightmare") is false for it.
        if (key.StartsWith("insane"))    return "#8b1a1a";  // dark red
        if (key.StartsWith("recruit"))   return "#ff7fbf";  // pink
        if (key.StartsWith("grunt"))     return "#c3b071";  // khaki
        if (key.StartsWith("soldier"))   return "#3c8ddc";  // blue
        if (key.StartsWith("veteran"))   return "#e8862b";  // orange
        if (key.StartsWith("nightmare")) return "#dc3c3c";  // red
        return "#d4a026";                                   // amber fallback
    }

    public static string GetSandboxPresetLabel()
    {
        try
        {
            string presetName = GamePrefs.GetString(EnumGamePrefs.SandboxPreset);
            if (string.IsNullOrEmpty(presetName))
                return "Custom";

            var mgr = SandboxOptions.SandboxOptionManager.Current;
            if (mgr != null)
            {
                var preset = mgr.GetPreset(presetName);
                if (preset != null)
                {
                    // v3.0.53: a user preset reports ONLY "User", never its name.
                    // The name is player-chosen, so it claims whatever they typed. Kualija's
                    // debrief read "A7 Nightmare (User)" for a run with smell and storms
                    // turned off, which implies she played Nightmare when she played
                    // something derived from it. A bare "User" makes no claim at all, and it
                    // cannot overflow the centred 1.6rem line the way an arbitrary name can.
                    // Colour falls through to the amber default, which is correct: a custom
                    // setup is not one of our tiers.
                    if (preset.IsUserPreset) return "User";

                    // v3.0.57: LocalizedName comes back EMPTY for our presets, so this has
                    // always been falling through to the internal Name. "Area7Soldier"
                    // strips to "Soldier" and looked correct by luck; "Area7InsaneNightmare"
                    // strips to "InsaneNightmare" and exposed it. Split camelCase so any
                    // multi-word preset reads properly without needing a localisation row.
                    string label = !string.IsNullOrEmpty(preset.LocalizedName)
                        ? preset.LocalizedName
                        : SplitCamelCase(preset.Name);
                    return StripArea7Prefix(label);
                }
            }

            // Preset recorded but not resolvable (a preset from a mod that is no longer
            // loaded, for instance). The raw name is still more use than nothing.
            return StripArea7Prefix(presetName);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] Could not read sandbox preset: " + e.Message);
            return "Unknown";
        }
    }

    public static string GetDifficultyName(int diff)
    {
        switch (diff)
        {
            case 0: return "Scavenger";
            case 1: return "Adventurer";
            case 2: return "Nomad";
            case 3: return "Warrior";
            case 4: return "Survivalist";
            case 5: return "Insane";
            default: return "Unknown (" + diff + ")";
        }
    }

    public static string GetZombieSpeedName(int speed)
    {
        switch (speed)
        {
            case 0: return "Walk";
            case 1: return "Jog";
            case 2: return "Run";
            case 3: return "Sprint";
            case 4: return "Nightmare";
            default: return "Unknown (" + speed + ")";
        }
    }

    // v3.0.57: minutes, not whole hours. Several timeline rows were showing (0h) simply
    // because two events happened inside the same hour, which made a fast run look like
    // nothing had taken any time at all.
    public static string FormatElapsed(ulong elapsed)
    {
        int days = (int)(elapsed / 24000UL);
        ulong rem = elapsed % 24000UL;
        int hours = (int)(rem / 1000UL);
        int minutes = (int)((rem % 1000UL) * 60UL / 1000UL);
        if (days > 0) return days + "d " + hours + "h " + minutes + "m";
        if (hours > 0) return hours + "h " + minutes + "m";
        return minutes + "m";
    }

    // 24000 world-time units per day, 1000 per hour. Matches the game's own
    // GameUtils.WorldTimeToDays (which is 1-based) / WorldTimeToHours / WorldTimeToMinutes.
    public static string FormatGameTime(ulong worldTime)
    {
        int day = (int)(worldTime / 24000UL) + 1;
        ulong rem = worldTime % 24000UL;
        int hour = (int)(rem / 1000UL);
        int minute = (int)((rem % 1000UL) * 60UL / 1000UL);
        return "Day " + day + ", " + hour.ToString("D2") + ":" + minute.ToString("D2");
    }

    // --- HTML Generation ---

    public static void GenerateStatsPage(EntityPlayerLocal player, bool escaped)
    {
        try
        {
            World world = GameManager.Instance?.World;
            if (world == null || player == null) return;

            ulong endWorldTime = world.worldTime;
            ulong elapsed = endWorldTime - startWorldTime;
            int endLevel = (player.Progression != null) ? player.Progression.Level : 0;
            int levelsGained = endLevel - startLevel;

            // Player name
            string playerName = "Unknown Survivor";
            try { playerName = player.EntityName ?? "Unknown Survivor"; } catch { }

            // Game settings
            string worldName = GamePrefs.GetString(EnumGamePrefs.GameWorld);
            // v3.0.66: leaderboard code, shown only on a successful escape.
            string completionCode = "";
            if (escaped)
            {
                try { completionCode = Area7CompletionCode.Build(player) ?? ""; }
                catch (Exception ccEx)
                {
                    UnityEngine.Debug.LogWarning("[Area 7] Completion code failed: " + ccEx.Message);
                }
            }
            string completionBlock = "";
            if (!string.IsNullOrEmpty(completionCode))
            {
                completionBlock =
                  "\n    <div class=\"panel green full-width\">\n"
                + "      <h2>Leaderboard Code</h2>\n"
                + "      <div style=\"font-family:monospace;font-size:1.05rem;letter-spacing:.06em;"
                + "word-break:break-all;background:#0d0d0d;border:1px solid #2f2f2f;padding:14px;"
                + "border-radius:4px;color:#7fff00\">" + EscapeHtml(completionCode) + "</div>\n"
                + "      <div style=\"color:#888;margin-top:10px;font-size:.9rem\">"
                + "Send this to Frilioth to have your run added to the leaderboard."
                + (Area7RunStats.statsWereReset
                    ? " <span style=\"color:#cd5c5c\">Note: this run's stats were reset partway through, "
                      + "so its time cannot be ranked.</span>"
                    : "")
                + "</div>\n    </div>\n";
            }

            string sandboxPreset = GetSandboxPresetLabel();
            string presetColour = GetPresetColour(sandboxPreset);
            string biomeName = GetBiomeName();
            string biomeColour = GetBiomeColour(biomeName);
            int difficulty = GamePrefs.GetInt(EnumGamePrefs.GameDifficulty);
            int lootRespawn = GamePrefs.GetInt(EnumGamePrefs.LootRespawnDays);

            // Zombie speeds
            int zombieDay = 0, zombieNight = 0, zombieBM = 0;
            try
            {
                zombieDay = GamePrefs.GetInt(EnumGamePrefs.ZombieMove);
                zombieNight = GamePrefs.GetInt(EnumGamePrefs.ZombieMoveNight);
                zombieBM = GamePrefs.GetInt(EnumGamePrefs.ZombieBMMove);
            }
            catch { }

            // Favourite weapon
            string favWeapon = "None";
            int favKills = 0;
            foreach (var kvp in killsByWeapon)
            {
                if (kvp.Value > favKills) { favKills = kvp.Value; favWeapon = kvp.Key; }
            }

            // Drop rate
            float dropRate = (zombieKills > 0) ? ((float)totalBagsDropped / zombieKills * 100f) : 0f;

            string statusText = escaped ? "MISSION COMPLETE" : "MISSION FAILED";
            string statusColor = escaped ? "#3cb043" : "#dc3c3c";
            string infectionDisplay = highestInfection > 0 ? highestInfection.ToString("F0") + "%" : "None";
            string realDate = DateTime.Now.ToString("dd MMM yyyy").ToUpper();

            // --- Build weapon bar chart (top 8) ---
            var sortedWeapons = killsByWeapon.OrderByDescending(kvp => kvp.Value).Take(8).ToList();
            int maxWeaponKills = sortedWeapons.Count > 0 ? sortedWeapons[0].Value : 1;
            StringBuilder weaponBars = new StringBuilder();
            foreach (var kvp in sortedWeapons)
            {
                float pct = (float)kvp.Value / maxWeaponKills * 100f;
                weaponBars.Append("<div class=\"bar-row\"><div class=\"bar-label\">" + EscapeHtml(kvp.Key) + "</div>");
                weaponBars.Append("<div class=\"bar-track\"><div class=\"bar-fill blue\" style=\"width:" + pct.ToString("F0") + "%\"></div></div>");
                weaponBars.Append("<div class=\"bar-count\">" + kvp.Value + "</div></div>\n");
            }

            // --- Build zombie bar chart (all types, two-column newspaper layout) ---
            var sortedZombies = killsByZombieType.OrderByDescending(kvp => kvp.Value).ToList();
            int maxZombieKills = sortedZombies.Count > 0 ? sortedZombies[0].Value : 1;
            int halfCount = (sortedZombies.Count + 1) / 2;
            StringBuilder zombieBarsLeft = new StringBuilder();
            StringBuilder zombieBarsRight = new StringBuilder();
            for (int z = 0; z < sortedZombies.Count; z++)
            {
                var kvp = sortedZombies[z];
                float pct = (float)kvp.Value / maxZombieKills * 100f;
                string row = "<div class=\"bar-row\"><div class=\"bar-label\">" + EscapeHtml(kvp.Key) + "</div>"
                    + "<div class=\"bar-track\"><div class=\"bar-fill red\" style=\"width:" + pct.ToString("F0") + "%\"></div></div>"
                    + "<div class=\"bar-count\">" + kvp.Value + "</div></div>\n";
                if (z < halfCount)
                    zombieBarsLeft.Append(row);
                else
                    zombieBarsRight.Append(row);
            }

            // --- Build bag donut chart ---
            var sortedBags = bagsByType.OrderByDescending(kvp => kvp.Value).ToList();
            float circumference = 314.16f;
            float donutOffset = 0f;
            StringBuilder donutSegments = new StringBuilder();
            StringBuilder donutLegend = new StringBuilder();

            foreach (var kvp in sortedBags)
            {
                string color = GetBagColor(kvp.Key);
                float segLen = (totalBagsDropped > 0) ? ((float)kvp.Value / totalBagsDropped * circumference) : 0f;
                float gapLen = circumference - segLen;

                donutSegments.Append("<circle cx=\"60\" cy=\"60\" r=\"50\" fill=\"none\" stroke=\"" + color + "\" stroke-width=\"16\" ");
                donutSegments.Append("stroke-dasharray=\"" + segLen.ToString("F1") + " " + gapLen.ToString("F1") + "\" ");
                donutSegments.Append("stroke-dashoffset=\"" + (-donutOffset).ToString("F1") + "\"/>\n");
                donutOffset += segLen;

                donutLegend.Append("<div class=\"legend-item\"><div class=\"legend-dot\" style=\"background:" + color + "\"></div>");
                donutLegend.Append(EscapeHtml(kvp.Key) + "<span class=\"legend-count\">" + kvp.Value + "</span></div>\n");
            }

            // --- Build challenge timeline ---
            // v3.0.55: real events, not challenge redemptions. See Area7TriggerTimePatch.
            // Mission Start dropped (always Day 1 07:00, and still the baseline the first
            // duration is measured from). Mission End dropped (identical to Escape, and the
            // total is already in the stat strip).
            string[] phaseKeys = { "trig2", "trig5", "trig10", "crucible", "deploytransmitter", "escapearea7" };
            string[] phaseLabels = { "Surface & Station Cleared", "Med Bay Cleared", "Hydroponics Cleared", "Car Park Cleared", "Transmitter Deployed", "Extraction" };

            // v3.0.57: SORT BY TIME, do not assume the listed order is the order things
            // happened. A real run produced Surface & Station on Day 3 with Med Bay on
            // Day 1, because the surface button can be pressed at any point (it is the
            // "cleared or bypassed" trigger, and a player can bypass early and come back).
            // Rendering in fixed order meant each gap was computed against the row above
            // rather than against the previous EVENT, so the durations were meaningless
            // and one of them counted backwards.
            // Completed phases are now ordered chronologically and each gap is measured
            // from the previous completed phase. Anything not done is listed after, in the
            // original logical order.
            var donePhases = new List<KeyValuePair<string, ulong>>();
            var missingPhases = new List<string>();
            for (int i = 0; i < phaseKeys.Length; i++)
            {
                if (challengeTimes.ContainsKey(phaseKeys[i]))
                    donePhases.Add(new KeyValuePair<string, ulong>(phaseLabels[i], challengeTimes[phaseKeys[i]]));
                else
                    missingPhases.Add(phaseLabels[i]);
            }
            donePhases.Sort((a, b) => a.Value.CompareTo(b.Value));

            StringBuilder phaseRows = new StringBuilder();
            ulong prevTime = startWorldTime;
            foreach (var phase in donePhases)
            {
                ulong t = phase.Value;
                ulong phaseDuration = (t >= prevTime) ? (t - prevTime) : 0UL;
                phaseRows.Append("<div class=\"tl-row\"><span class=\"tl-label\">" + phase.Key + "</span>");
                phaseRows.Append("<span class=\"tl-value\">" + FormatGameTime(t) + " <span class=\"tl-detail\">(" + FormatElapsed(phaseDuration) + ")</span></span></div>\n");
                prevTime = t;
            }
            foreach (string label in missingPhases)
            {
                phaseRows.Append("<div class=\"tl-row\"><span class=\"tl-label\">" + label + "</span>");
                phaseRows.Append("<span class=\"tl-value incomplete\">Incomplete</span></div>\n");
            }

            // --- Build airdrop contents list ---
            StringBuilder airdropRows = new StringBuilder();
            if (airdropContents.Count > 0)
            {
                foreach (string item in airdropContents)
                {
                    airdropRows.Append("<div class=\"info-row\"><span class=\"info-label\">" + EscapeHtml(item) + "</span></div>\n");
                }
            }
            else
            {
                airdropRows.Append("<div class=\"info-row\"><span class=\"info-label\" style=\"color:#553333\">Not opened</span></div>\n");
            }

            string html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Area 7 — Mission Debrief</title>
<link href=""https://fonts.googleapis.com/css2?family=Share+Tech+Mono&family=Orbitron:wght@400;700;900&family=Rajdhani:wght@300;400;600;700&display=swap"" rel=""stylesheet"">
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }

  body {
    background: #0a0c0f;
    color: #c8ccd0;
    font-family: 'Rajdhani', sans-serif;
    min-height: 100vh;
    overflow-x: hidden;
  }

  body::before {
    content: '';
    position: fixed;
    top: 0; left: 0; right: 0; bottom: 0;
    background:
      radial-gradient(ellipse at 20% 50%, rgba(220, 50, 50, 0.04) 0%, transparent 60%),
      radial-gradient(ellipse at 80% 20%, rgba(50, 180, 50, 0.03) 0%, transparent 50%),
      repeating-linear-gradient(0deg, transparent, transparent 2px, rgba(255,255,255,0.008) 2px, rgba(255,255,255,0.008) 3px);
    pointer-events: none;
    z-index: 0;
  }

  .container {
    max-width: 1100px;
    margin: 0 auto;
    padding: 30px 20px;
    position: relative;
    z-index: 1;
  }

  /* --- Header --- */
  .header {
    text-align: center;
    margin-bottom: 35px;
    position: relative;
  }
  .header::after {
    content: '';
    display: block;
    width: 60%;
    height: 1px;
    background: linear-gradient(90deg, transparent, rgba(220, 50, 50, 0.5), transparent);
    margin: 15px auto 0;
  }
  .header h1 {
    font-family: 'Orbitron', monospace;
    font-size: 2.4rem;
    font-weight: 900;
    letter-spacing: 8px;
    text-transform: uppercase;
    color: #e8eaec;
    text-shadow: 0 0 30px rgba(220, 50, 50, 0.3);
  }
  .header .subtitle {
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.75rem;
    color: #666;
    letter-spacing: 3px;
    margin-top: 6px;
  }
  .header .player-line {
    font-family: 'Orbitron', monospace;
    font-size: 1rem;
    font-weight: 700;
    letter-spacing: 2px;
    color: #3c8ddc;
    margin-top: 10px;
    text-shadow: 0 0 12px rgba(60,141,220,0.3);
  }
  .header .status-line {
    font-family: 'Orbitron', monospace;
    font-size: 1.1rem;
    font-weight: 700;
    letter-spacing: 4px;
    margin-top: 12px;
    text-shadow: 0 0 15px;
  }

  /* --- Top stat strip --- */
  .stat-strip {
    display: flex;
    justify-content: center;
    gap: 30px;
    margin-bottom: 30px;
    flex-wrap: wrap;
  }
  .stat-box {
    text-align: center;
    padding: 14px 22px;
    border: 1px solid rgba(255,255,255,0.06);
    background: rgba(255,255,255,0.02);
    min-width: 110px;
  }
  .stat-box .num {
    font-family: 'Orbitron', monospace;
    font-size: 1.8rem;
    font-weight: 700;
    color: #fff;
    line-height: 1;
  }
  .stat-box .num.red { color: #dc3c3c; }
  .stat-box .num.green { color: #3cb043; }
  .stat-box .num.amber { color: #d4a026; }
  .stat-box .num.blue { color: #3c8ddc; }
  .stat-box .num.purple { color: #8e44ad; }
  .stat-box .label {
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.6rem;
    color: #666;
    letter-spacing: 2px;
    text-transform: uppercase;
    margin-top: 6px;
  }

  /* --- Grid layout --- */
  .grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 22px;
    margin-bottom: 22px;
  }
  .full-width { grid-column: 1 / -1; }

  /* --- Panel --- */
  .panel {
    background: rgba(255,255,255,0.015);
    border: 1px solid rgba(255,255,255,0.05);
    padding: 22px;
    position: relative;
  }
  .panel::before {
    content: '';
    position: absolute;
    top: 0; left: 0;
    width: 3px;
    height: 100%;
  }
  .panel.red::before { background: linear-gradient(180deg, #dc3c3c, transparent); }
  .panel.green::before { background: linear-gradient(180deg, #3cb043, transparent); }
  .panel.amber::before { background: linear-gradient(180deg, #d4a026, transparent); }
  .panel.blue::before { background: linear-gradient(180deg, #3c8ddc, transparent); }
  .panel.purple::before { background: linear-gradient(180deg, #8e44ad, transparent); }
  .panel.teal::before { background: linear-gradient(180deg, #1abc9c, transparent); }

  .panel h2 {
    font-family: 'Orbitron', monospace;
    font-size: 0.7rem;
    font-weight: 700;
    letter-spacing: 3px;
    text-transform: uppercase;
    color: #888;
    margin-bottom: 16px;
  }

  /* --- Bar chart rows --- */
  .bar-row {
    display: flex;
    align-items: center;
    margin-bottom: 6px;
    gap: 10px;
  }
  .bar-label {
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.72rem;
    color: #999;
    width: 140px;
    text-align: right;
    flex-shrink: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .bar-track {
    flex: 1;
    height: 18px;
    background: rgba(255,255,255,0.03);
    position: relative;
    overflow: hidden;
  }
  .bar-fill {
    height: 100%;
    position: absolute;
    left: 0; top: 0;
  }
  .bar-fill.red { background: linear-gradient(90deg, #dc3c3c, #a82a2a); }
  .bar-fill.green { background: linear-gradient(90deg, #3cb043, #2a8a32); }
  .bar-fill.amber { background: linear-gradient(90deg, #d4a026, #b8891e); }
  .bar-fill.blue { background: linear-gradient(90deg, #3c8ddc, #2a6fb8); }
  .bar-fill.purple { background: linear-gradient(90deg, #8e44ad, #6c3483); }
  .bar-count {
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.72rem;
    color: #ccc;
    width: 30px;
    text-align: right;
    flex-shrink: 0;
  }

  /* --- Donut chart --- */
  .donut-section {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 30px;
    margin-top: 8px;
  }
  .donut-container {
    position: relative;
    width: 150px;
    height: 150px;
  }
  .donut-container svg {
    width: 150px;
    height: 150px;
    transform: rotate(-90deg);
  }
  .donut-center {
    position: absolute;
    top: 50%; left: 50%;
    transform: translate(-50%, -50%);
    text-align: center;
  }
  .donut-center .pct {
    font-family: 'Orbitron', monospace;
    font-size: 1.4rem;
    font-weight: 700;
    color: #fff;
  }
  .donut-center .pct-label {
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.55rem;
    color: #666;
    letter-spacing: 1px;
  }
  .legend {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .legend-item {
    display: flex;
    align-items: center;
    gap: 8px;
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.7rem;
    color: #999;
  }
  .legend-dot {
    width: 10px;
    height: 10px;
    flex-shrink: 0;
  }
  .legend-count {
    color: #ccc;
    margin-left: auto;
    padding-left: 12px;
  }

  /* --- Info rows (parameters, survivor, timeline) --- */
  .info-row {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 5px 0;
    border-bottom: 1px solid rgba(255,255,255,0.04);
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.78rem;
  }
  .info-row:last-child { border-bottom: none; }
  .info-label { color: #777; }
  .info-value { color: #ddd; text-align: right; }
  .info-value.amber { color: #d4a026; }
  .info-value.green { color: #3cb043; }
  .info-value.red { color: #dc3c3c; }
  .info-value.blue { color: #3c8ddc; }

  /* --- Difficulty (single value, no label, colour set inline per preset) --- */
  .difficulty-split {
    border-top: 1px solid rgba(255,255,255,0.08);
    margin: 10px 0 2px 0;
  }
  .difficulty-value {
    font-family: 'Share Tech Mono', monospace;
    font-size: 1.6rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    text-align: center;
    padding: 10px 0 4px 0;
  }

  /* --- Timeline --- */
  .tl-row {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 7px 0;
    border-bottom: 1px solid rgba(255,255,255,0.04);
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.78rem;
  }
  .tl-row:last-child { border-bottom: none; }
  .tl-label { color: #999; }
  .tl-value { color: #3cb043; text-align: right; }
  .tl-detail { color: #666; font-size: 0.68rem; }
  .tl-value.incomplete { color: #553333; }

  /* --- Footer --- */
  .footer {
    font-family: 'Share Tech Mono', monospace;
    font-size: 0.6rem;
    color: #444;
    text-align: center;
    margin-top: 30px;
    letter-spacing: 1px;
  }
  .footer a { color: #555; text-decoration: none; }
  .footer a:hover { color: #3c8ddc; }

  /* Two-column zombie list inside full-width panel */
  .zombie-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 4px 30px;
  }

  @media (max-width: 700px) {
    .grid { grid-template-columns: 1fr; }
    .zombie-grid { grid-template-columns: 1fr; }
    .stat-strip { gap: 12px; }
    .bar-label { width: 90px; font-size: 0.6rem; }
  }
</style>
</head>
<body>
<div class=""container"">

  <div class=""header"">
    <h1>Area 7</h1>
    <div class=""subtitle"">MISSION DEBRIEF — " + EscapeHtml(worldName) + @" — " + realDate + @"</div>
    <div class=""player-line"">" + EscapeHtml(playerName) + @"</div>
    <div class=""status-line"" style=""color:" + statusColor + @""">" + statusText + @"</div>
  </div>

  <div class=""stat-strip"">
    <div class=""stat-box"">
      <div class=""num amber"">" + totalBagsDropped + @"</div>
      <div class=""label"">Bags Dropped</div>
    </div>
    <div class=""stat-box"">
      <div class=""num green"">" + dropRate.ToString("F1") + @"%</div>
      <div class=""label"">Drop Rate</div>
    </div>
    <div class=""stat-box"">
      <div class=""num red"">" + zombieKills + @"</div>
      <div class=""label"">Kills</div>
    </div>
    <div class=""stat-box"">
      <div class=""num blue"">" + levelsGained + @"</div>
      <div class=""label"">Levels Gained</div>
    </div>
    <div class=""stat-box"">
      <div class=""num purple"">" + FormatElapsed(elapsed) + @"</div>
      <div class=""label"">Duration</div>
    </div>
  </div>

  <div class=""grid"">

    <!-- PARAMETERS -->
    <div class=""panel teal"">
      <h2>Difficulty</h2>
      <div class=""difficulty-value"" style=""color:" + presetColour + @""">" + EscapeHtml(sandboxPreset) + @"</div>
      <div class=""difficulty-split""></div>
      <h2>Biome</h2>
      <div class=""difficulty-value"" style=""color:" + biomeColour + @""">" + EscapeHtml(biomeName) + @"</div>
    </div>

    <!-- SURVIVOR -->
    <div class=""panel purple"">
      <h2>Survivor</h2>
      <div class=""info-row""><span class=""info-label"">Level</span><span class=""info-value blue"">" + endLevel + @" <span style=""color:#666"">(started " + startLevel + @")</span></span></div>
      <div class=""info-row""><span class=""info-label"">Highest Gamestage</span><span class=""info-value"">" + highestGamestage + @"</span></div>
      <div class=""info-row""><span class=""info-label"">Highest Infection</span><span class=""info-value red"">" + infectionDisplay + @"</span></div>
      <div class=""info-row""><span class=""info-label"">Airdrop Tier</span><span class=""info-value amber"">" + EscapeHtml(airdropTier) + @"</span></div>
      <div class=""info-row""><span class=""info-label"">Favourite Weapon</span><span class=""info-value green"">" + EscapeHtml(favWeapon) + @" <span style=""color:#666"">(" + favKills + @")</span></span></div>
    </div>

" + completionBlock + @"
    <!-- KILLS - FULL WIDTH -->
    <div class=""panel red full-width"">
      <h2>Kills</h2>
      <div class=""zombie-grid"">
        <div>
" + zombieBarsLeft.ToString() + @"
        </div>
        <div>
" + zombieBarsRight.ToString() + @"
        </div>
      </div>
    </div>

    <!-- BAG DROPS DONUT -->
    <div class=""panel amber"">
      <h2>Loot Bags by Type</h2>
      <div class=""donut-section"">
        <div class=""donut-container"">
          <svg viewBox=""0 0 120 120"">
            <circle cx=""60"" cy=""60"" r=""50"" fill=""none"" stroke=""rgba(255,255,255,0.03)"" stroke-width=""16""/>
" + donutSegments.ToString() + @"
          </svg>
          <div class=""donut-center"">
            <div class=""pct"">" + totalBagsDropped + @"</div>
            <div class=""pct-label"">BAGS</div>
          </div>
        </div>
        <div class=""legend"">
" + donutLegend.ToString() + @"
        </div>
      </div>
    </div>

    <!-- WEAPONS USED -->
    <div class=""panel blue"">
      <h2>Weapons Used</h2>
" + weaponBars.ToString() + @"
    </div>

    <!-- AIRDROP CONTENTS -->
    <div class=""panel amber"">
      <h2>Airdrop Contents — " + EscapeHtml(airdropTier) + @"</h2>
" + airdropRows.ToString() + @"
    </div>

    <!-- MISSION TIMELINE -->
    <div class=""panel green"">
      <h2>Mission Timeline</h2>
" + phaseRows.ToString() + @"
    </div>

  </div>

  <div class=""footer"">
    Area 7 Challenge Mod v" + Area7ChallengeMod.ModVersion + @" — by <a href=""https://twitch.tv/frilioth"" target=""_blank"">Frilioth</a>
    &nbsp;&bull;&nbsp; Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + @"
  </div>

</div>
</body>
</html>";

            // Save to mod folder under stats/ - overwrites previous debrief
            string statsDir = Path.Combine(Area7ChallengeMod.GetModPath(), "stats");
            if (!Directory.Exists(statsDir))
                Directory.CreateDirectory(statsDir);

            string filePath = Path.Combine(statsDir, "Area7_Debrief.html");

            File.WriteAllText(filePath, html, Encoding.UTF8);
            UnityEngine.Debug.Log("[Area 7] Stats page saved: " + filePath);

            if (player != null)
            {
                string msg = escaped
                    ? "Mission debrief saved to Mods/FrilArea7Challenge/stats/"
                    : "Mission failed. Debrief saved to Mods/FrilArea7Challenge/stats/";
                GameManager.ShowTooltip(player, msg, (string)null, null, null, false, true, 0f);
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Failed to generate stats page: " + e.Message);
        }
    }

    private static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}


// =====================================================================================
// v3.0.66: COMPLETION CODE for the website leaderboard.
//
// Emits a short Base32 string on a successful escape, which the player pastes to Fril
// and the site decodes into a leaderboard row. Format is specified in
// Area7_Completion_Code_SPEC_v1.md -- keep the two in step, and if the payload ever
// changes, bump FORMAT_VERSION rather than editing v1, because codes already in the
// wild must keep decoding.
//
// This is an honour-system leaderboard. The CRC catches transcription errors, nothing
// more; the code is generated on the player's machine and can be forged by anyone who
// reads this assembly. That is a deliberate accepted limitation, not an oversight.
// =====================================================================================
public static class Area7CompletionCode
{
    public const int FORMAT_VERSION = 1;

    private const string B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // Preset internal name -> index. Must match section 3.2 of the spec.
    private static readonly string[] PresetOrder =
    {
        "Area7Recruit", "Area7Grunt", "Area7Soldier",
        "Area7Veteran", "Area7Nightmare", "Area7InsaneNightmare"
    };

    public static string Build(EntityPlayerLocal player)
    {
        World world = GameManager.Instance?.World;
        if (world == null) return null;

        // v3.0.68: bit 0 is now honest. It used to be set unconditionally, which meant
        // `a7 code` on an unfinished run produced a code that looked like a completion and
        // would have ranked: Fril's first real test on 16 Aug returned "escaped, 1h 27m",
        // which would have topped the table. If escapearea7 is absent the run is NOT
        // complete, the elapsed time is only "so far", and the decoder refuses to rank it.
        ulong escapeTime;
        bool completed = Area7RunStats.challengeTimes.TryGetValue("escapearea7", out escapeTime);
        if (!completed) escapeTime = world.worldTime;

        ulong startTime = Area7RunStats.startWorldTime;
        ulong elapsed = escapeTime > startTime ? escapeTime - startTime : 0UL;

        byte flags = 0;
        if (completed) flags |= 0x01;                         // bit 0: run actually escaped
        if (Area7RunStats.statsWereReset) flags |= 0x02;      // bit 1: timing not trustworthy

        string name = "";
        try { name = player != null ? (player.EntityName ?? "") : ""; } catch { }
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 32)
        {
            byte[] trimmed = new byte[32];
            Array.Copy(nameBytes, trimmed, 32);
            nameBytes = trimmed;
        }

        List<byte> b = new List<byte>(64);
        b.Add(WorldIndex(world));
        b.Add(PresetIndex());
        PutU32(b, (uint)Math.Min(elapsed, uint.MaxValue));
        PutU32(b, (uint)Math.Min(startTime, uint.MaxValue));
        PutU16(b, (ushort)Clamp(Area7RunStats.highestGamestage, 0, ushort.MaxValue));
        PutU16(b, (ushort)Clamp(Area7RunStats.zombieKills, 0, ushort.MaxValue));
        PutU16(b, (ushort)Clamp(Area7RunStats.totalBagsDropped, 0, ushort.MaxValue));
        b.Add(AirdropTierIndex(Area7RunStats.airdropTier));
        PutU16(b, DaysSinceEpoch());
        PutU16(b, BuildNumber());
        b.Add(flags);
        b.Add((byte)nameBytes.Length);
        b.AddRange(nameBytes);

        ushort crc = Crc16(b);
        PutU16(b, crc);

        string payload = Base32(b.ToArray());
        StringBuilder sb = new StringBuilder();
        sb.Append("A7-").Append(FORMAT_VERSION);
        for (int i = 0; i < payload.Length; i += 8)
            sb.Append('-').Append(payload.Substring(i, Math.Min(8, payload.Length - i)));
        return sb.ToString();
    }

    // ---- field helpers -------------------------------------------------------------

    // Read the leading number out of "Area 7 Challenge (3. Desert)". Deriving it from the
    // number rather than matching the whole name means a renamed world falls back to 255
    // (unknown) instead of silently ranking as Forest.
    private static byte WorldIndex(World world)
    {
        try
        {
            string n = world.ChunkCache != null ? world.ChunkCache.Name : null;
            if (string.IsNullOrEmpty(n)) return 255;
            int open = n.IndexOf('(');
            if (open < 0) return 255;
            int dot = n.IndexOf('.', open);
            if (dot < 0) return 255;
            int idx;
            if (!int.TryParse(n.Substring(open + 1, dot - open - 1).Trim(), out idx)) return 255;
            return (idx >= 1 && idx <= 5) ? (byte)idx : (byte)255;
        }
        catch { return 255; }
    }

    // Matches on the INTERNAL preset name. Never the displayed one: that is translated,
    // and SandboxOptionPreset.LocalizedName comes back empty for our presets anyway.
    private static byte PresetIndex()
    {
        try
        {
            string p = GamePrefs.GetString(EnumGamePrefs.SandboxPreset);
            if (string.IsNullOrEmpty(p)) return 255;
            for (int i = 0; i < PresetOrder.Length; i++)
                if (string.Equals(p, PresetOrder[i], StringComparison.OrdinalIgnoreCase))
                    return (byte)(i + 1);

            var mgr = SandboxOptions.SandboxOptionManager.Current;
            if (mgr != null)
            {
                var preset = mgr.GetPreset(p);
                if (preset != null && preset.IsUserPreset) return 254;
            }
            return 255;
        }
        catch { return 255; }
    }

    private static byte AirdropTierIndex(string tier)
    {
        if (string.IsNullOrEmpty(tier)) return 0;
        for (int t = 5; t >= 1; t--)
            if (tier.IndexOf("Tier " + t, StringComparison.OrdinalIgnoreCase) >= 0) return (byte)t;
        return 0;
    }

    private static ushort DaysSinceEpoch()
    {
        try
        {
            TimeSpan d = DateTime.UtcNow.Date - new DateTime(2020, 1, 1);
            return (ushort)Clamp((int)d.TotalDays, 0, ushort.MaxValue);
        }
        catch { return 0; }
    }

    // "3.0.66" -> 3066, so the site can show which build produced a code.
    private static ushort BuildNumber()
    {
        try
        {
            string[] parts = Area7ChallengeMod.ModVersion.Split('.');
            if (parts.Length != 3) return 0;
            int a = int.Parse(parts[0]), b = int.Parse(parts[1]), c = int.Parse(parts[2]);
            return (ushort)Clamp(a * 1000 + b * 100 + c, 0, ushort.MaxValue);
        }
        catch { return 0; }
    }

    // ---- encoding ------------------------------------------------------------------

    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    private static void PutU16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void PutU32(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
    }

    // CRC-16/CCITT-FALSE: poly 0x1021, init 0xFFFF, no reflection, no final xor.
    private static ushort Crc16(List<byte> data)
    {
        int crc = 0xFFFF;
        for (int i = 0; i < data.Count; i++)
        {
            crc ^= data[i] << 8;
            for (int k = 0; k < 8; k++)
                crc = ((crc & 0x8000) != 0) ? (((crc << 1) ^ 0x1021) & 0xFFFF) : ((crc << 1) & 0xFFFF);
        }
        return (ushort)crc;
    }

    // RFC 4648 Base32, uppercase, padding omitted.
    private static string Base32(byte[] data)
    {
        StringBuilder sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        for (int i = 0; i < data.Length; i++)
        {
            buffer = (buffer << 8) | data[i];
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(B32[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(B32[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}

public class Area7ChallengeMod : IModApi
{
    public const string ModVersion = "3.0.80";
    private static string modPath = "";
    private static string saveFilePath = "";

    public void InitMod(Mod _modInstance)
    {
        UnityEngine.Debug.Log("[Area 7] Mod initializing... v" + ModVersion);

        modPath = _modInstance.Path;

        // Load named zombies from Config/names.txt
        Area7NamedZombies.LoadNames(modPath);

        var harmony = new Harmony("com.frilioth.area7challenge");
        harmony.PatchAll();
        UnityEngine.Debug.Log("[Area 7] Harmony patches applied!");

        ModEvents.GameStartDone.RegisterHandler((ref ModEvents.SGameStartDoneData data) => OnGameStartDone(ref data));
    }

    public static string GetModPath() => modPath;

    public static string GetSaveFilePath()
    {
        if (string.IsNullOrEmpty(saveFilePath))
        {
            string worldName = GamePrefs.GetString(EnumGamePrefs.GameWorld);
            string gameName = GamePrefs.GetString(EnumGamePrefs.GameName);
            string safeName = (worldName + "_" + gameName).Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
            saveFilePath = Path.Combine(modPath, "area7_state_" + safeName + ".txt");
            UnityEngine.Debug.Log("[Area 7] Save file: " + saveFilePath);
        }

        return saveFilePath;
    }

    // ---------------------------------------------------------------
    // POST-ESCAPE MARKER
    // Written on successful escape. Once present, the mod treats the
    // save as completed and stays dormant: no debrief overwrite on
    // later deaths, no fresh-run reset on reload, no radiation, no
    // ambient sounds. To replay on the same save, delete the file.
    //
    // The marker records the world time at the moment of escape.
    // OnGameStartDone uses this to detect a stale marker (world time
    // gone backwards = new save reusing the same save name) and clean
    // it up automatically.
    // ---------------------------------------------------------------
    public static string GetCompletedMarkerFilePath()
    {
        if (string.IsNullOrEmpty(modPath)) return null;
        string worldName = GamePrefs.GetString(EnumGamePrefs.GameWorld);
        string gameName = GamePrefs.GetString(EnumGamePrefs.GameName);
        string safeName = (worldName + "_" + gameName).Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
        return Path.Combine(modPath, "area7_completed_" + safeName + ".txt");
    }

    public static bool IsRunCompleted()
    {
        try
        {
            string path = GetCompletedMarkerFilePath();
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }
        catch { return false; }
    }

    public static void WriteCompletedMarker()
    {
        try
        {
            string path = GetCompletedMarkerFilePath();
            if (string.IsNullOrEmpty(path)) return;

            ulong worldTime = GameManager.Instance?.World?.worldTime ?? 0UL;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("completed=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("modVersion=" + ModVersion);
            sb.AppendLine("escapeWorldTime=" + worldTime);

            File.WriteAllText(path, sb.ToString());
            UnityEngine.Debug.Log("[Area 7] Post-escape marker written: " + path + " (worldTime=" + worldTime + ")");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Failed to write post-escape marker: " + e.Message);
        }
    }

    public static ulong ReadMarkerEscapeWorldTime()
    {
        try
        {
            string path = GetCompletedMarkerFilePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0UL;

            foreach (string line in File.ReadAllLines(path))
            {
                int eqIndex = line.IndexOf('=');
                if (eqIndex <= 0) continue;
                string key = line.Substring(0, eqIndex);
                string val = line.Substring(eqIndex + 1);
                if (key == "escapeWorldTime" && ulong.TryParse(val, out ulong t))
                    return t;
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Failed to read marker escape world time: " + e.Message);
        }
        return 0UL;
    }

    public static void DeleteCompletedMarker()
    {
        try
        {
            string path = GetCompletedMarkerFilePath();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
                UnityEngine.Debug.Log("[Area 7] Stale post-escape marker deleted: " + path);
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Failed to delete post-escape marker: " + e.Message);
        }
    }

    public static void CleanupRun(string reason, EntityPlayerLocal player = null)
    {
        try
        {
            // Write the post-escape marker BEFORE anything else, so even if
            // a later step throws, the marker persists and the mod knows the
            // run was completed.
            if (reason == "escapeComplete")
                WriteCompletedMarker();

            ChallengeRedeemPatch.CancelScheduledEvents(reason);
            ChallengeRedeemPatch.ResetInternalState(reason);

            Area7CentralRadiation.StopAndClear(player);

            if (player != null)
                RemoveArea7Markers(player);

            TryRemoveTraderHugh(reason);

            string fp = GetSaveFilePath();
            bool existed = !string.IsNullOrEmpty(fp) && File.Exists(fp);
            UnityEngine.Debug.Log("[Area 7] Run cleanup requested (" + reason + "). Path='" + fp + "', existed=" + existed);

            if (existed)
            {
                File.Delete(fp);
                UnityEngine.Debug.Log("[Area 7] Deleted state file (" + reason + "): " + fp);
            }

            // Delete stats file — debrief has already been generated at this point
            Area7RunStats.DeleteStatsFile();

            saveFilePath = "";
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] CleanupRun failed (" + reason + "): " + e.Message);
        }
    }

    public static bool ForceRedeemChallenge(EntityPlayerLocal player, string challengeName)
    {
        if (player == null || string.IsNullOrEmpty(challengeName))
            return false;

        try
        {
            var journal = player.challengeJournal;
            if (journal == null)
            {
                UnityEngine.Debug.LogWarning("[Area 7] ForceRedeemChallenge: No challenge journal");
                return false;
            }

            Challenge target = null;

            // Dictionary lookup - use lowercase
            if (!journal.ChallengeDictionary.TryGetValue(challengeName.ToLower(), out target))
            {
                // Fallback: search the full list case-insensitively
                foreach (Challenge c in journal.Challenges)
                {
                    if (c.ChallengeClass != null &&
                        string.Equals(c.ChallengeClass.Name, challengeName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = c;
                        break;
                    }
                }
            }

            if (target == null)
            {
                UnityEngine.Debug.LogWarning("[Area 7] ForceRedeemChallenge: '" + challengeName + "' not found in dictionary or list");
                return false;
            }

            // v3.0.80: DO NOT call CompleteChallenge here. 7 Days V3.2.0 added a third
            // parameter to it (forceRedeem, giveReward, forceComplete), so a DLL compiled
            // against 3.1 throws at this line on 3.2:
            //
            //   Method not found: void Challenges.Challenge.CompleteChallenge(bool,bool)
            //
            // This is the call the Extraction Order goes through, so on 3.2 reading the
            // order silently did nothing: no redeem, no reward event, no UH-60. Reported by
            // Humble Donkey, who finished the run twice with no helicopter, and reproduced
            // by Fril with `a7 chopper` on a 3.2 install.
            //
            // Instead do exactly what the redeem button does, read from
            // XUiC_ChallengeEntryDescriptionWindow.CompleteCurrentChallenege. Redeem() is
            // parameterless and IDENTICAL across 3.0, 3.1 and 3.2, so this needs no version
            // check and no reflection. A full MemberRef sweep of the mod against the 3.2
            // assembly found CompleteChallenge to be the ONLY incompatibility in the DLL.
            //
            // Objectives are marked complete first so the journal shows the challenge as
            // done rather than as an unfinished entry that somehow paid out.
            if (target.ObjectiveList != null)
            {
                for (int i = 0; i < target.ObjectiveList.Count; i++)
                {
                    if (target.ObjectiveList[i] != null)
                        target.ObjectiveList[i].Complete = true;
                }
            }

            target.ChallengeState = Challenge.ChallengeStates.Redeemed;
            target.Redeem();

            UnityEngine.Debug.Log("[Area 7] ForceRedeemChallenge: Redeemed '" + challengeName + "'");
            return true;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] ForceRedeemChallenge error: " + e.Message);
            return false;
        }
    }

    private static void RemoveArea7Markers(EntityPlayerLocal player)
    {
        try
        {
            object collection = player?.Waypoints?.Collection;
            if (collection == null) return;

            const string waypointName = "Camp Frilsville";
            Vector3i campPosI = new Vector3i(ChallengeRedeemPatch.GetHughSpawnPos());

            var toRemove = new List<Waypoint>();

            IEnumerator enumerator = Area7TryGetEnumerator(collection);
            if (enumerator != null)
            {
                while (enumerator.MoveNext())
                {
                    Waypoint wp = enumerator.Current as Waypoint;
                    if (wp == null) continue;

                    string wpName = TryGetWaypointName(wp);

                    bool match =
                        (!string.IsNullOrEmpty(wpName) && wpName == waypointName) ||
                        (wp.navObject != null && wp.navObject.name == waypointName) ||
                        wp.pos == campPosI;

                    if (match) toRemove.Add(wp);
                }
            }

            foreach (var wp in toRemove)
            {
                if (wp.navObject != null)
                {
                    TryUnregisterNavObject(wp.navObject);
                    wp.navObject = null;
                }

                Area7TryRemoveFromCollection(collection, wp);
            }

            if (toRemove.Count > 0)
                UnityEngine.Debug.Log("[Area 7] Removed " + toRemove.Count + " Area7 waypoint(s)/marker(s).");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Error removing Area7 markers: " + e.Message);
        }
    }

    // The Camp Frilsville waypoint is saved (its icon field persists), but its runtime nav object
    // (the map marker) is re-created WITHOUT the custom trader icon on load, so after a restart the
    // marker loses its trader look. Re-register that one waypoint's nav object with the trader icon
    // here. No second marker -- the existing one is just restored to how it looked on first deploy.
    private static void RefreshCampMarkerIcon(EntityPlayerLocal player)
    {
        try
        {
            object collection = player?.Waypoints?.Collection;
            if (collection == null) return;

            const string waypointName = "Camp Frilsville";
            const string traderIcon = "ui_game_symbol_trader";
            Vector3i campPosI = new Vector3i(ChallengeRedeemPatch.GetHughSpawnPos());

            IEnumerator enumerator = Area7TryGetEnumerator(collection);
            if (enumerator == null) return;

            while (enumerator.MoveNext())
            {
                Waypoint wp = enumerator.Current as Waypoint;
                if (wp == null) continue;

                string wpName = TryGetWaypointName(wp);
                bool match =
                    (!string.IsNullOrEmpty(wpName) && wpName == waypointName) ||
                    (wp.navObject != null && wp.navObject.name == waypointName) ||
                    wp.pos == campPosI;
                if (!match) continue;

                wp.icon = traderIcon;

                if (wp.navObject != null)
                {
                    TryUnregisterNavObject(wp.navObject);
                    wp.navObject = null;
                }

                NavObjectManager mgr = NavObjectManager.Instance;
                if (mgr != null)
                {
                    // Same offset as the deploy path, so the marker doesn't move after a restart.
                    Vector3 markerPos = new Vector3(wp.pos.x, wp.pos.y + ChallengeRedeemPatch.CampMarkerHeightOffset, wp.pos.z);
                    wp.navObject = mgr.RegisterNavObject("waypoint", markerPos, traderIcon, false, -1, null);
                    if (wp.navObject != null)
                        wp.navObject.name = waypointName;
                }

                // Re-establish the tracked/active state on load. This is exactly what the map's
                // "Track Waypoint" button does under the hood (set the waypoint's bTracked and its
                // nav object's IsActive), and IsActive is what draws the in-world distance/direction
                // label. The game doesn't restore which waypoint you were tracking, so we re-track the
                // camp here -- that brings back the "X m to extraction" pull without a manual click.
                wp.bTracked = true;
                if (wp.navObject != null)
                    wp.navObject.IsActive = true;

                UnityEngine.Debug.Log("[Area 7] Camp Frilsville marker icon re-applied (trader) on load.");
                break;
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Error refreshing camp marker icon: " + e.Message);
        }
    }

    private static IEnumerator Area7TryGetEnumerator(object collection)
    {
        try
        {
            var m = collection.GetType().GetMethod("GetEnumerator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return m?.Invoke(collection, null) as IEnumerator;
        }
        catch { return null; }
    }

    private static void Area7TryRemoveFromCollection(object collection, Waypoint wp)
    {
        try
        {
            var t = collection.GetType();

            var remove = t.GetMethod("Remove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (remove != null)
            {
                var ps = remove.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(typeof(Waypoint)))
                {
                    remove.Invoke(collection, new object[] { wp });
                    return;
                }
            }

            var removeObj = t.GetMethod("Remove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(object) }, null);
            removeObj?.Invoke(collection, new object[] { wp });
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Failed to remove waypoint from collection: " + e.Message);
        }
    }

    private static string TryGetWaypointName(Waypoint wp)
    {
        try
        {
            var nameObj = wp.name;
            if (nameObj == null) return null;
            var textProp = nameObj.GetType().GetProperty("Text");
            return textProp != null ? textProp.GetValue(nameObj, null) as string : nameObj.ToString();
        }
        catch { return null; }
    }

    private static void TryUnregisterNavObject(NavObject navObject)
    {
        try
        {
            var mgr = NavObjectManager.Instance;
            if (mgr == null || navObject == null) return;

            var mgrType = mgr.GetType();
            var navType = navObject.GetType();

            foreach (var name in new[] { "UnregisterNavObject", "UnRegisterNavObject", "DeregisterNavObject", "DeRegisterNavObject", "RemoveNavObject" })
            {
                var m = mgrType.GetMethod(name, new[] { navType });
                if (m != null) { m.Invoke(mgr, new object[] { navObject }); return; }
            }

            int? id = TryGetNavObjectId(navObject);
            if (id.HasValue)
            {
                foreach (var name in new[] { "UnregisterNavObject", "UnRegisterNavObject", "DeregisterNavObject", "DeRegisterNavObject", "RemoveNavObject" })
                {
                    var m = mgrType.GetMethod(name, new[] { typeof(int) });
                    if (m != null) { m.Invoke(mgr, new object[] { id.Value }); return; }
                }
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Error unregistering NavObject: " + e.Message);
        }
    }

    private static int? TryGetNavObjectId(NavObject navObject)
    {
        try
        {
            var t = navObject.GetType();
            foreach (var name in new[] { "id", "Id", "ID", "navObjectId", "NavObjectId" })
            {
                var p = t.GetProperty(name);
                if (p != null && p.PropertyType == typeof(int)) return (int)p.GetValue(navObject, null);
                var f = t.GetField(name);
                if (f != null && f.FieldType == typeof(int)) return (int)f.GetValue(navObject);
            }
            return null;
        }
        catch { return null; }
    }

    private static void TryRemoveTraderHugh(string reason)
    {
        try
        {
            World world = GameManager.Instance?.World;
            if (world == null) return;

            int hughClassId = EntityClass.FromString("npcTraderHugh");
            if (hughClassId == -1) return;

            var nearby = new List<Entity>();
            world.GetEntitiesInBounds(typeof(EntityNPC), new Bounds(ChallengeRedeemPatch.GetHughSpawnPos(), Vector3.one * 120f), nearby);

            Entity found = null;
            foreach (var e in nearby)
            {
                if (e is EntityNPC && e.entityClass == hughClassId) { found = e; break; }
            }

            if (found == null) return;

            if (TryInvokeWorldRemoveEntity(world, found.entityId))
            {
                UnityEngine.Debug.Log("[Area 7] Removed Trader Hugh via World remove (" + reason + "). id=" + found.entityId);
                return;
            }

            if (TryInvokeEntityDespawn(found))
                UnityEngine.Debug.Log("[Area 7] Removed Trader Hugh via Entity despawn (" + reason + "). id=" + found.entityId);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Error removing Trader Hugh: " + e.Message);
        }
    }

    private static bool TryInvokeWorldRemoveEntity(World world, int entityId)
    {
        try
        {
            var wt = world.GetType();

            var m1 = wt.GetMethod("RemoveEntity", new[] { typeof(int) });
            if (m1 != null) { m1.Invoke(world, new object[] { entityId }); return true; }

            var reasonType = wt.Assembly.GetType("EnumRemoveEntityReason");
            if (reasonType != null)
            {
                var m2 = wt.GetMethod("RemoveEntity", new[] { typeof(int), reasonType });
                if (m2 != null) { m2.Invoke(world, new object[] { entityId, Enum.Parse(reasonType, "Despawned") }); return true; }
            }

            var m3 = wt.GetMethod("RemoveEntityFromWorld", new[] { typeof(int) });
            if (m3 != null) { m3.Invoke(world, new object[] { entityId }); return true; }

            return false;
        }
        catch { return false; }
    }

    private static bool TryInvokeEntityDespawn(Entity e)
    {
        try
        {
            var et = e.GetType();
            foreach (var name in new[] { "Despawn", "ForceDespawn", "SetDead", "Kill" })
            {
                var m = et.GetMethod(name, Type.EmptyTypes);
                if (m != null) { m.Invoke(e, null); return true; }
            }
            return false;
        }
        catch { return false; }
    }

    private static System.Collections.IEnumerator RefreshChunksAroundOrigin()
    {
        yield return new WaitForSeconds(5f);

        World world = GameManager.Instance?.World;
        if (world == null) yield break;

        Vector3i nudgePos = new Vector3i(-8, 40, -25);
        BlockValue original = world.GetBlock(nudgePos);
        world.SetBlockRPC(nudgePos, BlockValue.Air);
        yield return new WaitForSeconds(0.5f);

        world = GameManager.Instance?.World;
        if (world == null) yield break;

        world.SetBlockRPC(nudgePos, original);
    }


    private static void OnGameStartDone(ref ModEvents.SGameStartDoneData data)
    {
        saveFilePath = "";
        UnityEngine.Debug.Log("[Area 7] Game loaded, checking for scheduled events...");

        EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
        if (player == null) return;

        World world = GameManager.Instance.World;

        // If this save has been completed (marker file present), check whether the
        // marker is genuine or stale. Stale = world time has gone backwards (only
        // possible if a fresh save reused the same save name). Genuine = keep the
        // mod dormant; the player won this save and is free-roaming.
        if (Area7ChallengeMod.IsRunCompleted())
        {
            ulong markerWorldTime = Area7ChallengeMod.ReadMarkerEscapeWorldTime();
            if (markerWorldTime > 0 && world.worldTime < markerWorldTime)
            {
                // World time is earlier than the recorded escape — the save has been
                // replaced. Clean up the stale marker and proceed as a fresh run.
                UnityEngine.Debug.Log("[Area 7] Stale post-escape marker detected (current worldTime " + world.worldTime + " < marker worldTime " + markerWorldTime + ") — cleaning up.");
                Area7ChallengeMod.DeleteCompletedMarker();
                // Fall through to normal fresh-run setup
            }
            else
            {
                // Genuine post-escape state — mod stays dormant for the rest of
                // this save's lifetime. Skip fresh-run reset, radiation, ambient
                // sounds, Hugh respawn, scheduled-event restore.
                UnityEngine.Debug.Log("[Area 7] Post-escape marker present — mod dormant on this save.");
                return;
            }
        }

        // Initialise stats tracking
        int playerLevel = (player.Progression != null) ? player.Progression.Level : 0;

        // Reset post-death mode on every world load, and invalidate any post-death
        // coroutine still running from a previous run (see sessionId).
        Area7PermadeathCleanupPatch.isPostDeathMode = false;
        Area7PermadeathCleanupPatch.sessionId++;

        // Load saved stats or start fresh.
        //
        // HISTORY, because this block has been wrong twice.
        //
        // Up to v3.0.60 a failed stats load triggered a full progression wipe:
        // Level = 1, SkillPoints = 0, ResetProgression(true, true, true).
        // LoadFromFile returns false for a deleted file, an unreadable one, a
        // null world and a null mod path alike, so live characters were being
        // wiped for reasons that had nothing to do with the run being new:
        // replacing the mod folder on update, the shared per-world stats file
        // being deleted by a death on a DIFFERENT save, or a read failure.
        // Reported by Holls 13 Aug, reproduced by Fril 14 Aug.
        //
        // v3.0.61 gated the wipe behind "level <= 1 AND day <= 1". Measurement
        // then showed that predicate is far too weak: an Area 7 character with
        // 32 kills, four bags and a redeemed challenge still reads level 1 on
        // day 1, because the whole early game happens on day 1 in the bunker.
        //
        // v3.0.63 removes the progression wipe entirely. A brand-new 7 Days
        // character is ALREADY level 1 with 0 skill points and the same 63
        // seeded progression values (measured, PROGDIAG build v3.0.62), so
        // Level = 1, SkillPoints = 0 and ResetProgression were no-ops on the
        // one case the branch existed for, and destructive on every other.
        //
        // What the branch was actually FOR, per the 19 March 2026 devlog entry,
        // is stopping gamestage carrying over between runs. That is
        // gameStageBornAtWorldTime, and that single line is what remains. It
        // is still guarded, because resetting an established character's
        // gamestage clock would hand them easier zombies; on a genuinely new
        // save the assignment is close to a no-op anyway.
        if (!Area7RunStats.LoadFromFile())
        {
            Area7RunStats.Reset(world.worldTime, playerLevel);

            bool levelLooksNew = (player.Progression == null) || (player.Progression.Level <= 1);
            bool worldLooksNew = GameUtils.WorldTimeToDays(world.worldTime) <= 1;

            if (levelLooksNew && worldLooksNew)
            {
                player.gameStageBornAtWorldTime = world.worldTime;
                UnityEngine.Debug.Log("[Area 7] Fresh run — stats reset, gamestage clock reset. Progression untouched.");
            }
            else
            {
                // v3.0.66: remember that this run's timing baseline is wrong. Sticky, and
                // persisted, so the completion code can declare the run's time unverified
                // rather than posting an impossibly fast run to the leaderboard.
                Area7RunStats.statsWereReset = true;
                Area7RunStats.SaveToFile();

                UnityEngine.Debug.LogWarning("[Area 7] No stats file found, but this character is not new (level "
                    + playerLevel + ", day " + GameUtils.WorldTimeToDays(world.worldTime)
                    + "). Fresh stats only — progression and gamestage clock untouched. Run marked time-unverified.");
            }
        }

        GameManager.Instance.StartCoroutine(RefreshChunksAroundOrigin());

        // Start ambient sound coroutine
        Area7AmbientSounds.Start();

        Area7CentralRadiation.Start();

        // Restore Hugh spawned state
        string hughData = ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_HUGH_SPAWNED);
        if (hughData == "true")
        {
            ChallengeRedeemPatch.SetHughSpawned(true);

            string savedPos = ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_HUGH_POSITION);
            Vector3 hughSpawnPos = ChallengeRedeemPatch.ParseVector3(savedPos, ChallengeRedeemPatch.GetHughSpawnPos());
            ChallengeRedeemPatch.SetCurrentHughSpawnPos(hughSpawnPos);

            bool hughFound = false;

            string savedEntityId = ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_HUGH_ENTITY_ID);
            if (!string.IsNullOrEmpty(savedEntityId) && int.TryParse(savedEntityId, out int hughEntityId))
            {
                Entity e = world.GetEntity(hughEntityId);
                if (e != null && e is EntityNPC && !e.IsDead())
                {
                    hughFound = true;
                    UnityEngine.Debug.Log("[Area 7] Hugh found by entity ID " + hughEntityId);
                }
            }

            if (!hughFound)
            {
                var nearby = new List<Entity>();
                world.GetEntitiesInBounds(typeof(EntityNPC), new Bounds(hughSpawnPos, Vector3.one * 50f), nearby);

                foreach (Entity entity in nearby)
                {
                    if (entity is EntityNPC)
                    {
                        string className = EntityClass.list[entity.entityClass].entityClassName;
                        if (className != null && className.Contains("Hugh")) { hughFound = true; break; }
                    }
                }
            }

            if (!hughFound)
            {
                UnityEngine.Debug.Log("[Area 7] Hugh not found - respawning...");
                ChallengeRedeemPatch.SpawnTraderHugh(world, hughSpawnPos);
            }

            // Restore the trader icon on the Camp Frilsville map marker (the nav object is recreated
            // without the custom icon on load, which is why the icon went missing after a restart).
            RefreshCampMarkerIcon(player);
        }

        // Restore scheduled airdrop
        string airdropTime = ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_AIRDROP_TIME);
        if (!string.IsNullOrEmpty(airdropTime) && ulong.TryParse(airdropTime, out ulong scheduledAirdropTime))
        {
            if (world.worldTime < scheduledAirdropTime)
            {
                UnityEngine.Debug.Log("[Area 7] Restoring airdrop scheduled for world time " + scheduledAirdropTime);
                var airDropComp = world.aiDirector.GetComponent<AIDirectorAirDropComponent>();
                ChallengeRedeemPatch.SetRestoreAirdropCoroutine(
                    GameManager.Instance.StartCoroutine(
                        ChallengeRedeemPatch.MonitorScheduledAirdrop(world, airDropComp, scheduledAirdropTime, player.position)
                    )
                );
            }
        }

        // Restore scheduled blood moon
        string bloodMoonTime = ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_BLOODMOON_TIME);
        if (!string.IsNullOrEmpty(bloodMoonTime) && ulong.TryParse(bloodMoonTime, out ulong scheduledBMTime))
        {
            if (world.worldTime < scheduledBMTime)
            {
                UnityEngine.Debug.Log("[Area 7] Restoring blood moon scheduled for world time " + scheduledBMTime);
                ChallengeRedeemPatch.SetRestoreBloodMoonCoroutine(
                    GameManager.Instance.StartCoroutine(
                        ChallengeRedeemPatch.MonitorScheduledBloodMoon(world, scheduledBMTime)
                    )
                );
            }
        }
    }
}

// ---------------------------------------------------------------
// MinEventActionRedeemChallenge - XML action to redeem challenges
// ---------------------------------------------------------------
public class MinEventActionRedeemChallenge : MinEventActionBase
{
    public string challenge;

    public override void Execute(MinEventParams _params)
    {
        if (string.IsNullOrEmpty(challenge))
            return;

        EntityPlayerLocal player = _params.Self as EntityPlayerLocal;
        if (player == null)
            return;

        Area7ChallengeMod.ForceRedeemChallenge(player, challenge);
    }
}

// ---------------------------------------------------------------
// Ambient vent sounds
// ---------------------------------------------------------------
public class Area7AmbientSounds
{
    private static Coroutine coAmbient;

    public static void Start()
    {
        if (coAmbient != null)
        {
            GameManager.Instance.StopCoroutine(coAmbient);
            coAmbient = null;
        }

        coAmbient = GameManager.Instance.StartCoroutine(AmbientSoundLoop());
    }

    private static IEnumerator AmbientSoundLoop()
    {
        float waitTime = UnityEngine.Random.Range(300f, 1200f);
        yield return new WaitForSeconds(waitTime);

        while (true)
        {
            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();

            if (player != null && player.position.y < 22f)
            {
                float offsetX = UnityEngine.Random.Range(-10f, 10f);
                float offsetZ = UnityEngine.Random.Range(-10f, 10f);
                Vector3 soundPos = new Vector3(
                    player.position.x + offsetX,
                    player.position.y + 5f,
                    player.position.z + offsetZ
                );

                Audio.Manager.Play(soundPos, "sleeperspawnmetal", -1, false);
            }

            waitTime = UnityEngine.Random.Range(300f, 1200f);
            yield return new WaitForSeconds(waitTime);
        }
    }
}

public static class Area7CentralRadiation
{
    private static Coroutine coZone;

    public static float HalfSize = 54f;
    private const float CentreX = -2f;

    // True if a world position sits inside the Area 7 compound (same square bounds as the radiation zone).
    public static bool IsInsideCompound(Vector3 pos)
    {
        return Math.Abs(pos.x - CentreX) <= HalfSize && Math.Abs(pos.z) <= HalfSize;
    }

    // v3.0.74: the item that grants the permit. Vanilla's Wasteland biome badge.
    private const string PermitArmorItemName = "biomeWeatherItem4";

    // True when the respirator is actually in an equipment slot, whatever the buffs say.
    public static bool IsPermitArmorWorn(EntityPlayerLocal player)
    {
        try
        {
            if (player == null) return false;
            Equipment eq = player.equipment;
            if (eq == null) return false;

            int slots = eq.GetSlotCount();
            for (int i = 0; i < slots; i++)
            {
                ItemValue iv = eq.GetSlotItem(i);
                if (iv == null || iv.IsEmpty()) continue;
                ItemClass ic = iv.ItemClass;
                if (ic != null && ic.Name == PermitArmorItemName) return true;
            }
        }
        catch { }
        return false;
    }

    // Bring the buff into line with the worn state, and say so when it was wrong.
    private static void ReconcileArmorPermit(EntityPlayerLocal player)
    {
        try
        {
            if (player == null || player.Buffs == null) return;

            bool worn = IsPermitArmorWorn(player);
            bool buffed = player.Buffs.HasBuff(ArmorPermitBuffName);
            if (worn == buffed) return;

            if (worn)
            {
                player.Buffs.AddBuff(ArmorPermitBuffName);
                UnityEngine.Debug.Log("[Area 7] Respirator worn but permit buff missing — buff added. "
                    + "The equip trigger did not fire.");
            }
            else
            {
                player.Buffs.RemoveBuff(ArmorPermitBuffName);
                UnityEngine.Debug.Log("[Area 7] Permit buff present but respirator NOT worn — buff removed. "
                    + "The unequip trigger did not fire.");
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] ReconcileArmorPermit failed: " + e.Message);
        }
    }

    private const string RadiationBuffName = "buffArea7CentralRadiation";
    private const string PermitBuffName = "buffArea7RadPermit";
    private const string ArmorPermitBuffName = "buffArea7RadPermitArmor";

    public static void Start()
    {
        Stop();

        if (GameManager.Instance == null) return;

        coZone = GameManager.Instance.StartCoroutine(ZoneLoop());
        UnityEngine.Debug.Log("[Area 7] Central radiation zone started.");
    }

    public static void Stop()
    {
        if (coZone == null) return;
        if (GameManager.Instance == null) return;

        GameManager.Instance.StopCoroutine(coZone);
        coZone = null;
    }

    public static void StopAndClear(EntityPlayerLocal player = null)
    {
        Stop();

        try
        {
            EntityPlayerLocal p = player ?? GameManager.Instance?.World?.GetPrimaryPlayer();
            if (p == null) return;

            if (p.Buffs != null && p.Buffs.HasBuff(RadiationBuffName))
                p.Buffs.RemoveBuff(RadiationBuffName);
        }
        catch { }
    }

    private static IEnumerator ZoneLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(1f);

            World world = GameManager.Instance?.World;
            if (world == null) continue;

            EntityPlayerLocal player = world.GetPrimaryPlayer();
            if (player == null) continue;
            if (player.IsDead()) continue;
            if (player.Buffs == null) continue;

            // --- Stats tracking: gamestage and infection ---
            try
            {
                Area7RunStats.UpdateGamestage(player.gameStage);

                float infection = player.GetCVar(".infectionDisplayPerc");
                Area7RunStats.UpdateInfection(infection);
            }
            catch { }

            Vector3 pos = player.position;

            bool outside =
                Math.Abs(pos.x - CentreX) > HalfSize ||
                Math.Abs(pos.z) > HalfSize;

            // v3.0.74: the DLL is now authoritative for the armour permit buff.
            //
            // Until now the buff was driven purely by the item's onSelfEquipStart /
            // onSelfEquipStop triggers in items.xml. Those do not fire on every route into
            // and out of the badge slot: equipping via the right-click "Wear" action calls
            // PlayerEquipment.EquipItem directly and left the player wearing the respirator
            // with NO buff, and on 18 Aug the reverse also happened, buff still showing after
            // the respirator had been moved to the toolbelt. That second case is dangerous
            // rather than cosmetic, because the buff is what stops ZoneLoop irradiating you.
            //
            // So instead of trusting the triggers, reconcile against the worn state once a
            // second, here, where we are already looking at the player. Any correction is
            // logged with the direction of the drift, which also tells us which action caused
            // it without needing a separate diagnostic build.
            ReconcileArmorPermit(player);

            bool hasPermit =
                player.Buffs.HasBuff(PermitBuffName) ||
                player.Buffs.HasBuff(ArmorPermitBuffName);

            if (outside && !hasPermit)
            {
                if (!player.Buffs.HasBuff(RadiationBuffName))
                    player.Buffs.AddBuff(RadiationBuffName);
            }
            else
            {
                if (player.Buffs.HasBuff(RadiationBuffName))
                    player.Buffs.RemoveBuff(RadiationBuffName);
            }
        }
    }
}

// =====================================================================================
// v3.0.75: AUTO-REDEEM Area 7's own challenges.
//
// A challenge's reward_event does NOT fire when its objectives are met. Challenge.
// HandleComplete just marks it complete and shows a tooltip; Challenge.Redeem is what
// reads ChallengeClass.RewardEvent and fires it, and Redeem only runs when the player
// opens the journal and clicks. So a player could place the transmitter, get the
// "challenge complete" toast, and have NOTHING happen -- no signal, no blood moon, no
// airdrop, no Hugh. Fril watched exactly that happen to a player on stream on 18 Aug
// and had to tell them in chat. Without him watching, the mod simply looks broken.
//
// There is no XML route to this. `redeem_always` exists but only lets a player claim a
// challenge EARLY; it never claims for them.
//
// So we mirror precisely what the redeem button does, read from
// XUiC_ChallengeEntryDescriptionWindow.CompleteCurrentChallenege:
//     challenge.ChallengeState = ChallengeStates.Redeemed;
//     challenge.Redeem();
// and nothing else. The UI refresh in that method is the window updating itself, which
// does not apply here.
//
// TWO TRAPS, both real:
//
// 1. DO NOT call CompleteChallenge from here. CompleteChallenge itself calls
//    HandleComplete, so it would recurse forever through this very postfix.
//
// 2. CompleteChallenge ALSO sets the state and calls Redeem straight after
//    HandleComplete returns. Area7ChallengeMod.ForceRedeemChallenge goes through
//    CompleteChallenge, and it is used for escapeArea7 in three places. Without a guard
//    this postfix would redeem, then CompleteChallenge would redeem AGAIN, firing the
//    reward event twice. Hence Area7_CompleteChallengeGuardPatch below.
//
// Scope: Area 7's own challenges only, matched by name. Vanilla challenges are left
// alone. ADD ANY NEW AREA 7 CHALLENGE TO THIS LIST or it will not auto-redeem.
// =====================================================================================
[HarmonyPatch(typeof(Challenge), "CompleteChallenge")]
public class Area7_CompleteChallengeGuardPatch
{
    public static bool InCompleteChallenge;

    static void Prefix() { InCompleteChallenge = true; }

    // Finalizer rather than Postfix so the flag is cleared even if something throws.
    static void Finalizer() { InCompleteChallenge = false; }
}

[HarmonyPatch(typeof(Challenge), "HandleComplete")]
public class Area7_AutoRedeemPatch
{
    private static readonly HashSet<string> Area7Challenges = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "killZombiesArea7",
        "gatherSteel", "gatherMechanicalParts", "gatherElectricalParts",
        "gatherBatteries", "gatherDuctTape",
        "craftTransmitter", "deployTransmitter",
        "escapeArea7"
    };

    private static bool reentry;

    static void Postfix(Challenge __instance)
    {
        try
        {
            if (__instance == null || __instance.ChallengeClass == null) return;
            if (Area7_CompleteChallengeGuardPatch.InCompleteChallenge) return;
            if (reentry) return;

            string name = __instance.ChallengeClass.Name;
            if (string.IsNullOrEmpty(name) || !Area7Challenges.Contains(name)) return;

            // v3.0.76: THE GUARD THAT WAS MISSING. HandleComplete is called speculatively —
            // every time an objective updates, as an "am I finished yet" check — and it
            // RETURNS EARLY when the objectives are not all complete. A Harmony Postfix runs
            // regardless of that early return, so 3.0.75 redeemed challenges that had not
            // been completed at all. Fril gathered the steel and the whole Generator group
            // redeemed itself, unlocking Deploy, without him having any of the other parts.
            //
            // HandleComplete only sets ChallengeState to Completed once every objective
            // really is done, so that state IS the proof the work happened. Anything else,
            // including Active, means it bailed out and there is nothing to redeem.
            if (__instance.ChallengeState != Challenge.ChallengeStates.Completed) return;

            reentry = true;
            try
            {
                __instance.ChallengeState = Challenge.ChallengeStates.Redeemed;
                __instance.Redeem();
                UnityEngine.Debug.Log("[Area 7] Auto-redeemed challenge: " + name);
            }
            finally
            {
                reentry = false;
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] Auto-redeem failed: " + e.Message);
        }
    }
}

[HarmonyPatch(typeof(Challenge), "Redeem")]
public class ChallengeRedeemPatch
{
    private static bool hughSpawned = false;
    private static Vector3 currentHughSpawnPos = Vector3.zero;

    public const string KEY_AIRDROP_TIME = "Area7_AirdropTime";
    public const string KEY_BLOODMOON_TIME = "Area7_BloodMoonTime";
    public const string KEY_HUGH_SPAWNED = "Area7_HughSpawned";
    public const string KEY_HUGH_POSITION = "Area7_HughPosition";
    public const string KEY_HUGH_ENTITY_ID = "Area7_HughEntityId";
    public const string KEY_SIGNAL_SENT = "Area7_SignalSent";

    // Height of the Camp Frilsville nav marker ABOVE Hugh's actual position.
    // The deploy handler used to hard-code +58 here while the on-load refresh used +0, so the
    // marker floated ~58m up on first deploy and then snapped down to ground after a restart.
    // Both paths now use this one value: 0 = sitting on Hugh, raise it if the marker needs to
    // clear the base structures to stay visible from a distance. Currently 10 = hovering
    // just above Hugh, clear of the tents and sandbag walls.
    public const float CampMarkerHeightOffset = 10f;

    private static readonly Vector3[] hughSpawnLocations = new Vector3[]
    {
        new Vector3(-449.5f, 42f,  1000.5f),
        new Vector3( 870.5f, 56f,  1305.5f),
        new Vector3(-941f,   43f,    -15f),
        new Vector3( 738f,   57f,   -317f),
        new Vector3(-632f,   53f,  -1359f),
        new Vector3( 744f,   49f,   -930f),
    };

    private static readonly float[] hughSpawnRotations = new float[]
    {
         45f, 270f, 0f, 0f, 180f, 0f,
    };

    public static Vector3 GetHughSpawnPos()
    {
        return (currentHughSpawnPos == Vector3.zero) ? hughSpawnLocations[0] : currentHughSpawnPos;
    }

    public static void SetCurrentHughSpawnPos(Vector3 pos) => currentHughSpawnPos = pos;

    private static float currentHughRotation = 0f;

    private static Vector3 PickRandomHughSpawnPos()
    {
        int index = UnityEngine.Random.Range(0, hughSpawnLocations.Length);
        currentHughSpawnPos = hughSpawnLocations[index];
        currentHughRotation = hughSpawnRotations[index];
        UnityEngine.Debug.Log("[Area 7] Selected exit location index " + index + ": " + currentHughSpawnPos + " rotation " + currentHughRotation);
        return currentHughSpawnPos;
    }

    public static Vector3 ParseVector3(string saved, Vector3 defaultVal)
    {
        if (string.IsNullOrEmpty(saved)) return defaultVal;
        try
        {
            var parts = saved.Split(',');
            if (parts.Length == 3)
            {
                float x = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                float y = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                float z = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                return new Vector3(x, y, z);
            }
        }
        catch { }
        return defaultVal;
    }

    private static string Vector3ToString(Vector3 v) =>
        v.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
        v.y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
        v.z.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Coroutine coBloodMoon;
    private static Coroutine coPlane;
    private static Coroutine coRestoreBloodMoon;
    private static Coroutine coRestoreAirdrop;
    private static Coroutine coCrateDrop;

    public static void SetRestoreAirdropCoroutine(Coroutine co) => coRestoreAirdrop = co;
    public static void SetRestoreBloodMoonCoroutine(Coroutine co) => coRestoreBloodMoon = co;
    public static void SetCrateDropCoroutine(Coroutine co) => coCrateDrop = co;

    public static void CancelScheduledEvents(string reason)
    {
        try
        {
            var gm = GameManager.Instance;
            if (gm != null)
            {
                StopIfRunning(gm, ref coBloodMoon, "bloodMoon (" + reason + ")");
                StopIfRunning(gm, ref coPlane, "plane (" + reason + ")");
                StopIfRunning(gm, ref coRestoreBloodMoon, "restoreBloodMoon (" + reason + ")");
                StopIfRunning(gm, ref coRestoreAirdrop, "restoreAirdrop (" + reason + ")");
                StopIfRunning(gm, ref coCrateDrop, "crateDrop (" + reason + ")");
            }

            var world = gm?.World;
            var player = world?.GetPrimaryPlayer();
            if (player != null)
            {
                SavePlayerData(player, KEY_AIRDROP_TIME, "");
                SavePlayerData(player, KEY_BLOODMOON_TIME, "");
            }

            UnityEngine.Debug.Log("[Area 7] CancelScheduledEvents complete (" + reason + ")");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] CancelScheduledEvents error (" + reason + "): " + e.Message);
        }
    }

    public static void ResetInternalState(string reason)
    {
        hughSpawned = false;
        currentHughSpawnPos = Vector3.zero;
    }

    private static void StopIfRunning(MonoBehaviour owner, ref Coroutine co, string label)
    {
        if (co == null) return;
        owner.StopCoroutine(co);
        co = null;
    }

    private static bool TrySplitKeyValue(string line, out string key, out string value)
    {
        key = null; value = null;
        if (string.IsNullOrEmpty(line)) return false;
        int idx = line.IndexOf('=');
        if (idx <= 0) return false;
        key = line.Substring(0, idx);
        value = (idx + 1 < line.Length) ? line.Substring(idx + 1) : string.Empty;
        return true;
    }

    public static void SavePlayerData(EntityPlayerLocal player, string key, string value)
    {
        try
        {
            string filePath = Area7ChallengeMod.GetSaveFilePath();
            var data = new Dictionary<string, string>();

            if (File.Exists(filePath))
            {
                foreach (string line in File.ReadAllLines(filePath))
                    if (TrySplitKeyValue(line, out var k, out var v))
                        data[k] = v;
            }

            if (string.IsNullOrEmpty(value)) data.Remove(key);
            else data[key] = value;

            var lines = new List<string>(data.Count);
            foreach (var kvp in data)
                lines.Add(kvp.Key + "=" + kvp.Value);

            File.WriteAllLines(filePath, lines.ToArray());
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Error saving data: " + e.Message);
        }
    }

    public static string LoadPlayerData(EntityPlayerLocal player, string key)
    {
        try
        {
            string filePath = Area7ChallengeMod.GetSaveFilePath();
            if (!File.Exists(filePath)) return null;

            foreach (string line in File.ReadAllLines(filePath))
            {
                if (!TrySplitKeyValue(line, out var k, out var v)) continue;
                if (k == key) return v;
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Error loading data: " + e.Message);
        }
        return null;
    }

    public static void SetHughSpawned(bool spawned) => hughSpawned = spawned;

    public static IEnumerator MonitorScheduledAirdrop(World world, AIDirectorAirDropComponent airDropComp, ulong targetTime, Vector3 playerPos)
    {
        while (world.worldTime < targetTime)
            yield return new WaitForSeconds(30f);

        UnityEngine.Debug.Log("[Area 7] Airdrop time reached! Spawning plane...");
        yield return SpawnPlane(world, airDropComp, playerPos);

        EntityPlayerLocal player = world.GetPrimaryPlayer();
        if (player != null)
            SavePlayerData(player, KEY_AIRDROP_TIME, "");
    }

    // Length of "Area 7 Ain't Home" (3:18) plus a small tail so the game music does not
    // fade back in over the song's own ending. If Fril ever swaps the song, update this to
    // roughly the new track length in seconds.
    private const float EXTRACTION_SONG_SECONDS = 200f;

    // Resume the dynamic background music once the extraction song has played out. Paired
    // with the dmsConductor.OnPauseGame() call at the escape-complete song cue. Guarded by
    // sessionId because coroutines started on GameManager SURVIVE quitting to the menu - if
    // the player quits mid-song we must NOT reach into the next session and un-pause the wrong
    // game's music. Re-resolves the current player and its manager at resume time rather than
    // trusting the captured reference. Any failure is swallowed - worst case the dynamic music
    // stays paused until the next natural game event that toggles it, never a crash.
    public static IEnumerator ResumeDynamicMusicAfterSong(EntityPlayerLocal songPlayer)
    {
        int startedInSession = Area7PermadeathCleanupPatch.sessionId;

        yield return new WaitForSeconds(EXTRACTION_SONG_SECONDS);

        if (Area7PermadeathCleanupPatch.sessionId != startedInSession)
        {
            UnityEngine.Debug.Log("[Area 7] Session changed during extraction song - not resuming dynamic music (avoids touching the next game).");
            yield break;
        }

        try
        {
            // Resume the live system. Unconditional and idempotent: FadeIn and UnPause when
            // nothing was faded or paused is what the game does every time the player closes
            // the pause menu, so the worst case is a no-op. That guarantees the music can
            // never be left silenced by us.
            World w = GameManager.Instance != null ? GameManager.Instance.World : null;
            DynamicMusic.Conductor cond = (w != null) ? w.dmsConductor : null;
            if (cond != null)
            {
                cond.OnUnPauseGame();
                DynamicMusic.ISection sect = cond.CurrentSection;
                if (sect != null) sect.FadeIn();
                UnityEngine.Debug.Log("[Area 7] Dynamic music resumed after extraction song.");
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] Could not resume dynamic music: " + e.Message);
        }
    }

    public static IEnumerator MonitorScheduledBloodMoon(World world, ulong targetTime)
    {
        while (world.worldTime < targetTime)
            yield return new WaitForSeconds(30f);

        UnityEngine.Debug.Log("[Area 7] Blood moon time reached! Triggering...");
        var bloodMoonComp = world.aiDirector.GetComponent<AIDirectorBloodMoonComponent>();
        if (bloodMoonComp != null)
        {
            bool success = bloodMoonComp.SetForToday(false);
            UnityEngine.Debug.Log("[Area 7] Blood moon set for today. Success: " + success);
        }

        EntityPlayerLocal player = world.GetPrimaryPlayer();
        if (player != null)
        {
            GameManager.ShowTooltip(player, "WARNING: Zombies detected. Blood moon protocol initiated!", (string)null, null, null, false, false, 0f);
            SavePlayerData(player, KEY_BLOODMOON_TIME, "");
        }
    }

    static void Postfix(Challenge __instance)
    {
        string challengeName = __instance.ChallengeClass.Name;
        UnityEngine.Debug.Log("[Area 7] Challenge redeemed: " + challengeName);

        EntityPlayerLocal player = GameManager.Instance.World.GetPrimaryPlayer();
        if (player == null) return;

        World world = GameManager.Instance.World;

        // Record challenge completion time for stats
        Area7RunStats.RecordChallengeComplete(challengeName, world.worldTime);

        // ---------------------------------------------------------------
        // TRANSMITTER DEPLOYED
        // ---------------------------------------------------------------
        if (challengeName == "deploytransmitter")
        {
            UnityEngine.Debug.Log("[Area 7] Transmitter deployed!");

            string signalSent = LoadPlayerData(player, KEY_SIGNAL_SENT);
            if (signalSent == "true")
            {
                UnityEngine.Debug.Log("[Area 7] Signal already sent previously, skipping event trigger");
                return;
            }

            SavePlayerData(player, KEY_SIGNAL_SENT, "true");

            // v3.0.70: reset a RANDOM half of the volumes (a different half each deploy).
            // v3.0.71: extracted to a shared static so the 'a7 sleepers' test command runs
            // the exact same reset. Non-verbose here - summary line only.
            ResetRandomHalfSleeperVolumes(world, player, false, -1f);

            Vector3 hughSpawnPos = PickRandomHughSpawnPos();
            Vector3 waypointPos = new Vector3(hughSpawnPos.x, hughSpawnPos.y + CampMarkerHeightOffset, hughSpawnPos.z);

            SavePlayerData(player, KEY_HUGH_POSITION, Vector3ToString(hughSpawnPos));

            // v3.0.1: spawn Hugh + persist his state BEFORE the waypoint/nav-object code, so that a
            // nav-object failure (NavObjectManager.RegisterNavObject can return null in 3.0, and the
            // handler has no try/catch) can no longer abort Hugh's spawn or the HughSpawned save.
            if (!hughSpawned)
            {
                SpawnTraderHugh(world, hughSpawnPos);
                hughSpawned = true;
                SavePlayerData(player, KEY_HUGH_SPAWNED, "true");
            }

            try
            {
                Waypoint campWaypoint = new Waypoint();
                campWaypoint.pos = new Vector3i(hughSpawnPos);
                campWaypoint.icon = "ui_game_symbol_trader";
                campWaypoint.name.Text = "Camp Frilsville";
                campWaypoint.bIsAutoWaypoint = true;
                player.Waypoints.Collection.Add(campWaypoint);

                campWaypoint.navObject = NavObjectManager.Instance.RegisterNavObject("waypoint", waypointPos, campWaypoint.icon, false, -1, null);
                if (campWaypoint.navObject != null)
                    campWaypoint.navObject.name = "Camp Frilsville";

                GameManager.ShowTooltip(player, "Camp Frilsville coordinates received! Check your map (M) - Waypoints tab.", (string)null, null, null, false, true, 0f);
                UnityEngine.Debug.Log("[Area 7] Camp Frilsville waypoint added at " + hughSpawnPos);
            }
            catch (System.Exception wpEx)
            {
                UnityEngine.Debug.LogError("[Area 7] Camp Frilsville waypoint failed (non-fatal): " + wpEx.Message);
            }

            AIDirectorAirDropComponent airDropController = world.aiDirector.GetComponent<AIDirectorAirDropComponent>();
            if (airDropController != null)
            {
                // Record airdrop tier now so it's captured even if player escapes before plane arrives
                ProgressionValue vehicleSkillPV = player.Progression.GetProgressionValue("craftingVehicles");
                int vehicleMags = (vehicleSkillPV != null) ? vehicleSkillPV.Level : 0;
                if (vehicleMags >= 40) Area7RunStats.airdropTier = "Tier 5 (40+ mags)";
                else if (vehicleMags >= 30) Area7RunStats.airdropTier = "Tier 4 (30-39 mags)";
                else if (vehicleMags >= 20) Area7RunStats.airdropTier = "Tier 3 (20-29 mags)";
                else if (vehicleMags >= 5) Area7RunStats.airdropTier = "Tier 2 (5-19 mags)";
                else Area7RunStats.airdropTier = "Tier 1 (0-4 mags)";

                // Compute both target times upfront from the same instant so
                // they stay locked together. Blood moon = next 22:00, airdrop
                // = 6 in-game hours later (end of the blood moon at 4am).
                ulong nowWorldTime = world.worldTime;
                ulong nowDayTime = nowWorldTime % 24000UL;
                const ulong BM_DAY_TIME = 22000UL;       // 10pm
                const ulong AIRDROP_OFFSET = 6000UL;     // 6 in-game hours after BM start

                ulong bmTargetWorldTime = (nowDayTime < BM_DAY_TIME)
                    ? nowWorldTime + (BM_DAY_TIME - nowDayTime)
                    : nowWorldTime + ((24000UL - nowDayTime) + BM_DAY_TIME);

                ulong airdropTargetWorldTime = bmTargetWorldTime + AIRDROP_OFFSET;

                coBloodMoon = GameManager.Instance.StartCoroutine(TriggerBloodMoonHorde(world, bmTargetWorldTime));
                coPlane = GameManager.Instance.StartCoroutine(SpawnPlaneDelayed(world, airDropController, player.position, airdropTargetWorldTime));

                GameManager.ShowTooltip(player, "Signal transmitted. Blood moon horde incoming at 10pm. Airdrop arrives at the end of the blood moon.", (string)null, null, null, false, false, 0f);
                UnityEngine.Debug.Log("[Area 7] Blood moon scheduled for world time " + bmTargetWorldTime + ", airdrop for " + airdropTargetWorldTime + ".");
            }
        }

        // ---------------------------------------------------------------
        // ESCAPE COMPLETE
        // ---------------------------------------------------------------
        else if (challengeName.Equals("escapeArea7", StringComparison.OrdinalIgnoreCase))
        {
            UnityEngine.Debug.Log("[Area 7] Escape complete.");

            // Cue the Area 7 song ("Area 7 Ain't Home") at the exact moment the Extraction
            // Order is read and the chopper is dispatched - it scores the read + the flight.
            // The track (3:18, ~its own fade at the end) is registered as the SoundDataNode
            // "Area7ExtractionSong" in Config/sounds.xml, pointing at Resources/area7music.unity3d.
            // Played at the player's position; the run is already recorded complete here, so
            // (per Fril) there is no death path to interrupt it - it just plays out.
            try
            {
                Audio.Manager.Play(player.position, "Area7ExtractionSong", -1, false);
                UnityEngine.Debug.Log("[Area 7] Extraction song cued.");

                // The extraction song and the game's own dynamic background music otherwise
                // play OVER each other, which is a hard listen, so the game music is paused
                // for the duration of the song and resumed afterwards.
                // v3.0.35: use the LIVE music system. Everything before this aimed at
                // DynamicMusicManager, which is DEAD CODE in 3.1.0 - its Init, Pause and
                // UnPause have NO callers anywhere in the assembly, so
                // player.DynamicMusicManager was always null, and because
                // TransitionManager.Master is only assigned inside DynamicMusicManager.Init,
                // the "mixer fallback" was always null too. The two were never independent.
                // The live system is World.dmsConductor, and GameManager.updatePauseState
                // drives it with OnPauseGame/OnUnPauseGame. That is the same lever the game
                // itself pulls when you pause, so this is vanilla behaviour on demand.
                bool musicHandled = false;
                try
                {
                    World w = GameManager.Instance != null ? GameManager.Instance.World : null;
                    DynamicMusic.Conductor cond = (w != null) ? w.dmsConductor : null;

                    if (cond == null)
                    {
                        UnityEngine.Debug.LogWarning("[Area 7] dmsConductor unavailable - extraction song may overlap the game music.");
                    }
                    else
                    {
                        // Two levers, in order of how the game itself uses them. FadeOut is
                        // the section's own IFadeable method; OnPauseGame is what
                        // GameManager pulls when the player opens the pause menu.
                        // NOTE the music handling is not settled: see the devlog. Confirmed
                        // in a 10 Aug test that the conductor goes from a playing Combat
                        // section to no section within 3s of the cue, but the section was
                        // Combat and escaping ends combat, so it may have wound down anyway.
                        DynamicMusic.ISection sect = cond.CurrentSection;
                        if (sect != null) sect.FadeOut();
                        cond.OnPauseGame();
                        UnityEngine.Debug.Log("[Area 7] Dynamic music faded and paused for extraction song.");
                        musicHandled = true;
                    }

                    if (musicHandled)
                        GameManager.Instance.StartCoroutine(ResumeDynamicMusicAfterSong(player));
                }
                catch (Exception dmEx)
                {
                    UnityEngine.Debug.LogWarning("[Area 7] Could not pause dynamic music: " + dmEx.Message);
                }
            }
            catch (Exception songEx)
            {
                UnityEngine.Debug.LogWarning("[Area 7] Could not play extraction song: " + songEx.Message);
            }

            Area7RunStats.GenerateStatsPage(player, true);

            // v3.0.66: leaderboard code. Logged as well as shown on the debrief, because
            // the debrief lives in the mod folder and is lost whenever the mod is updated.
            try
            {
                string a7code = Area7CompletionCode.Build(player);
                if (!string.IsNullOrEmpty(a7code))
                    UnityEngine.Debug.Log("[Area 7] Completion code: " + a7code);
            }
            catch (Exception codeEx)
            {
                UnityEngine.Debug.LogWarning("[Area 7] Could not build completion code: " + codeEx.Message);
            }

            Area7ChallengeMod.CleanupRun("escapeComplete", player);
        }
    }

    static IEnumerator TriggerBloodMoonHorde(World world, ulong targetWorldTime)
    {
        EntityPlayerLocal player = world.GetPrimaryPlayer();
        if (player != null)
            SavePlayerData(player, KEY_BLOODMOON_TIME, targetWorldTime.ToString());

        ulong lastChecked = world.worldTime;
        while (world.worldTime < targetWorldTime)
        {
            yield return new WaitForSeconds(30f);
            if (world.worldTime < lastChecked) yield break;
            lastChecked = world.worldTime;
        }

        var bloodMoonComp = world.aiDirector.GetComponent<AIDirectorBloodMoonComponent>();
        if (bloodMoonComp != null)
        {
            bool success = bloodMoonComp.SetForToday(false);
            UnityEngine.Debug.Log("[Area 7] Blood moon set for today. Success: " + success);
        }

        if (player != null)
        {
            GameManager.ShowTooltip(player, "WARNING: Zombies detected. Blood moon protocol initiated!", (string)null, null, null, false, false, 0f);
            SavePlayerData(player, KEY_BLOODMOON_TIME, "");
        }
    }

    static IEnumerator SpawnPlaneDelayed(World world, AIDirectorAirDropComponent controller, Vector3 originalPlayerPos, ulong targetWorldTime)
    {
        EntityPlayerLocal player = world.GetPrimaryPlayer();
        if (player != null)
            SavePlayerData(player, KEY_AIRDROP_TIME, targetWorldTime.ToString());

        ulong lastChecked = world.worldTime;
        while (world.worldTime < targetWorldTime)
        {
            yield return null;
            if (world.worldTime < lastChecked) yield break;
            lastChecked = world.worldTime;
        }

        yield return SpawnPlane(world, controller, originalPlayerPos);
    }

    public static IEnumerator SpawnPlane(World world, AIDirectorAirDropComponent controller, Vector3 playerPos)
    {
        EntityPlayerLocal player = world.GetPrimaryPlayer();
        if (player == null) yield break;

        ProgressionValue vehicleSkillPV = player.Progression.GetProgressionValue("craftingVehicles");
        int vehicleMags = (vehicleSkillPV != null) ? vehicleSkillPV.Level : 0;

        string containerID = "210";
        if (vehicleMags >= 40) containerID = "214";
        else if (vehicleMags >= 30) containerID = "213";
        else if (vehicleMags >= 20) containerID = "212";
        else if (vehicleMags >= 5) containerID = "211";

        float spawnHeight = Mathf.Min(playerPos.y + 180f, 276f);

        Vector3 dropCenter = new Vector3(0f, spawnHeight - 10f, 0f);
        Vector2 dropOffset = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(20f, 50f);
        Vector3 crateDropPos = new Vector3(dropCenter.x + dropOffset.x, dropCenter.y, dropCenter.z + dropOffset.y);

        Vector2 flyDir2 = UnityEngine.Random.insideUnitCircle.normalized;
        Vector3 flyDir3 = new Vector3(flyDir2.x, 0f, flyDir2.y).normalized;
        Vector3 startPos = new Vector3(crateDropPos.x, spawnHeight, crateDropPos.z) + flyDir3 * 1000f;
        Vector3 endPos = new Vector3(crateDropPos.x, spawnHeight, crateDropPos.z) - flyDir3 * 1000f;

        Vector3 direction = (endPos - startPos).normalized;
        float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        EntitySupplyPlane plane = (EntitySupplyPlane)EntityFactory.CreateEntity(
            EntityClass.FromString("supplyPlane"),
            startPos,
            new Vector3(0f, angle, 0f)
        );

        float distance = Vector3.Distance(startPos, endPos);
        plane.SetDirectionToFly(direction, (int)(20f * (distance / 120f + 10f)));
        world.SpawnEntityInWorld(plane);

        float timeToReachDrop = Vector3.Distance(startPos, new Vector3(crateDropPos.x, spawnHeight, crateDropPos.z)) / 120f;

        ChunkManager.ChunkObserver chunkObserver = world.GetGameManager().AddChunkObserver(crateDropPos, false, 3, -1);

        SetCrateDropCoroutine(
            GameManager.Instance.StartCoroutine(
                SpawnCrateDelayed(controller, crateDropPos, chunkObserver, timeToReachDrop, containerID, world)
            )
        );

        GameManager.ShowTooltip(player, "Supply plane overhead!", (string)null, null, null, false, false, 0f);
        SavePlayerData(player, KEY_AIRDROP_TIME, "");
    }

    static IEnumerator SpawnCrateDelayed(AIDirectorAirDropComponent controller, Vector3 position, ChunkManager.ChunkObserver observer, float delay, string containerID, World world)
    {
        yield return new WaitForSeconds(delay);

        string entityName = "sc_Area7_Tier1";
        if (containerID == "214") entityName = "sc_Area7_Tier5";
        else if (containerID == "213") entityName = "sc_Area7_Tier4";
        else if (containerID == "212") entityName = "sc_Area7_Tier3";
        else if (containerID == "211") entityName = "sc_Area7_Tier2";

        Entity crateEntity = EntityFactory.CreateEntity(
            EntityClass.FromString(entityName),
            position,
            new Vector3(UnityEngine.Random.Range(0f, 360f), 0f, 0f)
        );
        world.SpawnEntityInWorld(crateEntity);

        // Track crate entity ID so we can capture contents when opened
        Area7RunStats.airdropCrateEntityId = crateEntity.entityId;
        UnityEngine.Debug.Log("[Area 7] Supply crate spawned with entity ID " + crateEntity.entityId);

        controller.AddSupplyCrate(crateEntity.entityId);
        controller.SetSupplyCratePosition(crateEntity.entityId, World.worldToBlockPos(position));
        controller.RefreshCrates(-1);

        if (observer != null)
            world.GetGameManager().RemoveChunkObserver(observer);
    }

    // ---------------------------------------------------------------
    // TEST SHORTCUT - used by the "a7" console command.
    // Does what deploying the transmitter does for Hugh only: pick a camp, spawn Hugh,
    // persist the state and drop the Camp Frilsville waypoint. Lets the extraction be
    // tested without playing through the challenges first.
    // Lives here because PickRandomHughSpawnPos/Vector3ToString are private to this class.
    // ---------------------------------------------------------------
    // v3.0.71: reset a RANDOM half of the world's sleeper volumes, a different half each call.
    // Shared by the transmitter deploy (non-verbose) and the 'a7 sleepers' test command (verbose).
    // Fisher-Yates shuffle, then reset the first (count+1)/2 - same count as the old even-index
    // behaviour, only WHICH volumes is randomised. Returns the number actually reset.
    // ---------------------------------------------------------------
    // TRANSMITTER SLEEPER RESPONSE (v3.0.72)
    // Of the BUNKER's loaded volumes, reset a random half. That half splits into a HUNTING share
    // (force-spawned awake and locked onto the player, fed in on a stagger so it fills the wait
    // rather than dumping at once) and a SLEEPING share (DespawnAndReset, repopulate dormant, wake
    // on approach - the original behaviour). Gated to the Area 7 compound so far POIs (Hugh's base
    // etc.) are NEVER touched no matter what is loaded or where the transmitter is placed. Tunables:
    // ---------------------------------------------------------------
    public const float SLEEPER_HUNT_FRACTION = 0.5f;          // of the reset half, share that hunts (rest sleep)
    public const float SLEEPER_HUNT_STAGGER_SECONDS = 120f;   // real seconds between waking each hunting volume; ~120s fans ~22 hunters across ~45 min (spread scales with hunter count)
    public const float SLEEPER_HUNT_SPAWN_WAIT_SECONDS = 3f;  // wait after forcing a spawn before waking, lets the game create the zombies
    public const int   SLEEPER_HUNT_TARGET_TICKS = 6000;      // how long a woken zombie stays locked on the player

    public static int ResetRandomHalfSleeperVolumes(World world, EntityPlayerLocal player, bool verbose, float staggerOverride)
    {
        // Gate to the compound: only volumes whose centre is inside the Area 7 bunker bounds.
        var all = new List<(int, SleeperVolume)>();
        world.GetAllSleeperVolumes(all);
        var bunker = new List<SleeperVolume>();
        foreach (var pair in all)
        {
            SleeperVolume sv = pair.Item2;
            if (sv != null && Area7CentralRadiation.IsInsideCompound(sv.Center))
                bunker.Add(sv);
        }
        int bunkerCount = bunker.Count;

        // Fisher-Yates shuffle, take the first half (a different half each deploy).
        for (int i = bunkerCount - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            SleeperVolume swap = bunker[i]; bunker[i] = bunker[j]; bunker[j] = swap;
        }
        int resetTarget = (bunkerCount + 1) / 2;
        int huntTarget = Mathf.RoundToInt(resetTarget * SLEEPER_HUNT_FRACTION);

        var hunters = new List<SleeperVolume>();
        int sleepingCount = 0;
        for (int i = 0; i < resetTarget; i++)
        {
            SleeperVolume sv = bunker[i];
            if (sv == null) continue;
            sv.DespawnAndReset(world);   // clean slate for both halves
            if (i < huntTarget)
            {
                hunters.Add(sv);
            }
            else
            {
                sleepingCount++;
                if (verbose) UnityEngine.Debug.Log("[Area 7]   sleeping volume at " + sv.Center);
            }
        }

        float stagger = (staggerOverride >= 0f) ? staggerOverride : SLEEPER_HUNT_STAGGER_SECONDS;
        if (hunters.Count > 0 && player != null)
            GameManager.Instance.StartCoroutine(SpawnAndHuntSleeperVolumes(world, player, hunters, stagger, verbose));

        UnityEngine.Debug.Log("[Area 7] Transmitter sleeper response: " + bunkerCount + " volumes in compound, "
            + resetTarget + " reset (" + hunters.Count + " hunting on a " + stagger + "s stagger, " + sleepingCount + " sleeping).");
        return resetTarget;
    }

    // Staggered: one volume at a time - force it to spawn its zombies (even though the player is
    // elsewhere in the bunker), wait for them to appear, then wake each and lock it onto the player.
    // Session-guarded: GameManager coroutines survive quit-to-menu, so bail if the world reloaded.
    private static IEnumerator SpawnAndHuntSleeperVolumes(World world, EntityPlayerLocal player, List<SleeperVolume> volumes, float staggerSeconds, bool verbose)
    {
        int startedInSession = Area7PermadeathCleanupPatch.sessionId;
        foreach (SleeperVolume sv in volumes)
        {
            if (Area7PermadeathCleanupPatch.sessionId != startedInSession) yield break;
            if (sv == null) continue;

            bool spawnErr = false;
            try { sv.UpdatePlayerTouched(world, player); }
            catch (Exception e) { spawnErr = true; UnityEngine.Debug.LogWarning("[Area 7] hunt spawn error: " + e.Message); }

            yield return new WaitForSeconds(SLEEPER_HUNT_SPAWN_WAIT_SECONDS);
            if (Area7PermadeathCleanupPatch.sessionId != startedInSession) yield break;

            int woke = 0;
            if (!spawnErr)
            {
                try
                {
                    foreach (var kv in sv.respawnMap)
                    {
                        EntityAlive z = world.GetEntity(kv.Key) as EntityAlive;
                        if (z != null && !z.IsDead())
                        {
                            z.ConditionalTriggerSleeperWakeUp();
                            z.SetAttackTarget(player, SLEEPER_HUNT_TARGET_TICKS);
                            woke++;
                        }
                    }
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning("[Area 7] hunt wake error: " + e.Message); }
            }
            if (verbose) UnityEngine.Debug.Log("[Area 7]   hunting volume at " + sv.Center + " - woke " + woke + " zombie(s)");

            yield return new WaitForSeconds(staggerSeconds);
        }
        UnityEngine.Debug.Log("[Area 7] Sleeper hunt stagger complete (" + volumes.Count + " volumes).");
    }

    public static Vector3 TestDeployHugh(EntityPlayerLocal player)
    {
        World world = GameManager.Instance?.World;
        if (world == null || player == null) return Vector3.zero;

        Vector3 hughSpawnPos = PickRandomHughSpawnPos();

        SavePlayerData(player, KEY_SIGNAL_SENT, "true");
        SavePlayerData(player, KEY_HUGH_POSITION, Vector3ToString(hughSpawnPos));

        SpawnTraderHugh(world, hughSpawnPos);
        SavePlayerData(player, KEY_HUGH_SPAWNED, "true");

        try
        {
            Waypoint campWaypoint = new Waypoint();
            campWaypoint.pos = new Vector3i(hughSpawnPos);
            campWaypoint.icon = "ui_game_symbol_trader";
            campWaypoint.name.Text = "Camp Frilsville";
            campWaypoint.bIsAutoWaypoint = true;
            player.Waypoints.Collection.Add(campWaypoint);

            Vector3 markerPos = new Vector3(hughSpawnPos.x, hughSpawnPos.y + CampMarkerHeightOffset, hughSpawnPos.z);
            NavObjectManager mgr = NavObjectManager.Instance;
            if (mgr != null)
            {
                campWaypoint.navObject = mgr.RegisterNavObject("waypoint", markerPos, campWaypoint.icon, false, -1, null);
                if (campWaypoint.navObject != null)
                {
                    campWaypoint.navObject.name = "Camp Frilsville";
                    campWaypoint.navObject.IsActive = true;
                }
            }
            campWaypoint.bTracked = true;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Test waypoint failed (non-fatal): " + e.Message);
        }

        // v3.0.41: the "TEST:" log line dropped for release. The a7 console command
        // already reports back to the player, so this only ever duplicated it into the log.
        return hughSpawnPos;
    }

    public static void SpawnTraderHugh(World world, Vector3 spawnPos)
    {
        int hughClassId = EntityClass.FromString("npcTraderHugh");
        if (hughClassId == -1)
        {
            UnityEngine.Debug.LogError("[Area 7] npcTraderHugh entity not found!");
            return;
        }

        Entity hughEntity = EntityFactory.CreateEntity(hughClassId, spawnPos);
        hughEntity.rotation = new Vector3(0f, currentHughRotation, 0f);
        world.SpawnEntityInWorld(hughEntity);
        UnityEngine.Debug.Log("[Area 7] Trader Hugh spawned at " + spawnPos + " (Entity ID: " + hughEntity.entityId + ")");

        EntityPlayerLocal player = world.GetPrimaryPlayer();
        if (player != null)
            SavePlayerData(player, KEY_HUGH_ENTITY_ID, hughEntity.entityId.ToString());
    }
}

// Patch to set custom names on Area 7 loot containers
[HarmonyPatch(typeof(EntityLootContainer), "CopyPropertiesFromEntityClass")]
public class LootContainerNamePatch
{
    static void Postfix(EntityLootContainer __instance)
    {
        string entityClassName = EntityClass.list[__instance.entityClass].entityClassName;

        switch (entityClassName)
        {
            case "EntityLootContainerArea7Medical": __instance.OverrideName = "lootMedicalBag"; break;
            case "EntityLootContainerArea7Biker": __instance.OverrideName = "lootBikerBag"; break;
            case "EntityLootContainerArea7Cops": __instance.OverrideName = "lootCopsBag"; break;
            case "EntityLootContainerArea7Tokens": __instance.OverrideName = "lootTokensBag"; break;
            case "EntityLootContainerArea7Heavy": __instance.OverrideName = "lootHeavyBag"; break;
            case "EntityLootContainerArea7Bubbles": __instance.OverrideName = "lootHazmatBag"; break;
            case "EntityLootContainerArea7Creepy": __instance.OverrideName = "lootResearcherBag"; break;
            case "EntityLootContainerArea7Books": __instance.OverrideName = "lootBookBag"; break;
        }
    }
}

// Permadeath cleanup when the local primary player dies
[HarmonyPatch(typeof(EntityAlive), "OnEntityDeath")]
public class Area7PermadeathCleanupPatch
{
    public static bool isPostDeathMode = false;

    // v3.0.16: incremented on every world load. The post-death coroutines poll for a live
    // player for up to 2 minutes, and they run on GameManager, which SURVIVES quitting to the
    // menu — so without this a coroutine started by a death in one run could still be waiting
    // when a brand new game began, find that game's freshly spawned player, and kill them.
    // Each coroutine captures the counter at start and aborts the moment it changes.
    public static int sessionId = 0;

    static void Postfix(EntityAlive __instance)
    {
        try
        {
            if (!(__instance is EntityPlayerLocal deadPlayer)) return;

            World world = GameManager.Instance?.World;
            EntityPlayerLocal primary = world?.GetPrimaryPlayer();
            if (primary == null || deadPlayer.entityId != primary.entityId) return;

            // If the player has already completed the escape, ignore deaths
            // entirely. Their Mission Complete debrief stays intact and they
            // respawn under vanilla rules (no permadeath radiation buff).
            if (Area7ChallengeMod.IsRunCompleted())
            {
                UnityEngine.Debug.Log("[Area 7] Player died after escape — mod dormant, debrief preserved.");
                return;
            }

            // If already in post-death mode, just keep killing on next respawn
            if (isPostDeathMode)
            {
                UnityEngine.Debug.Log("[Area 7] Post-death mode: queueing another kill on respawn.");
                GameManager.Instance.StartCoroutine(ApplyPostDeathBuff());
                return;
            }

            // First real death — run cleanup, enter post-death mode
            Area7RunStats.GenerateStatsPage(primary, false);
            Area7ChallengeMod.CleanupRun("permadeath", primary);

            isPostDeathMode = true;

            // Restart radiation and start killing on respawn
            GameManager.Instance.StartCoroutine(RestartRadiationDelayed());
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Error in permadeath cleanup: " + e.Message);
        }
    }

    private static IEnumerator RestartRadiationDelayed()
    {
        int startedInSession = sessionId;

        yield return new WaitForSeconds(3f);

        if (sessionId != startedInSession)
            yield break; // a different game was loaded while we waited

        Area7CentralRadiation.Start();
        UnityEngine.Debug.Log("[Area 7] Radiation zone restarted after death.");

        yield return ApplyPostDeathBuff();
    }

    private static IEnumerator ApplyPostDeathBuff()
    {
        // Remember which run we belong to. If the player quits to the menu and loads or starts
        // ANY game while this is polling, sessionId changes and we abort — otherwise this would
        // apply the death buff to the new game's player (50 HP/sec: instant death on spawn).
        int startedInSession = sessionId;

        float timeout = 120f;
        float waited = 0f;
        EntityPlayerLocal player = null;

        while (waited < timeout)
        {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;

            if (sessionId != startedInSession || !isPostDeathMode)
            {
                UnityEngine.Debug.Log("[Area 7] Post-death buff cancelled — no longer the run that died.");
                yield break;
            }

            player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player != null && !player.IsDead() && player.Buffs != null)
                break;
        }

        // Re-check immediately before applying: the world can change during the final wait.
        if (sessionId != startedInSession || !isPostDeathMode)
        {
            UnityEngine.Debug.Log("[Area 7] Post-death buff cancelled — no longer the run that died.");
            yield break;
        }

        // v3.0.50: WAIT for vanilla's respawn grace to finish before starting the drain.
        //
        // WHY THE BAR BOUNCED. Vanilla applies buffDeathFoodDrinkAdjust on every respawn:
        //     <duration value="6.5"/>
        //     <triggered_effect trigger="onSelfBuffUpdate" action="ModifyStats"
        //                       stat="Health" operation="set" value="25000"/>
        //     <passive_effect name="PhysicalDamageResist" operation="base_add" value="100"/>
        // It SETS health back to full on every update for 6.5 seconds. Our buff drains
        // 50/s, vanilla slams it back, and the bar visibly bounces between full and half
        // until the grace expires. Per-frame probe on a real death:
        // hp 75, 50, 75, 50, 75, 50, ... , 25, dead.
        //
        // WHY WE DO NOT JUST REMOVE THE GRACE. Those 6.5 seconds are load-bearing: they are
        // the player's only window to quit to the menu. Without them a dead run is an
        // unbreakable die-respawn loop. The grace also carries 100 physical damage resist,
        // so a zombie standing over the corpse cannot interrupt that window either.
        //
        // So the grace is left completely alone and the drain simply starts after it. The
        // bar sits still while the player decides what to do, then falls once, smoothly.
        // Total time from respawn is about 8.5s, slightly longer than the 7s it was.
        // Note vanilla removes the grace EARLY if the player walks, runs, crouches or uses
        // an item, so a player who tries to carry on gets the drain immediately, which is
        // the right behaviour.
        if (player != null && !player.IsDead() && player.Buffs != null)
        {
            const string GraceBuff = "buffDeathFoodDrinkAdjust";
            float graceWaited = 0f;

            while (graceWaited < 12f)
            {
                if (sessionId != startedInSession || !isPostDeathMode)
                {
                    UnityEngine.Debug.Log("[Area 7] Post-death buff cancelled - no longer the run that died.");
                    yield break;
                }
                if (player == null || player.IsDead() || player.Buffs == null) yield break;

                bool hasGrace = false;
                try { hasGrace = player.Buffs.HasBuff(GraceBuff); } catch { }
                if (!hasGrace) break;

                yield return new WaitForSeconds(0.25f);
                graceWaited += 0.25f;
            }

            if (player == null || player.IsDead() || player.Buffs == null) yield break;

            player.Buffs.AddBuff("buffArea7PostDeathRadiation");
            UnityEngine.Debug.Log(string.Format(
                "[Area 7] Applied post-death radiation buff (waited {0:F2}s for vanilla respawn grace to end).",
                graceWaited));
        }
    }
}

// ========================================
// AREA CLEAR TIMES (v3.0.55)
//
// The Mission Timeline used to be a list of CHALLENGE REDEMPTION times, which meant it
// showed when the player happened to open the journal rather than when anything happened.
// It also mapped "Build the Transmitter" to the `gatherducttape` challenge, one arbitrary
// member of the five-part Generator group, while the real `craftTransmitter` challenge was
// not tracked at all.
//
// Fril's prefab already marks progress through the POI with trigger buttons:
//     Trigger 2   Surface and Station cleared (or bypassed)
//     Trigger 5   Med Bay cleared            - opens Hydroponics and the Badass room
//     Trigger 10  Hydroponics cleared (covers the Badass room too) - opens the Generator
//                 room and Car Park
// The Car Park has no button; the tell is the player holding the Crucible from the locked
// chest in the loot room.
//
// BlockTrigger.OnTriggered(player, world, index, ...) fires when a trigger activates, so
// this records the first fire of EVERY index, not just the three we display. That way the
// numbers the code sees can be checked against the numbers the prefab editor shows without
// another playthrough, and any future area we want to add is already in the save file.
// Stored through challengeTimes so it persists with the rest of the run state.
// ========================================
// BlockTrigger has TWO OnTriggered overloads, so the signature must be pinned or Harmony
// throws an ambiguous match at patch time:
//   OnTriggered(EntityPlayer, World,     int index,       List<BlockChangeInfo>, BlockTrigger)
//   OnTriggered(EntityPlayer, WorldBase, Vector3i, BlockValue, List<BlockChangeInfo>, BlockTrigger)
// The first is the one carrying the trigger index.
[HarmonyPatch(typeof(BlockTrigger), "OnTriggered", new Type[] {
    typeof(EntityPlayer), typeof(World), typeof(int),
    typeof(List<BlockChangeInfo>), typeof(BlockTrigger) })]
public class Area7TriggerTimePatch
{
    // Only a genuine press counts. OnTriggered is also reached from HandleNeedTriggers and
    // RefreshTriggers during POI setup and STATE RESTORE (flying back to the POI reloads it
    // and replays its trigger state), and those paths pass a NULL player, so that is the
    // discriminator. Proven in game: with this filter a reload logged no spurious records.
    //
    // Note this fires once per BLOCK triggered by the index, not once per press, so a button
    // opening seven doors calls it seven times with the same index and time. First write
    // wins, so the repeats are ignored.
    //
    // The diagnostic logging that established both of those facts has been removed; only the
    // recording remains.
    static void Postfix(EntityPlayer _player, int index, BlockTrigger _triggeredBy)
    {
        try
        {
            if (Area7ChallengeMod.IsRunCompleted()) return;
            if (_player == null) return;

            World world = GameManager.Instance?.World;
            if (world == null) return;

            string key = "trig" + index;
            if (Area7RunStats.challengeTimes.ContainsKey(key)) return;   // first write wins

            Area7RunStats.RecordChallengeComplete(key, world.worldTime);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] Trigger time record failed: " + e.Message);
        }
    }
}

// The Car Park has no trigger button. Holding the Crucible from the locked chest in the
// loot room is the practical tell that it has been cleared. Same two-patch shape as the
// Extraction Order pickup, because an item can land in either the toolbelt or the backpack.
public static class Area7CrucibleWatch
{
    public const string CRUCIBLE_ITEM = "toolForgeCrucible";
    public const string KEY = "crucible";

    public static void Note(ItemStack stack)
    {
        try
        {
            if (Area7ChallengeMod.IsRunCompleted()) return;
            if (stack == null || stack.itemValue == null || stack.itemValue.IsEmpty()) return;
            if (Area7RunStats.challengeTimes.ContainsKey(KEY)) return;

            string itemName = stack.itemValue.ItemClass != null ? stack.itemValue.ItemClass.GetItemName() : null;
            if (itemName != CRUCIBLE_ITEM) return;

            World world = GameManager.Instance?.World;
            if (world == null) return;

            Area7RunStats.RecordChallengeComplete(KEY, world.worldTime);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] Crucible watch failed: " + e.Message);
        }
    }
}

[HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(ItemStack) })]
public class Area7CruciblePickupPatch
{
    static void Postfix(ItemStack _itemStack, bool __result)
    {
        if (__result) Area7CrucibleWatch.Note(_itemStack);
    }
}

[HarmonyPatch(typeof(Bag), "AddItem", new[] { typeof(ItemStack) })]
public class Area7CrucibleBagPickupPatch
{
    static void Postfix(ItemStack _itemStack, bool __result)
    {
        if (__result) Area7CrucibleWatch.Note(_itemStack);
    }
}

// Detects Extraction Order entering the player's TOOLBELT
[HarmonyPatch(typeof(Inventory), "AddItem", new[] { typeof(ItemStack) })]
public class Area7ExtractionOrderPickupPatch
{
    private const string EXTRACTION_ORDER_ITEM = "noteArea7Extraction";

    static void Postfix(Inventory __instance, ItemStack _itemStack, bool __result)
    {
        try
        {
            if (!__result) return;

            // Run already completed — ignore (defensive: extraction order
            // shouldn't be obtainable post-escape, but never assume).
            if (Area7ChallengeMod.IsRunCompleted()) return;

            EntityPlayerLocal player = __instance.entity as EntityPlayerLocal;
            if (player == null) return;

            if (_itemStack == null || _itemStack.itemValue == null) return;
            if (_itemStack.itemValue.IsEmpty()) return;

            string itemName = _itemStack.itemValue.ItemClass?.GetItemName();
            if (itemName != EXTRACTION_ORDER_ITEM) return;

            UnityEngine.Debug.Log("[Area 7] Extraction Order received (toolbelt).");

            bool redeemed = Area7ChallengeMod.ForceRedeemChallenge(player, "escapeArea7");

            if (!redeemed)
            {
                UnityEngine.Debug.LogWarning("[Area 7] Could not redeem escapeArea7, running cleanup anyway.");
                Area7RunStats.GenerateStatsPage(player, true);
                Area7ChallengeMod.CleanupRun("escapeComplete", player);
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] ExtractionOrderPickupPatch error: " + e.Message);
        }
    }
}

// Detects Extraction Order entering the player's BACKPACK
[HarmonyPatch(typeof(Bag), "AddItem", new[] { typeof(ItemStack) })]
public class Area7ExtractionOrderBagPickupPatch
{
    private const string EXTRACTION_ORDER_ITEM = "noteArea7Extraction";

    // v3.0.1: Bag lost its `entity` field, so the old `___entity` Harmony injection
    // is gone (it was the cause of the mod failing to init). Area 7 is single-player,
    // so the bag being added to is the local player's backpack; resolve via GetPrimaryPlayer.
    static void Postfix(Bag __instance, ItemStack _itemStack, bool __result)
    {
        try
        {
            if (!__result) return;

            // Run already completed — ignore (defensive).
            if (Area7ChallengeMod.IsRunCompleted()) return;

            if (_itemStack == null || _itemStack.itemValue == null) return;
            if (_itemStack.itemValue.IsEmpty()) return;

            string itemName = _itemStack.itemValue.ItemClass?.GetItemName();
            if (itemName != EXTRACTION_ORDER_ITEM) return;

            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) return;

            UnityEngine.Debug.Log("[Area 7] Extraction Order received (bag).");

            bool redeemed = Area7ChallengeMod.ForceRedeemChallenge(player, "escapeArea7");

            if (!redeemed)
            {
                UnityEngine.Debug.LogWarning("[Area 7] Could not redeem escapeArea7, running cleanup anyway.");
                Area7RunStats.GenerateStatsPage(player, true);
                Area7ChallengeMod.CleanupRun("escapeComplete", player);
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] ExtractionOrderBagPickupPatch error: " + e.Message);
        }
    }
}

// Spawns a custom Area 7 loot bag when specific zombie types die.
// Also tracks zombie kills, weapon usage, and zombie types for stats.
[HarmonyPatch(typeof(EntityAlive), "OnEntityDeath")]
public class Area7ZombieLootDropPatch
{
    // entityThatKilledMe is nulled at end of OnEntityDeath, so capture it in Prefix
    private static Entity lastKillerEntity = null;

    static void Prefix(EntityAlive __instance)
    {
        if (__instance is EntityZombie || __instance is EntityAnimal || __instance is EntityEnemyAnimal || __instance is EntityFlying)
            lastKillerEntity = __instance.entityThatKilledMe;
    }

    static void Postfix(EntityAlive __instance)
    {
        try
        {
            bool isZombie = __instance is EntityZombie;
            bool isAnimal = __instance is EntityAnimal || __instance is EntityEnemyAnimal || __instance is EntityFlying;
            if (!isZombie && !isAnimal) return;

            World world = GameManager.Instance?.World;
            if (world == null) return;

            // --- Stats: record kill, weapon, and entity type (zombies AND animals) ---
            EntityPlayerLocal player = world.GetPrimaryPlayer();
            if (player != null)
            {
                string weaponName = "Environmental";

                // Only record held weapon if the player actually killed this entity
                if (lastKillerEntity != null && lastKillerEntity is EntityPlayerLocal)
                {
                    try
                    {
                        var holdingItem = player.inventory.holdingItem;
                        if (holdingItem != null && holdingItem.Actions != null && holdingItem.Actions.Length > 0)
                        {
                            var primaryAction = holdingItem.Actions[0];
                            if (primaryAction is ItemActionRanged || primaryAction is ItemActionMelee || primaryAction is ItemActionDynamic)
                            {
                                weaponName = holdingItem.GetLocalizedItemName() ?? holdingItem.GetItemName() ?? "Unknown";
                            }
                            // Otherwise keep "Environmental" — player was holding a non-weapon (water, block, etc.)
                        }
                    }
                    catch { }
                }

                string typeName = "Unknown";
                try
                {
                    EntityClass zec = EntityClass.list[__instance.entityClass];
                    if (zec != null)
                    {
                        string locName = Localization.Get(zec.entityClassName, false);
                        typeName = (!string.IsNullOrEmpty(locName) && locName != zec.entityClassName)
                            ? locName
                            : zec.entityClassName;
                    }
                }
                catch { }

                Area7RunStats.RecordKill(weaponName, typeName);
            }

            // Clear the captured reference
            lastKillerEntity = null;

            // --- Loot bag drops: zombies only ---
            if (!isZombie) return;
            EntityZombie zombie = __instance as EntityZombie;

            EntityClass ec = EntityClass.list[zombie.entityClass];
            if (ec == null) return;

            string entityClassName = ec.Properties.GetString("Area7LootDropEntityClass");
            if (string.IsNullOrEmpty(entityClassName)) return;

            float dropProb = 0.2f;
            string probStr = ec.Properties.GetString("Area7LootDropProb");
            if (!string.IsNullOrEmpty(probStr))
                float.TryParse(probStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dropProb);

            if (UnityEngine.Random.value > dropProb) return;

            int bagClassId = EntityClass.FromString(entityClassName);
            if (bagClassId == -1)
            {
                UnityEngine.Debug.LogWarning("[Area 7] ZombieLootDrop: Unknown entity class '" + entityClassName + "' on zombie '" + ec.entityClassName + "'");
                return;
            }

            Entity bagEntity = EntityFactory.CreateEntity(bagClassId, zombie.position);
            world.SpawnEntityInWorld(bagEntity);

            Audio.Manager.BroadcastPlay(bagEntity.position, "zpack_spawn", 0f);

            Area7RunStats.RecordBagDrop(entityClassName);

            UnityEngine.Debug.Log("[Area 7] ZombieLootDrop: Spawned '" + entityClassName + "' at " + zombie.position + " for zombie '" + ec.entityClassName + "'");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] ZombieLootDropPatch error: " + e.Message);
        }
    }
}

// Captures the contents of the Area 7 airdrop crate when the player closes it.
// Matches entity ID stored when we spawned the crate.
// ===================================================================
// Area7AirdropContentsPatch - DISABLED for the v3.0.1 migration.
// AIRDROP CONTENTS CAPTURE
//
// HISTORY, because this hook has now been wrong twice.
// v2.x hooked TileEntity.SetUserAccessing and matched TileEntityLootContainer.EntityId.
// v3.0.1 refactored loot storage into composite tile-entity features, so that could no
// longer fire and the feature was compiled out.
// v3.0.33 hooked XUiC_LootWindow.SetTileEntityChest, on the reasoning that every lootable
// opens through it. It does, but a supply crate is NOT a lootable, so it never fired.
//
// v3.0.34 hooks the crate itself. From the 3.1.0 IL, EntitySupplyCrate.OnEntityActivated is:
//     if (CommandIs("search"))
//         LockManager.Instance.LockRequestLocal(new EntityLockContext(this, commandId, this.bag), false);
// so the contents live in Entity.bag and the crate opens via LockManager, nowhere near
// TEFeatureStorage or the loot window. Hooking the entity also restores matching on
// entityId, which is what the original v2.x code did and is exact rather than inferred.
//
// Fires on the "search" activation. Reads the bag at that moment, which is when the
// player has just opened it and before anything is taken out.
// ===================================================================
[HarmonyPatch(typeof(EntitySupplyCrate), "OnEntityActivated")]
public class Area7AirdropContentsPatch
{
    static void Postfix(EntitySupplyCrate __instance, EntityActivationCommand _command)
    {
        try
        {
            if (__instance == null) return;

            // Only our airdrop crate, and only capture once per run.
            if (Area7RunStats.airdropCrateEntityId == -1) return;
            if (__instance.entityId != Area7RunStats.airdropCrateEntityId) return;
            if (Area7RunStats.airdropContents.Count > 0) return;

            Bag bag = __instance.bag;
            if (bag == null) return;

            ItemStack[] items = bag.GetSlots();
            if (items == null) return;

            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null || items[i].IsEmpty()) continue;

                string itemName = "Unknown";
                try
                {
                    var itemClass = items[i].itemValue.ItemClass;
                    if (itemClass != null)
                        itemName = itemClass.GetLocalizedItemName() ?? itemClass.GetItemName() ?? "Unknown";
                }
                catch { }

                int count = items[i].count;
                if (count > 1)
                    Area7RunStats.airdropContents.Add(itemName + " x" + count);
                else
                    Area7RunStats.airdropContents.Add(itemName);
            }

            UnityEngine.Debug.Log("[Area 7] Airdrop crate " + __instance.entityId
                + " opened, captured " + Area7RunStats.airdropContents.Count + " item(s).");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] AirdropContentsPatch error: " + e.Message);
        }
    }
}

// v3.0.1: loot containers are composite tile entities; storage lives in
// TEFeatureStorage. Vanilla TEFeatureStorage.UpdateTick respawns empty containers
// after LootRespawnDays but resets worldTimeTouched whenever a player is within
// bounds (proximity gate). This Prefix replicates vanilla minus that gate, so loot
// respawns purely on elapsed time. TEFeatureAbs.UpdateTick is a no-op in 3.0.1, so
// nothing is lost by skipping the original.
[HarmonyPatch(typeof(TEFeatureStorage), "UpdateTick", new Type[] { typeof(World) })]
public class Patch_TEFeatureStorage_UpdateTick
{
    private static bool Prefix(TEFeatureStorage __instance, World _world)
    {
        if ((__instance.Parent as TileEntityComposite)?.PlayerPlaced == true) return false;
        if (!__instance.bTouched) return false;

        // v3.0.2 FIX: only EMPTY, non-player-storage, touched containers may respawn. Everything
        // else returns early UNTOUCHED. Critically, bPlayerStorage and non-empty containers must
        // NOT have bTouched cleared, or the game regenerates their loot on the next open (that was
        // the "loot refreshes every time you press E" bug on cars and general containers).
        if (__instance.bPlayerStorage || !__instance.IsEmpty()) return false;

        int respawnDays = GamePrefs.GetInt(EnumGamePrefs.LootRespawnDays);
        if (respawnDays <= 0) return false;

        long touchedHours = GameUtils.WorldTimeToTotalHours(__instance.worldTimeTouched);
        long currentHours = GameUtils.WorldTimeToTotalHours(_world.worldTime);
        if ((currentHours - touchedHours) / 24 >= respawnDays)
        {
            // Timer elapsed -> respawn. Vanilla gates this on no players being within bounds
            // (the proximity gate); we intentionally skip that gate so Area 7 loot respawns on
            // elapsed time alone. Containers with loot still in them are never affected.
            __instance.bWasTouched = false;
            __instance.bTouched = false;
            __instance.SetModified();
        }
        return false;
    }
}

// Shows "Restocks on Day X at Y:00" when aiming at a looted empty container.
// v3.0.1: BlockLoot is gone; restock text now lives in TEFeatureStorage.GetActivationText.
[HarmonyPatch(typeof(TEFeatureStorage), "GetActivationText")]
public class Patch_TEFeatureStorage_RestockText
{
    private static bool Prefix(TEFeatureStorage __instance, ref string __result)
    {
        if (GamePrefs.GetInt(EnumGamePrefs.LootRespawnDays) <= 0) return true;
        if (!__instance.bTouched || !__instance.IsEmpty()) return true;

        // v3.0.32 FIX: the same two guards Patch_TEFeatureStorage_UpdateTick uses.
        // Without them a player-crafted, player-placed storage box that has been
        // opened and left empty reports "Restocks on Day X" even though UpdateTick
        // will never respawn it, because UpdateTick bails on exactly these two
        // conditions. The message was ours, so the mismatch was ours. POI
        // containers and cars are neither PlayerPlaced nor bPlayerStorage, so
        // they keep the indicator.
        if ((__instance.Parent as TileEntityComposite)?.PlayerPlaced == true) return true;
        if (__instance.bPlayerStorage) return true;

        // v3.0.51 OFF-BY-ONE FIX (reported by Kualija): the notice was a day early.
        // The game's own helpers use different bases, which is the trap:
        //     GameUtils.WorldTimeToDays(t)       = t / 24000 + 1     <- day is 1-BASED
        //     GameUtils.WorldTimeToTotalHours(t) = t / 1000          <- hours are 0-BASED
        //     GameUtils.DayTimeToWorldTime(d,..) = (d - 1) * 24000 + ...
        // So respawnHour / 24 is a 0-based day INDEX, and printing it as the day the
        // player sees is one short. Worked example, looted Day 1 at 10:00 with a 5 day
        // respawn: totalHours 10, +120 = 130, 130/24 = 5 so it printed "Day 5" while the
        // container actually restocked on Day 6. The hour was always right: 130 % 24 = 10.
        long touchedHours = GameUtils.WorldTimeToTotalHours(__instance.worldTimeTouched);
        long respawnHour = touchedHours + GamePrefs.GetInt(EnumGamePrefs.LootRespawnDays) * 24L;
        __result = string.Format("Restocks on Day {0} at {1}:00", respawnHour / 24 + 1, respawnHour % 24);
        return false;
    }
}

// Prevents the Emergency Transmitter being placed underground
[HarmonyPatch(typeof(Block), "CanPlaceBlockAt", new Type[] { typeof(WorldBase), typeof(Vector3i), typeof(BlockValue), typeof(bool) })]
public class Patch_Transmitter_CanPlaceBlockAt
{
    private static float lastTooltipTime = -999f;

    private static bool Postfix(bool __result, WorldBase _world, Vector3i _blockPos, BlockValue _blockValue)
    {
        if (!__result) return false;
        if (_blockValue.Block?.GetBlockName() != "satelliteUnitLargeArea7") return __result;
        if (_blockPos.y < 44)
        {
            if (Time.time - lastTooltipTime > 15f)
            {
                EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
                if (player != null)
                {
                    GameManager.ShowTooltip(player, "The transmitter must be placed above ground (Y 44 or higher).", (string)null, null, null, false, true, 10f);
                    lastTooltipTime = Time.time;
                }
            }
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Block), "PlaceBlock", new Type[] { typeof(WorldBase), typeof(BlockPlacement.Result), typeof(EntityAlive) })]
public class Patch_Transmitter_PlaceBlock
{
    private static bool Prefix(WorldBase _world, BlockPlacement.Result _result, EntityAlive _ea)
    {
        if (_result.blockValue.Block?.GetBlockName() != "satelliteUnitLargeArea7") return true;

        if (_result.blockPos.y < 44)
        {
            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player != null)
                GameManager.ShowTooltip(player, "The transmitter must be placed above ground (Y 44 or higher).", (string)null, null, null, false, true, 0f);
            return false;
        }

        return true;
    }
}

// ========================================
// Named Zombies — Challenge Completers
// ========================================
// ===================================================================
// v3.0.43: LOCK THE SANDBOX SETTINGS ON CONTINUED GAMES
//
// WHY. A player could end their session to the menu, hit Continue Game, change the
// preset, and carry on the same save. The debrief reads the preset at escape time, so a
// run played on Recruit could be made to report NIGHTMARE. Fril's rule: if a game is
// started on Grunt it stays Grunt.
//
// This also removes the need to record the starting preset anywhere. If it cannot change
// on continue, the current value IS the original by construction, so there is no state to
// persist and no "recording it just moves the exploit to the other end" problem.
//
// HOW. XUiC_ContinueGame and XUiC_NewGame are siblings under XUiC_NewContinueBase, and
// BOTH override OnOpenSandboxSettingsRequested with their own implementation. Prefixing
// the ContinueGame one therefore CANNOT leak into new games, which run a different method.
//
// Belt and braces, because a disabled button can still be reachable by keyboard or a
// stray binding:
//   - Prefix OnOpenSandboxSettingsRequested to block the action outright
//   - disable btnSandboxOptions and sandboxPresetSelector so it greys out properly
// XUiView.Enabled is a real settable property, so this is the game's own disabled styling
// rather than a click being silently swallowed. Note sandboxPresetSelector is a SEPARATE
// control from the options button: greying only the button would leave the preset
// changeable from the dropdown, which is exactly the exploit.
//
// Vanilla already does per-screen permissions this way (AllowChangingCreativeMode is read
// in XUiC_NewContinueBase.OnOpen), so this sits alongside the existing pattern.
// ===================================================================
public static class Area7SandboxLock
{
    private static bool warned = false;

    public static void DisableSandboxControls(XUiC_NewContinueBase screen)
    {
        try
        {
            if (screen == null || screen.Settings == null) return;

            if (screen.Settings.btnSandboxOptions != null
                && screen.Settings.btnSandboxOptions.ViewComponent != null)
                screen.Settings.btnSandboxOptions.ViewComponent.Enabled = false;

            // v3.0.44: the preset selector is a COMPOSITE. Disabling its own view greys the
            // panel but leaves the arrows live, because they belong to the two combo boxes
            // inside it, not to the selector. Both derive from XUiC_ComboBoxBase, which
            // carries a real Enabled property (with ColorDisabled), so an upcast reaches
            // them and they grey the game's own way.
            XUiC_SandboxPresetSelector sel = screen.Settings.sandboxPresetSelector;
            if (sel != null)
            {
                if (sel.ViewComponent != null)
                    sel.ViewComponent.Enabled = false;

                XUiC_ComboBoxBase groups = sel.cbxPresetGroups;   // the GROUP arrows
                if (groups != null) groups.Enabled = false;

                XUiC_ComboBoxBase presets = sel.cbxPreset;        // the PRESET arrows
                if (presets != null) presets.Enabled = false;
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] Could not disable sandbox controls: " + e.Message);
        }
    }

    public static void LogBlockOnce()
    {
        if (warned) return;
        warned = true;
        UnityEngine.Debug.Log("[Area 7] Sandbox settings are locked on continued games; a run keeps the difficulty it started on.");
    }
}

[HarmonyPatch(typeof(XUiC_ContinueGame), "OnOpenSandboxSettingsRequested")]
public class Area7ContinueSandboxBlockPatch
{
    // Returning false skips the original entirely, so the sandbox window never opens.
    static bool Prefix()
    {
        Area7SandboxLock.LogBlockOnce();
        return false;
    }
}

[HarmonyPatch(typeof(XUiC_ContinueGame), "OnOpen")]
public class Area7ContinueOnOpenPatch
{
    static void Postfix(XUiC_ContinueGame __instance)
    {
        Area7SandboxLock.DisableSandboxControls(__instance);
    }
}

// Selecting a save re-runs the screen's setup, which can re-enable the controls, so the
// disable is re-applied here too. Harmless if it was already off.
[HarmonyPatch(typeof(XUiC_ContinueGame), "SavesList_OnSelectionChanged")]
public class Area7ContinueSelectionPatch
{
    static void Postfix(XUiC_ContinueGame __instance)
    {
        Area7SandboxLock.DisableSandboxControls(__instance);
    }
}

// Loads Config/names.txt (format: PlayerName=ZombieType)
// v3.0.42: ZombieType may be EITHER the entity class name (zombieSoldier, works in every
// language) OR the English display name (Soldier, English clients only). Class names are
// preferred for anything new; see GetNameOverride for why.
// Patches zombie display names so completers appear on health bars.
// Each completer is assigned to a specific zombie type.
// Uses entityId for deterministic assignment — same zombie always gets the same name.

public static class Area7NamedZombies
{
    // Maps zombie type (localized, e.g. "Soldier") to list of completer names
    private static Dictionary<string, List<string>> namesByType = new Dictionary<string, List<string>>();
    private static bool loaded = false;

    public static void LoadNames(string modPath)
    {
        namesByType.Clear();
        string filePath = Path.Combine(modPath, "Config", "names.txt");
        if (!File.Exists(filePath))
        {
            UnityEngine.Debug.Log("[Area 7] No Config/names.txt found — named zombies disabled.");
            loaded = false;
            return;
        }

        string[] lines = File.ReadAllLines(filePath);
        int count = 0;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                continue;

            int eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0 || eqIndex >= trimmed.Length - 1)
                continue;

            string playerName = trimmed.Substring(0, eqIndex).Trim();
            string zombieType = trimmed.Substring(eqIndex + 1).Trim();

            if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(zombieType))
                continue;

            if (!namesByType.ContainsKey(zombieType))
                namesByType[zombieType] = new List<string>();

            namesByType[zombieType].Add(playerName);
            count++;
        }

        loaded = count > 0;
        UnityEngine.Debug.Log("[Area 7] Loaded " + count + " named zombie entries for " + namesByType.Count + " zombie types.");
    }

    // Kept for release (see GetNameOverride). One entry per renamed zombie the player
    // actually looks at, so the set stays small.
    private static readonly HashSet<int> loggedAssignments = new HashSet<int>();

    public static void LogAssignmentOnce(int entityId, string typeName, string chosenName)
    {
        try
        {
            if (loggedAssignments.Contains(entityId)) return;
            loggedAssignments.Add(entityId);
            UnityEngine.Debug.Log("[Area 7] Named zombie: id=" + entityId
                + " " + typeName + " -> " + chosenName);
        }
        catch { }
    }

    // v3.0.65: keep the variant modifier on a renamed zombie, so a Feral Soldier reads
    // "Feral Frilioth" rather than plain "Frilioth".
    //
    // Doing this by sticking "Feral " on the front would be English-only, and this whole
    // system moved to class names in v3.0.42 precisely to stop shipping English-only
    // behaviour. The modifier is translated AND its position and inflection change by
    // language: "Feral Soldier" / "Wilder Soldat" / "Soldado salvaje" / "Soldat féroce".
    //
    // So instead of building the string, we REUSE the game's own translated variant name
    // and swap the creature word inside it for the player's name. The variant display name
    // is already in hand; look up the BASE class's display name and replace that substring.
    //   en: "Feral Soldier"   contains "Soldier"  -> "Feral Frilioth"
    //   de: "Wilder Soldat"   contains "Soldat"   -> "Wilder Frilioth"
    //   es: "Soldado salvaje" contains "Soldado"  -> "Frilioth salvaje"
    //
    // MEASURED against vanilla Localization.csv across all 17 classes in names.txt and all
    // four languages the mod ships: 63 of 68 combinations contain the base name. The five
    // misses are ALL French, where the base carries an extra qualifier the variant drops
    // (Soldat tombé -> Soldat féroce, Fêtarde zombie -> Fêtarde féroce, Infirmière
    // pestiférée -> Infirmière féroce, Ouvrier zombie -> Ouvrier féroce, Touriste dérangé
    // -> Touriste féroce). Those fall through to the bare player name, which is exactly
    // what every language did before this change, so there is no regression anywhere.
    private static readonly string[] VariantSuffixes = { "Feral", "Radiated", "Charged", "Infernal" };

    private static string ApplyVariantModifier(string className, string localisedTypeName, string chosen)
    {
        try
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(localisedTypeName))
                return chosen;

            for (int i = 0; i < VariantSuffixes.Length; i++)
            {
                string suffix = VariantSuffixes[i];
                if (!className.EndsWith(suffix, StringComparison.Ordinal)) continue;

                string baseClass = className.Substring(0, className.Length - suffix.Length);
                if (baseClass.Length == 0) return chosen;

                string baseName = Localization.Get(baseClass, false);
                if (string.IsNullOrEmpty(baseName) || baseName == baseClass) return chosen;

                int at = localisedTypeName.IndexOf(baseName, StringComparison.Ordinal);
                if (at < 0) return chosen; // variant string does not contain the base word

                return localisedTypeName.Substring(0, at)
                     + chosen
                     + localisedTypeName.Substring(at + baseName.Length);
            }
        }
        catch { }
        return chosen;
    }

    public static string GetNameOverride(EntityAlive entity)
    {
        if (!loaded) return null;
        if (!(entity is EntityZombie)) return null;

        // v3.0.42: match on the ENTITY CLASS NAME as well as the localised display name.
        //
        // The lookup used to be display-name only, which quietly broke named zombies for
        // every non-English player. Proof came from Tycapanic's German debrief: her kill
        // list reads Soldat, Krankenschwester, Hausmeister, Laborant, Wartungstechniker,
        // so an English "Frilioth=Soldier" entry could never match for her. Only the
        // PROPER names survive translation (Arlene, Darlene, Joe, Moe, Yo, Boe, Marlene,
        // Steve, Chuck, Tom Clark, Big Mama, Bowler, Tourist), which is why she could see
        // herself at all, and that was luck rather than design. Note even those get a
        // translated MODIFIER: "Feral Steve" becomes "Wilder Steve".
        //
        // The class name (zombieSoldier) is language-independent, so it is checked FIRST.
        // The display name is still checked as a fallback so every existing names.txt
        // entry keeps working exactly as before and the file can be migrated gradually.
        //
        //   Frilioth=zombieSoldier   <- works for every player, every language
        //   Frilioth=Soldier         <- still works, English clients only
        string className = null;
        string typeName = null;
        try
        {
            EntityClass ec = EntityClass.list[entity.entityClass];
            if (ec != null)
            {
                className = ec.entityClassName;
                string locName = Localization.Get(ec.entityClassName, false);
                typeName = (!string.IsNullOrEmpty(locName) && locName != ec.entityClassName)
                    ? locName
                    : ec.entityClassName;
            }
        }
        catch { return null; }

        if (string.IsNullOrEmpty(className) && string.IsNullOrEmpty(typeName)) return null;

        List<string> names = null;
        if (!string.IsNullOrEmpty(className))
            namesByType.TryGetValue(className, out names);
        if ((names == null || names.Count == 0) && !string.IsNullOrEmpty(typeName))
            namesByType.TryGetValue(typeName, out names);
        if (names == null || names.Count == 0)
            return null;

        // Deterministic per-zombie pick, but FAIR between names.
        //
        // The old method did `slot = entityId % (names.Count + 2)` and used slot both to
        // decide whether to rename AND which name to use. That couples the two decisions and,
        // because zombies spawn in groups with consecutive entityIds, the raw IDs are not
        // uniform mod (count+2) - so with two names sharing a type, one name systematically
        // won (e.g. China showing far more than Red). Fix: hash the entityId first to break
        // the spawn-ID clustering, then make the two decisions SEPARATELY from independent
        // parts of the hash - a throttle gate (keep the ~1-in-3 default-name rate) and a
        // uniform choice among the names.
        uint h = (uint)entity.entityId;
        h *= 2654435761u;            // Knuth multiplicative mix
        h ^= h >> 16;
        h *= 2246822519u;
        h ^= h >> 13;

        // Throttle: roughly 1 in 3 zombies of this type stays un-named, matching the old
        // (+2)/(count+2) miss rate at two names. Uses the top bits of the hash.
        // missChance = 2 / (names.Count + 2); rename when the gate passes.
        uint gate = (h >> 8) % (uint)(names.Count + 2);
        if (gate >= (uint)names.Count)
            return null; // keeps its default name

        // v3.0.45: WHICH name comes from the RAW entity id, not the hash.
        //
        // Entity ids are handed out sequentially, verified in game with a throwaway probe:
        // 84 zombies spawned across several sleeper volumes gave 81 gaps of 1 and two of 2,
        // with nothing interleaving. So consecutive spawns walk along the name list, and a
        // room of sleepers gets a different name each. Measured on those real ids for
        // Soldier: at 15 names, 6 named zombies used 6 distinct names, zero repeats.
        //
        // That is the shuffled-bag idea achieved for free. No cursor, no cache, nothing to
        // persist, and it stays deterministic so a zombie's name never changes while you
        // look at it.
        //
        // The THROTTLE still uses the hash, which is what keeps the split fair between
        // people sharing a type. Only the pick changed. Using the raw id for BOTH is what
        // caused the old China-vs-Red bias, so they must stay on different sources.
        //
        // Small lists cannot benefit: five sleepers cannot have five different names from a
        // list of two. Measured on the same ids, repeats appear at N=2, 3 and 5 and vanish
        // by N=15. That is arithmetic, not a fault.
        int pick = (int)((uint)entity.entityId % (uint)names.Count);
        string chosen = names[pick];

        // v3.0.65: keep the variant modifier, e.g. "Feral Frilioth" not "Frilioth".
        chosen = ApplyVariantModifier(className, typeName, chosen);

        // KEPT DELIBERATELY (Fril's call, v3.0.46). Not a diagnostic: it is the only record
        // of who actually appeared in a run, which is useful for the naming decisions.
        // GetNameOverride runs every time the target bar refreshes, so this logs once per
        // entity id rather than once per frame, and only for zombies that get renamed.
        LogAssignmentOnce(entity.entityId, typeName ?? className, chosen);

        return chosen;
    }
}

// =====================================================================================
// v3.0.78: VARY THE WALK ANIMATION PER ZOMBIE.
//
// walkType is per ENTITY, but vanilla only ever sets it once at spawn from the class's
// WalkType property, so every Soldier in the game shambles identically. This gives each
// individual zombie its own gait, so a room of five no longer moves in lockstep.
//
// SAFE VALUES, read out of vanilla entityclasses.xml rather than guessed. Vanilla uses
// only these six standing gaits across every zombie it ships:
//     1 Fat      Moe, Big Mama, Fat Hawaiian, Bowler, Fat Cop
//     2          Chuck, Boe, Lumberjack, Soldier, Feral Wight, Mutated
//     3          slim female template, Arlene, Darlene, Screamer
//     5 Cripple  Marlene, Joe, Janitor, Inmate, Yo
//     6          Steve, Tom Clark, Utility Worker
//     7          male template, Nurse, Businessman, Burnt, Rancher, Hazmat, Lab, Biker
// 0, 4 and 8 to 20 are UNUSED and have no animation behind them: setting one drops the
// model through the floor. Fril hit exactly that on 19 Aug when a survey build handed
// out 13, 15, 16 and 17.
//
// NEVER TOUCHED:
//   - crawlers and spiders (native walkType >= 20). SetWalkType special-cases those and
//     calls TurnIntoCrawler, so reassigning one would stand it up or floor a walker.
//   - anything that is not an EntityZombie. npcSurvivorTemplate and npcTraderTemplate
//     also carry a WalkType, and Hugh must keep his.
//
// The pick is derived from the entity id so it is deterministic: the same zombie always
// walks the same way, with no saved state. Entity ids are handed out sequentially
// (verified: 84 zombies, 81 gaps of 1), so consecutive spawns in a room get consecutive
// ids and therefore different gaits. It is hashed first, and takes a DIFFERENT slice of
// the hash from the naming code, so a zombie's walk does not correlate with whether it
// carries someone's name.
// =====================================================================================
public static class Area7WalkVariety
{
    // Vanilla's six standing gaits. See the comment above before adding to this.
    private static readonly int[] SafeWalkTypes = { 1, 2, 3, 5, 6, 7 };

    private const int CrawlFirst = 20;   // EntityAlive.cWalkTypeCrawlFirst

    public static void Apply(EntityAlive entity)
    {
        try
        {
            if (entity == null) return;
            if (!(entity is EntityZombie)) return;

            // Leave crawlers and spiders exactly as the class defined them.
            if (entity.GetWalkType() >= CrawlFirst) return;

            // Straight from the RAW entity id, deliberately, and this is the OPPOSITE of the
            // decision the naming code makes. Ids are handed out sequentially, so raw id
            // modulo six guarantees that six consecutive spawns get six DIFFERENT gaits:
            // a room of five never moves in lockstep. A hash was tried first and clustered
            // badly on real ids -- 173 to 179 came out 3,5,5,5,1,5,5, five of seven the same
            // -- which is statistically fine over 60,000 ids and useless in one room.
            //
            // Naming uses a hash because there the goal is a FAIR SPLIT between people.
            // Here the goal is VISIBLE VARIETY in a group, so predictable beats random.
            int pick = SafeWalkTypes[(int)((uint)entity.entityId % (uint)SafeWalkTypes.Length)];
            if (pick == entity.GetWalkType()) return;

            entity.SetWalkType(pick);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(EntityAlive), "CopyPropertiesFromEntityClass")]
public class Area7_WalkVarietyPatch
{
    static void Postfix(EntityAlive __instance)
    {
        Area7WalkVariety.Apply(__instance);
    }
}

// Patches the target bar HUD to show completer names for matching zombies
[HarmonyPatch(typeof(XUiC_TargetBar), "GetBindingValueInternal")]
public class Area7NamedZombiePatch
{
    static void Postfix(XUiC_TargetBar __instance, ref bool __result, ref string value, string bindingName)
    {
        if (bindingName != "name") return;
        if (!__result) return;

        EntityAlive target = __instance.Target;
        if (target == null) return;

        string nameOverride = Area7NamedZombies.GetNameOverride(target);
        if (nameOverride != null)
            value = nameOverride;
    }
}



// v3.0.5: Force the "Survival Gear" (biome badge) tab visible in the character screen even when
// Biome Progression is OFF, so the Area 7 radiation badge can be equipped with progression disabled.
// The gear tab button uses visible="{gear_visible}", resolved by XUiC_CharacterFrameWindow's binding
// provider (returns false with progression off). We override just that one binding to "true". The
// biome HAZARD system reads progression separately, so this does NOT re-enable the biome-lock.
// Same binding-override pattern as the zombie-name patch above.
[HarmonyPatch(typeof(XUiC_CharacterFrameWindow), "GetBindingValueInternal")]
public class Area7_ShowSurvivalGearTabPatch
{
    // Harmony matches injected params by NAME; this override names them _value/_bindingName.
    static void Postfix(ref bool __result, ref string _value, string _bindingName)
    {
        if (_bindingName == "gear_visible")
        {
            _value = "true";
            __result = true;
        }
    }
}


// v3.0.64: Restore the right-click "Wear" option for biome-badge items with Biome Progression OFF.
//
// The patch above makes the Survival Gear TAB visible, so the badge can be dragged into its slot.
// The context-menu entry is gated separately and was still missing, which reads to a player as
// "the respirator is bugged" -- reported twice now, most recently by a viewer on stream who
// dragged it into the wrong slot and gave up.
//
// The gate is ItemClassArmor.CanEquip() (virtual, instance, no parameters). Decompiled:
//     if (!World.TemperatureSurvival && !World.StormFrequency
//         && (EquipSlot == ClothingHead || EquipSlot == ClothingFeet)) return false;
//     if (!World.BiomeProgressionEnabled
//         && EquipSlot is in BiomeBadge..BiomeBadge4)                  return false;
//     return true;
// XUiC_ItemActionList.SetCraftingActionList calls CanEquip immediately before constructing the
// ItemActionEntryWear entry, so a false result means the option is never added to the menu at all.
// ItemActionEntryWear.OnActivated has NO gate of its own -- it just calls PlayerEquipment.EquipItem
// -- which is exactly why DRAGGING into the slot has always worked.
//
// We flip only the badge branch, and only when vanilla already said no, so the temperature and
// storm gate on the clothing slots is left completely alone. Fril's call: all four badge slots
// rather than matching on the respirator specifically. Consistent with the tab already being
// forced visible, and on a single-biome world the vanilla badges becoming equippable is harmless.
[HarmonyPatch(typeof(ItemClassArmor), "CanEquip")]
public class Area7_BadgeSlotCanEquipPatch
{
    static void Postfix(ItemClassArmor __instance, ref bool __result)
    {
        try
        {
            if (__result || __instance == null) return;

            EquipmentSlots slot = __instance.EquipSlot;
            if (slot >= EquipmentSlots.BiomeBadge && slot <= EquipmentSlots.BiomeBadge4)
                __result = true;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[Area 7] CanEquip postfix failed: " + e.Message);
        }
    }
}
// (unresizable) message box and back to windowTip -- the same window Fril styled in 2.5 and we
// re-tuned for 3.0. 3.0 rerouted ShowMessageWindow to XUiC_MessageBoxWindowGroup.ShowOk; here we
// intercept just our message and call XUiC_TipWindow.ShowTip instead. Every other quest message
// keeps the default box untouched.
[HarmonyPatch(typeof(QuestActionShowMessageWindow), "PerformAction")]
public class Area7_MessageToTipWindowPatch
{
    static bool Prefix(QuestActionShowMessageWindow __instance)
    {
        if (__instance == null || string.IsNullOrEmpty(__instance.title) ||
            !__instance.title.StartsWith("area7", System.StringComparison.OrdinalIgnoreCase))
            return true; // not ours -- let the original show the default message box

        EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
        if (player == null)
            return true; // fallback to default behaviour

        string title = Localization.Get(__instance.title);
        string body = Localization.Get(__instance.message);
        XUiC_TipWindow.ShowTip(body, title, player, null);
        return false; // handled -- skip the built-in message box
    }
}


// v3.0.13: the airdrop "MILITARY SUPPLY REQUEST FORM" (IOU) notes are NOT quest notes -- they use
// an item use-prompt (ItemActionEntryUse.OnActivated -> XUiC_MessageBoxWindowGroup.ShowOkCancel),
// which is the engine's red OK/CANCEL box. Redirect just those six notes to windowTip so they match
// the rules/evac/debrief popups, and windowTip's single Continue button replaces OK/CANCEL.
// The prompt passes ALREADY-LOCALISED text, so we compare against the localised form of each
// PromptTitle key (works in any language, since both sides come from the same Localization.csv).
// NOTE: skipping ShowOkCancel means its _onOk callback never runs. That callback is what carried out
// the item's Eat action, so reading a supply form no longer consumes the note.
[HarmonyPatch(typeof(XUiC_MessageBoxWindowGroup), "ShowOkCancel")]
public class Area7_SupplyFormToTipWindowPatch
{
    // The six airdrop IOU notes in 0_Area7Items/Config/items.xml (each item's PromptTitle).
    private static readonly string[] SupplyFormTitleKeys =
    {
        "noteArea7IOU", "noteArea7IOU1", "noteArea7IOU2",
        "noteArea7IOU3", "noteArea7IOU4", "noteArea7IOU5"
    };

    static bool Prefix(string _title, string _text)
    {
        if (string.IsNullOrEmpty(_title))
            return true; // not ours -- let the original box show

        bool isSupplyForm = false;
        for (int i = 0; i < SupplyFormTitleKeys.Length; i++)
        {
            string localised = Localization.Get(SupplyFormTitleKeys[i]);
            if (!string.IsNullOrEmpty(localised) && _title == localised)
            {
                isSupplyForm = true;
                break;
            }
        }
        if (!isSupplyForm)
            return true; // some other dialog -- leave it alone

        EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
        if (player == null)
            return true; // fallback to default behaviour

        XUiC_TipWindow.ShowTip(_text, _title, player, null);
        return false; // handled -- skip the built-in OK/CANCEL box
    }
}


// v3.0.15: give Area 7 its OWN group in the Sandbox Options preset dropdown, instead of presets
// landing under "User". The dropdown's groups are simply the DISTINCT `Group` values across the
// loaded presets (SandboxOptionManager.GetAllPresetGroups), so a preset carrying a custom group
// creates a new category. LoadPresetFromXml takes a presetGroupOverride + isModded flag (public API).
// Vanilla only loads presets from a baked Unity resource + the user's own Presets folder, so a mod
// must feed its own in; this reads Config/area7_presets.xml. Editing that XML needs NO recompile.
//
// v3.0.14 hooked ONLY LoadPresets and logged nothing on the early-exit paths, so a miss was
// undiagnosable. This version: (1) registers from EITHER hook, so it cannot be defeated by
// LoadPresets having already run before our Harmony patches were applied; (2) is idempotent by
// asking the manager for its current groups rather than trusting a flag - LoadPresets CLEARS the
// preset dictionary before repopulating, so a one-shot flag would be wrong; (3) always logs what
// it found, so a missing file or an unfilled placeholder shows up in Player.log.
public static class Area7SandboxPresets
{
    private const string PresetFileName = "area7_presets.xml";
    private const string DefaultGroupName = "Area 7";
    private const string CodePlaceholder = "PASTE_YOUR_SANDBOX_CODE_HERE";

    private static string lastLogged = "";

    private static void LogOnce(string message)
    {
        // updatePresetGroups fires every time the screen refreshes; don't spam the log.
        if (message == lastLogged) return;
        lastLogged = message;
        UnityEngine.Debug.Log("[Area 7] " + message);
    }

    public static void EnsureRegistered(SandboxOptions.SandboxOptionManager manager)
    {
        try
        {
            if (manager == null) return;

            string modPath = Area7ChallengeMod.GetModPath();
            if (string.IsNullOrEmpty(modPath))
            {
                LogOnce("Sandbox presets: mod path not set yet, skipping for now.");
                return;
            }

            string path = System.IO.Path.Combine(modPath, "Config", PresetFileName);
            if (!System.IO.File.Exists(path))
            {
                LogOnce("Sandbox presets: file NOT FOUND at " + path);
                return;
            }

            System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(System.IO.File.ReadAllText(path));
            if (doc.Root == null)
            {
                LogOnce("Sandbox presets: " + PresetFileName + " has no root element.");
                return;
            }

            string groupName = DefaultGroupName;
            System.Xml.Linq.XAttribute groupAttr = doc.Root.Attribute("category");
            if (groupAttr != null && !string.IsNullOrEmpty(groupAttr.Value))
                groupName = groupAttr.Value;

            var existingGroups = manager.GetAllPresetGroups();
            if (existingGroups != null && existingGroups.Contains(groupName))
                return; // already registered this session

            int loaded = 0, skipped = 0;
            foreach (System.Xml.Linq.XElement element in doc.Root.Elements())
            {
                if (element.Name.LocalName != "preset") continue;

                System.Xml.Linq.XAttribute codeAttr = element.Attribute("code");
                if (codeAttr == null || string.IsNullOrEmpty(codeAttr.Value) || codeAttr.Value == CodePlaceholder)
                {
                    skipped++;
                    continue; // code not pasted in yet
                }

                try
                {
                    manager.LoadPresetFromXml(element, groupName, true);
                    loaded++;
                }
                catch (Exception inner)
                {
                    System.Xml.Linq.XAttribute nameAttr = element.Attribute("name");
                    string presetName = nameAttr != null ? nameAttr.Value : "(unnamed)";
                    UnityEngine.Debug.LogError("[Area 7] Sandbox preset '" + presetName + "' failed to load: " + inner.Message);
                }
            }

            LogOnce("Sandbox presets for group \"" + groupName + "\": " + loaded
                    + " loaded, " + skipped + " skipped (code still the placeholder). File: " + path);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Sandbox preset registration failed (non-fatal): " + e.Message);
        }
    }
}

// Hook 1: normal path, straight after the game loads its own presets.
[HarmonyPatch(typeof(SandboxOptions.SandboxOptionManager), "LoadPresets")]
public class Area7_SandboxPresetLoadPatch
{
    static void Postfix(SandboxOptions.SandboxOptionManager __instance)
    {
        Area7SandboxPresets.EnsureRegistered(__instance);
    }
}

// Hook 2: the selector rebuilds its group dropdown here every time the options screen opens, which
// is guaranteed to be long after the mod loaded. This is what makes the feature immune to
// LoadPresets having already run before our patches were applied.
[HarmonyPatch(typeof(XUiC_SandboxPresetSelector), "updatePresetGroups")]
public class Area7_SandboxPresetSelectorPatch
{
    // The group INTERNAL name (the preset XML category="Area7"), NOT the displayed
    // "Area 7". The combobox stores SandboxPresetGroupData whose InternalName is this.
    private const string LockGroupInternalName = "Area7";
    // The vanilla player-editable group. Kept alongside Area7 so players who want to set
    // their own sandbox settings can, without diluting the five curated Area 7 presets.
    private const string CustomGroupInternalName = "Custom";
    // The group that a player's OWN saved presets go into ("User"). Kept in the filter so
    // players can save AND keep multiple named custom modes and see their names (not
    // "Unsaved"). Safe to keep because Area7_SandboxDisplayNullGuardPatch defends the vanilla
    // NRE that keeping it would otherwise expose. Internal names via reflection = Custom/User/Modded.
    private const string UserGroupInternalName = "User";

    static void Prefix()
    {
        Area7SandboxPresets.EnsureRegistered(SandboxOptions.SandboxOptionManager.Current);
    }

    // Lock the New Game "Group" selector to Area 7 + Custom. updatePresetGroups clears the
    // combobox and re-adds every group GetAllPresetGroups() reports (skipping "Custom",
    // which it only adds when called with _allowCustom: true - the New Game screen passes
    // false), so it runs on every screen refresh. This Postfix strips everything except our
    // group AND Custom AFTER the game has built the list, then re-adds Custom since the
    // screen never included it. Area 7 stays selected by default; the player picks Custom
    // deliberately if they want the sliders. Because the Prefix above guarantees our group is
    // registered first, Area 7 is always present, and the game's own
    // selectGroupByName (a FindIndex + set_SelectedIndex) then lands on it cleanly - the
    // arrows have nowhere else to go. Fril's call: hard lock, Area 7 only, always.
    static void Postfix(XUiC_SandboxPresetSelector __instance)
    {
        try
        {
            var combo = __instance.cbxPresetGroups;
            if (combo == null) return;

            System.Collections.Generic.List<SandboxPresetGroupData> elements = combo.Elements;
            if (elements == null || elements.Count == 0) return;

            // Is our group present? If a load order or a missing presets file ever meant it
            // was NOT, filtering to it would leave an EMPTY selector, which is worse than a
            // full one - so only strip the others when we can confirm ours is there.
            bool haveOurs = false;
            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i].InternalName == LockGroupInternalName) { haveOurs = true; break; }
            }
            if (!haveOurs)
            {
                UnityEngine.Debug.LogWarning("[Area 7] Group lock skipped: '" + LockGroupInternalName
                    + "' not in the preset group list yet. Leaving the selector unfiltered.");
                return;
            }

            // Keep our Area 7 group, the vanilla "Custom" group, AND the "User" group.
            // Strip everything else (Modded, Official, difficulty groups).
            //   - Area7:  our five curated presets (the default, non-editable experience).
            //   - Custom: the engine's live-edit scratch preset - selecting it exposes the
            //             sandbox sliders through the working vanilla edit path.
            //   - User:   where a player's OWN saved+named presets live. Keeping it means
            //             (a) saved custom modes show their name instead of "Unsaved", and
            //             (b) a player can keep MORE THAN ONE saved custom mode and pick
            //             between them (without User there is only the single Custom slot,
            //             so every save overwrites the last).
            // Keeping User was tried in v3.0.26 and exposed a latent vanilla NRE in
            // XUiC_SandboxSettingsDisplay.updateSandboxData() (it derefs a null GetPreset
            // result). That is now defended by Area7_SandboxDisplayNullGuardPatch, so keeping
            // User is safe. Do NOT remove that guard while this keeps "User".
            int removed = elements.RemoveAll(g => g.InternalName != LockGroupInternalName
                                                && g.InternalName != CustomGroupInternalName
                                                && g.InternalName != UserGroupInternalName);

            // The New Game screen calls updatePresetGroups(_allowCustom: false), so the game
            // NEVER adds the Custom group to the list here - the RemoveAll above cannot keep
            // what was never present. So re-add it ourselves. SandboxPresetGroupData has a
            // public (internalName, formattedName) ctor; the game builds its own Custom entry
            // exactly as Add("Custom", "Custom"), which we mirror. The group's display name
            // still localises via sandboxPresetGroupCustom, same as vanilla.
            bool haveCustom = false;
            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i].InternalName == CustomGroupInternalName) { haveCustom = true; break; }
            }
            if (!haveCustom)
            {
                elements.Add(new SandboxPresetGroupData(CustomGroupInternalName, CustomGroupInternalName));
            }

            // Point the combobox at our Area 7 group so the New Game screen OPENS on the
            // curated presets, not on User or Custom. Hardcoding index 0 was wrong: after
            // keeping three groups (Area7 + Custom + User) the game's own ordering does not
            // guarantee Area7 is first, and Fril saw User show first. So find Area7's actual
            // index and select THAT. Falls back to 0 only if (somehow) it isn't found.
            int area7Index = elements.FindIndex(g => g.InternalName == LockGroupInternalName);
            combo.SelectedIndex = (area7Index >= 0) ? area7Index : 0;

            UnityEngine.Debug.Log("[Area 7] Group selector locked to '" + LockGroupInternalName
                + "' + Custom + User (removed " + removed + " other group(s), custom "
                + (haveCustom ? "kept" : "re-added") + ").");
        }
        catch (Exception e)
        {
            // Never let this break the New Game screen - a full group list is a harmless
            // fallback, an exception here is not.
            UnityEngine.Debug.LogError("[Area 7] Group lock failed (non-fatal): " + e.Message);
        }
    }
}


// Lock the New Game "Game World Type" selector to Existing Random World only.
//
// Area 7 ships five pre-generated worlds in the mod's Worlds folder (confirmed from a
// Player.log: createWorld logs them with "src: Mods"). Those load via the ExistingRandom
// world type. The other two options are wrong for this mod: CreateRandom generates a fresh
// plain map with no Area 7 in it, and Handmade means Navezgane. Fril wants the selector
// pinned so players can only pick the mode his worlds actually use; the "Choose Area 7
// (Biome)" selector underneath still lets them choose which of the five.
//
// The control is XUiC_NewGame.cbxWorldType, a XUiC_ComboBoxEnum<EWorldType>. Unlike the
// group selector (a list we filtered), an enum combobox bounds its reachable values with
// nullable Min/Max properties. So we clamp Min = Max = Value = ExistingRandom. Setting
// Value fires CbxWorldType_OnValueChanged, which calls updateWorlds(), so the biome list
// underneath repopulates correctly for the locked type - that is the wiring that made this
// safe to do (verified in IL before building).
//
// Patched on OnOpen so it re-applies every time the New Game screen opens. Wrapped so it
// can never break the screen; an unlocked selector is a harmless fallback.
[HarmonyPatch(typeof(XUiC_NewGame), "OnOpen")]
public class Area7_WorldTypeLockPatch
{
    static void Postfix(XUiC_NewGame __instance)
    {
        try
        {
            var combo = __instance.cbxWorldType;
            if (combo == null) return;

            XUiC_NewGame.EWorldType existing = XUiC_NewGame.EWorldType.ExistingRandom;

            // Clamp the reachable range to the single value, then force the current value.
            // Order matters: set the value last so the OnValueChanged -> updateWorlds() runs
            // with the range already locked.
            combo.Min = existing;
            combo.Max = existing;
            if (!combo.Value.Equals(existing))
                combo.Value = existing;   // fires updateWorlds(), refreshes the biome list

            UnityEngine.Debug.Log("[Area 7] World type locked to Existing Random World.");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] World type lock failed (non-fatal): " + e.Message);
        }
    }
}


// ---------------------------------------------------------------
// NEW GAME WORLD-LIST FILTER
// The New Game "Choose Area 7 (Biome)" selector normally lists EVERY world on the
// player's PC (base-game, downloaded, and mod-provided). Area 7 ships its own worlds
// inside Mods/FrilArea7Challenge/Worlds, and we want ONLY those to be selectable.
//
// updateWorlds() rebuilds cbxWorldName.Elements (a List<GameWorldInfo>) for the current
// world type. Each GameWorldInfo carries an AbstractedLocation (Location) whose
// ContainingMod is the mod that provided the world, or null for base-game / user worlds.
// So this Postfix keeps only worlds whose Location.ContainingMod is OUR mod, then repairs
// the selection so the combobox lands on a valid remaining world.
[HarmonyPatch(typeof(XUiC_NewGame), "updateWorlds")]
public class Area7_WorldListFilterPatch
{
    // The mod folder name whose worlds are allowed. Matches ModInfo <Name>.
    private const string AllowedModName = "FrilArea7Challenge";

    // updateWorlds() fires on every screen refresh, so the skip warning is de-duplicated
    // the same way Area7_SandboxPresetRegistrar does it.
    private static string lastWarned = "";

    private static void WarnOnce(string message)
    {
        if (message == lastWarned) return;
        lastWarned = message;
        UnityEngine.Debug.LogWarning("[Area 7] " + message);
    }

    static void Postfix(XUiC_NewGame __instance)
    {
        try
        {
            var combo = __instance.cbxWorldName;
            if (combo == null || combo.Elements == null) return;

            var elements = combo.Elements;
            int before = elements.Count;
            if (before == 0) return;

            // v3.0.32 GUARD: the same shape the group lock uses, and for the same reason.
            // updateWorlds() can run at a point where the Area 7 worlds are not in the list
            // yet (Holls hit this on a first-ever New Game: she saved a tweaked preset under
            // "User", came back, and the world selector was blank with the arrows greyed out;
            // leaving the tab and returning rebuilt it). Without this check RemoveAll strips
            // everything and we hand the player a mod with no worlds. An unfiltered list is
            // recoverable; an empty one is not. So only strip the others once we can confirm
            // ours is actually there.
            bool haveOurs = false;
            for (int i = 0; i < elements.Count; i++)
            {
                var m = elements[i].Location.ContainingMod;
                if (m != null && !string.IsNullOrEmpty(m.Name)
                    && m.Name.Equals(AllowedModName, StringComparison.OrdinalIgnoreCase))
                {
                    haveOurs = true;
                    break;
                }
            }
            if (!haveOurs)
            {
                WarnOnce("World list filter skipped: no '" + AllowedModName
                    + "' worlds in the list yet (" + before + " entries). Leaving it unfiltered.");
                return;
            }

            // Keep only worlds provided by our mod. A world is ours when its location's
            // ContainingMod is non-null and its Name matches AllowedModName. Base-game and
            // user-folder worlds have a null ContainingMod and are dropped.
            int removed = elements.RemoveAll(w =>
            {
                var mod = w.Location.ContainingMod;
                return mod == null
                    || string.IsNullOrEmpty(mod.Name)
                    || !mod.Name.Equals(AllowedModName, StringComparison.OrdinalIgnoreCase);
            });

            if (removed <= 0)
                return; // nothing to strip (already only our worlds, or list empty)

            // Repair the selection: if the previously selected world was removed, point the
            // combobox at the first surviving Area 7 world so nothing downstream reads a stale
            // value. Setting Value fires the combo's OnValueChanged, which updates the preview.
            if (elements.Count > 0)
            {
                bool currentStillPresent = false;
                var current = combo.Value;
                for (int i = 0; i < elements.Count; i++)
                {
                    if (elements[i].Equals(current)) { currentStillPresent = true; break; }
                }
                if (!currentStillPresent)
                    combo.Value = elements[0];
            }

            UnityEngine.Debug.Log("[Area 7] World list filtered to '" + AllowedModName
                + "' worlds (removed " + removed + " of " + before + ").");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] World list filter failed (non-fatal): " + e.Message);
        }
    }
}


// ---------------------------------------------------------------
// SANDBOX SETTINGS DISPLAY - NULL-PRESET GUARD
// This defends a latent VANILLA bug that our group filter exposes, so we can safely
// keep the vanilla "User" group (where a player's own saved presets live) without the
// Continue screen throwing.
//
// XUiC_SandboxSettingsDisplay.updateSandboxData() does, in effect:
//     if (sandboxName != "") {
//         var preset = sandboxManager.GetPreset(sandboxName);
//         this.sandboxCode = preset.get_SandboxCode();   // <-- NRE if preset == null
//     } else {
//         ... construct a fresh SandboxOptionPreset from the code instead ...
//     }
// The named-preset branch calls GetPreset then immediately dereferences the result with
// NO null-check. When a save references a preset whose group isn't currently in the list,
// GetPreset returns null and vanilla throws "Object reference not set" (once, on the
// Continue screen). The method ALREADY has a valid fallback: the else-branch builds a
// preset straight from the sandbox code. So the safe, minimal fix is - when the named
// lookup would return null - blank sandboxName for this one call so vanilla takes its OWN
// construct-from-code path. We restore sandboxName in a Postfix so nothing else sees the
// change. This touches ONLY the display path, never the edit/save path.
[HarmonyPatch(typeof(XUiC_SandboxSettingsDisplay), "updateSandboxData")]
public class Area7_SandboxDisplayNullGuardPatch
{
    static void Prefix(XUiC_SandboxSettingsDisplay __instance, out string __state)
    {
        __state = null; // by default, changed nothing
        try
        {
            string name = __instance.sandboxName;
            if (string.IsNullOrEmpty(name))
                return; // empty name already takes the safe construct-from-code branch

            var mgr = __instance.sandboxManager;
            if (mgr == null)
                return;

            // Mirror vanilla's own lookup. If it resolves, leave everything alone - the
            // normal path is fine. Only intervene when it would be null (the crash case).
            if (mgr.GetPreset(name) == null)
            {
                __state = name;            // remember the real name to restore after
                __instance.sandboxName = ""; // steer vanilla into its construct-from-code branch
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("[Area 7] Sandbox display guard (prefix) failed (non-fatal): " + e.Message);
        }
    }

    static void Postfix(XUiC_SandboxSettingsDisplay __instance, string __state)
    {
        // Restore the original name if we blanked it, so no other code sees the temporary change.
        if (__state != null)
        {
            try { __instance.sandboxName = __state; }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[Area 7] Sandbox display guard (postfix) failed (non-fatal): " + e.Message);
            }
        }
    }
}
// Skips the ~20 minutes of challenges when testing the extraction sequence.
//   a7 hugh     spawn Hugh at a camp, save state, add the Camp Frilsville waypoint
//   a7 tp       teleport to Hugh's camp
//   a7 chopper  redeem escapeArea7 right now, which fires the extraction/fly-in
//   a7 status   print the current saved state
// ---------------------------------------------------------------
public class ConsoleCmdArea7Test : ConsoleCmdAbstract
{
    public override string[] getCommands()
    {
        return new string[] { "a7", "area7test" };
    }

    public override string getDescription()
    {
        return "Area 7 test shortcuts (hugh / tp / chopper / status / sleepers)";
    }

    public override string getHelp()
    {
        return "Usage:\n"
             + "  a7 hugh     - spawn Hugh at a camp and add the Camp Frilsville waypoint\n"
             + "  a7 tp       - teleport to Hugh's camp\n"
             + "  a7 chopper  - trigger the extraction (fly-in) immediately\n"
             + "  a7 status   - show the saved Area 7 state\n"
             + "  a7 code     - print the leaderboard completion code for this run\n"
             + "  a7 sleepers - fire the transmitter sleeper response in the bunker (repeatable). Optional: a7 sleepers <seconds> sets the hunt stagger";
    }

    public override void Execute(System.Collections.Generic.List<string> _params, CommandSenderInfo _senderInfo)
    {
        try
        {
            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null)
            {
                SdtdConsole.Instance.Output("[Area 7] No local player.");
                return;
            }

            string sub = (_params != null && _params.Count > 0) ? _params[0].ToLower() : "";

            switch (sub)
            {
                case "hugh":
                {
                    Vector3 pos = ChallengeRedeemPatch.TestDeployHugh(player);
                    SdtdConsole.Instance.Output("[Area 7] Hugh deployed at " + pos
                        + ". Waypoint added - use 'a7 tp' to jump there.");
                    break;
                }

                case "tp":
                {
                    Vector3 pos = ChallengeRedeemPatch.GetHughSpawnPos();
                    if (pos == Vector3.zero)
                    {
                        SdtdConsole.Instance.Output("[Area 7] No camp recorded yet - run 'a7 hugh' first.");
                        break;
                    }
                    // drop in slightly above ground so we never land inside terrain
                    player.SetPosition(new Vector3(pos.x, pos.y + 2f, pos.z), true);
                    SdtdConsole.Instance.Output("[Area 7] Teleported to " + pos);
                    break;
                }

                case "chopper":
                {
                    bool ok = Area7ChallengeMod.ForceRedeemChallenge(player, "escapeArea7");
                    SdtdConsole.Instance.Output(ok
                        ? "[Area 7] escapeArea7 redeemed - extraction inbound."
                        : "[Area 7] Could not redeem escapeArea7.");
                    break;
                }

                case "code":
                {
                    // v3.0.67: write to the LOG as well as the console. SdtdConsole.Output
                    // goes to the console window only and never reaches Player.log, so a
                    // player running this had nothing to copy from except the console, which
                    // is awkward to select text in. Logging it means the code lands in a text
                    // file they can copy out of and send on, and it means we can see whether
                    // the command actually worked when someone sends us their log.
                    string c = Area7CompletionCode.Build(player);
                    if (string.IsNullOrEmpty(c))
                    {
                        SdtdConsole.Instance.Output("[Area 7] Could not build a completion code.");
                        UnityEngine.Debug.LogWarning("[Area 7] Could not build a completion code.");
                    }
                    else
                    {
                        bool done = Area7RunStats.challengeTimes.ContainsKey("escapearea7");
                        string note = done ? "" : "  (RUN NOT COMPLETE - this code cannot be ranked)";
                        SdtdConsole.Instance.Output("[Area 7] Completion code: " + c + note);
                        UnityEngine.Debug.Log("[Area 7] Completion code: " + c + note);
                    }
                    break;
                }

                case "status":
                {
                    SdtdConsole.Instance.Output("[Area 7] SignalSent  = "
                        + ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_SIGNAL_SENT));
                    SdtdConsole.Instance.Output("[Area 7] HughSpawned = "
                        + ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_HUGH_SPAWNED));
                    SdtdConsole.Instance.Output("[Area 7] HughPos     = "
                        + ChallengeRedeemPatch.LoadPlayerData(player, ChallengeRedeemPatch.KEY_HUGH_POSITION));
                    break;
                }

                case "sleepers":
                {
                    // Run just the transmitter's random-half sleeper reset, repeatable, bypassing
                    // the one-shot signal guard. Logs each reset volume's centre to Player.log so
                    // successive runs can be diffed to confirm a different half each time.
                    float staggerOverride = -1f;
                    if (_params != null && _params.Count > 1)
                    {
                        float parsed;
                        if (float.TryParse(_params[1], out parsed)) staggerOverride = parsed;
                    }
                    int done = ChallengeRedeemPatch.ResetRandomHalfSleeperVolumes(GameManager.Instance.World, player, true, staggerOverride);
                    SdtdConsole.Instance.Output("[Area 7] Sleeper response fired: " + done
                        + " volumes in the compound reset (see Player.log for the hunting/sleeping split + stagger). "
                        + "Repeatable; 'a7 sleepers <seconds>' overrides the hunt stagger for testing.");
                    break;
                }

                default:
                    SdtdConsole.Instance.Output(getHelp());
                    break;
            }
        }
        catch (Exception e)
        {
            SdtdConsole.Instance.Output("[Area 7] Command failed: " + e.Message);
        }
    }
}
