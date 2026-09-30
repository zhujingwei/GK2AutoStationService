# Auto Station Service (Graveyard Keeper 2)

Caretaker zombies pick up the finished products from **auto-crafting stations that have no zombie assigned** (furnace, distillation cube, etc.), so those stations keep working instead of filling up with output nobody collects. They also clear loose drops off the ground of their zone and take the tech point orbs lying there.

搬运工僵尸会去**没插僵尸的自动工作站**（熔炉、蒸馏立方体等）取走成品，让工作站能继续生产，而不是被成品堵死；顺手还会把**本区域地上的掉落**捡进仓库、把地上的**科技点球**收掉。

- Plugin GUID: `com.gk2mod.autostationservice`
- Version: 1.6.3
- Requires: BepInEx 5.4.x (x64) for Graveyard Keeper 2
- Optional: [GK2 Mod Framework](https://www.nexusmods.com/graveyardkeeper2/mods/42) — adds this mod's settings to its Mods menu
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

- **It never does the work for you.** The mod does not fast-forward crafts. Nothing happens at a station until a zombie is available to carry the product away, so the station can never outrun the zombie that serves it.
- **Zone-bound.** Orders can only be taken by a caretaker standing in the same zone, so a station in a zone without a caretaker is left alone (it also will not produce output on its own). Put a caretaker in that zone to have the station served.
- **The garden gets a gardener instead.** A garden zone cannot host a zombie at all: the only zombie station the game offers (`zombie_supplier_station`, where caretakers live) cannot be built there. So when a zone has no caretaker but has a gardener zombie, the gardener walks to the station, takes the product and puts it into the zone storages — the same job, done by the zombie that zone actually has.
- **The gardener works by waiting time.** Its carrying jobs are queued against the garden work it could do instead (planting, harvesting): whatever has been waiting longest goes first, and the mod gives it the station hand-over the moment it is free instead of letting the game send it to a newer garden order. It is never interrupted in the middle of a garden task, and after a hand-over it walks home the vanilla way (`GardenerTryMoveToStation`) and takes the next order from there.
- **A gardener someone else is already using is left alone.** Its state cannot show whether it is idle or on its way somewhere — a mod that walks a gardener itself (Auto Harvest Fruit/Honey do) keeps it in `OnStation` the whole time — so the mod also looks at the order in its hand and at whether it is walking, and neither the hand-over nor the loose drop errand takes it from under that other job.
- **Storage choice follows the vanilla caretaker rule.** The nearest storage that already holds the item, otherwise the nearest one with room. The station itself never counts (the product would go straight back in).
- **No material delivery needed.** A workerless station already pulls ingredients straight out of the zone's storages by itself (that is the vanilla multi-inventory for work objects), so the carrier only carries products out. The mod does not create delivery orders.
- **Stations with an attached zombie are ignored** — those already work the vanilla way.
- **Conveyor workbenches are ignored** — they run their own loop and push products to their output cell.
- **Stations that share storage with another object** (workbench placed on top of a chest) are skipped: input and output already go through that shared storage.
- **The ground gets cleaned up too.** A zombie also picks up ordinary items lying on the floor of its own zone — what a full chest spat out, or a product that dropped — and puts them into a storage: the nearest one that already holds that item, otherwise the nearest one with room. A full chest sends it to the next one, and when no chest in the zone can take it the item goes back on the ground and the zombie returns to its station. Big items — the ones the player carries over his head, like logs and supply crates — and anything the game ties to a world object (corpses) are left where they are. A caretaker carries such a pile to the chest; a gardener hands it over from where it stands, the way the game's own garden deposit works, so he never makes a trip for it.
- **Tech point orbs on the ground go to the zombie now.** In vanilla only the player can collect them; here a worker absorbs an orb lying within its reach and a "+1 <orb icon>" pops over its head. A player standing in range always keeps priority, decided by the same rule the game uses for him.
- **Four settings** (see below); no keybinds, the mod is active as soon as the plugin loads.

### Settings / 设置

All four live in the same place: with [GK2 Mod Framework](https://www.nexusmods.com/graveyardkeeper2/mods/42) installed they are on this mod's page in the Mods menu and apply immediately; without it they are in `BepInEx/config/com.gk2mod.autostationservice.cfg` (restart to apply). The mod works fine without the framework; only the UI is missing.

四个设置：装了框架就在 Mods 菜单本 mod 的页面上，改完立即生效；没装就在 `BepInEx/config/com.gk2mod.autostationservice.cfg` 里改（重启生效）。

#### 1. Tech points / 科技点 — `Carrier takes the tech points` (default **on**)

An auto craft stores its tech points in the station, and they are handed out when the product leaves the station. With this mod that means the zombie carrying the product away — caretaker or gardener — and by default it takes the points with it, the same way a crafter zombie collects them for its own crafts (zombie tech points are the currency for that zombie's talents). Turn the setting off and the points drop on the ground as orbs for the player to collect instead, which is what a workerless station does in vanilla.

自动合成的经验（tech points）会先存在工作站里，产物离开工作站时才结算。无人站的产物是搬运工（或园丁）搬走的，所以默认由**搬它的那只僵尸吃掉**这些经验——和插了僵尸工人时一样，经验进它自己的口袋（僵尸天赋的货币）。把设置关掉则恢复原版行为：经验变成地上的红/绿/蓝球，玩家自己过去捡。

- Log line when a zombie takes them: `<station>: <type> <guid> took the tech points (red r, green g, blue b)`.

#### 2. Loose drops / 地面掉落 — `Carrier collects loose drops in its zone` (default **on**)

Items lying on the ground of a zone that has a caretaker or a gardener are picked up and stored, as described above. Only things that are really lying there count: a drop a station has just produced is left until it has come to rest (the game holds every drop back for a moment after it pops out, and refuses it to the player as well until then). Turn it off and the mod leaves the floor alone (the station service keeps working).

开启时（默认）搬运工/园丁会把本区域地上的普通掉落捡进仓库（见上文）。只算**真正躺在地上**的东西：工作站刚产出的掉落会先放着，等它停稳再说（原版对刚弹出的掉落有同样的短暂延迟，延迟里连玩家都捡不起来）。关掉后 mod 不再动地上的东西，工作站服务照旧。

#### 3. Tech point orbs / 科技点球 — `Tech point absorption range` (default **0**)

How close a zombie has to be for an orb on the ground to drift to it. **0** uses exactly the range the game itself pulls orbs to the player with, so a zombie reaches as far as the player does. Any value above 0 is that many units. A player in range always keeps his orbs.

地上的科技点离僵尸多近才会飘过去被吸收。**0（默认）**表示和游戏把科技点吸给玩家的范围完全一致，僵尸够得着的距离和玩家一样；填大于 0 的数值则用指定距离。玩家在附近时科技点始终优先归玩家。

#### 4. Detailed log / 详细日志 — `Detailed log` (default **on**)

Everything the mod does is written to `BepInEx/LogOutput.log` with the `[ASS]` prefix. Turn this off for a quiet log once everything works — station scans, pickup orders, the gardener's errands and the loose drop lines stop being logged. Errors are always logged, whatever this setting says, and the one line naming the loaded version stays as well.

mod 的工作过程（扫描工作站、创建取货单、园丁代搬、捡地面掉落等）都会写进 `BepInEx/LogOutput.log`。一切正常后可以关掉，日志就安静了；**报错信息始终记录**，启动时那行版本信息也保留。

- Note: the texts are read at startup, so a language change in the game options needs a restart before the framework's menu shows the new labels.

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
3. Make sure there is a **caretaker zombie** in the same zone as the station — or, in a zone that cannot host one (the garden), an **idle gardener**.
4. The station works, the zombie comes for the product, and the queue continues on its own.

Loose drops need no setup: whatever ends up on the floor of a zone that has one of these zombies is picked up on its own (see the setting above). Only items that have actually come to rest are taken — a drop a station has just produced is waited out, the same way the game waits before it lets anyone pick it up. Big items — the ones carried over the head (logs, supply crates) — and items tied to a world object are never touched.

地上的掉落不需要额外设置：那个区域里有搬运工或园丁，地上的普通掉落就会被自动捡走（见上面的设置）。只捡**已经落地停稳**的：工作站刚产出的掉落要等一等，和原版放行玩家去捡的时机一致。**大件（原木、补给箱等举在头顶、不能收进背包的物品）**和与世界物件绑定的物品不会动，留在地上给玩家自己处理。

Notes / 注意：

- A station in a zone with neither a caretaker nor a gardener stays untouched on purpose. That is the intended behaviour, not a bug.
- The carrier uses the vanilla order system, so it will still do its other jobs; a busy caretaker may take a while to reach the station, and a gardener finishes the garden task in hand first.
- Before producing, the station must pass the same vanilla check it uses to decide whether it can store the product (otherwise the item would drop on the ground). If it fails, the mod leaves the craft alone and logs `the station cannot store its output`.
- If a gardener cannot reach a station (pathing), it gives up after a few tries and goes back to its garden work; the log says `the gardener cannot reach this station`.

## Uninstalling / 卸载

Delete `BepInEx/plugins/GK2AutoStationService.dll`.

Before you remove it, make sure **no station is waiting for pickup** — the log line `<station>: craft finished, waiting for the product to be carried away` (printed every 60 s) tells you. This mod hands the zombie an order that targets the station, and that target is only resolvable while the mod is running.

If you already uninstalled with an order pending: put the DLL back, load the save, wait until the product has been carried away (the log line above stops appearing), save, and then remove the mod. Reinstalling 1.3.2 or newer also works — it deletes such leftover orders itself and logs `removed stale PickupOrder ...`.

删 DLL 即可卸载。但**卸载前请确认没有站处于「等待取货」状态**（日志里每 60s 一条的 `craft finished, waiting for the product to be carried away` 就是这个状态的标志）：本 mod 生成的取货订单目标只有在 mod 运行时才能被解析。若已经卸载但订单还在，把 DLL 放回去读一次档、等僵尸把产品取走并存档，再移除；装回 1.3.2 或更新版本也可以——它会自己删掉这类残留订单并在日志里写 `removed stale PickupOrder ...`。

## Troubleshooting / 排查

Log file: `BepInEx/LogOutput.log` — everything from this mod is prefixed with `[ASS]`.

Useful lines:

| Line | Meaning |
| --- | --- |
| `scan: N auto-crafter station(s), M workerless to service, K conveyor workbench(s) excluded` | How many stations the mod is looking at |
| `zombies on scene: <type> <guid> zone=<zone> state=<state> \| ...` | All zombies currently loaded, with the zone they belong to |
| `caretaker zone(s): ...` / `gardener zone(s): ...` | Zones with a caretaker, and zones with a gardener |
| `<station>: no caretaker and no gardener in zone <zone> - left alone, the station will not continue on its own` | Expected when the zone has no zombie that could serve it |
| `<station>: station <guid> in zone <zone> waits for a carrier` | The product is queued; the zone's zombie is busy with something else |
| `<station>: output produced, waiting for it to be carried away` | Product made, waiting for pickup |
| `<station>: craft finished, waiting for the product to be carried away` | Repeats every 60 s while the product sits in the station |
| `<station>: PickupOrder created <- <item> xN` | A pickup order was created for a caretaker |
| `<station>: gardener <guid> walks over to collect <item> xN (station is N units away)` | A gardener was sent (zone without a caretaker) |
| `<station>: gardener <guid> reached the station (distance N)` | It got close enough and stops there |
| `<station>: gardener <guid> took <item> xN at the station (distance N)` | The product left the station; the queue moves on |
| `<station>: gardener <guid> delivered <item> xN to the zone storage` | The product reached the zone storages |
| `<station>: gardener <guid> walks home from the station` | The hand-over is done and he walks back to his own station; the next order is picked up there. If this line is missing, the reflection into `GardenerTryMoveToStation` failed (an error follows) and the gardener was released where he stood instead |
| `<item> xN goes into <storage.id> [guid] (distance N from the station)` | Which storage was picked, and how far it is |
| `the gardener cannot reach this station` | Pathing failed a few times; the station is left to the player |
| `<station>: shares storage with <id> [...] - products already go there, skipping` | The station forwards its storage, so it is skipped |
| `<station>: the station cannot store its output - left at ...` | The station's storage is full; nothing was produced |
| `<station>: revoked PickupOrder ...` | A stale order of this mod was cleaned up |
| `<station>: <type> <guid> took the tech points (red r, green g, blue b)` | The carrier collected the craft's tech points instead of leaving them on the ground |
| `<station>: removed stale PickupOrder ...` | A leftover pickup order (item no longer in the station) was deleted on load |
| `<station>: removed leftover DeliveryOrder ...` | A 1.2.x delivery order was deleted (stations feed themselves) |
| `caretaker <guid>: order <guid> points at <guid> ... - order dropped` | A caretaker found an order whose target no longer exists; it was dropped instead of crashing the caretaker |
| `zone drop scan: N loose drop(s) in M zone(s) the mod watches (turn on 'Detailed log' for one line per drop)` | Printed when the number of drops the mod watches changes — the line that says whether the drop feature sees anything at all. A drop still on its way down is not counted |
| `loose drop in <zone>: <item> xN -> <chest> [guid] (distance N)` | With `Detailed log` on: one line per drop, naming the storage it would go into |
| `loose drop in <zone>: <item> xN - big item carried over the head, left for the player` | An item the player holds overhead (a log, a supply crate) — never pocketed or stored |
| `loose drop in <zone>: <item> xN - wgo-linked item, left for the player` | Logs, corpses and other world-linked items are deliberately left alone |
| `<type> <guid>: going for loose drop <item> xN [guid] in <zone> (... walking to (x, z) on the GD point graph\|zone graph)` | A zombie took a drop as its next errand |
| `<type> <guid>: picked up <item> xN (carrying N)` | It reached the drop and took it (as much as fits in one stack) |
| `put <item> xN into <chest> [guid] on the spot` | A gardener stored the item from where he stood — he does not walk to the chest |
| `carrying <item> xN to <chest> [guid]` / `put <item> xN into <chest> [guid]` | A caretaker walks the pile to the chest and puts it in |
| `<chest> cannot take <item> - looking for another chest` | That chest was full; the next one is tried |
| `no chest can take <item> xN in <zone> - putting it back on the ground` | No storage in the zone can take it, so the item is dropped where the zombie stands and the errand ends |
| `<type> <guid>: absorbed tech point orb tech_red` | A worker absorbed an orb lying on the ground; the point went to the zombie |
| `<type> <guid> has no bubble point - the tech point pop is hung over his head instead` | The `+1` pop cannot use the game's own anchor on a zombie, so it is shown above his head |
| `<type> <guid> stopped collecting (<reason>)` | The errand ended: the drop was gone, or it could not be reached |
| `<type> <guid>: the zone graph has no path from here (...) - falling back to the GD point graph` | A zone with no caretaker graph; the walk uses the garden graph instead |

If a station never produces anything, first check whether its zone appears in `caretaker zone(s):` or `gardener zone(s):`.

## Compatibility / 兼容性

- Built for Graveyard Keeper 2 (Steam, app id 4358690) with BepInEx 5.4.23.4, Unity 6000.3.x, Mono.
- Uses five Harmony prefixes: `ZombieSystemData.GetZombie` (resolves a serviced station to its stand-in zombie), `ZombieWgoData.CaretakerTryMoveToZombie` (drops an order whose target is gone instead of letting the caretaker crash on it), `WgoData.DropStoredTechPoints` (hands the station's stored tech points to the zombie carrying the product away), `ZombieWgoData.GardenerUpdateBehaviour` (drives the gardener's errands in a zone without a caretaker) and `ZombieWgoData.CaretakerUpdateBehaviour` (drives the caretaker's loose drop errand). A zombie's own behaviour is only skipped while one of the mod's errands is running, and never while it is busy with an order of the game's. It does not patch crafting, the caretaker state machine or any garden order.
- Tech point orbs are absorbed without using the orb's own `Collect()` — the orb is removed from the scene and the point is written to the zombie, so the value never lands on the player by accident.
- [GK2 Mod Framework](https://www.nexusmods.com/graveyardkeeper2/mods/42) (Nexus mod 42) is optional: when installed, the mod registers a page with its settings in the framework's Mods menu. The framework is only a soft dependency — the mod loads and works without it, with the settings in its own config file.
- Load order is irrelevant; no other mod is required.
- Verified alongside BepInEx 5 based mods (framework mods, inventory mods, time-of-day mods).
- **Auto Harvest Fruit / Auto Harvest Honey (and any mod that drives a gardener itself):** a gardener who holds a garden order, or who is walking, is left to that other job — neither the station hand-over nor the loose drop errand takes him, and the station's short "wait at the station" hold steps aside for him. Both mods can therefore share one gardener. This was not true before 1.6.2, and the two mods could strand each other: this mod took the gardener out of the fruit/honey route mid-walk, and because it did not walk him home afterwards he stayed at the station for good — with the product icon still over his head if he was still carrying one.

  和 Auto Harvest Fruit / Auto Harvest Honey（以及任何自己驱动园丁的 mod）同用：**手里有花园订单、或者正在走路的园丁不会被本 mod 抢走**，工作站那点「原地等交接」的暂留也会为他让路，两个 mod 可以共用一个园丁。1.6.2 之前不是这样——本 mod 会在半路把园丁从果实/蜂蜜路线上夺走，而交接完又不送他回家，他就永远杵在工作站前（手上还拿着东西的话，头顶那个图标也一直挂着）。

## Changelog / 更新日志

### 1.6.3
- **A fresh drop is waited out before it is taken.** A drop is written the moment the item is dropped, but it is not lying there yet: the game holds every fresh drop back for a moment (`DropView` starts a collect delay, and `DropCollector.CanCollectDrop` refuses the drop until it is over — the player cannot pick it up either). The errand picks its targets out of `DropData` and never asked, so it could pull an item out of the air the instant a craft finished. It now uses the same rule. A drop whose view cannot be found counts as landed, so a failed lookup can never leave an item lying around for good.

### 1.6.2
- **The gardener walks home after a hand-over.** The mod used to put him into `OnStation` where he stood — a state the game never produces by itself, whose branch only looks for a new order and never walks. So after taking a product (the peat out of a compost pile, for one) he stood at that station for good: the station had nothing left to hand him, the mod's short "wait at the station" hold kept the vanilla garden orders away from him, and nothing else could reach him either. The hand-over now ends with the game's own `GardenerTryMoveToStation`, exactly like every vanilla garden task does, so he walks back to his station and takes the next order there — a garden bed's or another mod's.
- **A gardener another mod is using is no longer taken.** A mod that drives a gardener itself — Auto Harvest Fruit and Auto Harvest Honey do, hop by hop along their own routes — leaves it in `OnStation` the whole time, which looked exactly like an idle gardener. The mod now also checks the order in his hand and whether he is walking, and the station's hold steps aside for him too. Fixes the freeze that pairing the two mods used to cause (a gardener stranded in front of a composter with the peat icon still over his head).

### 1.6.1
- **Big items are no longer picked up.** Logs, supply crates and every other item the player carries over his head (the game's `ItemSize.Big` category) were being taken by a zombie and stuffed into a chest. The drop errand now skips them, exactly like the game's own ground pickup does, so they stay on the ground for the player. The zone drop scan names them (`big item carried over the head, left for the player`) instead of lumping them in with the world-linked items.

### 1.6.0
- **Loose drops are collected.** A zombie now picks up ordinary items lying on the ground in its own zone and stores them: the nearest storage that already holds that item, otherwise the nearest one with room. A full chest sends it to the next one; when no chest in the zone can take the item, it goes back on the ground and the zombie returns to its station. Big items and anything tied to a world object (logs, corpses) are left where they are. New setting `Carrier collects loose drops in its zone` (default on).
- **A gardener stores what it picks up on the spot**, the way the game's own garden deposit works, so it no longer makes a trip to the chest for it. A caretaker still walks the pile over and puts it in.
- **Tech point orbs on the ground go to the zombie.** A worker absorbs an orb lying within reach and a `+1` with the orb icon appears over its head. New setting `Tech point absorption range` (default 0 = exactly the range the game pulls orbs to the player with; a player in range always keeps priority).
- The setting labels now name both zombies, since either of them does the same jobs in its own zone.

### 1.5.1
- Fixed a station whose craft queue also needs its own output as material — a glass furnace with bottles queued behind the glass. The product detection treated the finished glass as "material the queue still needs", so no pickup order was created and the caretaker never came for it. The product is now taken from the craft that is parked waiting for pickup, whatever the rest of the queue asks for.
- The log now names that case instead of staying silent: `<station>: no product recognised - craft inventory (...), the queue still needs [...]`.

### 1.5.0
- **A zone that has no caretaker now gets served by its gardener.** A garden zone cannot host a zombie station at all, so its workerless auto stations used to stay untouched. An idle gardener now walks over, takes the product and puts it into the zone storages — which storage follows the vanilla caretaker rule (nearest one that already holds the item, otherwise nearest one with room).
- The gardener's errands are ordered by waiting time against the garden work it could do instead, so a station product that waited longest is collected before newer planting/harvesting, and the mod hands it the errand as soon as it is free. It is never interrupted mid-task.
- New setting `Detailed log` (default on): turn it off for a quiet `LogOutput.log`; errors are always logged.
- Tech points: any carrier now takes them, gardener included (was caretaker-only wording in the log).

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
