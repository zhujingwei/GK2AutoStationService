using System;
using System.Collections.Generic;
using UnityEngine;

namespace GK2AutoStationService
{
    // Picking the chest for an item and putting it in, shared by the zone drop scan and the
    // caretaker's collecting errand so both always agree on where an item would go.
    internal static class StorageDeposit
    {
        // only chests count: a work station (millstone, oven, barrel) also owns an inventory, and
        // putting a loose item into one of those feeds it to the craft instead of storing it.
        // ChestInteractionHandler covers chests, the garden bag storages and the wine store.
        internal static WgoData PickChest(WorldZoneData zone, Item item, Vector3 from, HashSet<Guid> exclude, out int nonChestAccepts)
        {
            nonChestAccepts = 0;

            List<SGuid> wgos = zone != null ? zone.wgoDataList : null;
            if (wgos == null || item == null)
            {
                return null;
            }

            WgoData holding = null;
            float holdingDistance = float.MaxValue;
            WgoData free = null;
            float freeDistance = float.MaxValue;

            for (int i = 0; i < wgos.Count; i++)
            {
                WgoData storage;
                try
                {
                    storage = MainGame.WorldData.GetWgoData(wgos[i]);
                }
                catch (Exception)
                {
                    continue;
                }

                if (storage == null || storage.Definition == null || storage.Inventory == null || storage.Inventory.Data == null)
                {
                    continue;
                }

                if (exclude != null && storage.UniqueId != null && exclude.Contains(storage.UniqueId.Guid))
                {
                    continue;
                }

                if (storage.Inventory.Data.CanAddItemCountToInventory(item) <= 0)
                {
                    continue;
                }

                if (storage.Definition.interactionType != WGODef.InteractionType.Chest)
                {
                    nonChestAccepts++;
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

        // puts as much of the item into the chest as fits and reduces item.Count by that amount
        internal static int TryDeposit(WgoData storage, Item item)
        {
            if (storage == null || item == null || item.Count <= 0 || storage.Inventory == null || storage.Inventory.Data == null)
            {
                return 0;
            }

            int fits = storage.Inventory.Data.CanAddItemCountToInventory(item);
            if (fits > item.Count)
            {
                fits = item.Count;
            }

            if (fits <= 0)
            {
                return 0;
            }

            Item part = Item.Copy(item);
            part.Count = fits;
            if (!storage.Inventory.AddItemToInventory(part))
            {
                return 0;
            }

            item.Count -= fits;
            return fits;
        }

        // the per-item limit for one carried stack: CaretakerPortableItem is a single Item, so a
        // trip can only move one stack of one item id
        internal static int StackLimit(string itemId)
        {
            ItemDef def = GameBalance.Me.GetData<ItemDef>(itemId);
            int stack = def != null ? def.stackCount : 1;
            return stack > 0 ? stack : 1;
        }

        internal static string ShortGuid(Guid guid)
        {
            string text = guid.ToString();
            return text.Length >= 8 ? text.Substring(0, 8) : text;
        }

        // puts an item back on the ground a zombie is standing on - the fallback when nothing can
        // take it, so that a carried item never stays in a zombie's hands
        internal static void PutOnGround(WgoData carrier, Item item)
        {
            if (carrier == null || item == null || item.Count <= 0)
            {
                return;
            }

            try
            {
                WorldZoneData zone = carrier.WorldZoneData;
                GameSceneData scene = zone != null ? MainGame.WorldData.GetGameSceneDataById(zone.gameSceneId) : null;
                if (scene != null)
                {
                    scene.AddDrop(item, carrier.Position);
                }
                else
                {
                    MainGame.Instance.dropSystem.DropItem(item, carrier.WorldId, carrier.Position);
                }
            }
            catch (Exception ex)
            {
                AutoStationServicePlugin.Log?.LogError("[ASS] putting an item back on the ground: " + ex);
            }
        }
    }
}