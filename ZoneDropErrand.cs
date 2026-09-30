using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using LazyBearTechnology;
using Pathfinding;
using UnityEngine;

namespace GK2AutoStationService
{
    // The loose drop errand, for caretakers and gardeners alike: walk to a drop in the worker's own
    // zone, pick it up, carry it to a chest and put it in. Neither zombie has such a behaviour in
    // vanilla (the drop collectors belong to the player), so the walk is driven from a prefix on the
    // zombie's update method - the vanilla logic is only skipped while an errand runs, and never
    // while the worker is busy with an order.
    internal static class ZoneDropErrand
    {
        private enum Phase
        {
            Fetching,
            Carrying,
        }

        private sealed class Errand
        {
            internal SGuid WorkerId;
            internal string ZoneId;
            internal Phase Phase;
            internal Guid DropGuid;
            internal SGuid ChestId;
            internal Vector3 Target;
            internal bool HasAlternate;
            internal bool UsingAlternate;
            internal Vector3 Alternate;
            internal float Deadline;
            internal float NextAttempt;
            internal float NextProgressLog;
            internal float LastDistance;
            // the walk through the zone graph needs a graph the zone actually has: a request on one it
            // does not have is dropped without a word (GlobalNavigationManager returns on an empty
            // mask), leaving the worker to build a path forever, so the mode is picked per zone and
            // retried the other way when a path is accepted but never starts walking
            internal bool MaskUsable;
            internal bool GdGraph;
            internal int WalkStage;
            internal float WalkDeadline;
            internal bool Attempted;
            internal bool LoggedWalk;
            internal readonly HashSet<Guid> TriedChests = new HashSet<Guid>();
        }

        private const float ARRIVE_DISTANCE = 1.5f;
        private const float STEP_TIMEOUT = 30f;
        private const float RETRY_INTERVAL = 1f;
        // same item, another pile close by: one trip carries as much as fits in a single stack
        private const float NEARBY_DROP_DISTANCE = 8f;
        // a drop this close to a wgo may lie inside its footprint, where no path can end (the peat that
        // fell out of the compost pile), so the walk goes to that object's dock point instead
        private const float DROP_APPROACH_DISTANCE = 4f;
        // a path that was accepted but produces no movement within this grace counts as the wrong
        // movement type for the zone, and the walk is tried the other way
        private const float WALK_GRACE = 4f;

        private static readonly Dictionary<Guid, Errand> errands = new Dictionary<Guid, Errand>();
        // looking for a drop means walking the drop lists and every chest of the zone, so an idle
        // worker only searches once a second instead of every frame
        private static readonly Dictionary<Guid, float> nextSearch = new Dictionary<Guid, float>();

        private static MethodInfo caretakerGoHomeMethod;
        private static MethodInfo gardenerGoHomeMethod;
        private static bool loggedNoMoveHelpers;

        // a world change drops every errand, but nothing may be left behind: an item in a worker's
        // hands, or the state borrowed for the walk
        internal static void Clear()
        {
            foreach (KeyValuePair<Guid, Errand> pair in errands)
            {
                ZombieWgoData worker = FindWorker(pair.Key);
                if (worker == null)
                {
                    continue;
                }

                try
                {
                    Item carried = GetPortable(worker);
                    if (carried != null && !carried.IsEmpty && carried.Count > 0)
                    {
                        AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {pair.Key}: world changed while collecting - putting {carried.id} x{carried.Count} back on the ground");
                        StorageDeposit.PutOnGround(worker, carried);
                        SetPortable(worker, Item.Empty);
                    }

                    if (IsBorrowedState(worker))
                    {
                        SetIdleState(worker);
                    }
                }
                catch (Exception ex)
                {
                    AutoStationServicePlugin.Log?.LogError("[ASS] zone drop errand: releasing after a world change: " + ex);
                }
            }

            if (errands.Count > 0)
            {
                AutoStationServicePlugin.LogInfo($"[ASS] zone drop errands dropped: the world changed ({errands.Count})");
            }

            errands.Clear();
            nextSearch.Clear();
        }

        internal static bool HasErrand(ZombieWgoData worker)
        {
            return worker != null && errands.ContainsKey(worker.UniqueId.Guid);
        }

        internal static void RemoveWorkersGone(HashSet<Guid> workersOnScene)
        {
            List<Errand> gone = null;
            foreach (KeyValuePair<Guid, Errand> pair in errands)
            {
                if (!workersOnScene.Contains(pair.Key))
                {
                    gone = gone ?? new List<Errand>();
                    gone.Add(pair.Value);
                }
            }

            if (gone == null)
            {
                return;
            }

            for (int i = 0; i < gone.Count; i++)
            {
                AutoStationServicePlugin.LogInfo($"[ASS] worker {gone[i].WorkerId.Guid} is gone - collecting errand dropped");
                errands.Remove(gone[i].WorkerId.Guid);
            }
        }

        public static bool CaretakerUpdateBehaviourPrefix(ZombieWgoData __instance)
        {
            return !DriveGuarded(__instance);
        }

        // the gardener's prefix calls this too: the error handling lives here so both zombies behave
        // the same when something goes wrong mid-errand
        internal static bool DriveGuarded(ZombieWgoData worker)
        {
            try
            {
                if (Drive(worker))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] zone drop errand: " + ex);

                Errand broken;
                if (worker != null && errands.TryGetValue(worker.UniqueId.Guid, out broken))
                {
                    Finish(worker, broken, "an error while collecting");
                }
            }

            return false;
        }

        // true means the frame belongs to the mod and the vanilla behaviour must not run
        internal static bool Drive(ZombieWgoData worker)
        {
            if (worker == null)
            {
                return false;
            }

            Errand errand;
            if (!errands.TryGetValue(worker.UniqueId.Guid, out errand))
            {
                // a borrowed state must never be left behind: when an errand is dropped (world change,
                // an error, a missing zombie entry) the vanilla logic would run the state we borrowed
                // and crash on the order it does not have (CaretakerTryPickUpFromInventory NREs)
                if (IsBorrowedState(worker))
                {
                    SetIdleState(worker);
                }

                if (AutoStationServicePlugin.CollectZoneDrops != null && !AutoStationServicePlugin.CollectZoneDrops.Value)
                {
                    return false;
                }

                if (!CanStart(worker))
                {
                    return false;
                }

                float next;
                if (nextSearch.TryGetValue(worker.UniqueId.Guid, out next) && Time.unscaledTime < next)
                {
                    return false;
                }

                nextSearch[worker.UniqueId.Guid] = Time.unscaledTime + RETRY_INTERVAL;

                errand = Start(worker);
                if (errand == null)
                {
                    return false;
                }
            }

            Advance(worker, errand);
            return true;
        }

        private static bool CanStart(ZombieWgoData worker)
        {
            if (!IsIdle(worker))
            {
                return false;
            }

            return worker.WorldZoneData != null && worker.AttachedWgoData != null;
        }

        private static Errand Start(ZombieWgoData worker)
        {
            WorldZoneData zone = worker.WorldZoneData;

            // only drops: tech point orbs drift to the worker on their own (OrbMagnet), so an orb that
            // cannot be reached never keeps him from the piles waiting to be carried to a chest
            DropData drop = FindNearestDrop(worker, zone);
            if (drop == null)
            {
                return null;
            }

            // another worker may already be on its way to this very drop
            if (IsTargetedByAnother(drop.UniqueId.Guid, worker.UniqueId.Guid))
            {
                return null;
            }

            GraphMask zoneMask = NavigationGraphMaskUtils.ToGraphMask(zone.MovementGraphs, zone.navigationGraph);
            bool maskUsable = !(zoneMask == default(GraphMask));

            Errand errand = new Errand
            {
                WorkerId = new SGuid(worker.UniqueId.Guid),
                ZoneId = zone.id,
                Phase = Phase.Fetching,
                DropGuid = drop.UniqueId.Guid,
                Target = drop.Position,
                Deadline = Time.unscaledTime + STEP_TIMEOUT,
                MaskUsable = maskUsable,
                // vanilla walks a gardener on the GD point graph only and a caretaker on the zone graph
                // only, and the zone a gardener works in has no zone graph to walk on
                GdGraph = IsGardener(worker) || !maskUsable,
            };

            errands[worker.UniqueId.Guid] = errand;
            BorrowState(worker);

            float dropDistance = Vector3.Distance(worker.Position, drop.Position);
            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: going for loose drop {drop.Id} x{drop.Count} [{StorageDeposit.ShortGuid(drop.UniqueId.Guid)}] in {zone.id} (drop is {dropDistance:F1} away, walking to ({errand.Target.x:F1}, {errand.Target.z:F1}) on the {(errand.GdGraph ? "GD point graph" : "zone graph")})");
            return errand;
        }

        private static bool IsTargetedByAnother(Guid dropGuid, Guid workerGuid)
        {
            foreach (KeyValuePair<Guid, Errand> pair in errands)
            {
                if (pair.Key != workerGuid && pair.Value.DropGuid == dropGuid)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Advance(ZombieWgoData worker, Errand errand)
        {
            if (errand.Phase == Phase.Fetching)
            {
                AdvanceFetching(worker, errand);
            }
            else
            {
                AdvanceCarrying(worker, errand);
            }
        }

        private static void AdvanceFetching(ZombieWgoData worker, Errand errand)
        {
            DropData drop = FindDrop(worker.WorldZoneData, errand.DropGuid);
            if (drop == null || drop.Item == null || drop.Count <= 0)
            {
                Finish(worker, errand, "the drop is gone");
                return;
            }

            if (!errand.UsingAlternate)
            {
                // straight at the drop: that is where the item lies. the dock point of the object it fell
                // out of is kept as the alternate, for a drop that ended up where no path can reach
                errand.Target = drop.Position;

                WgoData host = FindWgoNear(worker.WorldZoneData, drop.Position, DROP_APPROACH_DISTANCE);
                errand.HasAlternate = host != null;
                errand.Alternate = host != null ? DockPointOf(host, drop.Position) : drop.Position;
            }

            if (!Reached(worker, errand, ARRIVE_DISTANCE))
            {
                if (Time.unscaledTime > errand.Deadline)
                {
                    Finish(worker, errand, $"cannot reach the drop (still {Vector3.Distance(worker.Position, drop.Position):F1} from it, at ({worker.Position.x:F1}, {worker.Position.z:F1}) heading to ({errand.Target.x:F1}, {errand.Target.z:F1}))");
                }

                return;
            }

            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: reached the drop (distance {Vector3.Distance(worker.Position, drop.Position):F1})");

            // tech point orbs are not carried anywhere: the worker absorbs them on the spot
            if (drop.IsResDrop)
            {
                AbsorbResDrop(worker, drop);
                Finish(worker, errand, null);
                return;
            }

            Item carried = GetPortable(worker);
            int already = carried != null && !carried.IsEmpty && carried.id == drop.Id ? carried.Count : 0;
            int limit = StorageDeposit.StackLimit(drop.Id);
            int take = Math.Min(drop.Count, limit - already);
            if (take <= 0)
            {
                // the stack is full: carry it to the chest and come back for the rest
                SendToChest(worker, errand);
                return;
            }

            SetPortable(worker, new Item(drop.Id, already + take));

            if (take >= drop.Count)
            {
                RemoveDrop(drop);
            }
            else
            {
                drop.Item.Count -= take;
                drop.NotifyCountChanged();
            }

            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: picked up {drop.Id} x{take} (carrying {already + take})");

            SendToChest(worker, errand);
        }

        // walk to the chest we picked; if it turns out to be full (or is gone) the next chest is
        // tried, and only when none is left the item goes back on the ground
        private static void AdvanceCarrying(ZombieWgoData worker, Errand errand)
        {
            WgoData chest = Resolve(errand.ChestId);
            if (chest == null)
            {
                errand.TriedChests.Add(errand.ChestId != null ? errand.ChestId.Guid : Guid.Empty);
                SendToChest(worker, errand);
                return;
            }

            if (!errand.UsingAlternate)
            {
                // chests have dock points of their own; the chest itself is the fallback in case
                // that dock point cannot be reached
                errand.Target = DockPointOf(chest, worker.Position);
                errand.HasAlternate = true;
                errand.Alternate = chest.Position;
            }

            if (!Reached(worker, errand, ARRIVE_DISTANCE))
            {
                if (Time.unscaledTime > errand.Deadline)
                {
                    Finish(worker, errand, $"cannot reach the chest (still {Vector3.Distance(worker.Position, chest.Position):F1} from it, at ({worker.Position.x:F1}, {worker.Position.z:F1}) heading to ({errand.Target.x:F1}, {errand.Target.z:F1}))");
                }

                return;
            }

            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: reached {chest.id} (distance {Vector3.Distance(worker.Position, chest.Position):F1})");

            Item carried = GetPortable(worker);
            if (carried == null || carried.IsEmpty || carried.Count <= 0)
            {
                Finish(worker, errand, null);
                return;
            }

            int added = StorageDeposit.TryDeposit(chest, carried);
            if (added <= 0)
            {
                errand.TriedChests.Add(chest.UniqueId != null ? chest.UniqueId.Guid : Guid.Empty);
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: {chest.id} cannot take {carried.id} - looking for another chest");
                SendToChest(worker, errand);
                return;
            }

            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: put {carried.id} x{added} into {chest.id} [{StorageDeposit.ShortGuid(chest.UniqueId.Guid)}]");

            if (carried.Count <= 0)
            {
                SetPortable(worker, Item.Empty);
                Finish(worker, errand, null);
                return;
            }

            // partially stored: the chest filled up while walking, so look for the next one
            errand.TriedChests.Add(chest.UniqueId != null ? chest.UniqueId.Guid : Guid.Empty);
            SendToChest(worker, errand);
        }

        private static void SendToChest(ZombieWgoData worker, Errand errand)
        {
            Item carried = GetPortable(worker);
            if (carried == null || carried.IsEmpty || carried.Count <= 0)
            {
                Finish(worker, errand, null);
                return;
            }

            // nothing left to pick up of the same item nearby? then this trip is done fetching
            DropData more = FindNearbyDrop(worker, worker.WorldZoneData, carried.id, errand, carried.Count);
            if (more != null && carried.Count < StorageDeposit.StackLimit(carried.id))
            {
                errand.Phase = Phase.Fetching;
                errand.DropGuid = more.UniqueId.Guid;
                errand.Attempted = false;
                errand.WalkStage = 0;
                errand.Deadline = Time.unscaledTime + STEP_TIMEOUT;
                return;
            }

            // a gardener hands the item over from where he stands: the game's own garden deposit
            // (GardenerTryPutGardenItemsToMultiInventory) writes into the zone storage without walking
            // anywhere, so he has no reason to make a trip to the chest either
            if (IsGardener(worker))
            {
                HandOverOnTheSpot(worker, errand, carried);
                return;
            }

            int nonChestAccepts;
            WgoData chest = StorageDeposit.PickChest(worker.WorldZoneData, carried, worker.Position, errand.TriedChests, out nonChestAccepts);
            if (chest == null)
            {
                PutBackOnGround(worker, errand, carried, nonChestAccepts);
                return;
            }

            errand.Phase = Phase.Carrying;
            errand.ChestId = new SGuid(chest.UniqueId.Guid);
            errand.UsingAlternate = false;
            errand.Target = DockPointOf(chest, worker.Position);
            errand.HasAlternate = true;
            errand.Alternate = chest.Position;
            errand.Attempted = false;
            errand.WalkStage = 0;
            errand.Deadline = Time.unscaledTime + STEP_TIMEOUT;

            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: carrying {carried.id} x{carried.Count} to {chest.id} [{StorageDeposit.ShortGuid(chest.UniqueId.Guid)}]");
        }

        // the gardener's way to the chest: the chest is picked by the same rule the caretaker uses, but
        // the item goes in right away - and a full chest sends him to the next one, the last one back
        // onto the ground, so the errand never turns into a walk
        private static void HandOverOnTheSpot(ZombieWgoData worker, Errand errand, Item carried)
        {
            while (carried.Count > 0)
            {
                int nonChestAccepts;
                WgoData chest = StorageDeposit.PickChest(worker.WorldZoneData, carried, worker.Position, errand.TriedChests, out nonChestAccepts);
                if (chest == null)
                {
                    PutBackOnGround(worker, errand, carried, nonChestAccepts);
                    return;
                }

                int added = StorageDeposit.TryDeposit(chest, carried);
                if (added <= 0)
                {
                    errand.TriedChests.Add(chest.UniqueId != null ? chest.UniqueId.Guid : Guid.Empty);
                    AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: {chest.id} cannot take {carried.id} - looking for another chest");
                    continue;
                }

                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: put {carried.id} x{added} into {chest.id} [{StorageDeposit.ShortGuid(chest.UniqueId.Guid)}] on the spot");
            }

            SetPortable(worker, Item.Empty);
            Finish(worker, errand, null);
        }

        // the last resort the user asked for: no chest can take it, so it goes back on the ground
        // and the worker returns to his station instead of keeping it in his hands
        private static void PutBackOnGround(ZombieWgoData worker, Errand errand, Item carried, int nonChestAccepts)
        {
            string extra = nonChestAccepts > 0
                ? $" ({nonChestAccepts} other inventory/inventories would take it, but they are work stations, not chests)"
                : string.Empty;
            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: no chest can take {carried.id} x{carried.Count} in {errand.ZoneId} - putting it back on the ground{extra}");

            StorageDeposit.PutOnGround(worker, carried);
            SetPortable(worker, Item.Empty);
            Finish(worker, errand, null);
        }

        // tech points picked up off the ground go into the worker's own talent currency, exactly like
        // the points of a craft he carried away
        private static void AbsorbResDrop(ZombieWgoData worker, DropData drop)
        {
            string resId = drop.Item.id.Replace("game_res_", string.Empty);
            int count = drop.Count;

            int red = resId == "tech_red" ? count : 0;
            int green = resId == "tech_green" ? count : 0;
            int blue = resId == "tech_blue" ? count : 0;

            RemoveDrop(drop);

            if (red + green + blue <= 0)
            {
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: picked up {drop.Item.id} x{count} from the ground");
                return;
            }

            worker.DoTechPointsReward(worker, red, green, blue);
            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: absorbed {drop.Item.id} x{count} from the ground (red {red}, green {green}, blue {blue})");
        }

        private static void RemoveDrop(DropData drop)
        {
            MainGame.Instance.dropSystem.RemoveDrop(drop, drop.WorldId);
        }

        // the game's own path start, so the movement type and graph mask match the worker's normal
        // walking (CaretakerTryMoveToCurrentTarget and the gardener helpers do exactly this)
        private static bool WalkTo(ZombieWgoData worker, Errand errand)
        {
            if (Time.unscaledTime < errand.NextAttempt)
            {
                return false;
            }

            errand.NextAttempt = Time.unscaledTime + RETRY_INTERVAL;

            WorldZoneData zone = worker.WorldZoneData;
            MovementComponent component = worker.MovementComponent;
            if (zone == null || component == null)
            {
                errand.Attempted = true;
                return false;
            }

            if (errand.Attempted && !component.IsMoving && Time.unscaledTime >= errand.WalkDeadline)
            {
                RetryWalkDifferently(worker, errand, zone);
            }

            if (component.IsMoving)
            {
                component.ForceStop();
            }

            if (!errand.Attempted)
            {
                errand.WalkDeadline = Time.unscaledTime + WALK_GRACE;
            }

            // the game's own walk start, one movement type per worker: the zone graph of the zone for a
            // caretaker (CaretakerTryMoveToCurrentTarget does exactly this), the GD point graph for a
            // gardener (GardenerTryMoveToCurrentTarget does exactly this)
            MovementComponent.StartPathResult result = errand.GdGraph
                ? component.StartPath(errand.Target, worker.WorldId, worker.WorldId, MovementType.GDGraph)
                : component.StartPath(errand.Target, NavigationGraphMaskUtils.ToGraphMask(zone.MovementGraphs, zone.navigationGraph), worker.WorldId);

            errand.Attempted = true;

            if (result == MovementComponent.StartPathResult.IncorrectMovementType)
            {
                // only the zone-graph overload refuses, and only when the zone has no walking graph
                if (errand.MaskUsable)
                {
                    errand.GdGraph = true;
                    errand.Attempted = false;
                }

                if (!errand.LoggedWalk)
                {
                    errand.LoggedWalk = true;
                    AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: the zone graph has no path from here ({result}) - falling back to the GD point graph");
                }
            }

            // while an errand walks, a line every few seconds says whether the worker is actually moving
            // and how far it still has to go - the only way to tell "walking" from "standing still"
            if (Time.unscaledTime >= errand.NextProgressLog)
            {
                errand.NextProgressLog = Time.unscaledTime + 3f;
                float distance = Vector3.Distance(worker.Position, errand.Target);
                string note = Mathf.Abs(distance - errand.LastDistance) < 0.05f && errand.LastDistance > 0f ? " (not moving!)" : string.Empty;
                errand.LastDistance = distance;
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: walking to ({errand.Target.x:F1}, {errand.Target.z:F1}) - at ({worker.Position.x:F1}, {worker.Position.z:F1}), moving={component.IsMoving} on the {(errand.GdGraph ? "GD point graph" : "zone graph")}, {distance:F1} to go{note}");
            }

            return true;
        }

        // a path that was accepted but produces no movement at all means the movement type does not fit
        // this zone, and the game says nothing about it: the request is simply dropped. So the other
        // graph is tried, then the alternate spot (a drop inside an object's footprint has no path of
        // its own), and after that the errand is left to its own timeout
        private static void RetryWalkDifferently(ZombieWgoData worker, Errand errand, WorldZoneData zone)
        {
            errand.WalkStage++;

            if (errand.WalkStage == 1 && errand.MaskUsable)
            {
                errand.GdGraph = !errand.GdGraph;
                errand.Attempted = false;
                errand.NextAttempt = 0f;
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: the {(errand.GdGraph ? "zone" : "GD point")} graph path never started walking - trying the {(errand.GdGraph ? "GD point" : "zone")} graph");
                return;
            }

            if (errand.WalkStage <= 2 && errand.HasAlternate && !errand.UsingAlternate)
            {
                errand.WalkStage = 2;
                errand.UsingAlternate = true;
                errand.Target = errand.Alternate;
                errand.Attempted = false;
                errand.NextAttempt = 0f;
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: no walk to the {errand.Phase} spot - trying ({errand.Target.x:F1}, {errand.Target.z:F1}) instead");
                return;
            }

            if (!errand.LoggedWalk)
            {
                errand.LoggedWalk = true;
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: the worker cannot walk to ({errand.Target.x:F1}, {errand.Target.y:F1}, {errand.Target.z:F1}) in {zone.id} - standing still until the errand times out");
            }
        }

        private static bool Reached(ZombieWgoData worker, Errand errand, float radius)
        {
            if (Vector3.Distance(worker.Position, errand.Target) <= radius)
            {
                if (worker.MovementComponent.IsMoving)
                {
                    worker.MovementComponent.ForceStop();
                }

                return true;
            }

            if (!errand.Attempted || !worker.MovementComponent.IsMoving)
            {
                WalkTo(worker, errand);
            }

            return false;
        }

        private static void Finish(ZombieWgoData worker, Errand errand, string reason)
        {
            errands.Remove(errand.WorkerId.Guid);

            // nothing may stay in his hands: vanilla zombies only ever put down what an order gave
            // them, so a carried item would sit there for good
            Item carried = GetPortable(worker);
            if (carried != null && !carried.IsEmpty && carried.Count > 0)
            {
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: dropping {carried.id} x{carried.Count} instead of carrying it around");
                StorageDeposit.PutOnGround(worker, carried);
                SetPortable(worker, Item.Empty);
            }

            SetIdleState(worker);

            if (!string.IsNullOrEmpty(reason))
            {
                AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid} stopped collecting ({reason})");
            }

            GoHome(worker);
        }

        // ---- worker type differences --------------------------------------------------------

        private static bool IsCaretaker(ZombieWgoData worker)
        {
            return worker.ZombieType == ZombieType.Caretaker;
        }

        private static bool IsGardener(ZombieWgoData worker)
        {
            return worker.ZombieType == ZombieType.Gardener;
        }

        private static bool IsIdle(ZombieWgoData worker)
        {
            if (IsCaretaker(worker))
            {
                if (worker.CaretakerState != ZombieWgoData.ZombieCaretakerState.OnStation)
                {
                    return false;
                }

                // an order in hand always comes first: the errand only takes an idle worker
                return HasNoCaretakerOrder(worker);
            }

            if (IsGardener(worker))
            {
                // the same test the station job makes before it takes a gardener: an order in his
                // hand or a walk under his feet belongs to someone else, even if he reads OnStation
                return worker.GardenerState == ZombieWgoData.ZombieGardenerState.OnStation
                    && !GardenerJobRegistry.IsBusyElsewhere(worker);
            }

            return false;
        }

        private static bool HasNoCaretakerOrder(ZombieWgoData caretaker)
        {
            SGuid executing = CaretakerOrderGuard.GetExecutingOrder(caretaker);
            return executing == null || executing.IsEmpty;
        }

        private static bool IsBorrowedState(ZombieWgoData worker)
        {
            if (IsCaretaker(worker))
            {
                // this state with no order in hand never happens in vanilla, and running it crashes
                // (CaretakerTryPickUpFromInventory dereferences the missing order)
                return worker.CaretakerState == ZombieWgoData.ZombieCaretakerState.PickingUpOrderItemFromInventory
                    && HasNoCaretakerOrder(worker);
            }

            if (IsGardener(worker))
            {
                return worker.GardenerState == ZombieWgoData.ZombieGardenerState.TeleportSeedsFromMultiInventory
                    && !GardenerJobRegistry.HasGardenerOrder(worker);
            }

            return false;
        }

        private static void BorrowState(ZombieWgoData worker)
        {
            // states the vanilla state machine ignores on arrival: CaretakerOnPathSuccess does nothing
            // for PickingUpOrderItemFromInventory, and the gardener's TeleportSeedsFromMultiInventory
            // branch is empty too (GoToStation would teleport the zombie back to its station)
            if (IsCaretaker(worker))
            {
                worker.CaretakerState = ZombieWgoData.ZombieCaretakerState.PickingUpOrderItemFromInventory;
            }
            else if (IsGardener(worker))
            {
                worker.GardenerState = ZombieWgoData.ZombieGardenerState.TeleportSeedsFromMultiInventory;
            }
        }

        private static void SetIdleState(ZombieWgoData worker)
        {
            if (IsCaretaker(worker))
            {
                worker.CaretakerState = ZombieWgoData.ZombieCaretakerState.OnStation;
            }
            else if (IsGardener(worker))
            {
                worker.GardenerState = ZombieWgoData.ZombieGardenerState.OnStation;
            }
        }

        private static Item GetPortable(ZombieWgoData worker)
        {
            return IsGardener(worker) ? worker.GardenerPortableItem : worker.CaretakerPortableItem;
        }

        private static void SetPortable(ZombieWgoData worker, Item item)
        {
            if (IsGardener(worker))
            {
                worker.GardenerPortableItem = item;
            }
            else
            {
                worker.CaretakerPortableItem = item;
            }
        }

        // vanilla ends every piece of garden work by walking home, and the station job in
        // GardenerJobRegistry borrows this too: where a worker stands is not where he lives
        internal static void GoHome(ZombieWgoData worker)
        {
            if (!ResolveMoveHelpers())
            {
                return;
            }

            MethodInfo method = IsGardener(worker) ? gardenerGoHomeMethod : caretakerGoHomeMethod;
            if (method == null || worker.AttachedWgoData == null)
            {
                return;
            }

            try
            {
                method.Invoke(worker, null);
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] zone drop errand: going home: " + ex);
            }
        }

        private static bool ResolveMoveHelpers()
        {
            if (caretakerGoHomeMethod != null && gardenerGoHomeMethod != null)
            {
                return true;
            }

            caretakerGoHomeMethod = AccessTools.Method(typeof(ZombieWgoData), "CaretakerTryMoveToStation");
            gardenerGoHomeMethod = AccessTools.Method(typeof(ZombieWgoData), "GardenerTryMoveToStation");

            if (caretakerGoHomeMethod == null || gardenerGoHomeMethod == null)
            {
                if (!loggedNoMoveHelpers)
                {
                    loggedNoMoveHelpers = true;
                    AutoStationServicePlugin.Log?.LogError("[ASS] ZombieWgoData.TryMoveToStation not found - workers will not walk home after an errand");
                }

                return false;
            }

            return true;
        }

        // ---- walking destinations ------------------------------------------------------------

        // DockPointData.WorldPosition or the wgo's own position when it has no dock point at all
        private static Vector3 DockPointOf(WgoData wgo, Vector3 from)
        {
            if (wgo == null)
            {
                return from;
            }

            try
            {
                // Availability.All on purpose: a target of ours (the pile a drop fell out of) usually has
                // every dock point taken, and the free-only default would fall back to a spot no path can
                // reach
                return wgo.GetNearestDockPointDataWorldPositionOrMyPosition(from, DockPointData.Availability.All);
            }
            catch (Exception)
            {
                return wgo.Position;
            }
        }

        private static WgoData FindWgoNear(WorldZoneData zone, Vector3 position, float maxDistance)
        {
            List<SGuid> wgos = zone != null ? zone.wgoDataList : null;
            if (wgos == null)
            {
                return null;
            }

            WgoData best = null;
            float bestDistance = maxDistance;

            for (int i = 0; i < wgos.Count; i++)
            {
                WgoData candidate;
                try
                {
                    candidate = MainGame.WorldData.GetWgoData(wgos[i]);
                }
                catch (Exception)
                {
                    continue;
                }

                if (candidate == null)
                {
                    continue;
                }

                float distance = Vector3.Distance(candidate.Position, position);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }

        // ---- finding the drops --------------------------------------------------------------

        private static DropData FindDrop(WorldZoneData zone, Guid dropGuid)
        {
            foreach (DropData drop in DropsOf(zone))
            {
                if (drop != null && drop.UniqueId != null && drop.UniqueId.Guid == dropGuid)
                {
                    return drop;
                }
            }

            return null;
        }

        private static DropData FindNearestDrop(ZombieWgoData worker, WorldZoneData zone)
        {
            DropData best = null;
            float bestDistance = float.MaxValue;

            foreach (DropData drop in DropsOf(zone))
            {
                if (!IsCollectable(worker, zone, drop))
                {
                    continue;
                }

                float distance = Vector3.Distance(drop.Position, worker.Position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = drop;
                }
            }

            return best;
        }

        private static DropData FindNearbyDrop(ZombieWgoData worker, WorldZoneData zone, string itemId, Errand errand, int alreadyCarried)
        {
            int limit = StorageDeposit.StackLimit(itemId);
            if (alreadyCarried >= limit)
            {
                return null;
            }

            DropData best = null;
            float bestDistance = float.MaxValue;

            foreach (DropData drop in DropsOf(zone))
            {
                if (drop == null || drop.Id != itemId || drop.UniqueId == null || drop.UniqueId.Guid == errand.DropGuid)
                {
                    continue;
                }

                if (!IsCollectable(worker, zone, drop))
                {
                    continue;
                }

                float distance = Vector3.Distance(drop.Position, worker.Position);
                if (distance <= NEARBY_DROP_DISTANCE && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = drop;
                }
            }

            return best;
        }

        // a drop this worker may go for: in his zone, not a wgo-linked big item, and - unless it is a
        // tech point drop the setting allows - with a chest that can take it right now
        private static bool IsCollectable(ZombieWgoData worker, WorldZoneData zone, DropData drop)
        {
            if (drop == null || drop.IsRemoving || drop.Item == null || string.IsNullOrEmpty(drop.Id) || drop.Count <= 0)
            {
                return false;
            }

            if (drop.DropType == DropType.WgoData)
            {
                return false;
            }

            // the game holds a drop back for a moment after it pops out of a station - DropView.SpawnDrop
            // starts a collect delay and DropCollector.CanCollectDrop refuses the drop until it is over,
            // so even the player cannot pick it up yet. An item that is still on its way down is not lying
            // there, and a worker that walked off with it would take it out of the air the moment the
            // craft ends, so the errand waits the same delay out
            if (!IsLanded(drop))
            {
                return false;
            }

            // an item the player has to carry over his head (a log, a supply crate) never goes into a
            // backpack or a chest: the game's own DropCollector refuses these drops, so the errand does
            // too and leaves them for the player
            if (StorageDeposit.IsBigItem(drop.Item))
            {
                return false;
            }

            if (!IsInZone(zone, drop.Position) || !MayCollectHere(worker, zone))
            {
                return false;
            }

            if (drop.IsResDrop)
            {
                return MayAbsorbTechPoints(drop);
            }

            int nonChestAccepts;
            return StorageDeposit.PickChest(zone, drop.Item, worker.Position, null, out nonChestAccepts) != null;
        }

        // whether a drop has come to rest and may be taken by anyone. The drop list says nothing about
        // that: DropData is written the moment the item is dropped, while the view that plays the fall
        // and holds the collider only exists once the item is in the world. The view is therefore what
        // gets asked, and only a view that answers "still collecting delayed" or "already flying to a
        // collector" holds the errand back - a drop whose view cannot be found at all counts as landed,
        // so a lookup that fails can never park an item on the ground for good. Drops of a zone whose
        // scene is not loaded have no view and need none: they are written straight into the world's
        // data, the delay is about an item coming down rather than about a scene, and the errand is
        // free to take them
        internal static bool IsLanded(DropData drop)
        {
            DropView view = FindView(drop);
            return view == null || (!view.IsCollectDelayed && !view.IsTimedCollecting);
        }

        // the views live on the loaded scenes, one cache per scene, keyed by the item's unique id - the
        // same lookup DropSystem does for its own FindDropView
        private static DropView FindView(DropData drop)
        {
            if (drop == null || drop.Item == null)
            {
                return null;
            }

            try
            {
                List<GameScene> scenes = LazySingleton<GameSceneManager>.Instance.LoadedGameScenes;
                if (scenes == null)
                {
                    return null;
                }

                for (int i = 0; i < scenes.Count; i++)
                {
                    GameScene scene = scenes[i];
                    if (scene == null || scene.Id != drop.WorldId)
                    {
                        continue;
                    }

                    DropView view;
                    if (scene.TryGetDropView(drop.Item, out view))
                    {
                        return view;
                    }
                }
            }
            catch (Exception)
            {
                // a scene that is still starting or already going away says nothing about the drop
            }

            return null;
        }

        // both settings have to be on for the mod to go for tech points lying around: the loose drop
        // errand itself ("Caretaker collects loose drops in its zone") and the one that decides a
        // zombie may take tech points at all ("Caretaker takes the tech points")
        private static bool MayAbsorbTechPoints(DropData drop)
        {
            bool drops = AutoStationServicePlugin.CollectZoneDrops == null || AutoStationServicePlugin.CollectZoneDrops.Value;
            bool tech = AutoStationServicePlugin.CaretakerTakesTechPoints != null && AutoStationServicePlugin.CaretakerTakesTechPoints.Value;
            if (!drops || !tech)
            {
                return false;
            }

            string resId = drop.Item.id.Replace("game_res_", string.Empty);
            return resId == "tech_red" || resId == "tech_green" || resId == "tech_blue";
        }

        // a gardener only collects where no caretaker could (the garden cannot host a caretaker
        // station at all); a caretaker collects in his own zone
        internal static bool MayCollectHere(ZombieWgoData worker, WorldZoneData zone)
        {
            if (IsCaretaker(worker))
            {
                return true;
            }

            if (!IsGardener(worker) || zone == null)
            {
                return false;
            }

            return !HasCaretakerInZone(zone.id);
        }

        private static bool HasCaretakerInZone(string zoneId)
        {
            List<SGuid> zombieIds = MainGame.ZombieSystemData?.zombieOnSceneWgoIds;
            if (zombieIds == null)
            {
                return false;
            }

            for (int i = 0; i < zombieIds.Count; i++)
            {
                ZombieWgoData zombie = MainGame.ZombieSystemData.GetZombie(zombieIds[i]);
                if (zombie != null && zombie.ZombieType == ZombieType.Caretaker)
                {
                    WorldZoneData zone = zombie.WorldZoneData;
                    if (zone != null && zone.id == zoneId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        internal static bool IsInZone(WorldZoneData zone, Vector3 position)
        {
            if (zone == null)
            {
                return false;
            }

            return zone.wholeZoneRect.Contains(new Vector2(position.x, position.z));
        }

        // every drop the zone holds, from both of the lists a scene keeps. droppedItems is what lies in
        // the world; queuedDrops is where DropSystem.DropItemInternal puts a drop whose scene is not
        // loaded at that moment, and GameSceneData.ProcessQueuedDrops moves those into the world when
        // the scene loads. That is a difference in the view layer, not in the drop: the item is created
        // all the same, at the same position in the same world. And a worker is not part of the view
        // layer either - ZombieSystem ticks every placed zombie wherever it stands, MovementComponent
        // carries a position, and the nav graphs are loaded game-wide; none of that asks whether the
        // zombie's scene is loaded. So a drop waiting in a zone nobody has loaded is exactly the kind
        // of thing this errand is for, and the zone a worker stands in is the only zone he ever looks
        // at. What keeps him off a fresh drop is not which list it is in but whether it has come down
        // yet (IsLanded)
        private static List<DropData> DropsOf(WorldZoneData zone)
        {
            List<DropData> result = new List<DropData>();

            GameSceneData scene = SceneOf(zone);
            if (scene == null)
            {
                return result;
            }

            if (scene.droppedItems != null)
            {
                result.AddRange(scene.droppedItems);
            }

            if (scene.queuedDrops != null)
            {
                result.AddRange(scene.queuedDrops);
            }

            return result;
        }

        private static GameSceneData SceneOf(WorldZoneData zone)
        {
            if (zone == null)
            {
                return null;
            }

            try
            {
                return MainGame.WorldData.GetGameSceneDataById(zone.gameSceneId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static WgoData Resolve(SGuid id)
        {
            if (id == null || id.IsEmpty)
            {
                return null;
            }

            try
            {
                return MainGame.WorldData.GetWgoData(id);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ZombieWgoData FindWorker(Guid guid)
        {
            List<SGuid> zombieIds = MainGame.ZombieSystemData?.zombieOnSceneWgoIds;
            if (zombieIds == null)
            {
                return null;
            }

            for (int i = 0; i < zombieIds.Count; i++)
            {
                ZombieWgoData zombie = MainGame.ZombieSystemData.GetZombie(zombieIds[i]);
                if (zombie != null && zombie.UniqueId.Guid == guid)
                {
                    return zombie;
                }
            }

            return null;
        }
    }
}