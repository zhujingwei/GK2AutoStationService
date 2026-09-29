using System;
using System.Collections.Generic;
using UnityEngine;

namespace GK2AutoStationService
{
    // Read-only view of the loose drops lying in a caretaker's zone.
    //
    // This is the detection half of "the caretaker also collects the drops of its own zone": it
    // walks the scene drop lists, decides which zone each drop belongs to (by the zone rectangle,
    // the way the game itself does it), and works out which chest would take the item. Nothing is
    // picked up or moved here - the errand that does that is ZoneDropErrand.
    internal static class ZoneDrops
    {
        // one line per distinct (drop, chest) pair per world
        private static readonly HashSet<string> reported = new HashSet<string>();
        private static readonly HashSet<string> reportedZoneState = new HashSet<string>();

        internal static void Forget()
        {
            reported.Clear();
            reportedZoneState.Clear();
        }

        internal static void Scan(Dictionary<string, List<ZombieWgoData>> caretakersByZone, HashSet<string> caretakerZones)
        {
            MainGame game = MainGame.Instance;
            List<GameSceneData> scenes = game?.GameSave?.worldData?.gameSceneDataList;
            if (scenes == null || caretakerZones.Count == 0)
            {
                return;
            }

            ReportCaretakerStates(caretakersByZone);

            HashSet<string> zonesWithDrops = new HashSet<string>();
            int dropCount = 0;

            for (int s = 0; s < scenes.Count; s++)
            {
                GameSceneData scene = scenes[s];
                if (scene == null)
                {
                    continue;
                }

                dropCount += ScanList(scene, scene.droppedItems, caretakerZones, zonesWithDrops);
                dropCount += ScanList(scene, scene.queuedDrops, caretakerZones, zonesWithDrops);
            }

            // always printed, whatever the log setting says: it is the only sign that the zone drop
            // scan is alive, and it says where the per-drop detail would come from
            string summaryKey = $"summary|{dropCount}|{zonesWithDrops.Count}";
            if (reported.Add(summaryKey))
            {
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] zone drop scan: {dropCount} loose drop(s) in {zonesWithDrops.Count} zone(s) the mod watches (turn on 'Detailed log' for one line per drop)");
            }
        }

        private static int ScanList(GameSceneData scene, List<DropData> drops, HashSet<string> caretakerZones, HashSet<string> zonesWithDrops)
        {
            if (drops == null)
            {
                return 0;
            }

            int found = 0;
            for (int i = 0; i < drops.Count; i++)
            {
                try
                {
                    if (ReportDrop(scene, drops[i], caretakerZones, zonesWithDrops))
                    {
                        found++;
                    }
                }
                catch (Exception ex)
                {
                    AutoStationServicePlugin.Log?.LogError("[ASS] zone drops: " + ex);
                }
            }

            return found;
        }

        private static bool ReportDrop(GameSceneData scene, DropData drop, HashSet<string> caretakerZones, HashSet<string> zonesWithDrops)
        {
            if (drop == null || drop.IsRemoving || drop.Item == null || string.IsNullOrEmpty(drop.Id))
            {
                return false;
            }

            WorldZoneData zone = FindZone(scene, drop.Position);
            if (zone == null || !caretakerZones.Contains(zone.id))
            {
                return false;
            }

            zonesWithDrops.Add(zone.id);

            // a drop linked to a wgo (logs, bodies) needs a dedicated storage, and a big item (a log, a
            // supply crate) is the kind the player carries over his head rather than pocketing: neither
            // goes into a chest, so both are left on the ground for the player
            if (drop.DropType == DropType.WgoData || StorageDeposit.IsBigItem(drop.Item))
            {
                bool big = drop.DropType != DropType.WgoData;
                Log($"{drop.UniqueId.Guid}|{(big ? "big" : "wgo")}", $"[ASS] loose drop in {zone.id}: {describe(drop)} - {(big ? "big item carried over the head" : "wgo-linked item")}, left for the player");
                return true;
            }

            // tech point orbs are not carried to a chest: the zombie that comes for them absorbs them
            if (drop.IsResDrop)
            {
                string resId = drop.Item.id.Replace("game_res_", string.Empty);
                bool tech = resId == "tech_red" || resId == "tech_green" || resId == "tech_blue";
                bool absorb = tech
                    && AutoStationServicePlugin.CaretakerTakesTechPoints != null
                    && AutoStationServicePlugin.CaretakerTakesTechPoints.Value;

                string note = absorb
                    ? " -> the first zombie that comes for it absorbs it"
                    : $" - left for the player ({(tech ? "the tech points setting is off" : "player resource")})";
                Log($"{drop.UniqueId.Guid}|res", $"[ASS] loose drop in {zone.id}: {describe(drop)}{note}");
                return true;
            }

            int nonChestAccepts;
            WgoData chest = StorageDeposit.PickChest(zone, drop.Item, drop.Position, null, out nonChestAccepts);
            if (chest == null)
            {
                string extra = nonChestAccepts > 0
                    ? $" ({nonChestAccepts} other inventory/inventories would take it, but they are work stations, not chests)"
                    : string.Empty;
                Log($"{drop.UniqueId.Guid}|none", $"[ASS] loose drop in {zone.id}: {describe(drop)} - no chest in the zone can take it{extra}");
                return true;
            }

            float distance = Vector3.Distance(chest.Position, drop.Position);
            Log($"{drop.UniqueId.Guid}|{chest.UniqueId.Guid}", $"[ASS] loose drop in {zone.id}: {describe(drop)} -> {chest.id} [{StorageDeposit.ShortGuid(chest.UniqueId.Guid)}] (distance {distance:F1})");
            return true;
        }

        private static string describe(DropData drop)
        {
            return $"{drop.Id} x{drop.Count} [{StorageDeposit.ShortGuid(drop.UniqueId != null ? drop.UniqueId.Guid : Guid.Empty)}]";
        }

        private static WorldZoneData FindZone(GameSceneData scene, Vector3 position)
        {
            List<WorldZoneData> zones = scene.worldZones;
            if (zones == null)
            {
                return null;
            }

            Vector2 point = new Vector2(position.x, position.z);
            for (int i = 0; i < zones.Count; i++)
            {
                WorldZoneData zone = zones[i];
                if (zone != null && zone.wholeZoneRect.Contains(point))
                {
                    return zone;
                }
            }

            return null;
        }

        private static void ReportCaretakerStates(Dictionary<string, List<ZombieWgoData>> caretakersByZone)
        {
            if (caretakersByZone == null)
            {
                return;
            }

            foreach (KeyValuePair<string, List<ZombieWgoData>> pair in caretakersByZone)
            {
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    ZombieWgoData caretaker = pair.Value[i];
                    if (caretaker == null)
                    {
                        continue;
                    }

                    string state = caretaker.CaretakerState.ToString();
                    string key = pair.Key + "|" + caretaker.UniqueId.Guid + "|" + state;
                    if (reportedZoneState.Add(key))
                    {
                        AutoStationServicePlugin.LogInfo($"[ASS] zone drops: caretaker {caretaker.UniqueId.Guid} of {pair.Key} is {state} (idle caretakers are the ones that would go for a drop)");
                    }
                }
            }
        }

        private static void Log(string key, string message)
        {
            if (reported.Add(key))
            {
                AutoStationServicePlugin.LogInfo(message);
            }
        }
    }
}