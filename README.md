# Auto Station Service (Graveyard Keeper 2)

Caretaker zombies pick up the finished products from **auto-crafting stations that have no zombie assigned** (furnace, distillation cube, etc.), so those stations keep working instead of filling up with output nobody collects.

搬运工僵尸会去**没插僵尸的自动工作站**（熔炉、蒸馏立方体等）取走成品，让工作站能继续生产，而不是被成品堵死。

- Plugin GUID: `com.gk2mod.autostationservice`
- Version: 1.4.0
- Requires: BepInEx 5.4.x (x64) for Graveyard Keeper 2
- Optional: [GK2 Mod Framework](https://www.nexusmods.com/graveyardkeeper2/mods/42) — adds this mod's setting to its Mods menu
- Single file: `BepInEx/plugins/GK2AutoStationService.dll` (plus a translation file under the framework's folder, see below)

---

## What it does / 功能

In vanilla, an auto-crafter with no worker attached has no way to hand its output over: the craft either parks forever waiting for someone to finish it, or the product piles up inside the station until the station's storage is full (and then items start dropping on the ground). Nothing moves the products out.

原版里，无人自动站没有把手头成品交出去的方式：合成要么一直停在「等待完成」，要么产物堆在站内直到站内仓满（随后掉在地上），没人把产物搬走。

This mod closes that gap with the vanilla worker model:

本 mod 用原版「有工人」的那套语义补上这一段：

1. The station produces its output when the craft finishes.
2. The craft parks in `WaitingForWorkerPickUp` — exactly the state a station with a crafter zombie would be in — and the queue does **not** advance.
3. A caretaker accepts the pickup order, walks over, takes the product to the zone storage, and only then does the station continue with the next queued craft.

即：产物正常产出 → 合成停在「等待取货」状态（和插了僵尸工人时一样），队列**不**推进 → 搬运工接单过去取走、放进本区仓库 → 之后工作站才继续下一件。

### Details / 细节

- **It never does the work for you.** The mod does not fast-forward crafts. Nothing happens at a station until a caretaker is available to carry the product away, so the station can never outrun the zombie that serves it.
- **Zone-bound.** Orders can only be taken by a caretaker standing in the same zone, so a station in a zone without a caretaker is left completely alone (it also will not produce output on its own). Put a caretaker in that zone to have the station served.
- **No material delivery needed.** A workerless station already pulls ingredients straight out of the zone's storages by itself (that is the vanilla multi-inventory for work objects), so the caretaker only carries products out. The mod does not create delivery orders.
- **Stations with an attached zombie are ignored** — those already work the vanilla way.
- **Conveyor workbenches are ignored** — they run their own loop and push products to their output cell.
- **Stations that share storage with another object** (workbench placed on top of a chest) are skipped: input and output already go through that shared storage.
- **One setting** (see below); no keybinds, the mod is active as soon as the plugin loads.

### The tech points setting / 经验设置

An auto craft stores its tech points in the station, and they are handed out when the product leaves the station. With a caretaker that means the zombie carrying the product away — and by default it takes the points with it, the same way a crafter zombie collects them for its own crafts (zombie tech points are the currency for that zombie's talents). Turn the setting off and the points drop on the ground as orbs for the player to collect instead, which is what a workerless station does in vanilla.

自动合成的经验（tech points）会先存在工作站里，产物离开工作站时才结算。无人站的产物是搬运工搬走的，所以默认由**搬运工吃掉**这些经验——和插了僵尸工人时一样，经验进搬运工自己的口袋（僵尸天赋的货币）。把设置关掉则恢复原版行为：经验变成地上的红/绿/蓝球，玩家自己过去捡。

- Setting: `Caretaker takes the tech points` (default **on**)
- With [GK2 Mod Framework](https://www.nexusmods.com/graveyardkeeper2/mods/42) installed: open the Mods menu and use the toggle on this mod's page — it applies immediately.
- Without it: edit `BepInEx/config/com.gk2mod.autostationservice.cfg` and restart the game. The mod works fine without the framework; only the UI is missing.
- Language: the framework looks the mod's texts up in `BepInEx/plugins/GK2.Framework/Localization/com.gk2mod.autostationservice/<game language>.json`; the archive carries `zh.json` for Chinese, and any other language can be added by dropping in a translated file (English is used when there is none). The texts are read at startup, so change the game language first and then restart.
- Log line when a caretaker takes them: `<station>: caretaker <guid> took the tech points (red r, green g, blue b)`.

多语言：设置文本由 GK2 Mod Framework 按游戏语言从 `BepInEx/plugins/GK2.Framework/Localization/com.gk2mod.autostationservice/<语言>.json` 读取，压缩包里带了简体中文 `zh.json`；想加别的语言，复制一份翻译即可（没有对应文件时显示英文）。文本只在启动时读一次，**改了游戏语言要重启**才会显示新语言。

## Installation / 安装

1. Install **BepInEx 5.4.x (x64)** into the game folder (Steam → Graveyard Keeper 2 → right click → Manage → Browse local files) if you have not already. Run the game once so that `BepInEx/plugins` is created.
2. Drop `GK2AutoStationService.dll` into `BepInEx/plugins/`.
3. Start the game.

The archive also contains `BepInEx/plugins/GK2.Framework/Localization/com.gk2mod.autostationservice/zh.json` — unpacking it gives Chinese labels in the framework's Mods menu. Copying only the DLL works as well, the setting then shows in English.

Vortex: install the archive as-is; it contains the `BepInEx/plugins/` folder structure, so Vortex deploys it to the right place.

中文：装好 BepInEx 5.4 x64，把 `GK2AutoStationService.dll` 放进 `BepInEx/plugins/` 即可；Vortex 可直接安装压缩包。

## How to use / 怎么用

1. Build the auto station (e.g. a furnace) and **do not** put a zombie into it.
2. Queue up the crafts you want (or let it be queued by whatever you normally do).
3. Make sure there is a **caretaker zombie** in the same zone as the station.
4. The station works, the caretaker comes for the product, and the queue continues on its own.

Notes / 注意：

- A station in a zone without a caretaker stays untouched on purpose. That is the intended behaviour, not a bug.
- The caretaker uses the vanilla order system, so it will still do its other jobs; a busy caretaker may take a while to reach the station.
- Before producing, the station must pass the same vanilla check it uses to decide whether it can store the product (otherwise the item would drop on the ground). If it fails, the mod leaves the craft alone and logs `the station cannot store its output`.

## Uninstalling / 卸载

Delete `BepInEx/plugins/GK2AutoStationService.dll`.

Before you remove it, make sure **no station is waiting for pickup** — the log line `<station>: craft finished, waiting for a caretaker to pick up the product` (printed every 60 s) tells you. This mod hands the caretaker an order that targets the station, and that target is only resolvable while the mod is running.

If you already uninstalled with an order pending: put the DLL back, load the save, wait until the caretaker has carried the product away (the log line above stops appearing), save, and then remove the mod. Reinstalling 1.3.2 or newer also works — it deletes such leftover orders itself and logs `removed stale PickupOrder ...`.

删 DLL 即可卸载。但**卸载前请确认没有站处于「等待取货」状态**（日志里每 60s 一条的 `craft finished, waiting for a caretaker to pick up the product` 就是这个状态的标志）：本 mod 生成的取货订单目标只有在 mod 运行时才能被解析。若已经卸载但订单还在，把 DLL 放回去读一次档、等搬运工把产品取走并存档，再移除；装回 1.3.2 或更新版本也可以——它会自己删掉这类残留订单并在日志里写 `removed stale PickupOrder ...`。

## Troubleshooting / 排查

Log file: `BepInEx/LogOutput.log` — everything from this mod is prefixed with `[ASS]`.

Useful lines:

| Line | Meaning |
| --- | --- |
| `scan: N auto-crafter station(s), M workerless to service, K conveyor workbench(s) excluded` | How many stations the mod is looking at |
| `zombies on scene: <type> <guid> zone=<zone> state=<state> \| ...` | All zombies currently loaded, with the zone they belong to |
| `caretaker zone(s): <zone>[, <zone>]` | Zones that have a caretaker — only these can be served |
| `<station>: no caretaker in zone <zone> - left alone, the station will not continue on its own` | Expected when the zone has no caretaker |
| `<station>: output produced, waiting for a caretaker to take it` | Product made, waiting for pickup |
| `<station>: craft finished, waiting for a caretaker to pick up the product` | Repeats every 60 s while the product sits in the station |
| `<station>: PickupOrder created <- <item> xN` | A pickup order was created for the caretaker |
| `<station>: shares storage with <id> [...] - products already go there, skipping` | The station forwards its storage, so it is skipped |
| `<station>: the station cannot store its output - left at ...` | The station's storage is full; nothing was produced |
| `<station>: revoked PickupOrder ...` | A stale order of this mod was cleaned up |
| `<station>: caretaker <guid> took the tech points (red r, green g, blue b)` | The caretaker collected the craft's tech points instead of leaving them on the ground |
| `<station>: removed stale PickupOrder ...` | A leftover pickup order (item no longer in the station) was deleted on load |
| `<station>: removed leftover DeliveryOrder ...` | A 1.2.x delivery order was deleted (stations feed themselves) |
| `caretaker <guid>: order <guid> points at <guid> ... - order dropped` | A caretaker found an order whose target no longer exists; it was dropped instead of crashing the caretaker |

If a station never produces anything, first check whether its zone appears in `caretaker zone(s):`.

## Compatibility / 兼容性

- Built for Graveyard Keeper 2 (Steam, app id 4358690) with BepInEx 5.4.23.4, Unity 6000.3.x, Mono.
- Uses three Harmony prefixes: `ZombieSystemData.GetZombie` (resolves a serviced station to its stand-in zombie), `ZombieWgoData.CaretakerTryMoveToZombie` (drops an order whose target is gone instead of letting the caretaker crash on it) and `WgoData.DropStoredTechPoints` (hands the station's stored tech points to the caretaker that is carrying the product away). It does not patch crafting or the caretaker state machine.
- [GK2 Mod Framework](https://www.nexusmods.com/graveyardkeeper2/mods/42) (Nexus mod 42) is optional: when installed, the mod registers a page with its setting in the framework's Mods menu. The framework is only a soft dependency — the mod loads and works without it, with the setting in its own config file.
- Load order is irrelevant; no other mod is required.
- Verified alongside BepInEx 5 based mods (framework mods, inventory mods, time-of-day mods).

## Changelog / 更新日志

### 1.4.0
- New setting `Caretaker takes the tech points` (default on): the tech points an auto craft stores in the station go to the caretaker that carries the product away, instead of dropping on the ground for the player to collect. Turn it off for the vanilla ground drops.
- Optional [GK2 Mod Framework](https://www.nexusmods.com/graveyardkeeper2/mods/42) integration: the setting shows up on this mod's page in the framework's Mods menu. Without the framework the setting stays in `BepInEx/config/com.gk2mod.autostationservice.cfg`.

### 1.3.2
- Fixed a `NullReferenceException` a caretaker could throw when it was placed in a zone that still held an order from an older version of the mod (an order resolving to a target that no longer exists). The mod now removes such leftover orders on load, and a caretaker-side guard drops any unresolvable order instead of crashing.
- Leftover delivery orders (1.2.x) at serviced stations are removed: the stations feed themselves, so the caretaker no longer makes pointless trips.
- No more `NullReferenceException` spam at the main menu before a save is loaded.

### 1.3.1
- Removed material delivery orders. Workerless stations feed themselves from the zone storages, so the caretaker only carries products out. This also removes the risk of a delivery filling the station's storage and making the next product disappear.

### 1.3.0
- A station without a caretaker in its zone is now left completely alone (it will not produce output on its own) — the station can no longer run ahead of the zombie that serves it.
- Producing output now uses the vanilla worker semantics (`WaitingForWorkerPickUp`) instead of advancing the queue internally.
- Diagnostics for zone / caretaker matching in the log.

### 1.2.0
- Pickup orders are created even when the craft queue has emptied.
- Product detection restricted to the ids the station's crafts can actually output.
- Stations that share their storage with another object are skipped.
- Stale pickup orders from previous sessions are cleaned up.

### 1.0.0
- Initial version: caretaker pickup (and delivery) orders at workerless auto stations.

## Source / 源码

https://github.com/zhujingwei/GK2AutoStationService

Build with `dotnet build -c Release` (netstandard2.1) against the game's `Assembly-CSharp.dll` / `BepInEx.dll` references — the `<HintPath>` entries in the `.csproj` point to a local Steam install, adjust them for your own. The build also references `BepInEx/plugins/GK2.Framework.dll` (GK2 Mod Framework) for the optional settings integration; install the framework or remove that one `<Reference>` to build without it. `tools/checkdll.ps1` verifies the built DLL's strings; `tools/makezip.ps1` packs the release archive.
