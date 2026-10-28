# LilliaDemosaic

一个极简的 BepInEx 6（IL2CPP）去马赛克插件：**每帧把马赛克贴片关掉**。

A minimal demosaic plugin for BepInEx 6 (IL2CPP). It disables mosaic decal renderers every frame.

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

本插件的做法：

1. 每 `RefreshFrames` 帧扫描场景里的 `Renderer`；
2. 判定「这个 renderer 是不是马赛克贴片」：
   - 它的**全部**材质名都命中关键字 → 是（独立贴片通常只有一个材质）；
   - 或者 renderer 名字命中关键字且它只有 1 个材质 → 是（材质名不规范的场合）；
3. 在 `LateUpdate`（**晚于**游戏的 `Update`）里把它 `enabled = false`。

因为覆盖发生在游戏开关之后，渲染时一定是关闭的 —— 不需要 Harmony 补丁，也不需要记任何函数地址或字段偏移，换游戏、换版本都不会失效。

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
| `RefreshFrames` | 重新扫描间隔（帧），120 ≈ 2 秒 |
| `Mode` | `Dedicated` / `Materials` / `Both`，见下 |
| `MatchRendererName` | renderer 名也参与匹配（限单材质 renderer） |

### Mode

| 值 | 行为 | 适用 |
|---|---|---|
| `Dedicated` | 只关闭「全部材质都是马赛克」的 renderer | 独立贴片，最安全，**默认** |
| `Materials` | 只把命中的那**一个材质**换成透明 shader | 身体与部位**合批进同一 renderer** 的游戏；若该材质是本体一部分会留洞 |
| `Both` | 两种都做 | 不确定时用，覆盖面最广 |

## 排查

看 `BepInEx\LogOutput.log`：

```
[Info : Lillia Demosaic] Lillia Demosaic loaded (mode=Dedicated)
[Info : Lillia Demosaic] mosaic renderer: Pussy_Mosaic (1 mat)
```

- 有 `mosaic renderer:` → 已命中；
- 没有但有马赛克 → 换 `Mode = Both`，或把游戏里那个对象的材质名加进 `Keywords`；
- 出现 `neutralized material:` → 走的是材质替换（`Materials`/`Both`）。

## 已知不适用的情况

- 马赛克**烤在贴图里**（整块区域纹理本身就是像素化）；
- 马赛克在 shader 里**无条件**渲染，没有开关也没有关键字；
- **全屏后期**马赛克（需要抓 post-process，思路完全不同）；
- 非 IL2CPP 游戏（Mono 游戏请用 BepInEx 5 + 传统插件）。

## 说明

- 仅在个人环境的三款 Unity 2022.3 IL2CPP 作品上验证过，不保证通用；
- 不修改游戏资源文件，只改运行时对象状态，退出即恢复。
