using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GK2AutoStationService
{
    [BepInPlugin("com.gk2mod.autostationservice", "Auto Station Service", "1.3.2")]
    public class AutoStationServicePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;

        private void Awake()
        {
            Log = Logger;

            try
            {
                HarmonyInstance = new Harmony("com.gk2mod.autostationservice");

                MethodInfo getZombie = AccessTools.Method(typeof(ZombieSystemData), "GetZombie", new Type[] { typeof(SGuid) });
                if (getZombie == null)
                {
                    Log.LogError("[ASS] ZombieSystemData.GetZombie not found - mod disabled");
                    return;
                }

                HarmonyInstance.Patch(getZombie, prefix: new HarmonyMethod(typeof(AutoStationServicePlugin), nameof(GetZombiePrefix)));
                Log.LogInfo("[ASS] patched ZombieSystemData.GetZombie");

                MethodInfo moveToZombie = AccessTools.Method(typeof(ZombieWgoData), "CaretakerTryMoveToZombie");
                if (moveToZombie == null)
                {
                    Log.LogError("[ASS] ZombieWgoData.CaretakerTryMoveToZombie not found - the caretaker order guard is off");
                }
                else
                {
                    HarmonyInstance.Patch(moveToZombie, prefix: new HarmonyMethod(typeof(CaretakerOrderGuard), nameof(CaretakerOrderGuard.CaretakerTryMoveToZombiePrefix)));
                    Log.LogInfo("[ASS] patched ZombieWgoData.CaretakerTryMoveToZombie");
                }

                GameObject host = new GameObject("GK2AutoStationService");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<AutoStationServiceTick>();

                Log.LogInfo("[ASS] Auto Station Service v1.3.2 ready");
            }
            catch (Exception ex)
            {
                Log.LogError("[ASS] Awake failed: " + ex);
            }
        }

        public static bool GetZombiePrefix(SGuid uniqueId, ref ZombieWgoData __result)
        {
            try
            {
                ZombieWgoData proxy = StationProxyRegistry.GetProxy(uniqueId);
                if (proxy != null)
                {
                    __result = proxy;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log?.LogError("[ASS] GetZombiePrefix: " + ex);
            }

            return true;
        }
    }

    internal class AutoStationServiceTick : MonoBehaviour
    {
        private const float SERVICE_INTERVAL = 1f;
        private const float RESCAN_INTERVAL = 15f;
        private const float PICKUP_WAIT_LOG_INTERVAL = 60f;
        private const int MAX_CANDIDATE_LOGS = 10;

        private readonly List<WgoData> stations = new List<WgoData>();
        private readonly List<Guid> excludedStationIds = new List<Guid>();
        private readonly HashSet<string> loggedStations = new HashSet<string>();
        private readonly HashSet<string> loggedCandidates = new HashSet<string>();
        private readonly HashSet<Guid> loggedDiagnostics = new HashSet<Guid>();
        private readonly HashSet<Guid> loggedForwarded = new HashSet<Guid>();
        private readonly HashSet<Guid> loggedNoCaretaker = new HashSet<Guid>();
        private readonly HashSet<Guid> loggedNoOutputSpace = new HashSet<Guid>();
        private readonly Dictionary<Guid, float> pickupWaitLogTime = new Dictionary<Guid, float>();
        private readonly HashSet<string> caretakerZones = new HashSet<string>();
        private bool loggedCaretakers;

        private static MethodInfo handleOutputMethod;

        private float serviceTimer;
        private float rescanTimer = 999f;

        private void Update()
        {
            try
            {
                float dt = Time.unscaledDeltaTime;
                rescanTimer += dt;
                serviceTimer += dt;

                if (rescanTimer >= RESCAN_INTERVAL)
                {
                    rescanTimer = 0f;
                    ScanStations();
                }

                if (serviceTimer < SERVICE_INTERVAL)
                {
                    return;
                }

                serviceTimer = 0f;
                ServiceStations();
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] tick: " + ex);
            }
        }

        private void ScanStations()
        {
            stations.Clear();
            excludedStationIds.Clear();

            WorldData worldData = MainGame.Instance?.GameSave?.worldData;
            List<GameSceneData> scenes = worldData?.gameSceneDataList;
            if (scenes == null)
            {
                return;
            }

            if (StationProxyRegistry.SyncForWorld(worldData))
            {
                ServiceOrderRegistry.Clear();
                loggedStations.Clear();
                loggedCandidates.Clear();
                loggedDiagnostics.Clear();
                loggedForwarded.Clear();
                loggedNoCaretaker.Clear();
                loggedNoOutputSpace.Clear();
                pickupWaitLogTime.Clear();
                loggedCaretakers = false;
            }

            int autoCrafterCount = 0;
            HashSet<Guid> knownStationIds = new HashSet<Guid>();

            for (int s = 0; s < scenes.Count; s++)
            {
                GameSceneData scene = scenes[s];
                List<WgoData> wgoList = scene?.wgoDataList;
                if (wgoList == null)
                {
                    continue;
                }

                for (int i = 0; i < wgoList.Count; i++)
                {
                    WgoData w = wgoList[i];
                    if (w == null || w.Definition == null || w.CraftComponent == null)
                    {
                        continue;
                    }

                    if (!w.Definition.isAutoCrafter)
                    {
                        if (loggedCandidates.Count < MAX_CANDIDATE_LOGS
                            && w.CraftableAttachedWorker == null
                            && w.CraftComponent.HasCraftsInQueue
                            && w.CraftComponent.IsAutoCraftable
                            && loggedCandidates.Add(w.id))
                        {
                            AutoStationServicePlugin.Log?.LogInfo($"[ASS] candidate without isAutoCrafter: id={w.id}, def={w.Definition.GetType().Name}, type={w.CraftableType}, status={w.CraftComponent.Status}");
                        }

                        continue;
                    }

                    autoCrafterCount++;
                    knownStationIds.Add(w.UniqueId.Guid);

                    // Conveyor workbenches run their own loop: ConveyorWorkbenchComponent
                    // consumes input cells, finishes the craft, re-queues and pushes the
                    // product to the output cell (WaitingForOutputDrop). Caretaker orders
                    // created here bypass that state machine, so leave them alone.
                    if (w.CraftableType == CraftableType.ConveyorWorkbench)
                    {
                        excludedStationIds.Add(w.UniqueId.Guid);
                        if (loggedStations.Add("excluded:" + w.id))
                        {
                            AutoStationServicePlugin.Log?.LogInfo($"[ASS] {w.id}: conveyor workbench - left to the vanilla conveyor system");
                        }

                        continue;
                    }

                    if (w.CraftableAttachedWorker != null)
                    {
                        continue;
                    }

                    if (loggedStations.Add("served:" + w.id))
                    {
                        AutoStationServicePlugin.Log?.LogInfo($"[ASS] {w.id}: serviced by this mod (type={w.CraftableType}, status={w.CraftComponent.Status}, queue={w.CraftComponent.CraftElementsQueue?.Count ?? 0})");
                    }

                    stations.Add(w);
                }
            }

            ServiceOrderRegistry.RemoveMissing(knownStationIds);

            AutoStationServicePlugin.Log?.LogInfo($"[ASS] scan: {autoCrafterCount} auto-crafter station(s), {stations.Count} workerless to service, {excludedStationIds.Count} conveyor workbench(s) excluded");
        }

        private void ServiceStations()
        {
            for (int i = 0; i < excludedStationIds.Count; i++)
            {
                try
                {
                    CleanupExcludedStation(excludedStationIds[i]);
                }
                catch (Exception ex)
                {
                    AutoStationServicePlugin.Log?.LogError("[ASS] cleanup excluded station: " + ex);
                }
            }

            CollectCaretakerZones();

            for (int i = stations.Count - 1; i >= 0; i--)
            {
                WgoData w = stations[i];
                if (w == null)
                {
                    stations.RemoveAt(i);
                    continue;
                }

                try
                {
                    ServiceOne(w);
                }
                catch (Exception ex)
                {
                    AutoStationServicePlugin.Log?.LogError($"[ASS] service {w.id}: {ex}");
                }
            }
        }

        // orders can only be taken by a caretaker standing in the same zone, so a station in a
        // zone without one must not run ahead on its own
        private void CollectCaretakerZones()
        {
            caretakerZones.Clear();

            // ZombieSystemData is a static property that dereferences MainGame.Instance, so it
            // throws at the main menu - before a save is loaded there is no zombie list to read
            MainGame game = MainGame.Instance;
            if (game == null || game.GameSave == null)
            {
                return;
            }

            List<SGuid> zombieIds = MainGame.ZombieSystemData?.zombieOnSceneWgoIds;
            if (zombieIds == null)
            {
                return;
            }

            string text = string.Empty;
            for (int i = 0; i < zombieIds.Count; i++)
            {
                ZombieWgoData zombie = MainGame.ZombieSystemData.GetZombie(zombieIds[i]);
                if (zombie == null)
                {
                    continue;
                }

                WorldZoneData zone = zombie.WorldZoneData;
                string zoneId = zone != null ? zone.id : "?";

                if (!loggedCaretakers)
                {
                    if (text.Length > 0)
                    {
                        text += " | ";
                    }

                    text += $"{zombie.ZombieType} {zombie.UniqueId.Guid} zone={zoneId} state={zombie.CaretakerState}";
                }

                if (zombie.ZombieType == ZombieType.Caretaker && zone != null)
                {
                    caretakerZones.Add(zone.id);
                }
            }

            if (!loggedCaretakers)
            {
                loggedCaretakers = true;
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] zombies on scene: {(text.Length > 0 ? text : "none")}");
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] caretaker zone(s): {(caretakerZones.Count > 0 ? string.Join(", ", new List<string>(caretakerZones).ToArray()) : "none")}");
            }
        }

        private static void CleanupExcludedStation(Guid stationGuid)
        {
            List<ServiceOrderRegistry.TrackedOrder> mine = ServiceOrderRegistry.GetForStation(stationGuid);
            for (int i = 0; i < mine.Count; i++)
            {
                ServiceOrderRegistry.TrackedOrder t = mine[i];

                if (t.Zone != null)
                {
                    t.Zone.RemoveOrder(t.UniqueId);
                }

                ServiceOrderRegistry.Untrack(t.UniqueId.Guid);
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] revoked mod order PickupOrder {t.ItemId} x{t.Count} (station is a conveyor workbench)");
            }

            StationProxyRegistry.Remove(stationGuid);
        }

        private void ServiceOne(WgoData station)
        {
            CraftComponent cc = station.CraftComponent;
            if (cc == null || station.CraftableType == CraftableType.ConveyorWorkbench)
            {
                return;
            }

            WorldZoneData zone = station.WorldZoneData;
            if (zone == null)
            {
                return;
            }

            if (station.CraftableAttachedWorker != null)
            {
                RevokeTrackedOrders(zone, station, "a worker is attached now");
                return;
            }

            Inventory inv = station.CraftInventory;
            if (inv == null || inv.Data == null)
            {
                return;
            }

            DumpDiagnosticsOnce(station, cc, inv);

            // orders outlive a reload while the proxy table does not, and a caretaker may act on
            // one before the next scan - so the proxy and the order list are repaired for every
            // serviced station, even in a zone without a caretaker
            List<OrderBase> existing = WorldZoneOrders.GetOrders(zone, station.UniqueId);
            if (existing == null)
            {
                return;
            }

            if (existing.Count > 0)
            {
                StationProxyRegistry.EnsureProxy(station);
                if (RemoveStaleOrders(zone, station, inv, existing))
                {
                    existing = WorldZoneOrders.GetOrders(zone, station.UniqueId);
                    if (existing == null)
                    {
                        return;
                    }
                }
            }

            // only a caretaker standing in this very zone can take an order from it, so a
            // station nobody services must be left untouched instead of running ahead
            if (!caretakerZones.Contains(zone.id))
            {
                if (loggedNoCaretaker.Add(station.UniqueId.Guid))
                {
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: no caretaker in zone {zone.id} - left alone, the station will not continue on its own");
                }

                RevokeTrackedOrders(zone, station, "no caretaker in zone " + zone.id);
                return;
            }

            // a station that forwards its storage to another wgo (workbench-on-top-of-chest
            // setups) already moves both its input and its output through that storage, so
            // there is nothing for a caretaker to carry
            if (IsStorageForwarded(station, out WgoData forwardingTarget))
            {
                if (loggedForwarded.Add(station.UniqueId.Guid))
                {
                    string full = IsFull(forwardingTarget.Inventory) ? " - LINKED STORAGE IS FULL, products cannot leave the station" : string.Empty;
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: shares storage with {forwardingTarget.id} [{forwardingTarget.UniqueId.Guid}] - products already go there, skipping (fill {FillText(forwardingTarget.Inventory)}){full}");
                }

                RevokeTrackedOrders(zone, station, "station shares its storage with " + forwardingTarget.id);
                return;
            }

            if (ValidateTrackedOrders(zone, existing, station, inv))
            {
                existing = WorldZoneOrders.GetOrders(zone, station.UniqueId);
                if (existing == null)
                {
                    return;
                }
            }

            if (cc.Status == CraftComponentStatus.ReadyToFinishAutoCraft)
            {
                // produce the output but do not let the queue move on: the product has to be
                // carried away first, exactly like it works when a crafter zombie is attached
                if (StationProxyRegistry.EnsureProxy(station))
                {
                    ProduceOutputAndWaitForPickup(station, cc);
                }
            }
            else if (cc.Status == CraftComponentStatus.WaitingForWorkerPickUp)
            {
                LogPickupWait(station, cc, inv);
            }

            HashSet<string> productIds = CollectProductIds(station, cc);
            HashSet<string> required = CollectRequiredItemIds(cc);

            bool pendingPickup = false;
            for (int i = 0; i < existing.Count; i++)
            {
                OrderBase order = existing[i];
                if (order == null || !(order is PickupOrder))
                {
                    continue;
                }

                if (order.Item != null && !string.IsNullOrEmpty(order.Item.id) && order.Item.Count > 0
                    && inv.Data.HasItemQuantityInInventory(order.Item.id, order.Item.Count))
                {
                    pendingPickup = true;
                }
            }

            // the station feeds itself straight from the zone storages, so the caretaker's only
            // job here is carrying the product away - and that has to happen even when the queue
            // has emptied, otherwise nobody ever comes for the last product
            if (!pendingPickup && (productIds.Count > 0 || cc.HasCraftsInQueue))
            {
                Item product = FindProduct(inv, required, productIds);
                if (product != null && StationProxyRegistry.EnsureProxy(station))
                {
                    PickupOrder order = new PickupOrder(station.UniqueId, new Item(product.id, product.Count));
                    zone.PlaceNewOrder(order);
                    ServiceOrderRegistry.Track(order, zone, station.UniqueId);
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: PickupOrder created <- {product.id} x{product.Count}");
                }
            }
        }

        // the vanilla worker model, without the worker: the output is produced and the craft
        // parks in WaitingForWorkerPickUp, which only the caretaker taking the product away
        // resolves (PickupOrder.ExecuteOrder -> TryFinishCurCraft). the queue therefore never
        // runs ahead of whoever serves the station
        private void ProduceOutputAndWaitForPickup(WgoData station, CraftComponent cc)
        {
            CraftElementBase ce = cc.CurrentCraftElement;
            if (ce == null)
            {
                return;
            }

            Inventory craftInventory = station.CraftableObjectCraftInventory;
            if (craftInventory == null || craftInventory.Data == null || craftInventory.Data.IsEmpty)
            {
                // HandleOutput would drop the product on the ground instead of storing it
                if (loggedNoOutputSpace.Add(station.UniqueId.Guid))
                {
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: the station cannot store its output - left at {cc.Status}");
                }

                return;
            }

            if (handleOutputMethod == null)
            {
                handleOutputMethod = AccessTools.Method(typeof(CraftComponent), "HandleOutput", new Type[] { typeof(CraftElementBase), typeof(bool).MakeByRefType() });
                if (handleOutputMethod == null)
                {
                    AutoStationServicePlugin.Log?.LogError("[ASS] CraftComponent.HandleOutput not found - the station cannot produce its output");
                    return;
                }
            }

            try
            {
                object[] args = new object[] { ce, false };
                handleOutputMethod.Invoke(cc, args);

                station.OnCraftEnd(ce);
                cc.Status = CraftComponentStatus.WaitingForWorkerPickUp;
                ce.PrevCraftComponentStatus = cc.Status;

                AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: output produced, waiting for a caretaker to take it (queue={cc.CraftElementsQueue?.Count ?? 0}, craft inventory ({FillText(craftInventory)}): {NamesText(craftInventory)})");
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError($"[ASS] ProduceOutputAndWaitForPickup {station.id}: {ex}");
            }
        }

        // the product is sitting in the station and only a caretaker can move it on, so say
        // so once in a while to tell "waiting for pickup" apart from "stuck"
        private void LogPickupWait(WgoData station, CraftComponent cc, Inventory inv)
        {
            float now = Time.unscaledTime;
            float last;
            if (pickupWaitLogTime.TryGetValue(station.UniqueId.Guid, out last) && now - last < PICKUP_WAIT_LOG_INTERVAL)
            {
                return;
            }

            pickupWaitLogTime[station.UniqueId.Guid] = now;
            AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: craft finished, waiting for a caretaker to pick up the product (queue={cc.CraftElementsQueue?.Count ?? 0}, craft inventory ({FillText(inv)}): {NamesText(inv)})");
        }

        // an untaken order this mod did not create is a leftover: either a material delivery from
        // an older version of the mod (the station feeds itself from the zone storages now) or a
        // pickup of an item the station no longer holds. both make a caretaker walk to the station
        // for nothing, and the delivery would even push materials back in
        private static bool RemoveStaleOrders(WorldZoneData zone, WgoData station, Inventory inv, List<OrderBase> zoneOrders)
        {
            bool removedAny = false;

            for (int i = 0; i < zoneOrders.Count; i++)
            {
                OrderBase order = zoneOrders[i];
                if (order == null || !order.ExecutorUniqueId.IsEmpty || ServiceOrderRegistry.IsTracked(order))
                {
                    continue;
                }

                if (order is DeliveryOrder)
                {
                    zone.RemoveOrder(order.UniqueId);
                    removedAny = true;
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: removed leftover DeliveryOrder {ItemText(order.Item)} (the station feeds itself from the zone storages)");
                    continue;
                }

                if (order is PickupOrder
                    && (order.Item == null || string.IsNullOrEmpty(order.Item.id) || order.Item.Count <= 0
                        || !inv.Data.HasItemQuantityInInventory(order.Item.id, order.Item.Count)))
                {
                    zone.RemoveOrder(order.UniqueId);
                    removedAny = true;
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: removed stale PickupOrder {ItemText(order.Item)} (the station no longer holds it)");
                }
            }

            return removedAny;
        }

        // returns true when at least one order was revoked
        private static bool ValidateTrackedOrders(WorldZoneData zone, List<OrderBase> zoneOrders, WgoData station, Inventory inv)
        {
            List<ServiceOrderRegistry.TrackedOrder> mine = ServiceOrderRegistry.GetForStation(station.UniqueId.Guid);
            if (mine.Count == 0)
            {
                return false;
            }

            bool revokedAny = false;

            for (int i = 0; i < mine.Count; i++)
            {
                ServiceOrderRegistry.TrackedOrder t = mine[i];

                bool present = false;
                for (int j = 0; j < zoneOrders.Count; j++)
                {
                    OrderBase order = zoneOrders[j];
                    if (order != null && order.UniqueId.Guid == t.UniqueId.Guid)
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                {
                    ServiceOrderRegistry.Untrack(t.UniqueId.Guid);
                    continue;
                }

                bool stale = !inv.Data.HasItemQuantityInInventory(t.ItemId, t.Count);
                string reason = "the station no longer holds that item";

                if (!stale)
                {
                    continue;
                }

                zone.RemoveOrder(t.UniqueId);
                ServiceOrderRegistry.Untrack(t.UniqueId.Guid);
                revokedAny = true;
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: revoked PickupOrder {t.ItemId} x{t.Count} ({reason})");
            }

            return revokedAny;
        }

        private static void RevokeTrackedOrders(WorldZoneData zone, WgoData station, string reason)
        {
            List<ServiceOrderRegistry.TrackedOrder> mine = ServiceOrderRegistry.GetForStation(station.UniqueId.Guid);
            for (int i = 0; i < mine.Count; i++)
            {
                ServiceOrderRegistry.TrackedOrder t = mine[i];
                zone.RemoveOrder(t.UniqueId);
                ServiceOrderRegistry.Untrack(t.UniqueId.Guid);
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: revoked PickupOrder {t.ItemId} x{t.Count} ({reason})");
            }
        }

        private static Item FindProduct(Inventory inv, HashSet<string> required, HashSet<string> productIds)
        {
            List<Item> items = inv.Data.Inventory;
            if (items == null)
            {
                return null;
            }

            bool strict = productIds.Count > 0;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it == null || it.IsEmpty || required.Contains(it.id))
                {
                    continue;
                }

                if (strict && !productIds.Contains(it.id))
                {
                    continue;
                }

                ItemDef def = GameBalance.Me.GetData<ItemDef>(it.id);
                if (def != null && def.isFuel)
                {
                    continue;
                }

                return it;
            }

            return null;
        }

        // product ids the station's crafts can produce - taken from the craft definitions of
        // this wgo and from whatever is queued right now
        private static HashSet<string> CollectProductIds(WgoData station, CraftComponent cc)
        {
            HashSet<string> result = new HashSet<string>();

            try
            {
                GameBalance balance = GameBalance.Me;
                List<CraftDefBase> defs;
                if (balance != null && balance.craftsInCache != null
                    && balance.craftsInCache.TryGetValue(station.id, out defs) && defs != null)
                {
                    for (int i = 0; i < defs.Count; i++)
                    {
                        AddOutputIds(result, defs[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError($"[ASS] CollectProductIds({station.id}): {ex}");
            }

            List<CraftElementBase> queue = cc.CraftElementsQueue;
            if (queue == null)
            {
                return result;
            }

            for (int i = 0; i < queue.Count; i++)
            {
                CraftElementBase el = queue[i];
                if (el == null)
                {
                    continue;
                }

                AddOutputIds(result, el.Def);

                List<ItemCount> pre = el.PreOutputItems;
                if (pre == null)
                {
                    continue;
                }

                for (int j = 0; j < pre.Count; j++)
                {
                    if (pre[j] != null && !string.IsNullOrEmpty(pre[j].itemId))
                    {
                        result.Add(pre[j].itemId);
                    }
                }
            }

            return result;
        }

        private static void AddOutputIds(HashSet<string> result, CraftDefBase def)
        {
            if (def == null || def.outputItems == null)
            {
                return;
            }

            List<ChanceOutputItem> direct = def.outputItems.chanceOutputItems;
            if (direct != null)
            {
                for (int i = 0; i < direct.Count; i++)
                {
                    if (direct[i] != null && !string.IsNullOrEmpty(direct[i].id))
                    {
                        result.Add(direct[i].id);
                    }
                }
            }

            List<GroupChanceOutputItem> groups = def.outputItems.groupChanceOutputItems;
            if (groups == null)
            {
                return;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                GroupChanceOutputItem group = groups[i];
                if (group == null || group.chanceItems == null)
                {
                    continue;
                }

                for (int j = 0; j < group.chanceItems.Count; j++)
                {
                    if (group.chanceItems[j] != null && !string.IsNullOrEmpty(group.chanceItems[j].id))
                    {
                        result.Add(group.chanceItems[j].id);
                    }
                }
            }
        }

        private static bool IsStorageForwarded(WgoData station, out WgoData target)
        {
            target = null;
            WGODef def = station.Definition;
            if (def == null || !def.hasRefToOtherWgoInventory || string.IsNullOrEmpty(def.refToOtherWgoInventory))
            {
                return false;
            }

            try
            {
                target = MainGame.WorldData.GetWgoData(def.refToOtherWgoInventory);
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError($"[ASS] IsStorageForwarded({station.id}): {ex}");
                return false;
            }

            if (target == null)
            {
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] {station.id}: forwards its storage to {def.refToOtherWgoInventory} but that wgo is not loaded");
                return false;
            }

            return ReferenceEquals(station.CraftInventory, target.CraftInventory);
        }

        private static string ItemText(Item item)
        {
            return item == null ? "<empty>" : item.id + " x" + item.Count;
        }

        private static string FillText(Inventory inv)
        {
            if (inv == null || inv.Data == null)
            {
                return "no inventory";
            }

            return inv.Data.InventoryFillSize + "/" + inv.Data.InventorySize;
        }

        private static bool IsFull(Inventory inv)
        {
            return inv != null && inv.Data != null && inv.Data.InventoryFillSize >= inv.Data.InventorySize;
        }

        private static string NamesText(Inventory inv)
        {
            if (inv == null || inv.Data == null || inv.Data.Inventory == null)
            {
                return "no inventory";
            }

            List<Item> items = inv.Data.Inventory;
            string text = string.Empty;
            int shown = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it == null || it.IsEmpty)
                {
                    continue;
                }

                if (shown > 0)
                {
                    text += ", ";
                }

                text += it.id + " x" + it.Count;
                shown++;
                if (shown >= 12)
                {
                    text += ", ...";
                    break;
                }
            }

            return shown == 0 ? "empty" : text;
        }

        private static string OutputIdsText(OutputItems outputItems)
        {
            if (outputItems == null)
            {
                return "?";
            }

            List<string> ids = new List<string>();

            List<ChanceOutputItem> direct = outputItems.chanceOutputItems;
            if (direct != null)
            {
                for (int i = 0; i < direct.Count; i++)
                {
                    if (direct[i] != null && !string.IsNullOrEmpty(direct[i].id))
                    {
                        ids.Add(direct[i].id);
                    }
                }
            }

            List<GroupChanceOutputItem> groups = outputItems.groupChanceOutputItems;
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    GroupChanceOutputItem group = groups[i];
                    if (group == null || group.chanceItems == null)
                    {
                        continue;
                    }

                    for (int j = 0; j < group.chanceItems.Count; j++)
                    {
                        if (group.chanceItems[j] != null && !string.IsNullOrEmpty(group.chanceItems[j].id))
                        {
                            ids.Add("group:" + group.outputGroupId + ":" + group.chanceItems[j].id);
                        }
                    }
                }
            }

            return ids.Count == 0 ? string.Empty : string.Join(", ", ids.ToArray());
        }

        private static string NeedIdsText(List<NeedItemData> needs)
        {
            if (needs == null || needs.Count == 0)
            {
                return string.Empty;
            }

            string text = string.Empty;
            for (int i = 0; i < needs.Count; i++)
            {
                if (needs[i] == null || string.IsNullOrEmpty(needs[i].id))
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text += ", ";
                }

                text += needs[i].id;
            }

            return text;
        }

        private static string ItemCountIdsText(List<ItemCount> items)
        {
            if (items == null || items.Count == 0)
            {
                return string.Empty;
            }

            string text = string.Empty;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null || string.IsNullOrEmpty(items[i].itemId))
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text += ", ";
                }

                text += items[i].itemId + " x" + items[i].count;
            }

            return text;
        }

        // one full picture per station per world load: where its items live, what its crafts
        // produce and what the queued crafts still need
        private void DumpDiagnosticsOnce(WgoData station, CraftComponent cc, Inventory inv)
        {
            if (!loggedDiagnostics.Add(station.UniqueId.Guid))
            {
                return;
            }

            try
            {
                WGODef def = station.Definition;
                WorldZoneData zone = station.WorldZoneData;
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] diag {station.id} [{station.UniqueId.Guid}]: def={def.GetType().Name} conveyorType={def.conveyorType} isAutoCrafter={def.isAutoCrafter} forwards={def.hasRefToOtherWgoInventory}({def.refToOtherWgoInventory}) zone={zone?.id} caretakersInZone={zone != null && caretakerZones.Contains(zone.id)} status={cc.Status} queue={cc.CraftElementsQueue?.Count ?? 0}");
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] diag {station.id}: inventory ({FillText(station.Inventory)}): {NamesText(station.Inventory)}");
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] diag {station.id}: craft inventory ({FillText(station.CraftInventory)}): {NamesText(station.CraftInventory)}");

                WgoData forwardingTarget;
                if (IsStorageForwarded(station, out forwardingTarget))
                {
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] diag {station.id}: linked {forwardingTarget.id} [{forwardingTarget.UniqueId.Guid}] inventory ({FillText(forwardingTarget.Inventory)}): {NamesText(forwardingTarget.Inventory)}");
                }

                GameBalance balance = GameBalance.Me;
                List<CraftDefBase> defs;
                if (balance != null && balance.craftsInCache != null
                    && balance.craftsInCache.TryGetValue(station.id, out defs) && defs != null)
                {
                    for (int i = 0; i < defs.Count; i++)
                    {
                        CraftDefBase cd = defs[i];
                        if (cd == null)
                        {
                            continue;
                        }

                        string extra = string.Empty;
                        CraftDef craftDef = cd as CraftDef;
                        if (craftDef != null)
                        {
                            extra = $" transferStart={craftDef.transferDestinationStart}({craftDef.destinationItemStart}) transferEnd={craftDef.transferDestinationEnd}({craftDef.destinationItemEnd}) dropStart=[{NeedIdsText(craftDef.dropFromWgoItemsStart)}] dropEnd=[{NeedIdsText(craftDef.dropFromWgoItemsEnd)}] removeFromWgo=[{NeedIdsText(craftDef.removeItemsFromWgo)}]";
                        }

                        AutoStationServicePlugin.Log?.LogInfo($"[ASS] diag {station.id}: craft {cd.id} auto={cd.isAuto} hidden={cd.isHidden} autoFinish={cd.autoFinishAutoCraft} out=[{OutputIdsText(cd.outputItems)}] need=[{NeedIdsText(cd.needItems)}] needFromWgo=[{NeedIdsText(cd.needItemsFromWgo)}] addStart=[{OutputIdsText(cd.addItemsToWgoOnStart)}] addFinish=[{OutputIdsText(cd.addItemsToWgoOnFinish)}]{extra}");
                    }
                }

                List<CraftElementBase> queue = cc.CraftElementsQueue;
                if (queue == null)
                {
                    return;
                }

                for (int i = 0; i < queue.Count; i++)
                {
                    CraftElementBase el = queue[i];
                    if (el == null)
                    {
                        continue;
                    }

                    string outIds = el.Def != null ? OutputIdsText(el.Def.outputItems) : "?";
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] diag {station.id}: queued {el.CraftId} started={el.IsStarted} count={el.Count} out=[{outIds}] preOut=[{ItemCountIdsText(el.PreOutputItems)}] need=[{NeedIdsText(el.Requirements)}]");
                }
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError($"[ASS] diag {station.id}: {ex}");
            }
        }

        private static HashSet<string> CollectRequiredItemIds(CraftComponent cc)
        {
            HashSet<string> result = new HashSet<string>();
            List<CraftElementBase> queue = cc.CraftElementsQueue;
            if (queue == null)
            {
                return result;
            }

            for (int i = 0; i < queue.Count; i++)
            {
                CraftElementBase el = queue[i];
                if (el == null || el.IsStarted || el.Requirements == null)
                {
                    continue;
                }

                for (int j = 0; j < el.Requirements.Count; j++)
                {
                    NeedItemData need = el.Requirements[j];
                    if (need != null && !string.IsNullOrEmpty(need.id))
                    {
                        result.Add(need.id);
                    }
                }
            }

            return result;
        }
    }

    internal static class ServiceOrderRegistry
    {
        internal sealed class TrackedOrder
        {
            internal SGuid UniqueId;
            internal SGuid StationId;
            internal WorldZoneData Zone;
            internal string ItemId;
            internal int Count;
        }

        private static readonly Dictionary<Guid, TrackedOrder> tracked = new Dictionary<Guid, TrackedOrder>();

        internal static void Track(OrderBase order, WorldZoneData zone, SGuid stationId)
        {
            if (order == null || order.UniqueId == null || order.UniqueId.Guid == Guid.Empty)
            {
                return;
            }

            tracked[order.UniqueId.Guid] = new TrackedOrder
            {
                UniqueId = order.UniqueId,
                StationId = stationId,
                Zone = zone,
                ItemId = order.Item?.id,
                Count = order.Item?.Count ?? 0,
            };
        }

        internal static void Untrack(Guid orderGuid)
        {
            tracked.Remove(orderGuid);
        }

        internal static bool IsTracked(OrderBase order)
        {
            return order != null && order.UniqueId != null && tracked.ContainsKey(order.UniqueId.Guid);
        }

        internal static void Clear()
        {
            tracked.Clear();
        }

        // drops entries whose station no longer exists in the scanned world
        internal static void RemoveMissing(HashSet<Guid> knownStationGuids)
        {
            List<TrackedOrder> gone = null;
            foreach (KeyValuePair<Guid, TrackedOrder> pair in tracked)
            {
                TrackedOrder t = pair.Value;
                if (t.StationId != null && knownStationGuids.Contains(t.StationId.Guid))
                {
                    continue;
                }

                if (t.Zone != null)
                {
                    try
                    {
                        t.Zone.RemoveOrder(t.UniqueId);
                    }
                    catch (Exception ex)
                    {
                        AutoStationServicePlugin.Log?.LogError("[ASS] RemoveMissing: " + ex);
                    }
                }

                gone = gone ?? new List<TrackedOrder>();
                gone.Add(t);
            }

            if (gone == null)
            {
                return;
            }

            for (int i = 0; i < gone.Count; i++)
            {
                tracked.Remove(gone[i].UniqueId.Guid);

                string stationId = gone[i].StationId != null ? gone[i].StationId.ToString() : "?";
                if (gone[i].StationId != null)
                {
                    StationProxyRegistry.Remove(gone[i].StationId.Guid);
                }

                AutoStationServicePlugin.Log?.LogInfo($"[ASS] station {stationId} is gone - revoked PickupOrder {gone[i].ItemId} x{gone[i].Count}");
            }
        }

        internal static List<TrackedOrder> GetForStation(Guid stationGuid)
        {
            List<TrackedOrder> result = new List<TrackedOrder>();
            foreach (KeyValuePair<Guid, TrackedOrder> pair in tracked)
            {
                if (pair.Value.StationId != null && pair.Value.StationId.Guid == stationGuid)
                {
                    result.Add(pair.Value);
                }
            }

            return result;
        }
    }

    internal static class WorldZoneOrders
    {
        private static FieldInfo ordersField;

        internal static List<OrderBase> GetOrders(WorldZoneData zone, SGuid targetUniqueId)
        {
            if (ordersField == null)
            {
                ordersField = AccessTools.Field(typeof(WorldZoneData), "orders");
                if (ordersField == null)
                {
                    AutoStationServicePlugin.Log?.LogError("[ASS] WorldZoneData.orders not found");
                    return null;
                }
            }

            List<OrderBase> orders = ordersField.GetValue(zone) as List<OrderBase>;
            if (orders == null || orders.Count == 0)
            {
                return new List<OrderBase>();
            }

            List<OrderBase> result = new List<OrderBase>();
            for (int i = 0; i < orders.Count; i++)
            {
                OrderBase order = orders[i];
                if (order != null && order.TargetWgoUniqueId == targetUniqueId)
                {
                    result.Add(order);
                }
            }

            return result;
        }
    }

    internal static class StationProxyRegistry
    {
        private static readonly Dictionary<Guid, ZombieWgoData> proxies = new Dictionary<Guid, ZombieWgoData>();

        private static object lastWorld;
        private static FieldInfo attachedWgoDataField;
        private static FieldInfo attachedWgoDataUniqueIdField;
        private static FieldInfo crafterOrdersField;

        internal static ZombieWgoData GetProxy(SGuid uniqueId)
        {
            if (uniqueId == null || uniqueId.IsEmpty)
            {
                return null;
            }

            ZombieWgoData proxy;
            return proxies.TryGetValue(uniqueId.Guid, out proxy) ? proxy : null;
        }

        // returns true when the world changed (callers must drop world-bound state)
        internal static bool SyncForWorld(object world)
        {
            if (ReferenceEquals(lastWorld, world))
            {
                return false;
            }

            lastWorld = world;
            if (proxies.Count > 0)
            {
                proxies.Clear();
                AutoStationServicePlugin.Log?.LogInfo("[ASS] world changed - proxy zombies reset");
            }

            return true;
        }

        internal static void Remove(Guid stationGuid)
        {
            if (proxies.Remove(stationGuid))
            {
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] proxy zombie removed for station {stationGuid}");
            }
        }

        internal static bool EnsureProxy(WgoData station)
        {
            if (station == null)
            {
                return false;
            }

            Guid key = station.UniqueId.Guid;
            if (key == Guid.Empty)
            {
                return false;
            }

            if (!ResolveFields())
            {
                return false;
            }

            try
            {
                ZombieWgoData proxy;
                if (proxies.TryGetValue(key, out proxy))
                {
                    // after a save reload the WgoData instances are new objects with the same ids
                    if (ReferenceEquals(attachedWgoDataField.GetValue(proxy), station))
                    {
                        return true;
                    }

                    Bind(proxy, station);
                    return true;
                }

                proxy = new ZombieWgoData("zombie", station.Position, station.WorldId);
                Bind(proxy, station);

                // a non-empty crafterOrders list makes every crafter-only code path on this
                // proxy return immediately - the station must keep its own auto-craft logic
                if (crafterOrdersField != null)
                {
                    List<SGuid> crafterOrders = crafterOrdersField.GetValue(proxy) as List<SGuid>;
                    if (crafterOrders != null)
                    {
                        crafterOrders.Add(SGuid.Empty);
                    }
                }

                proxies[key] = proxy;
                AutoStationServicePlugin.Log?.LogInfo($"[ASS] proxy zombie registered for station {station.id} ({key})");
                return true;
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError($"[ASS] EnsureProxy {station.id}: {ex}");
                return false;
            }
        }

        // vanilla reaches a caretaker order's target through ZombieSystemData.GetZombie, which only
        // knows real zombies - a serviced station resolves only while its proxy exists. orders are
        // taken the moment a caretaker appears, so the proxy is created on demand here
        internal static bool EnsureProxyForTarget(SGuid targetGuid)
        {
            if (targetGuid == null || targetGuid.IsEmpty)
            {
                return false;
            }

            WgoData target;
            try
            {
                target = MainGame.WorldData.GetWgoData(targetGuid);
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] EnsureProxyForTarget: " + ex);
                return false;
            }

            if (target == null || target.Definition == null || !target.Definition.isAutoCrafter)
            {
                return false;
            }

            if (target.CraftableType == CraftableType.ConveyorWorkbench || target.CraftableAttachedWorker != null)
            {
                return false;
            }

            return EnsureProxy(target);
        }

        private static void Bind(ZombieWgoData proxy, WgoData station)
        {
            attachedWgoDataField.SetValue(proxy, station);
            SGuid attachedId = (SGuid)attachedWgoDataUniqueIdField.GetValue(proxy);
            attachedId.SetGuid(station.UniqueId);
            proxy.WorldId = station.WorldId;
            proxy.WorldZoneData = station.WorldZoneData;
        }

        private static bool ResolveFields()
        {
            if (attachedWgoDataField != null && attachedWgoDataUniqueIdField != null)
            {
                return true;
            }

            attachedWgoDataField = AccessTools.Field(typeof(ZombieWgoData), "attachedWgoData");
            attachedWgoDataUniqueIdField = AccessTools.Field(typeof(ZombieWgoData), "attachedWgoDataUniqueId");
            crafterOrdersField = AccessTools.Field(typeof(ZombieWgoData), "crafterOrders");

            if (attachedWgoDataField == null || attachedWgoDataUniqueIdField == null)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] cannot resolve ZombieWgoData attach fields");
                return false;
            }

            return true;
        }
    }

    // vanilla walks an order's target with MainGame.ZombieSystemData.GetZombie(...).AttachedWgoData
    // and never checks for null: an order whose target is gone (left in a save by an older mod
    // version, or by a station that no longer exists) crashes the caretaker the moment it takes it.
    // serviced stations get their proxy on demand here, everything else is dropped so the caretaker
    // looks for other work instead of throwing
    internal static class CaretakerOrderGuard
    {
        private static FieldInfo executingOrderField;
        private static MethodInfo stopOrderExecutionMethod;
        private static MethodInfo getNewOrderMethod;

        public static bool CaretakerTryMoveToZombiePrefix(ZombieWgoData __instance)
        {
            try
            {
                if (__instance == null || !Resolve())
                {
                    return true;
                }

                SGuid orderId = (SGuid)executingOrderField.GetValue(__instance);
                WorldZoneData zone = __instance.WorldZoneData;
                if (orderId == null || orderId.IsEmpty || zone == null)
                {
                    return true;
                }

                OrderBase order = zone.FindOrder(orderId);
                if (order == null)
                {
                    AutoStationServicePlugin.Log?.LogInfo($"[ASS] caretaker {__instance.UniqueId.Guid}: order {orderId.Guid} is not in zone {zone.id} anymore - looking for another one");
                    Replan(__instance);
                    return false;
                }

                if (MainGame.ZombieSystemData.GetZombie(order.TargetWgoUniqueId) != null)
                {
                    return true;
                }

                if (StationProxyRegistry.EnsureProxyForTarget(order.TargetWgoUniqueId)
                    && MainGame.ZombieSystemData.GetZombie(order.TargetWgoUniqueId) != null)
                {
                    return true;
                }

                AutoStationServicePlugin.Log?.LogInfo($"[ASS] caretaker {__instance.UniqueId.Guid}: order {order.UniqueId.Guid} points at {order.TargetWgoUniqueId.Guid}, which is neither a zombie nor a station this mod serves - order dropped");
                zone.RemoveOrder(order.UniqueId);

                // removing the order notifies the caretaker, which stops it and looks for new work;
                // states that do not react to that are replanned here
                SGuid stillExecuting = (SGuid)executingOrderField.GetValue(__instance);
                if (stillExecuting != null && !stillExecuting.IsEmpty && stillExecuting.Guid == orderId.Guid)
                {
                    Replan(__instance);
                }

                return false;
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] caretaker order guard: " + ex);
            }

            return true;
        }

        private static void Replan(ZombieWgoData caretaker)
        {
            stopOrderExecutionMethod.Invoke(caretaker, null);
            getNewOrderMethod.Invoke(caretaker, null);
        }

        private static bool Resolve()
        {
            if (executingOrderField != null && stopOrderExecutionMethod != null && getNewOrderMethod != null)
            {
                return true;
            }

            executingOrderField = AccessTools.Field(typeof(ZombieWgoData), "caretakerExecutingOrder");
            stopOrderExecutionMethod = AccessTools.Method(typeof(ZombieWgoData), "CaretakerTryStopOrderExecution");
            getNewOrderMethod = AccessTools.Method(typeof(ZombieWgoData), "CaretakerTryGetNewOrderOrMoveToStation");

            if (executingOrderField == null || stopOrderExecutionMethod == null || getNewOrderMethod == null)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] cannot resolve the caretaker order fields - the guard is off");
                return false;
            }

            return true;
        }
    }
}
