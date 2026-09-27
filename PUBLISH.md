# Nexus 发布文案（Auto Station Service 1.5.1）

下面是可以直接复制粘贴到 Nexus 上传页的内容。上传步骤和检查清单在最下面。

---

## Name（Mod 名称）

```
Auto Station Service
```

## Summary（短简介，约 200 字符）

```
Caretaker zombies pick up the finished products from auto-crafting stations that have no zombie assigned (furnace, distillation cube...), so the station keeps working instead of filling up. A garden zone, where no caretaker can live, is served by its gardener instead. Two settings: who takes the craft's tech points, and how noisy the log is.
```

## Category / Tags

- Category: `Gameplay`（或 `Buildings`，视 Nexus 上的分类而定）
- Tags: `BepInEx`, `Zombies`, `Automation`, `Quality of Life`, `Crafting`
- Requires: `BepInEx 5.4.x (x64)`
- Optional: `GK2 Mod Framework`（模组菜单里的设置开关；没装也能用，设置写在 cfg 里）

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
[*]A caretaker accepts the pickup order, walks over, carries the product to the zone storage, and only then does the station continue with the next queued craft. In a garden zone, where no caretaker station can be built, an idle [b]gardener[/b] does that job instead.
[/list]

[b]It never does the work for you.[/b] The mod does not fast-forward crafts. Nothing happens at a station until a zombie is available to carry the product away, so a station can never outrun the zombie that serves it.

[size=4][b]Details[/b][/size]
[list]
[*][b]Zone-bound.[/b] Orders can only be taken by a caretaker standing in the same zone, so a station in a zone without a caretaker is left alone — it will not even produce output on its own. Put a caretaker in that zone to have the station served.
[*][b]A garden zone gets its gardener.[/b] The garden cannot host a zombie at all: the zombie station caretakers live in cannot be built there. So when a zone has no caretaker but has a gardener zombie, that gardener walks to the station, takes the product and puts it into the zone storages. It keeps its garden work too — the mod queues both by waiting time, so whatever waited longest goes first, and the gardener is handed the station errand the moment it is free. It is never interrupted in the middle of a garden task.
[*][b]Storage choice follows the vanilla caretaker rule:[/b] the nearest storage that already holds that item, otherwise the nearest one with room.
[*][b]No material delivery needed.[/b] A workerless station already pulls ingredients straight out of the zone's storages by itself (that is the vanilla multi-inventory for work objects), so the carrier only carries products out.
[*][b]Stations with an attached zombie are ignored[/b] — those already work the vanilla way.
[*][b]Conveyor workbenches are ignored[/b] — they run their own loop and push products to their output cell.
[*][b]Stations that share storage with another object[/b] (workbench placed on top of a chest) are skipped: input and output already go through that shared storage.
[*][b]Two settings[/b] (see below), no keybinds. The mod is active as soon as the plugin loads.
[/list]

[size=4][b]Tech points[/b][/size]
An auto craft hands its tech points out when the product leaves the station — which, with no worker assigned, is when the caretaker (or the gardener) carries it away. By default that zombie [b]takes the points with it[/b], exactly like a crafter zombie does for its own crafts (a zombie's tech points are the currency for its talents). Turn the setting off and the points drop on the ground as red/green/blue orbs for the player to collect, which is what a workerless station does in vanilla.

[size=4][b]Detailed log[/b][/size]
Everything the mod does is written to [b]BepInEx/LogOutput.log[/b] with the [b][ASS][/b] prefix. Turn [b]Detailed log[/b] off for a quiet log once everything works — station scans, pickup orders and the gardener's errands stop being logged. Errors are always logged whatever the setting says, and the one startup line naming the loaded version stays as well.

[size=4][b]Settings[/b][/size]
[list]
[*]With [b]GK2 Mod Framework[/b] installed the toggles show up on this mod's page in the Mods menu and apply immediately.
[*]Without it, edit [b]BepInEx/config/com.gk2mod.autostationservice.cfg[/b] and restart the game. The mod works fine without the framework — only the UI is missing.
[*]Menu texts follow the game language: a Chinese translation is included, and other languages can be added by dropping a translated file into [b]BepInEx/plugins/GK2.Framework/Localization/com.gk2mod.autostationservice/[/b] (English is used when there is none). The texts are read at startup, so restart after changing the game language.
[/list]

[size=4][b]Installation[/b][/size]
[list=1]
[*]Install [b]BepInEx 5.4.x (x64)[/b] into the game folder if you have not already, and run the game once so that the plugins folder is created.
[*]Drop [b]GK2AutoStationService.dll[/b] into [b]BepInEx/plugins/[/b].
[*]Start the game. Mod manager (Vortex) install also works — the archive already contains the BepInEx/plugins folder structure (plus the optional Chinese translation for the framework's Mods menu).
[/list]

[size=4][b]How to use[/b][/size]
[list=1]
[*]Build the auto station and [b]do not[/b] put a zombie into it.
[*]Queue the crafts you want.
[*]Make sure a [b]caretaker zombie[/b] is in the same zone as the station — or, in a zone that cannot host one (the garden), an [b]idle gardener[/b].
[*]The station works, the zombie comes for the product, and the queue continues on its own.
[/list]
A station in a zone with neither a caretaker nor a gardener stays untouched on purpose — that is the intended behaviour, not a bug.

[size=4][b]Troubleshooting[/b][/size]
Everything from this mod is logged to [b]BepInEx/LogOutput.log[/b] with the prefix [b][ASS][/b]. Useful lines:
[list]
[*][i]caretaker zone(s): ...[/i] / [i]gardener zone(s): ...[/i] — which zones can be served, and by whom
[*][i]zombies on scene: ...[/i] — every loaded zombie and the zone it belongs to
[*][i]no caretaker and no gardener in zone ... - left alone[/i] — expected when a zone has no zombie that could serve it
[*][i]... waits for a carrier[/i] — the product is queued; the zone's zombie is busy with something else
[*][i]PickupOrder created <- item xN[/i] — a pickup order was created for a caretaker
[*][i]gardener ... walks over to collect item xN (station is N units away)[/i] — a gardener was sent
[*][i]gardener ... took item xN at the station (distance N)[/i] and [i]... delivered item xN to the zone storage[/i] — the errand succeeded
[*][i]item xN goes into <storage> ...[/i] — which storage was picked
[*][i]craft finished, waiting for the product to be carried away[/i] — repeats every 60 s while the product sits in the station
[*][i]the station cannot store its output[/i] — the station's storage is full, nothing was produced
[*][i]the gardener cannot reach this station[/i] — pathing failed a few times, the station is left to the player
[*][i]... took the tech points (red r, green g, blue b)[/i] — the carrier collected the craft's tech points (setting on)
[*][i]GK2 Mod Framework language: ...[/i] — the language the framework menu texts were resolved in
[/list]

[size=4][b]Uninstalling[/b][/size]
Delete the DLL. Before you do, make sure no station is waiting for pickup — the log line [i]craft finished, waiting for the product to be carried away[/i] (printed every 60 s) is the tell. The pickup order this mod creates targets the station itself and is only resolvable while the mod is running, so a pending order in a save would make a caretaker throw errors at that station once the mod is removed. If you already uninstalled with an order pending, put the DLL back, load the save, wait until the product has been carried away, save, then remove it again. Reinstalling 1.3.2 or newer also cleans up such leftover orders.

[size=4][b]Compatibility[/b][/size]
Built for Graveyard Keeper 2 (Steam) on BepInEx 5.4.23.4. It uses four Harmony prefixes — [b]ZombieSystemData.GetZombie[/b], [b]ZombieWgoData.CaretakerTryMoveToZombie[/b], [b]WgoData.DropStoredTechPoints[/b] and [b]ZombieWgoData.GardenerUpdateBehaviour[/b] (only while a gardener errand runs) — and does not touch crafting, the caretaker state machine or the garden orders, so it can be used with other BepInEx mods. No other mod is required; [b]GK2 Mod Framework[/b] is optional and only adds the settings page.

[size=4][b]Source[/b][/size]
Source code and build instructions: https://github.com/zhujingwei/GK2AutoStationService — built with netstandard2.1 against the game's assemblies.
```

## Changelog（Nexus 的 Changelog 字段）

```
1.5.1
- Fixed a station whose craft queue also needs its own output as material (a glass furnace with bottles queued behind the glass): the mod treated the finished glass as material the queue still needed, created no pickup order and the caretaker never came for it. The product is now taken from the craft that is parked waiting for pickup, whatever the rest of the queue asks for.
- The log now names the case where no product can be identified instead of staying silent.

1.5.0
- A zone without a caretaker is now served by its gardener. The garden can't host a zombie station at all, so its workerless auto stations used to sit untouched; an idle gardener now walks over, takes the product and puts it into the zone storages (nearest storage that already holds the item, otherwise nearest with room).
- Gardener errands are ordered by waiting time against the gardener's garden work, so a product that waited longest is collected first; the gardener is never interrupted in the middle of a garden task.
- New setting "Detailed log" (default on): turn it off for a quiet BepInEx log. Errors are always logged.
- The tech points of a pickup can now go to the gardener too, not only to a caretaker.

1.4.0
- New setting (default on): the caretaker that carries a workerless auto station's product away also takes the craft's tech points with it, like a crafter zombie does for its own crafts. Turn it off to keep the vanilla behaviour — the tech points drop on the ground as orbs for the player.
- The setting appears on this mod's page in the Mods menu of GK2 Mod Framework (optional, installed separately). Without the framework the setting lives in BepInEx/config/com.gk2mod.autostationservice.cfg.
- The menu texts can be translated: Chinese is included, other languages are picked up from BepInEx/plugins/GK2.Framework/Localization/com.gk2mod.autostationservice/.

1.3.2
- Fixed a NullReferenceException a caretaker could throw when placed in a zone that still held an order from an older version of the mod. Leftover orders are now removed on load, and a guard drops any unresolvable order instead of crashing the caretaker.
- Leftover delivery orders (1.2.x) at serviced stations are removed.
- No more NullReferenceException spam at the main menu before a save is loaded.

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

1. **主文件**：上传 `dist/GK2AutoStationService-1.5.1.zip`（内部结构：`BepInEx/plugins/GK2AutoStationService.dll` + `BepInEx/plugins/GK2.Framework/Localization/com.gk2mod.autostationservice/zh.json`，Vortex 可直接部署）。文件名为 Nexus 上传后的显示名，可改成 `GK2AutoStationService-1.5.1.zip` 之外的任意名，但保持 `.zip`。
2. **游戏页**：Graveyard Keeper 2（Steam app id 4358690，Nexus 上选游戏时不要选成一代 Graveyard Keeper）。
3. **License / permissions**：Nexus 必填。建议 `MIT` 或 `All rights reserved + 允许转载/整合`；请自行决定，仓库里目前没有 LICENSE 文件。
4. **图片**：Nexus 页面首图不是必填但强烈建议（1600×900 或相近 16:9）。需要的话可以让我生成一张标题图/截图版式。
5. **Requirements 字段**：填 `BepInEx 5.4.x (x64)`（Nexus 上若已有 BepInEx 条目可直接链接；没有就写在 Description 里）。`GK2 Mod Framework` 是可选的，可在 Description 里提及。
6. **Related mods**：如果之后也发布 Caretaker Priority，两边互相填 Related；本 mod 不依赖它，单独可用。
7. **Source 字段**：填 `https://github.com/zhujingwei/GK2AutoStationService`（Description 里也写了）。
8. **首次发布**：Nexus 新 mod 需要等待审核/首页曝光（普通 mod 立即发布，仅成人/敏感内容才审核）；上传后建议在 Description 里保留排查用的日志关键字，减少问答量。
9. **GitHub 侧可选**：给 `v1.5.1` 打 tag / 建 Release 并附上 `dist/GK2AutoStationService-1.5.1.zip`，方便别人直接从源码页下载（Nexus 仍作为主要下载渠道）。
