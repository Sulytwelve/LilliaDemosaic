# LilliaDemosaic

一个极简的 BepInEx 6（IL2CPP）去马赛克插件：**让马赛克贴片不再被渲染**。

A minimal demosaic plugin for BepInEx 6 (IL2CPP). It stops mosaic decal renderers from being rendered.

---

## 原理

很多 Unity 游戏把马赛克做成**独立的贴片 mesh**（一块盖在身体表面的网格），由一个控制器按穿着/剧情状态开关。
例如本插件的命名来源作品里：

```csharp
// DressController.UpdateMosaicVisibility()
foreach (var r in _mosaicRenderers)
    r.enabled = IsDoingSex || !IsPartsWorn(DressPartsType.Panties);
```

关键点：**游戏每帧都会把它打开**，所以「只关一次」是没用的。

### 做法（v2）

1. **发现**：每帧做一次「脏探测」——读活动场景 `handle` 与 `rootCount`（2 次轻量调用），
   变化了才全量重扫（15 帧限频）；`RefreshFrames` 周期扫描保留为兜底。
   扫描用 `FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)`：
   不排序（比 `FindObjectsOfType` 快），且能发现**未激活**的贴片。
2. **判定**：renderer 的**全部**材质名都命中关键字 → 贴片；或 renderer 名命中且只有 1 个材质 → 贴片。
   判定结果按 `InstanceID` 缓存（名字也有缓存），被动过的对象不重复计算。
3. **执行**：给命中对象置 `Renderer.forceRenderingOff = true` —— 一次置位，之后游戏怎么翻 `enabled` 都画不出来，
   不需要每帧对抗。每 30 帧低频复核一次是否被复位。

全程不需要 Harmony 补丁，也不依赖任何函数地址或字段偏移，换游戏、换版本都不会失效。
若 `forceRenderingOff` 在当前 Unity 上不可用（异常），自动回退到 v1 的做法：
在 `LateUpdate`（晚于游戏的 `Update`）里逐帧 `enabled = false`。

## 安装

1. 安装 [BepInEx 6 (IL2CPP)](https://builds.bepinex.dev/projects/bepinex_be)，需要支持该游戏 Unity 版本的构建
   （例如 Unity 2022.3 需要较新的 be 版本，旧的会报 `Unsupported metadata version ... got 31`）；
2. 启动一次游戏、退出，确认生成了 `BepInEx\interop\`；
3. 把 `LilliaDemosaic.dll` 放进 `BepInEx\plugins\`。

## 编译

```powershell
dotnet build -c Release -p:GameDir="D:\Path\To\Game"
```

或把仓库放到游戏目录里，直接 `dotnet build -c Release`。
产物 `bin\Release\LilliaDemosaic.dll`。

## 配置

`BepInEx\config\lillia.demosaic.cfg`：

```ini
[General]
Enabled = true
Keywords = mosaic/mozaic/mosic/mosiac/mozaik/mosaik/pixelate/censor/모자이크/モザイク
RefreshFrames = 120
Mode = Dedicated
MatchRendererName = true
```

| 键 | 说明 |
|---|---|
| `Keywords` | 名称关键字，`/` 分隔，不区分大小写。新游戏先看日志里命中什么，不够就加 |
| `RefreshFrames` | 兜底重扫间隔（帧），120 ≈ 2 秒 |
| `Mode` | `Dedicated` / `Materials` / `Both`，见下 |
| `MatchRendererName` | renderer 名也参与匹配（限单材质 renderer） |
| `MirrorEnabled` | 额外每帧把 `enabled` 写成 `false`（v1 行为）。默认 `false`，见「设计说明」 |

配置改动即时生效：字符串重解析在回调里做，涉及 Unity 对象的操作会推迟到主线程下一帧执行
（BepInEx 的配置监听可能在线程池线程触发，直接调 Unity API 会抛异常）。

### Mode

| 值 | 行为 | 适用 |
|---|---|---|
| `Dedicated` | 只处理「全部材质都是马赛克」的 renderer | 独立贴片，最安全，**默认** |
| `Materials` | 只把命中的那**一个材质**换成透明 shader | 身体与部位**合批进同一 renderer** 的游戏；若该材质是本体一部分会留洞 |
| `Both` | 两种都做 | 不确定时用，覆盖面最广 |

> `Materials` 会修改材质对象，且在会话内不可逆（`Enabled=false` 只恢复 renderer，不恢复材质）。

## 排查

看 `BepInEx\LogOutput.log`：

```
[Info : Lillia Demosaic] Lillia Demosaic loaded (mode=Dedicated)
[Info : Lillia Demosaic] mosaic renderer: Sphere (1 mat)
[Info : Lillia Demosaic] mosaic renderer: Pu**y_Mosaic (1 mat)
```

- 有 `mosaic renderer:` → 已命中，且**每个对象只打印一次**（换场景 / 新实例才会再打），
  所以「进游戏时出现两三行、之后不再输出」是正常的；
- 没有但有马赛克 → 换 `Mode = Both`，或把游戏里那个对象的材质名加进 `Keywords`；
- 出现 `neutralized material:` → 走的是材质替换（`Materials`/`Both`）；
- 出现 `scan failed:` → 扫描中途异常，插件会保留上一次的目标状态并继续工作，
  把异常内容反馈出来即可。

## 已知不适用的情况

- 马赛克**烤在贴图里**（整块区域纹理本身就是像素化）；
- 马赛克在 shader 里**无条件**渲染，没有开关也没有关键字；
- **全屏后期**马赛克（需要抓 post-process，思路完全不同）；
- 非 IL2CPP 游戏（Mono 游戏请用 BepInEx 5 + 传统插件）。

## 设计说明（取舍）

- **`forceRenderingOff` 不改 `enabled`**：这是 v2 与 v1 唯一的语义级差异。游戏自己的控制器每帧先写后读，
  两条路径下它读到的都是自己的值，没有区别；受影响的只是「帧内晚于 `LateUpdate` 读取 `enabled` 的第三方」——
  v1 会看到 `false`，v2 会看到游戏设定的值。
  若某个游戏确有这类读取者（表现为「马赛克没了但游戏逻辑异常」），把配置里的 `MirrorEnabled` 打开即可：
  该模式下每帧在 `LateUpdate`（游戏 `Update` 之后）额外写一次 `enabled = false`，与 v1 的可观测行为完全一致，
  成本也回到 v1 水平（每帧每目标一次写入），且**只在这一项为 true 时产生**。
  注意不能只写一次：游戏每帧都会把自己的值写回去，一次性写入无法覆盖帧内后续的读取者。
- **判定缓存淘汰**：`_nameCache` 与场景无关（同名同结论），永久保留；`_rendererCache` 每 10 次**兜底**扫描
  清一次（默认 120 帧/次，约 20 秒），用于收窄「游戏运行时替换材质」导致的陈旧判定。脏探测触发的重扫不清缓存——
  那个时刻新对象居多，缓存价值最低。被抵消的只是「跳过重判」的少量 interop 调用，字符串匹配开销仍由名字缓存吸收。
- **`FindObjectsInactive.Include` 的集合扩大**：`FindObjectsByType` 不返回资产与 prefab 本体，也不返回
  `HideFlags.DontSave` 的对象，所以扩大的只是**运行时场景实例**（例如换装菜单里的预览 dummy 角色）。
  这类对象被同样处理，多数情况下反而是期望行为（预览也不该有马赛克），最坏也是中性。

## 更新记录

**v1.3** — 算法优化（实机验证：本插件命名来源作品，Unity 2022.3）

- 发现：每 120 帧无差别盲扫 → 脏探测（场景/根对象变化）+ 兜底周期扫描；
- 扫描：`FindObjectsByType(Include, None)` 取代 `FindObjectsOfType`，能发现未激活贴片；
- 判定：名字缓存 + renderer 判定缓存，匹配改为零分配的大小写不敏感比较；
- 执行：`forceRenderingOff` 一次置位取代逐帧 `enabled` 对抗（不可用时自动回退）；
- 去重键由 native `Pointer` 改为 `InstanceID`（指针可能被复用）；
- 修复：配置回调可能在线程池线程调 Unity API；扫描异常会丢失已关闭对象的状态。

**v1.2** — 新增 `Mode`（`Dedicated`/`Materials`/`Both`）、`MatchRendererName`、日志去重、关键字补常见拼写错误。

**v1.1** — 初版。

## 说明

- 仅在个人环境的 Unity 2022.3 IL2CPP 作品上验证过（当前版本实测于命名来源作品），不保证通用；
- 不修改游戏资源文件，只改运行时对象状态，退出即恢复。
