# Nexus 发布文案（Auto Station Service 1.3.1）

下面是可以直接复制粘贴到 Nexus 上传页的内容。上传步骤和检查清单在最下面。

---

## Name（Mod 名称）

```
Auto Station Service
```

## Summary（短简介，约 200 字符）

```
Caretaker zombies pick up the finished products from auto-crafting stations that have no zombie assigned (furnace, distillation cube...), so the station keeps working instead of filling up. Playerless stations no longer run ahead of the caretaker serving them.
```

## Category / Tags

- Category: `Gameplay`（或 `Buildings`，视 Nexus 上的分类而定）
- Tags: `BepInEx`, `Zombies`, `Automation`, `Quality of Life`, `Crafting`
- Requires: `BepInEx 5.4.x (x64)`

## Description（正文，BBCode）

```
[size=5][b]Auto Station Service[/b][/size]

Caretaker zombies pick up the finished products from [b]auto-crafting stations that have no zombie assigned[/b] — furnace, distillation cube and friends — so those stations keep working instead of filling up with output nobody collects.

[line]

[size=4][b]The problem[/b][/size]
In vanilla, an auto-crafter with no worker attached has no way to hand its output over. The craft parks waiting for someone to finish it, or the product simply piles up inside the station until its storage is full — and then items start dropping on the ground. Nothing carries the products out.

[size=4][b]What this mod does[/b][/size]
It gives workerless auto stations the vanilla worker treatment:

[list=1]
[*]The station produces its output when the craft finishes.
[*]The craft parks in "waiting for worker pickup" — the exact state a station with a crafter zombie would be in — and the queue [b]does not[/b] advance.
[*]A caretaker accepts the pickup order, walks over, carries the product to the zone storage, and only then does the station continue with the next queued craft.
[/list]

[b]It never does the work for you.[/b] The mod does not fast-forward crafts. Nothing happens at a station until a caretaker is available to carry the product away, so a station can never outrun the zombie that serves it.

[size=4][b]Details[/b][/size]
[list]
[*][b]Zone-bound.[/b] Orders can only be taken by a caretaker standing in the same zone, so a station in a zone without a caretaker is left completely alone — it will not even produce output on its own. Put a caretaker in that zone to have the station served.
[*][b]No material delivery needed.[/b] A workerless station already pulls ingredients straight out of the zone's storages by itself (that is the vanilla multi-inventory for work objects), so the caretaker only carries products out.
[*][b]Stations with an attached zombie are ignored[/b] — those already work the vanilla way.
[*][b]Conveyor workbenches are ignored[/b] — they run their own loop and push products to their output cell.
[*][b]Stations that share storage with another object[/b] (workbench placed on top of a chest) are skipped: input and output already go through that shared storage.
[*]No configuration file, no keybinds. The mod is active as soon as the plugin loads.
[/list]

[size=4][b]Installation[/b][/size]
[list=1]
[*]Install [b]BepInEx 5.4.x (x64)[/b] into the game folder if you have not already, and run the game once so that the plugins folder is created.
[*]Drop [b]GK2AutoStationService.dll[/b] into [b]BepInEx/plugins/[/b].
[*]Start the game. Mod manager (Vortex) install also works — the archive already contains the BepInEx/plugins folder structure.
[/list]

[size=4][b]How to use[/b][/size]
[list=1]
[*]Build the auto station and [b]do not[/b] put a zombie into it.
[*]Queue the crafts you want.
[*]Make sure a [b]caretaker zombie[/b] is in the same zone as the station.
[*]The station works, the caretaker comes for the product, and the queue continues on its own.
[/list]
A station in a zone without a caretaker stays untouched on purpose — that is the intended behaviour, not a bug.

[size=4][b]Troubleshooting[/b][/size]
Everything from this mod is logged to [b]BepInEx/LogOutput.log[/b] with the prefix [b][ASS][/b]. Useful lines:
[list]
[*][i]caretaker zone(s): ...[/i] — which zones can be served at all
[*][i]zombies on scene: ...[/i] — every loaded zombie and the zone it belongs to
[*][i]no caretaker in zone ... - left alone[/i] — expected when a zone has no caretaker
[*][i]PickupOrder created <- item xN[/i] — a pickup order was created
[*][i]craft finished, waiting for a caretaker to pick up the product[/i] — repeats every 60 s while the product sits in the station
[*][i]the station cannot store its output[/i] — the station's storage is full, nothing was produced
[/list]

[size=4][b]Uninstalling[/b][/size]
Delete the DLL. Before you do, make sure no station is waiting for pickup — the log line [i]craft finished, waiting for a caretaker to pick up the product[/i] (printed every 60 s) is the tell. The pickup order this mod creates targets the station itself and is only resolvable while the mod is running, so a pending order in a save would make a caretaker throw errors at that station once the mod is removed. If you already uninstalled with an order pending, put the DLL back, load the save, wait until the caretaker has carried the product away, save, then remove it again.

[size=4][b]Compatibility[/b][/size]
Built for Graveyard Keeper 2 (Steam) on BepInEx 5.4.23.4. It patches only [b]ZombieSystemData.GetZombie[/b] and does not touch the crafting or caretaker state machines, so it can be used with other BepInEx mods. No other mod is required.

[size=4][b]Source[/b][/size]
Source code and build instructions: https://github.com/zhujingwei/GK2AutoStationService — built with netstandard2.1 against the game's assemblies.
```

## Changelog（Nexus 的 Changelog 字段）

```
1.3.1
- Removed material delivery orders. Workerless stations feed themselves from the zone storages, so the caretaker only carries products out. Also removes the risk of a delivery filling the station's storage and the next product disappearing.

1.3.0
- A station without a caretaker in its zone is now left completely alone and will not produce output on its own.
- Producing output now uses the vanilla worker semantics (waiting for worker pickup) instead of advancing the craft queue internally.
- Added zone / caretaker diagnostics to the log.

1.2.0
- Pickup orders are created even when the craft queue has emptied.
- Product detection restricted to the ids the station's crafts can actually output.
- Stations that share their storage with another object are skipped.
- Stale pickup orders from previous sessions are cleaned up.

1.0.0
- Initial public release.
```

---

## 上传前检查清单

1. **主文件**：上传 `dist/GK2AutoStationService-1.3.1.zip`（内部结构 `BepInEx/plugins/GK2AutoStationService.dll`，Vortex 可直接部署）。文件名为 Nexus 上传后的显示名，可改成 `GK2AutoStationService-1.3.1.zip` 之外的任意名，但保持 `.zip`。
2. **游戏页**：Graveyard Keeper 2（Steam app id 4358690，Nexus 上选游戏时不要选成一代 Graveyard Keeper）。
3. **License / permissions**：Nexus 必填。建议 `MIT` 或 `All rights reserved + 允许转载/整合`；请自行决定，仓库里目前没有 LICENSE 文件。
4. **图片**：Nexus 页面首图不是必填但强烈建议（1600×900 或相近 16:9）。需要的话可以让我生成一张标题图/截图版式。
5. **Requirements 字段**：填 `BepInEx 5.4.x (x64)`（Nexus 上若已有 BepInEx 条目可直接链接；没有就写在 Description 里）。
6. **Related mods**：如果之后也发布 Caretaker Priority，两边互相填 Related；本 mod 不依赖它，单独可用。
7. **Source 字段**：填 `https://github.com/zhujingwei/GK2AutoStationService`（Description 里也写了）。
8. **首次发布**：Nexus 新 mod 需要等待审核/首页曝光（普通 mod 立即发布，仅成人/敏感内容才审核）；上传后建议在 Description 里保留排查用的日志关键字，减少问答量。
9. **GitHub 侧可选**：给 `v1.3.1` 打 tag / 建 Release 并附上 `dist/GK2AutoStationService-1.3.1.zip`，方便别人直接从源码页下载（Nexus 仍作为主要下载渠道）。
