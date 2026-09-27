using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GK2AutoStationService
{
    [BepInPlugin(ModGuid, "Auto Station Service", ModVersion)]
    // soft: with the framework installed it loads before us and we register our setting in its
    // Mods menu; without it nothing changes and the setting stays in our own config file
    [BepInDependency("ru.superman4eg.gk2.framework", BepInDependency.DependencyFlags.SoftDependency)]
    public class AutoStationServicePlugin : BaseUnityPlugin
    {
        internal const string ModGuid = "com.gk2mod.autostationservice";
        internal const string ModVersion = "1.5.1";
        internal const string TechPointsSection = "General";
        internal const string TechPointsKey = "CaretakerTakesTechPoints";
        internal const bool TechPointsDefault = true;
        // english text: also the fallback of the localization lookup in FrameworkIntegration.cs
        internal const string TechPointsLabel = "Caretaker takes the tech points";
        internal const string TechPointsDescription =
            "A caretaker that carries the product away from a workerless auto station takes the tech points with it, like a crafter zombie does for its own crafts. Off: the tech points drop on the ground for the player to collect.";

        internal const string LogSection = "General";
        internal const string LogKey = "DetailedLog";
        internal const bool LogDefault = true;
        internal const string LogLabel = "Detailed log";
        internal const string LogDescription =
            "Write what the mod does to the BepInEx log (station scans, pickup orders, gardener runs). Errors are always logged. Turn off for a quiet log once everything works.";

        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;

        // bound by the framework bridge when GK2 Mod Framework is installed, otherwise in Awake
        internal static ConfigEntry<bool> CaretakerTakesTechPoints;
        internal static ConfigEntry<bool> DetailedLog;

        // one place for the mod's chatter: the whole info level can be silenced from the settings
        // (errors keep going out - a broken run must stay visible in the log)
        internal static void LogInfo(string message)
        {
            if (DetailedLog == null || DetailedLog.Value)
            {
                Log?.LogInfo(message);
            }
        }

        private void Awake()
        {
            Log = Logger;

            try
            {
                if (!FrameworkBridge.TryRegister(this))
                {
                    CaretakerTakesTechPoints = Config.Bind(TechPointsSection, TechPointsKey, TechPointsDefault, TechPointsDescription);
                    DetailedLog = Config.Bind(LogSection, LogKey, LogDefault, LogDescription);
                    Log.LogInfo("[ASS] GK2 Mod Framework not found - the settings can be edited in BepInEx/config/com.gk2mod.autostationservice.cfg");
                }

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

                MethodInfo dropStoredTechPoints = AccessTools.Method(typeof(WgoData), "DropStoredTechPoints");
                if (dropStoredTechPoints == null)
                {
                    Log.LogError("[ASS] WgoData.DropStoredTechPoints not found - caretakers cannot take the tech points");
                }
                else
                {
                    HarmonyInstance.Patch(dropStoredTechPoints, prefix: new HarmonyMethod(typeof(CaretakerTechPoints), nameof(CaretakerTechPoints.DropStoredTechPointsPrefix)));
                    Log.LogInfo("[ASS] patched WgoData.DropStoredTechPoints");
                }

                // a zone that cannot host a zombie_supplier_station (the garden) never has a
                // caretaker, so an idle gardener standing in that zone is sent to collect instead
                MethodInfo gardenerUpdate = AccessTools.Method(typeof(ZombieWgoData), "GardenerUpdateBehaviour");
                if (gardenerUpdate == null)
                {
                    Log.LogError("[ASS] ZombieWgoData.GardenerUpdateBehaviour not found - gardeners cannot collect in a zone without a caretaker");
                }
                else
                {
                    HarmonyInstance.Patch(gardenerUpdate, prefix: new HarmonyMethod(typeof(GardenerJobRegistry), nameof(GardenerJobRegistry.GardenerUpdateBehaviourPrefix)));
                    Log.LogInfo("[ASS] patched ZombieWgoData.GardenerUpdateBehaviour");
                }

                GameObject host = new GameObject("GK2AutoStationService");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<AutoStationServiceTick>();

                // always printed, whatever the log setting says: it names the loaded build
                Log.LogInfo($"[ASS] Auto Station Service v{ModVersion} ready (caretaker takes tech points: {CaretakerTakesTechPoints.Value}, detailed log: {DetailedLog.Value})");
                if (!DetailedLog.Value)
                {
                    Log.LogInfo("[ASS] detailed log is off - turn on 'Detailed log' (General) in the settings for diagnostics");
                }
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
        private readonly HashSet<Guid> loggedUnreachable = new HashSet<Guid>();
        private readonly Dictionary<Guid, float> pickupWaitLogTime = new Dictionary<Guid, float>();
        private readonly Dictionary<Guid, float> noProductLogTime = new Dictionary<Guid, float>();
        private readonly HashSet<string> caretakerZones = new HashSet<string>();
        // gardeners standing idle in their zone, by zone id - they serve the stations of a zone
        // that has no caretaker at all (the garden zone cannot host a zombie_supplier_station)
        private readonly Dictionary<string, List<ZombieWgoData>> idleGardenersByZone = new Dictionary<string, List<ZombieWgoData>>();
        private readonly HashSet<string> gardenerZones = new HashSet<string>();
        private readonly Dictionary<string, WorldZoneData> gardenerZoneRefs = new Dictionary<string, WorldZoneData>();
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
                GardenerJobRegistry.Clear();
                loggedStations.Clear();
                loggedCandidates.Clear();
                loggedDiagnostics.Clear();
                loggedForwarded.Clear();
                loggedNoCaretaker.Clear();
                loggedNoOutputSpace.Clear();
                loggedUnreachable.Clear();
                pickupWaitLogTime.Clear();
                noProductLogTime.Clear();
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
                            AutoStationServicePlugin.LogInfo($"[ASS] candidate without isAutoCrafter: id={w.id}, def={w.Definition.GetType().Name}, type={w.CraftableType}, status={w.CraftComponent.Status}");
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
                            AutoStationServicePlugin.LogInfo($"[ASS] {w.id}: conveyor workbench - left to the vanilla conveyor system");
                        }

                        continue;
                    }

                    if (w.CraftableAttachedWorker != null)
                    {
                        continue;
                    }

                    if (loggedStations.Add("served:" + w.id))
                    {
                        AutoStationServicePlugin.LogInfo($"[ASS] {w.id}: serviced by this mod (type={w.CraftableType}, status={w.CraftComponent.Status}, queue={w.CraftComponent.CraftElementsQueue?.Count ?? 0})");
                    }

                    stations.Add(w);
                }
            }

            ServiceOrderRegistry.RemoveMissing(knownStationIds);
            GardenerJobRegistry.RemoveMissing(knownStationIds);

            AutoStationServicePlugin.LogInfo($"[ASS] scan: {autoCrafterCount} auto-crafter station(s), {stations.Count} workerless to service, {excludedStationIds.Count} conveyor workbench(s) excluded");
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
        // zone without one must not run ahead on its own. the garden zone cannot host a
        // zombie_supplier_station at all, so an idle gardener standing there collects instead
        private void CollectCaretakerZones()
        {
            caretakerZones.Clear();
            idleGardenersByZone.Clear();
            gardenerZones.Clear();
            gardenerZoneRefs.Clear();

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
            HashSet<Guid> gardenersOnScene = new HashSet<Guid>();
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

                    string state = zombie.ZombieType == ZombieType.Gardener
                        ? zombie.GardenerState.ToString()
                        : zombie.CaretakerState.ToString();
                    text += $"{zombie.ZombieType} {zombie.UniqueId.Guid} zone={zoneId} state={state}";
                }

                if (zombie.ZombieType == ZombieType.Caretaker && zone != null)
                {
                    caretakerZones.Add(zone.id);
                }
                else if (zombie.ZombieType == ZombieType.Gardener)
                {
                    gardenersOnScene.Add(zombie.UniqueId.Guid);

                    if (zone != null)
                    {
                        gardenerZones.Add(zone.id);
                        gardenerZoneRefs[zone.id] = zone;

                        if (zombie.GardenerState == ZombieWgoData.ZombieGardenerState.OnStation
                            && !GardenerJobRegistry.HasJob(zombie.UniqueId.Guid))
                        {
                            List<ZombieWgoData> idle;
                            if (!idleGardenersByZone.TryGetValue(zone.id, out idle))
                            {
                                idle = new List<ZombieWgoData>();
                                idleGardenersByZone[zone.id] = idle;
                            }

                            idle.Add(zombie);
                        }
                    }
                }
            }

            GardenerJobRegistry.RemoveGardenersGone(gardenersOnScene);

            // the garden orders a gardener could take right now are stamped here, so a waiting
            // station competes with them on age instead of with the vanilla order priority
            foreach (KeyValuePair<string, WorldZoneData> pair in gardenerZoneRefs)
            {
                GardenerJobRegistry.NoteBedWork(pair.Value);
            }

            if (!loggedCaretakers)
            {
                loggedCaretakers = true;
                AutoStationServicePlugin.LogInfo($"[ASS] zombies on scene: {(text.Length > 0 ? text : "none")}");
                AutoStationServicePlugin.LogInfo($"[ASS] caretaker zone(s): {(caretakerZones.Count > 0 ? string.Join(", ", new List<string>(caretakerZones).ToArray()) : "none")}");
                AutoStationServicePlugin.LogInfo($"[ASS] gardener zone(s): {(gardenerZones.Count > 0 ? string.Join(", ", new List<string>(gardenerZones).ToArray()) : "none")}");
            }
        }

        private ZombieWgoData PickIdleGardener(WorldZoneData zone)
        {
            List<ZombieWgoData> idle;
            if (zone == null || !idleGardenersByZone.TryGetValue(zone.id, out idle) || idle.Count == 0)
            {
                return null;
            }

            return idle[0];
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
                AutoStationServicePlugin.LogInfo($"[ASS] revoked mod order PickupOrder {t.ItemId} x{t.Count} (station is a conveyor workbench)");
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

            // only a caretaker standing in this very zone can take an order from it, so a station
            // nobody services must be left untouched instead of running ahead. a zone that cannot
            // host a zombie_supplier_station (the garden) is served by its gardener instead
            if (!caretakerZones.Contains(zone.id))
            {
                if (gardenerZones.Contains(zone.id))
                {
                    ServiceWithGardener(station, cc, inv, zone);
                }
                else if (loggedNoCaretaker.Add(station.UniqueId.Guid))
                {
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: no caretaker and no gardener in zone {zone.id} - left alone, the station will not continue on its own");
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
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: shares storage with {forwardingTarget.id} [{forwardingTarget.UniqueId.Guid}] - products already go there, skipping (fill {FillText(forwardingTarget.Inventory)}){full}");
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
                Item product = FindProduct(inv, required, productIds, ParkedOutputIds(cc));
                if (product != null && StationProxyRegistry.EnsureProxy(station))
                {
                    PickupOrder order = new PickupOrder(station.UniqueId, new Item(product.id, product.Count));
                    zone.PlaceNewOrder(order);
                    ServiceOrderRegistry.Track(order, zone, station.UniqueId);
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: PickupOrder created <- {product.id} x{product.Count}");
                }
                else if (product == null)
                {
                    LogNoProduct(station, inv, required);
                }
            }
        }

        // a station in a zone that cannot host a zombie_supplier_station: its gardener is the only
        // carrier there. the product is produced as soon as the zone has a gardener and the hand-over
        // is queued by age against the garden orders he could take instead, so a product that waited
        // longest is collected first - and the gardener does not run off to a newer garden order
        private void ServiceWithGardener(WgoData station, CraftComponent cc, Inventory inv, WorldZoneData zone)
        {
            if (cc.Status != CraftComponentStatus.ReadyToFinishAutoCraft && cc.Status != CraftComponentStatus.WaitingForWorkerPickUp)
            {
                GardenerJobRegistry.DropRequest(station.UniqueId.Guid);
                return;
            }

            // a station that shares its storage with another wgo moves input and output through that
            // storage on its own, so there is nothing for the gardener to carry
            if (IsStorageForwarded(station, out WgoData forwardingTarget))
            {
                if (loggedForwarded.Add(station.UniqueId.Guid))
                {
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: shares storage with {forwardingTarget.id} [{forwardingTarget.UniqueId.Guid}] - products already go there, skipping");
                }

                GardenerJobRegistry.DropRequest(station.UniqueId.Guid);
                return;
            }

            GardenerJobRegistry.NoteRequest(station.UniqueId, zone.id);

            if (GardenerJobRegistry.HasTooManyFailures(station.UniqueId.Guid))
            {
                GardenerJobRegistry.DropRequest(station.UniqueId.Guid);
                if (loggedUnreachable.Add(station.UniqueId.Guid))
                {
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: the gardener cannot reach this station - leaving its product alone");
                }

                return;
            }

            // older garden work or an older waiting station goes first - this one keeps its place in
            // the queue (and stays collectable by hand while it waits)
            if (!GardenerJobRegistry.IsOldestDueRequest(station.UniqueId.Guid))
            {
                return;
            }

            if (cc.Status == CraftComponentStatus.ReadyToFinishAutoCraft)
            {
                // produce the output but hold the queue, exactly like the caretaker path: the product
                // may only leave the station once a zombie carries it away
                if (!StationProxyRegistry.EnsureProxy(station))
                {
                    return;
                }

                ProduceOutputAndWaitForPickup(station, cc);
            }

            HashSet<string> productIds = CollectProductIds(station, cc);
            HashSet<string> required = CollectRequiredItemIds(cc);
            Item product = FindProduct(inv, required, productIds, ParkedOutputIds(cc));
            if (product == null)
            {
                LogNoProduct(station, inv, required);
                return;
            }

            // the hand-over is real: the gardener now waits at his station for it instead of taking
            // a newer garden order, and keeps waiting while he is still busy with older garden work
            GardenerJobRegistry.RefreshHold(zone.id);

            if (GardenerJobRegistry.HasJobForStation(station.UniqueId.Guid))
            {
                return;
            }

            ZombieWgoData gardener = PickIdleGardener(zone);
            if (gardener != null)
            {
                GardenerJobRegistry.Assign(gardener, station, product);
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
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: the station cannot store its output - left at {cc.Status}");
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

                AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: output produced, waiting for it to be carried away (queue={cc.CraftElementsQueue?.Count ?? 0}, craft inventory ({FillText(craftInventory)}): {NamesText(craftInventory)})");
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
            AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: craft finished, waiting for the product to be carried away (queue={cc.CraftElementsQueue?.Count ?? 0}, craft inventory ({FillText(inv)}): {NamesText(inv)})");
        }

        // an unidentifiable product means no pickup order and a station that waits forever, so name
        // what is lying in the station and what the queue still asks for
        private void LogNoProduct(WgoData station, Inventory inv, HashSet<string> required)
        {
            float now = Time.unscaledTime;
            float last;
            if (noProductLogTime.TryGetValue(station.UniqueId.Guid, out last) && now - last < PICKUP_WAIT_LOG_INTERVAL)
            {
                return;
            }

            noProductLogTime[station.UniqueId.Guid] = now;
            AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: no product recognised - craft inventory ({FillText(inv)}): {NamesText(inv)}, the queue still needs [{string.Join(", ", new List<string>(required).ToArray())}]");
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
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: removed leftover DeliveryOrder {ItemText(order.Item)} (the station feeds itself from the zone storages)");
                    continue;
                }

                if (order is PickupOrder
                    && (order.Item == null || string.IsNullOrEmpty(order.Item.id) || order.Item.Count <= 0
                        || !inv.Data.HasItemQuantityInInventory(order.Item.id, order.Item.Count)))
                {
                    zone.RemoveOrder(order.UniqueId);
                    removedAny = true;
                    AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: removed stale PickupOrder {ItemText(order.Item)} (the station no longer holds it)");
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
                AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: revoked PickupOrder {t.ItemId} x{t.Count} ({reason})");
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
                AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: revoked PickupOrder {t.ItemId} x{t.Count} ({reason})");
            }
        }

        // the ids of the craft that is parked waiting for pickup. its own output is the product, even
        // when a craft further down the queue needs the same item as material - a glass furnace
        // makes glass and later bottles it, so "anything the queue does not need" is not enough
        private static HashSet<string> ParkedOutputIds(CraftComponent cc)
        {
            HashSet<string> result = new HashSet<string>();
            if (cc == null)
            {
                return result;
            }

            CraftElementBase parked = cc.CurrentCraftElement;
            if (parked == null)
            {
                return result;
            }

            AddOutputIds(result, parked.Def);

            List<ItemCount> pre = parked.PreOutputItems;
            if (pre != null)
            {
                for (int i = 0; i < pre.Count; i++)
                {
                    if (pre[i] != null && !string.IsNullOrEmpty(pre[i].itemId))
                    {
                        result.Add(pre[i].itemId);
                    }
                }
            }

            return result;
        }

        private static Item FindProduct(Inventory inv, HashSet<string> required, HashSet<string> productIds, HashSet<string> parkedOutputIds)
        {
            List<Item> items = inv.Data.Inventory;
            if (items == null)
            {
                return null;
            }

            // the parked craft's own output wins over the queue-requirements rule
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it != null && !it.IsEmpty && parkedOutputIds.Contains(it.id) && !IsFuelItem(it.id))
                {
                    return it;
                }
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

                if (IsFuelItem(it.id))
                {
                    continue;
                }

                return it;
            }

            return null;
        }

        private static bool IsFuelItem(string itemId)
        {
            ItemDef def = GameBalance.Me.GetData<ItemDef>(itemId);
            return def != null && def.isFuel;
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
                AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: forwards its storage to {def.refToOtherWgoInventory} but that wgo is not loaded");
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
                AutoStationServicePlugin.LogInfo($"[ASS] diag {station.id} [{station.UniqueId.Guid}]: def={def.GetType().Name} conveyorType={def.conveyorType} isAutoCrafter={def.isAutoCrafter} forwards={def.hasRefToOtherWgoInventory}({def.refToOtherWgoInventory}) zone={zone?.id} caretakersInZone={zone != null && caretakerZones.Contains(zone.id)} status={cc.Status} queue={cc.CraftElementsQueue?.Count ?? 0}");
                AutoStationServicePlugin.LogInfo($"[ASS] diag {station.id}: inventory ({FillText(station.Inventory)}): {NamesText(station.Inventory)}");
                AutoStationServicePlugin.LogInfo($"[ASS] diag {station.id}: craft inventory ({FillText(station.CraftInventory)}): {NamesText(station.CraftInventory)}");

                WgoData forwardingTarget;
                if (IsStorageForwarded(station, out forwardingTarget))
                {
                    AutoStationServicePlugin.LogInfo($"[ASS] diag {station.id}: linked {forwardingTarget.id} [{forwardingTarget.UniqueId.Guid}] inventory ({FillText(forwardingTarget.Inventory)}): {NamesText(forwardingTarget.Inventory)}");
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

                        AutoStationServicePlugin.LogInfo($"[ASS] diag {station.id}: craft {cd.id} auto={cd.isAuto} hidden={cd.isHidden} autoFinish={cd.autoFinishAutoCraft} out=[{OutputIdsText(cd.outputItems)}] need=[{NeedIdsText(cd.needItems)}] needFromWgo=[{NeedIdsText(cd.needItemsFromWgo)}] addStart=[{OutputIdsText(cd.addItemsToWgoOnStart)}] addFinish=[{OutputIdsText(cd.addItemsToWgoOnFinish)}]{extra}");
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
                    AutoStationServicePlugin.LogInfo($"[ASS] diag {station.id}: queued {el.CraftId} started={el.IsStarted} count={el.Count} out=[{outIds}] preOut=[{ItemCountIdsText(el.PreOutputItems)}] need=[{NeedIdsText(el.Requirements)}]");
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

                AutoStationServicePlugin.LogInfo($"[ASS] station {stationId} is gone - revoked PickupOrder {gone[i].ItemId} x{gone[i].Count}");
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

    // a zone that cannot host a zombie_supplier_station never gets a caretaker - the garden is
    // such a zone, since its build desk only offers garden buildings. an idle gardener standing
    // in that zone collects instead: he walks to the station, takes the product out of the parked
    // craft and drops it into the zone storages. the walking is driven from a prefix on
    // GardenerUpdateBehaviour, so nothing in the vanilla gardener behaviour is replaced
    internal static class GardenerJobRegistry
    {
        private enum Phase
        {
            Walking,
            Taking,
            Depositing,
        }

        private sealed class Job
        {
            internal SGuid StationId;
            internal SGuid GardenerId;
            internal Phase Phase;
            internal string ItemId;
            internal float Deadline;
            internal float NextAttempt;
            internal bool Attempted;
            internal bool LoggedBlocked;
        }

        // a station whose product is waiting for a carrier, with the moment it started waiting: the
        // gardener serves the work that waited longest, so this timestamp decides who goes first
        private sealed class Request
        {
            internal SGuid StationId;
            internal string ZoneId;
            internal float ReadyTime;
            internal int Failures;
        }

        private sealed class BedWork
        {
            internal string ZoneId;
            internal float FirstSeen;
        }

        private const float WALK_TIMEOUT = 30f;
        // how close the gardener gets before he takes the product. the station's own dock point sits
        // inside its footprint (a compost pile has its dock point on the pile itself), so the walk is
        // stopped on this ring instead of letting him step into the station
        private const float ARRIVE_DISTANCE = 1f;

        // a walk across a zone is worth a generous timeout: it only has to be short enough to
        // recover from a path that cannot be built
        private static readonly Dictionary<Guid, Job> jobs = new Dictionary<Guid, Job>();
        private static readonly Dictionary<Guid, Request> requests = new Dictionary<Guid, Request>();
        private static readonly Dictionary<Guid, BedWork> bedWork = new Dictionary<Guid, BedWork>();
        private static readonly Dictionary<string, float> holds = new Dictionary<string, float>();

        // how long a station request keeps the gardener at his station waiting for the hand-over.
        // the tick refreshes it every second while the station is the oldest work of the zone, so
        // an expired hold means the tick stopped trying (a station that cannot hand its product out)
        private const float HOLD_SECONDS = 2.5f;

        private static FieldInfo targetField;
        private static MethodInfo moveToTargetMethod;
        private static bool loggedNoFields;

        internal static bool HasJob(Guid gardenerGuid)
        {
            return jobs.ContainsKey(gardenerGuid);
        }

        internal static bool HasJobForStation(Guid stationGuid)
        {
            foreach (KeyValuePair<Guid, Job> pair in jobs)
            {
                if (pair.Value.StationId != null && pair.Value.StationId.Guid == stationGuid)
                {
                    return true;
                }
            }

            return false;
        }

        // a station with a product waiting gets a request, and the request keeps its first timestamp
        // until the product is gone - so a station that waited through a busy gardener keeps its
        // place in the queue instead of starting over
        internal static void NoteRequest(SGuid stationId, string zoneId)
        {
            if (stationId == null || stationId.IsEmpty || zoneId == null || requests.ContainsKey(stationId.Guid))
            {
                return;
            }

            requests[stationId.Guid] = new Request
            {
                StationId = new SGuid(stationId.Guid),
                ZoneId = zoneId,
                ReadyTime = Time.unscaledTime,
            };

            AutoStationServicePlugin.LogInfo($"[ASS] station {stationId.Guid} in zone {zoneId} waits for a carrier");
        }

        internal static void DropRequest(Guid stationGuid)
        {
            requests.Remove(stationGuid);
        }

        // a gardener that could not walk to a station must not keep trying forever: after a few
        // attempts the station is dropped, so the gardener goes back to his garden work
        internal static void NoteFailure(Guid stationGuid)
        {
            Request request;
            if (requests.TryGetValue(stationGuid, out request))
            {
                request.Failures++;
            }
        }

        internal static bool HasTooManyFailures(Guid stationGuid)
        {
            Request request;
            return requests.TryGetValue(stationGuid, out request) && request.Failures >= 3;
        }

        // the garden orders a gardener would take, stamped when they are first seen pending: the mod
        // compares them with its own requests so the gardener always does the oldest work first
        internal static void NoteBedWork(WorldZoneData zone)
        {
            List<OrderBase> orders = WorldZoneOrders.GetAllOrders(zone);
            if (orders == null)
            {
                return;
            }

            HashSet<Guid> present = new HashSet<Guid>();
            for (int i = 0; i < orders.Count; i++)
            {
                OrderBase order = orders[i];
                if (order == null || order.UniqueId == null || !order.ExecutorUniqueId.IsEmpty)
                {
                    continue;
                }

                if (order is PlantOrder plantOrder)
                {
                    string enoughItemId;
                    if (!zone.CanPlantOrderBeTakenOnExecution(plantOrder, out enoughItemId))
                    {
                        continue;
                    }
                }
                else if (!(order is GatherOrder))
                {
                    continue;
                }

                present.Add(order.UniqueId.Guid);

                if (!bedWork.ContainsKey(order.UniqueId.Guid))
                {
                    bedWork[order.UniqueId.Guid] = new BedWork
                    {
                        ZoneId = zone.id,
                        FirstSeen = Time.unscaledTime,
                    };
                }
            }

            List<Guid> gone = null;
            foreach (KeyValuePair<Guid, BedWork> pair in bedWork)
            {
                if (pair.Value.ZoneId == zone.id && !present.Contains(pair.Key))
                {
                    gone = gone ?? new List<Guid>();
                    gone.Add(pair.Key);
                }
            }

            if (gone != null)
            {
                for (int i = 0; i < gone.Count; i++)
                {
                    bedWork.Remove(gone[i]);
                }
            }
        }

        private static float OldestBedWork(string zoneId)
        {
            float oldest = float.MaxValue;
            foreach (KeyValuePair<Guid, BedWork> pair in bedWork)
            {
                if (pair.Value.ZoneId == zoneId && pair.Value.FirstSeen < oldest)
                {
                    oldest = pair.Value.FirstSeen;
                }
            }

            return oldest;
        }

        // true when this station's product is the work that waited longest in its zone - the tick
        // only hands a gardener over to that one, so several waiting stations are served in turn
        internal static bool IsOldestDueRequest(Guid stationGuid)
        {
            Request mine;
            if (!requests.TryGetValue(stationGuid, out mine))
            {
                return false;
            }

            if (OldestBedWork(mine.ZoneId) < mine.ReadyTime)
            {
                return false;
            }

            foreach (KeyValuePair<Guid, Request> pair in requests)
            {
                if (pair.Key != stationGuid && pair.Value.ZoneId == mine.ZoneId && pair.Value.ReadyTime < mine.ReadyTime)
                {
                    return false;
                }
            }

            return true;
        }

        // set by the tick while a station of that zone is the next work: the gardener then waits at
        // his station for the hand-over instead of taking a garden order that is newer
        internal static void RefreshHold(string zoneId)
        {
            if (zoneId != null)
            {
                holds[zoneId] = Time.unscaledTime + HOLD_SECONDS;
            }
        }

        internal static bool HasHold(string zoneId)
        {
            float until;
            return zoneId != null && holds.TryGetValue(zoneId, out until) && Time.unscaledTime <= until;
        }

        // the zombie carrying this station's product right now - the one the tech points belong to
        internal static ZombieWgoData GetCarrierOf(Guid stationGuid)
        {
            List<SGuid> zombieIds = MainGame.ZombieSystemData?.zombieOnSceneWgoIds;
            if (zombieIds == null)
            {
                return null;
            }

            foreach (KeyValuePair<Guid, Job> pair in jobs)
            {
                if (pair.Value.StationId == null || pair.Value.StationId.Guid != stationGuid || pair.Value.Phase == Phase.Walking)
                {
                    continue;
                }

                for (int i = 0; i < zombieIds.Count; i++)
                {
                    ZombieWgoData zombie = MainGame.ZombieSystemData.GetZombie(zombieIds[i]);
                    if (zombie != null && zombie.UniqueId.Guid == pair.Key)
                    {
                        return zombie;
                    }
                }
            }

            return null;
        }

        internal static void Assign(ZombieWgoData gardener, WgoData station, Item product)
        {
            jobs[gardener.UniqueId.Guid] = new Job
            {
                StationId = new SGuid(station.UniqueId.Guid),
                GardenerId = new SGuid(gardener.UniqueId.Guid),
                Phase = Phase.Walking,
                ItemId = product.id,
                Deadline = Time.unscaledTime + WALK_TIMEOUT,
            };

            AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: gardener {gardener.UniqueId.Guid} walks over to collect {product.id} x{product.Count} (station is {Vector3.Distance(gardener.Position, station.Position):F1} units away)");
        }

        internal static void Clear()
        {
            jobs.Clear();
            requests.Clear();
            bedWork.Clear();
            holds.Clear();
        }

        // a job whose station left the scanned world can never be finished
        internal static void RemoveMissing(HashSet<Guid> knownStationGuids)
        {
            List<Job> gone = null;
            foreach (KeyValuePair<Guid, Job> pair in jobs)
            {
                if (pair.Value.StationId != null && knownStationGuids.Contains(pair.Value.StationId.Guid))
                {
                    continue;
                }

                gone = gone ?? new List<Job>();
                gone.Add(pair.Value);
            }

            if (gone != null)
            {
                for (int i = 0; i < gone.Count; i++)
                {
                    Remove(gone[i], "the station is gone");
                }
            }

            List<Guid> staleRequests = null;
            foreach (KeyValuePair<Guid, Request> pair in requests)
            {
                if (!knownStationGuids.Contains(pair.Key))
                {
                    staleRequests = staleRequests ?? new List<Guid>();
                    staleRequests.Add(pair.Key);
                }
            }

            if (staleRequests != null)
            {
                for (int i = 0; i < staleRequests.Count; i++)
                {
                    requests.Remove(staleRequests[i]);
                }
            }
        }

        // a zombie that no longer exists in the scene cannot finish his job, and a job left behind
        // would keep the station unserviced forever
        internal static void RemoveGardenersGone(HashSet<Guid> gardenersOnScene)
        {
            List<Job> gone = null;
            foreach (KeyValuePair<Guid, Job> pair in jobs)
            {
                if (gardenersOnScene.Contains(pair.Key))
                {
                    continue;
                }

                gone = gone ?? new List<Job>();
                gone.Add(pair.Value);
            }

            if (gone == null)
            {
                return;
            }

            for (int i = 0; i < gone.Count; i++)
            {
                Remove(gone[i], "the gardener is gone");
            }
        }

        public static bool GardenerUpdateBehaviourPrefix(ZombieWgoData __instance)
        {
            try
            {
                if (Drive(__instance))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] gardener drive: " + ex);

                Job broken;
                if (__instance != null && jobs.TryGetValue(__instance.UniqueId.Guid, out broken))
                {
                    Remove(broken, "an error while collecting");
                }
            }

            return true;
        }

        // true means this frame belongs to the mod and the vanilla gardener behaviour must not run
        private static bool Drive(ZombieWgoData gardener)
        {
            Job job;
            if (gardener == null)
            {
                return false;
            }

            if (!jobs.TryGetValue(gardener.UniqueId.Guid, out job))
            {
                // a station product is the next work of this zone: stand at the station for the
                // moment it takes the tick to hand it over instead of taking a newer garden order.
                // without this the gardener grabs the next bed order the frame he arrives home and
                // the waiting station is never served
                WorldZoneData idleZone = gardener.WorldZoneData;
                return gardener.GardenerState == ZombieWgoData.ZombieGardenerState.OnStation
                    && HasHold(idleZone != null ? idleZone.id : null);
            }

            WgoData station;
            try
            {
                station = MainGame.WorldData.GetWgoData(job.StationId);
            }
            catch (Exception)
            {
                return false;
            }

            if (station == null || station.isRemovingFromData)
            {
                Remove(job, "the station is gone");
                return false;
            }

            // somebody put a zombie into the station: vanilla takes over
            if (station.CraftableAttachedWorker != null)
            {
                Remove(job, "a worker is attached now");
                return false;
            }

            if (job.Phase == Phase.Walking)
            {
                if (WalkTo(gardener, station, job))
                {
                    job.Phase = Phase.Taking;
                }
                else if (Time.unscaledTime > job.Deadline)
                {
                    Remove(job, $"cannot reach the station (distance {Vector3.Distance(gardener.Position, station.Position):F1})", failure: true);
                    return false;
                }

                return true;
            }

            return job.Phase == Phase.Taking ? Take(job, gardener, station) : Deposit(job, gardener, station);
        }

        // the gardener's own walk machinery: GardenerTryMoveToCurrentTarget paths to whatever
        // gardenerCurrentTargetUniqueId points at. GardenerOnPathSuccess only acts on the GoToStation
        // and GoToGardenBed* states, so the walk runs in a state that means nothing to it. a path
        // that cannot be built leaves the gardener in FailedToFindPath, whose vanilla retry timer
        // only ticks in the behaviour this prefix replaces - hence the mod's own retry interval
        private static bool WalkTo(ZombieWgoData gardener, WgoData station, Job job)
        {
            float distance = Vector3.Distance(gardener.Position, station.Position);

            if (job.Attempted && distance <= ARRIVE_DISTANCE)
            {
                // stop on the ring: the game's own walk target for this station is its dock point,
                // which for a compost pile means standing in the middle of it
                if (gardener.MovementComponent.IsMoving)
                {
                    gardener.MovementComponent.ForceStop();
                }

                AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: gardener {gardener.UniqueId.Guid} reached the station (distance {distance:F1})");
                return true;
            }

            if (Time.unscaledTime < job.NextAttempt)
            {
                return false;
            }

            job.NextAttempt = Time.unscaledTime + 1f;

            if (ResolveMovement())
            {
                gardener.GardenerState = ZombieWgoData.ZombieGardenerState.TeleportSeedsFromMultiInventory;
                targetField.SetValue(gardener, new SGuid(station.UniqueId.Guid));
                moveToTargetMethod.Invoke(gardener, null);
                job.Attempted = true;
            }

            return false;
        }

        // mirrors PickupOrder.ExecuteOrder: the product leaves the station's craft inventory, the
        // parked craft is finished so the queue moves on, and the item is the gardener's to carry
        private static bool Take(Job job, ZombieWgoData gardener, WgoData station)
        {
            Inventory inv = station.CraftableObjectCraftInventory;
            if (inv == null || inv.Data == null)
            {
                Remove(job, "the station has no craft inventory");
                return false;
            }

            int count = inv.Data.GetTotalCountInInventory(job.ItemId);
            if (count <= 0)
            {
                Remove(job, "the product is gone");
                return false;
            }

            List<Item> taken = inv.RemoveItemById(job.ItemId, count);
            if (taken == null || taken.Count == 0)
            {
                Remove(job, "the product could not be taken");
                return false;
            }

            Item carried = null;
            for (int i = 0; i < taken.Count; i++)
            {
                if (taken[i] == null || taken[i].Count <= 0)
                {
                    continue;
                }

                if (carried == null)
                {
                    carried = taken[i];
                }
                else
                {
                    carried.Count += taken[i].Count;
                }
            }

            if (carried == null)
            {
                Remove(job, "the product could not be taken");
                return false;
            }

            gardener.GardenerPortableItem = carried;

            CraftComponent cc = station.CraftComponent;
            if (cc != null && cc.Status == CraftComponentStatus.WaitingForWorkerPickUp)
            {
                cc.TryFinishCurCraft();
                station.DropStoredTechPoints();
            }

            job.Phase = Phase.Depositing;
            job.ItemId = carried.id;
            DropRequest(job.StationId.Guid);
            AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: gardener {gardener.UniqueId.Guid} took {carried.id} x{carried.Count} at the station (distance {Vector3.Distance(gardener.Position, station.Position):F1})");
            return true;
        }

        private static bool Deposit(Job job, ZombieWgoData gardener, WgoData station)
        {
            Item carried = gardener.GardenerPortableItem;
            if (carried == null || carried.IsEmpty || carried.Count <= 0)
            {
                Finish(job, gardener, station, 0);
                return true;
            }

            WorldZoneData zone = station.WorldZoneData;
            if (zone == null)
            {
                Finish(job, gardener, station, 0);
                return true;
            }

            int added = DepositIntoZone(zone, station.UniqueId.Guid, carried, station.Position);
            if (carried.Count <= 0)
            {
                gardener.GardenerPortableItem = Item.Empty;
                Finish(job, gardener, station, added);
                return true;
            }

            if (!job.LoggedBlocked)
            {
                job.LoggedBlocked = true;
                AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: the zone storages cannot take {carried.id} - the gardener holds on to it");
            }

            return true;
        }

        // the vanilla caretaker rule for which storage to use: the nearest one that already holds the
        // item, else the nearest one with room. the pool order alone (MultiInventory.TryAddItem) would
        // fill whatever chest comes first in the zone's wgo list, which is neither near nor tidy
        private static int DepositIntoZone(WorldZoneData zone, Guid stationGuid, Item item, Vector3 from)
        {
            int added = 0;

            while (item.Count > 0)
            {
                WgoData storage = PickStorage(zone, stationGuid, item, from);
                if (storage == null)
                {
                    break;
                }

                int fits = storage.Inventory.Data.CanAddItemCountToInventory(item);
                if (fits > item.Count)
                {
                    fits = item.Count;
                }

                if (fits <= 0)
                {
                    break;
                }

                Item part = Item.Copy(item);
                part.Count = fits;
                if (!storage.Inventory.AddItemToInventory(part))
                {
                    break;
                }

                AutoStationServicePlugin.LogInfo($"[ASS] {item.id} x{fits} goes into {storage.id} [{storage.UniqueId.Guid}] (distance {Vector3.Distance(storage.Position, from):F1} from the station)");

                item.Count -= fits;
                added += fits;
            }

            return added;
        }

        private static WgoData PickStorage(WorldZoneData zone, Guid stationGuid, Item item, Vector3 from)
        {
            List<WgoData> storages = zone != null ? zone.MultiInventoryWgoDatas : null;
            if (storages == null)
            {
                return null;
            }

            WgoData holding = null;
            float holdingDistance = float.MaxValue;
            WgoData free = null;
            float freeDistance = float.MaxValue;

            for (int i = 0; i < storages.Count; i++)
            {
                WgoData storage = storages[i];
                if (storage == null || storage.UniqueId == null || storage.UniqueId.Guid == stationGuid
                    || storage.Inventory == null || storage.Inventory.Data == null
                    || !storage.Inventory.CanAddItemToInventory(item))
                {
                    continue;
                }

                float distance = Vector3.Distance(storage.Position, from);
                if (storage.Inventory.Data.HasItemByItemId(item.id, 1))
                {
                    if (distance < holdingDistance)
                    {
                        holdingDistance = distance;
                        holding = storage;
                    }
                }
                else if (distance < freeDistance)
                {
                    freeDistance = distance;
                    free = storage;
                }
            }

            return holding ?? free;
        }

        private static void Finish(Job job, ZombieWgoData gardener, WgoData station, int delivered)
        {
            jobs.Remove(job.GardenerId.Guid);

            if (delivered > 0)
            {
                AutoStationServicePlugin.LogInfo($"[ASS] {station.id}: gardener delivered {job.ItemId} x{delivered} to the zone storage");
            }

            // back to the vanilla gardener: he walks home or takes the next garden order
            gardener.GardenerState = ZombieWgoData.ZombieGardenerState.OnStation;
        }

        private static void Remove(Job job, string reason, bool failure = false)
        {
            jobs.Remove(job.GardenerId.Guid);

            if (failure && job.StationId != null)
            {
                NoteFailure(job.StationId.Guid);
            }

            AutoStationServicePlugin.LogInfo($"[ASS] gardener {job.GardenerId.Guid} stopped collecting ({reason})");

            ZombieWgoData gardener = FindZombie(job.GardenerId.Guid);
            if (gardener == null)
            {
                return;
            }

            try
            {
                // whatever he still carries must not stay in his hands: vanilla gardeners only ever
                // deposit seeds and crops, so a product left there would be lost for the player
                Item carried = gardener.GardenerPortableItem;
                if (carried != null && !carried.IsEmpty && carried.Count > 0)
                {
                    WorldZoneData zone = gardener.WorldZoneData;
                    if (zone != null)
                    {
                        Guid source = job.StationId != null ? job.StationId.Guid : Guid.Empty;
                        DepositIntoZone(zone, source, carried, gardener.Position);
                    }

                    if (carried.Count <= 0)
                    {
                        gardener.GardenerPortableItem = Item.Empty;
                    }
                }

                gardener.GardenerState = ZombieWgoData.ZombieGardenerState.OnStation;
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] gardener cleanup: " + ex);
            }
        }

        private static ZombieWgoData FindZombie(Guid guid)
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

        private static bool ResolveMovement()
        {
            if (targetField != null && moveToTargetMethod != null)
            {
                return true;
            }

            targetField = AccessTools.Field(typeof(ZombieWgoData), "gardenerCurrentTargetUniqueId");
            moveToTargetMethod = AccessTools.Method(typeof(ZombieWgoData), "GardenerTryMoveToCurrentTarget");

            if (targetField == null || moveToTargetMethod == null)
            {
                if (!loggedNoFields)
                {
                    loggedNoFields = true;
                    AutoStationServicePlugin.Log?.LogError("[ASS] ZombieWgoData gardener movement not found - gardeners cannot walk to a station");
                }

                return false;
            }

            return true;
        }
    }

    internal static class WorldZoneOrders
    {
        private static FieldInfo ordersField;

        internal static List<OrderBase> GetOrders(WorldZoneData zone, SGuid targetUniqueId)
        {
            List<OrderBase> orders = ReadOrders(zone);
            if (orders == null)
            {
                return null;
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

        internal static List<OrderBase> GetAllOrders(WorldZoneData zone)
        {
            return ReadOrders(zone);
        }

        private static List<OrderBase> ReadOrders(WorldZoneData zone)
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

            if (zone == null)
            {
                return null;
            }

            List<OrderBase> orders = ordersField.GetValue(zone) as List<OrderBase>;
            return orders ?? new List<OrderBase>();
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
                AutoStationServicePlugin.LogInfo("[ASS] world changed - proxy zombies reset");
            }

            return true;
        }

        internal static void Remove(Guid stationGuid)
        {
            if (proxies.Remove(stationGuid))
            {
                AutoStationServicePlugin.LogInfo($"[ASS] proxy zombie removed for station {stationGuid}");
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
                AutoStationServicePlugin.LogInfo($"[ASS] proxy zombie registered for station {station.id} ({key})");
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

    // an auto craft stores its tech points in the station's game res (WgoData.OnCraftEnd) and they
    // are turned into orbs on the ground when the product is handed over. for a workerless station
    // that hand-over is the caretaker's pickup, so the points can go to the caretaker instead -
    // the zombie talent currency a crafter zombie earns for its own crafts
    internal static class CaretakerTechPoints
    {
        private const string TECH_RED = "game_res_tech_red";
        private const string TECH_GREEN = "game_res_tech_green";
        private const string TECH_BLUE = "game_res_tech_blue";

        public static bool DropStoredTechPointsPrefix(WgoData __instance)
        {
            try
            {
                ConfigEntry<bool> setting = AutoStationServicePlugin.CaretakerTakesTechPoints;
                if (__instance == null || setting == null || !setting.Value)
                {
                    return true;
                }

                // a station with a worker earns the points the vanilla way (the worker takes them)
                if (__instance.CraftableAttachedWorker != null)
                {
                    return true;
                }

                ZombieWgoData carrier = FindCaretakerCarryingFrom(__instance);
                if (carrier == null)
                {
                    return true;
                }

                int red = __instance.GetGameResInt(TECH_RED);
                int green = __instance.GetGameResInt(TECH_GREEN);
                int blue = __instance.GetGameResInt(TECH_BLUE);
                if (red + green + blue <= 0)
                {
                    return true;
                }

                carrier.DoTechPointsReward(__instance, red, green, blue);
                __instance.SetGameRes(TECH_RED, 0);
                __instance.SetGameRes(TECH_GREEN, 0);
                __instance.SetGameRes(TECH_BLUE, 0);

                AutoStationServicePlugin.LogInfo($"[ASS] {__instance.id}: {carrier.ZombieType} {carrier.UniqueId.Guid} took the tech points (red {red}, green {green}, blue {blue})");
                return false;
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] caretaker tech points: " + ex);
            }

            return true;
        }

        // the zombie that is carrying this station's product away right now - a caretaker executing the
        // station's pickup order, or a gardener this mod sent to collect (a zone without a caretaker).
        // this is the only moment the method is reached with no worker on the station
        private static ZombieWgoData FindCaretakerCarryingFrom(WgoData station)
        {
            MainGame game = MainGame.Instance;
            if (game == null || game.GameSave == null)
            {
                return null;
            }

            ZombieWgoData gardener = GardenerJobRegistry.GetCarrierOf(station.UniqueId.Guid);
            if (gardener != null)
            {
                return gardener;
            }

            WorldZoneData zone = station.WorldZoneData;
            if (zone == null)
            {
                return null;
            }

            List<OrderBase> stationOrders = WorldZoneOrders.GetOrders(zone, station.UniqueId);
            if (stationOrders == null || stationOrders.Count == 0)
            {
                return null;
            }

            List<SGuid> zombieIds = MainGame.ZombieSystemData?.zombieOnSceneWgoIds;
            if (zombieIds == null)
            {
                return null;
            }

            for (int i = 0; i < zombieIds.Count; i++)
            {
                ZombieWgoData zombie = MainGame.ZombieSystemData.GetZombie(zombieIds[i]);
                if (zombie == null || zombie.ZombieType != ZombieType.Caretaker)
                {
                    continue;
                }

                SGuid executing = CaretakerOrderGuard.GetExecutingOrder(zombie);
                if (executing == null || executing.IsEmpty)
                {
                    continue;
                }

                for (int o = 0; o < stationOrders.Count; o++)
                {
                    OrderBase order = stationOrders[o];
                    if (order != null && order.UniqueId.Guid == executing.Guid)
                    {
                        return zombie;
                    }
                }
            }

            return null;
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
                    AutoStationServicePlugin.LogInfo($"[ASS] caretaker {__instance.UniqueId.Guid}: order {orderId.Guid} is not in zone {zone.id} anymore - looking for another one");
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

                AutoStationServicePlugin.LogInfo($"[ASS] caretaker {__instance.UniqueId.Guid}: order {order.UniqueId.Guid} points at {order.TargetWgoUniqueId.Guid}, which is neither a zombie nor a station this mod serves - order dropped");
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

        internal static SGuid GetExecutingOrder(ZombieWgoData caretaker)
        {
            if (caretaker == null || !Resolve())
            {
                return null;
            }

            return executingOrderField.GetValue(caretaker) as SGuid;
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
