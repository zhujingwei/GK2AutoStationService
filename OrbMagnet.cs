using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GK2AutoStationService
{
    // Tech point orbs are pooled actors that drift to the PLAYER as soon as he is close, and the
    // flying visual spawned on contact is what settles the value on him. A zombie cannot collect them
    // in vanilla, so a worker becomes a second magnet here: an orb in reach stops following the player,
    // drifts to the worker and is absorbed there - the point goes to the zombie and the orb disappears.
    //
    // The player always has priority: while the game is pulling an orb towards him (the player is
    // within the orb's own magnet radius) it is left alone, and an orb the mod already froze is let go
    // again if the player walks up.
    //
    // This is passive on purpose: no errand, no walking, so an orb that cannot be reached never keeps
    // a worker from the piles that are waiting to be carried to a chest.
    internal static class OrbMagnet
    {
        private const float MAGNET_SPEED = 8f;
        private const float COLLECT_DISTANCE = 1.2f;
        // an orb that already lies at the worker's feet (the ones a station spawns while he stands
        // there) still glides for this long, so the point can be seen going into him instead of
        // blinking out of existence
        private const float MIN_FLIGHT_TIME = 0.35f;
        // how long the empty marker of a pop stays alive: the pop follows its transform every frame, and
        // the game force-hides it the moment that transform is gone
        private const float POP_MARKER_LIFETIME = 6f;
        // over the worker's head: the game's own anchor for such a pop is the part's bubble point, and a
        // zombie prefab carries none, so this is just how far above his feet the text starts
        private const float HEAD_HEIGHT = 1.7f;

        private static FieldInfo orbListField;
        private static FieldInfo orbInitializedField;
        private static FieldInfo orbDataField;
        private static FieldInfo orbMagnetRadiusField;
        private static MethodInfo orbUnregisterMethod;
        private static FieldInfo hudDataField;
        private static bool loggedNoOrbApi;
        private static bool loggedNoPopAnchor;
        private static bool loggedNoPop;

        // orbs on their way into a worker: the moment their glide may end
        private static readonly Dictionary<TechPointDrop, float> flightUntil = new Dictionary<TechPointDrop, float>();

        internal static void Tick()
        {
            if (!MayAbsorb())
            {
                return;
            }

            List<TechPointDrop> orbs = OrbList();
            if (orbs == null || orbs.Count == 0)
            {
                return;
            }

            List<SGuid> zombieIds = MainGame.ZombieSystemData?.zombieOnSceneWgoIds;
            if (zombieIds == null)
            {
                return;
            }

            // backwards: absorbing an orb removes it from this very list
            for (int i = orbs.Count - 1; i >= 0; i--)
            {
                try
                {
                    TechPointDrop orb = orbs[i];
                    TechPointDropData data = orb != null ? orb.Data : null;
                    if (data == null)
                    {
                        continue;
                    }

                    // the player always wins: within his magnet range the game pulls the orb towards
                    // him, so it is his to collect - a zombie must not steal it (and an orb the mod
                    // already froze has to be let go again if the player walks up)
                    if (PlayerPulls(orb, data))
                    {
                        Unfreeze(orb);
                        flightUntil.Remove(orb);
                        continue;
                    }

                    ZombieWgoData worker = FindWorkerFor(zombieIds, data.pos, out float distance);
                    if (worker == null || !InReach(orb, data, worker, distance))
                    {
                        flightUntil.Remove(orb);
                        continue;
                    }

                    float until;
                    if (!flightUntil.TryGetValue(orb, out until))
                    {
                        until = Time.unscaledTime + MIN_FLIGHT_TIME;
                        flightUntil[orb] = until;
                    }

                    if (distance > COLLECT_DISTANCE || Time.unscaledTime < until)
                    {
                        Pull(orb, data, worker.Position);
                        continue;
                    }

                    flightUntil.Remove(orb);
                    Absorb(worker, orb, data);
                }
                catch (Exception ex)
                {
                    AutoStationServicePlugin.Log?.LogError("[ASS] tech point magnet: " + ex);
                }
            }
        }

        // how close the worker has to be: a set number of units from the setting, or - with its default
        // of 0 - exactly the rule the game pulls orbs to the player with (squared XZ distance against
        // the serialized magnetRadius)
        private static bool InReach(TechPointDrop orb, TechPointDropData data, ZombieWgoData worker, float distance)
        {
            float configured = AutoStationServicePlugin.DropMagnetRange != null ? AutoStationServicePlugin.DropMagnetRange.Value : 0f;
            if (configured > 0f)
            {
                return distance <= configured;
            }

            try
            {
                Vector3 flat = worker.Position - data.pos;
                flat.y = 0f;
                return flat.sqrMagnitude <= (float)orbMagnetRadiusField.GetValue(orb);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // the game's own magnet rule, mirrored exactly - including its sloppy units: it compares the
        // squared XZ distance against the serialized magnetRadius directly (it squares collectRadius but
        // not magnetRadius), so the effective pull range is sqrt(200) ~ 14 units. Mirroring it keeps
        // "the player is pulling this orb" exactly in sync with what the game does; squaring the radius
        // here would stretch the player's priority to 200 units and leave a zombie nothing to collect.
        private static bool PlayerPulls(TechPointDrop orb, TechPointDropData data)
        {
            if (!Resolve() || MainGame.PlayerController == null || orbMagnetRadiusField == null)
            {
                return true;
            }

            try
            {
                Vector3 player = MainGame.PlayerController.transform.position;
                Vector3 flat = data.pos - player;
                flat.y = 0f;
                return flat.sqrMagnitude <= (float)orbMagnetRadiusField.GetValue(orb);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static ZombieWgoData FindWorkerFor(List<SGuid> zombieIds, Vector3 position, out float distance)
        {
            ZombieWgoData best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < zombieIds.Count; i++)
            {
                ZombieWgoData zombie = MainGame.ZombieSystemData.GetZombie(zombieIds[i]);
                if (zombie == null)
                {
                    continue;
                }

                if (zombie.ZombieType != ZombieType.Caretaker && zombie.ZombieType != ZombieType.Gardener)
                {
                    continue;
                }

                WorldZoneData zone = zombie.WorldZoneData;
                if (zone == null || !ZoneDropErrand.IsInZone(zone, position) || !ZoneDropErrand.MayCollectHere(zombie, zone))
                {
                    continue;
                }

                float candidate = Vector3.Distance(zombie.Position, position);
                if (candidate < bestDistance)
                {
                    bestDistance = candidate;
                    best = zombie;
                }
            }

            distance = bestDistance;
            return best;
        }

        // its own magnet must be stopped first, or it keeps fighting the pull towards the worker (and
        // would rush off to the player the moment he comes closer)
        private static void Pull(TechPointDrop orb, TechPointDropData data, Vector3 target)
        {
            if (!Freeze(orb))
            {
                return;
            }

            Vector3 next = Vector3.MoveTowards(data.pos, target, MAGNET_SPEED * Time.deltaTime);
            data.pos = next;

            try
            {
                orb.transform.position = next;
                Rigidbody body = orb.RigidBody;
                if (body != null)
                {
                    // what is left of its own drift would drag it away from the worker
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.position = next;
                }
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] tech point magnet: pulling an orb: " + ex);
            }
        }

        private static void Absorb(ZombieWgoData worker, TechPointDrop orb, TechPointDropData data)
        {
            string resName = ResNameOf(data);
            int red = resName == "tech_red" ? 1 : 0;
            int green = resName == "tech_green" ? 1 : 0;
            int blue = resName == "tech_blue" ? 1 : 0;

            if (!Despawn(orb, data))
            {
                // could not remove it: let it go back to following the player rather than leaving it
                // inert on the ground
                Unfreeze(orb);
                return;
            }

            if (red + green + blue > 0)
            {
                worker.DoTechPointsReward(worker, red, green, blue);
                // the reward's own event already pops "+1 <orb>" over the wgo it was handed, and it can
                // only do that when that wgo has a bubble point to hang the text on - a worker often has
                // none, so the pop is written here as well. Both go into the same slot of the HUD
                // notification (one per res type), so exactly one pop shows, at the anchor picked here
                ShowPop(worker, resName);
            }

            AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid}: absorbed tech point orb {resName}");
        }

        // the game's own "gained a tech point" pop (UIGameResNotificator): a "+1 <orb icon>" that
        // appears over a Transform it follows every frame and fades out. Vanilla feeds it the wgo that
        // granted the point, which for a worker like a zombie is the object he was working at
        private static void ShowPop(ZombieWgoData worker, string resName)
        {
            try
            {
                HUD hud = LazyUI.Get<HUD>();
                HUDData hudData = hud != null ? HudData(hud) : null;
                UIGameResNotificatorData notificator = hudData != null ? hudData.GameResNotificatiorData : null;
                if (notificator == null)
                {
                    if (!loggedNoPop)
                    {
                        loggedNoPop = true;
                        AutoStationServicePlugin.LogInfo("[ASS] the HUD is not there to pop the tech point over the worker - absorbed silently");
                    }

                    return;
                }

                Transform anchor = BubblePointOf(worker);
                if (anchor == null)
                {
                    // nothing to hang the text on: the pop gets an empty marker over the worker's head to
                    // follow (the orb itself is pooled and about to be handed to the next orb), cleaned up
                    // once the pop has faded
                    Wgo view = ViewOf(worker);
                    GameObject marker = new GameObject("ASS tech point pop");
                    marker.transform.position = worker.Position + Vector3.up * HEAD_HEIGHT;
                    if (view != null)
                    {
                        marker.transform.SetParent(view.transform, worldPositionStays: true);
                    }

                    UnityEngine.Object.Destroy(marker, POP_MARKER_LIFETIME);
                    anchor = marker.transform;

                    if (!loggedNoPopAnchor)
                    {
                        loggedNoPopAnchor = true;
                        AutoStationServicePlugin.LogInfo($"[ASS] {worker.ZombieType} {worker.UniqueId.Guid} has no bubble point - the tech point pop is hung over his head instead");
                    }
                }

                notificator.ResElementsWithAccumulators[resName] =
                    new UIResElementData(resName, UIGameResDisplayingType.AppearOverTargetType, resName, 1f, anchor, isUITarget: false);
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] tech point pop: " + ex);
            }
        }

        private static Transform BubblePointOf(ZombieWgoData worker)
        {
            Wgo view = ViewOf(worker);
            return view != null && view.MainWgoPart != null ? view.MainWgoPart.BubblePoint : null;
        }

        private static Wgo ViewOf(ZombieWgoData worker)
        {
            try
            {
                return GameScene.GetWgoViewGlobal(worker.UniqueId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static HUDData HudData(HUD hud)
        {
            if (hudDataField == null)
            {
                // the widget's data field is protected on the generic base class, so the chain is walked
                // by hand instead of asking reflection for an inherited non-public member
                for (Type type = hud.GetType(); type != null && hudDataField == null; type = type.BaseType)
                {
                    hudDataField = type.GetField("data", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
            }

            return hudDataField != null ? hudDataField.GetValue(hud) as HUDData : null;
        }

        // everything the orb's own Collect() does except spawning the flying visual that hands the value
        // to the player
        private static bool Despawn(TechPointDrop orb, TechPointDropData data)
        {
            if (!Resolve())
            {
                return false;
            }

            try
            {
                orbUnregisterMethod.Invoke(orb, null);
                orbInitializedField.SetValue(orb, false);
                orbDataField.SetValue(orb, null);

                if (data != null)
                {
                    GameSceneData scene = MainGame.WorldData.GetGameSceneDataById(data.worldId);
                    if (scene != null)
                    {
                        scene.RemoveTechPointDrop(data);
                    }
                }

                orb.gameObject.SetActive(false);
                return true;
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] tech point magnet: removing an orb: " + ex);
                return false;
            }
        }

        private static bool Freeze(TechPointDrop orb)
        {
            if (!Resolve())
            {
                return false;
            }

            if (!(bool)orbInitializedField.GetValue(orb))
            {
                return true;
            }

            orbInitializedField.SetValue(orb, false);
            return true;
        }

        private static void Unfreeze(TechPointDrop orb)
        {
            try
            {
                orbInitializedField.SetValue(orb, true);
            }
            catch (Exception)
            {
                // nothing to do about it here
            }
        }

        private static bool MayAbsorb()
        {
            bool drops = AutoStationServicePlugin.CollectZoneDrops == null || AutoStationServicePlugin.CollectZoneDrops.Value;
            bool tech = AutoStationServicePlugin.CaretakerTakesTechPoints != null && AutoStationServicePlugin.CaretakerTakesTechPoints.Value;
            return drops && tech;
        }

        // the orbs of the world that just went away are gone with it, so the flights in progress are
        // dropped as well
        internal static void Clear()
        {
            flightUntil.Clear();
        }

        private static string ResNameOf(TechPointDropData data)
        {
            int index = (int)data.type;
            return index >= 0 && index < TechDef.FlyingReses.Count ? TechDef.FlyingReses[index] : "tech_point";
        }

        private static List<TechPointDrop> OrbList()
        {
            if (!Resolve())
            {
                return null;
            }

            return orbListField.GetValue(null) as List<TechPointDrop>;
        }

        private static bool Resolve()
        {
            if (orbListField != null && orbInitializedField != null && orbDataField != null && orbMagnetRadiusField != null && orbUnregisterMethod != null)
            {
                return true;
            }

            orbListField = AccessTools.Field(typeof(TechPointDrop), "activeDrops");
            orbInitializedField = AccessTools.Field(typeof(TechPointDrop), "isInitialized");
            orbDataField = AccessTools.Field(typeof(TechPointDrop), "data");
            orbMagnetRadiusField = AccessTools.Field(typeof(TechPointDrop), "magnetRadius");
            orbUnregisterMethod = AccessTools.Method(typeof(TechPointDrop), "UnregisterActive");

            if (orbListField == null || orbInitializedField == null || orbDataField == null || orbMagnetRadiusField == null || orbUnregisterMethod == null)
            {
                if (!loggedNoOrbApi)
                {
                    loggedNoOrbApi = true;
                    AutoStationServicePlugin.Log?.LogError("[ASS] TechPointDrop internals not found - tech points on the ground cannot be collected");
                }

                return false;
            }

            return true;
        }
    }
}