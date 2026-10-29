# AGENTS.md

面向要**修改本仓库**的人 / 编码 agent。使用编译好的 DLL 的玩家不需要看这个。

## 这是什么

BepInEx 6（IL2CPP）去马赛克插件。运行时扫描场景里的马赛克贴片 renderer，让它不被渲染。
判定与执行都在 `src/DemosaicBehaviour.cs`，配置在 `src/Plugin.cs`。

## 不变量（改动前先读，破坏了就不是这个项目了）

1. **零 Harmony、零 IL2CPP 偏移**。不 hook 游戏方法、不读 `dump.cs` 里的字段/函数地址。
   所有能力只用 Unity 公开 API 实现。这是它能跨游戏、跨版本存活的原因。
2. **Unity API 只在主线程调用**。`SettingChanged` 回调可能来自线程池（BepInEx 监听配置文件），
   所以回调里**只能**记录（字符串引用 + `volatile bool` 标记），处理放到 `Update`。
   同理：两个判定字典与 `_keywords` 数组只在主线程读写。
3. **判定语义固定**：`renderer` 的**全部**材质名命中关键字 → 贴片；
   或 `renderer` 名命中关键字且它**只有 1 个材质** → 贴片。材质数 > 8 直接跳过（身体部件）。
   改这条会引入误伤本体（整块消失/透明洞）的风险。
4. **执行优先级**：`Renderer.forceRenderingOff = true` 一次置位（每 30 帧复核）；
   仅当该 API 抛异常时才回退到 `LateUpdate` 逐帧 `enabled = false`。
   **不要**为了「更彻底」把逐帧写 `enabled` 变成默认行为——那是 v1 的成本模型。
5. **`MirrorEnabled`（默认 false）** 是唯一允许逐帧写 `enabled` 的开关。
   它存在的理由是「第三方在 `LateUpdate` 之后读取 `enabled`」这一可观测性差异，不是渲染问题。
   注意它必须**每帧**写：游戏每帧把自己的值写回去，一次性写入无效。
6. **缓存策略**：`_nameCache` 与场景无关、永久保留；`_rendererCache` 只在**兜底扫描**时
   每 10 次清一次（收窄「运行时换材质」的陈旧窗口）。不要改成每次扫描都清（浪费）或从不清（陈旧）。
7. **日志格式稳定**：`mosaic renderer: <name> (<n> mat)`、`neutralized material: <name>`、
   `scan failed: <msg>`。下游靠它排障，改格式等于破坏兼容。

## 构建

需要目标游戏的 BepInEx `core` 与 `interop` 程序集（`interop` 由 BepInEx 首次运行生成）：

```powershell
dotnet build src/LilliaDemosaic.csproj -c Release -p:GameDir="D:\Path\To\Game"
```

或在游戏目录内直接 `dotnet build src/LilliaDemosaic.csproj -c Release`。
产物 `src/bin/Release/LilliaDemosaic.dll`。

不要引入 NuGet 依赖，也不要改 `csproj` 的引用方式（它刻意只依赖本地 BepInEx 与 interop）。

## 验证

无法在无游戏环境编译时，至少确认 `interop` 里存在这些 API（本仓库用到）：
`Renderer.forceRenderingOff`、`Object.FindObjectsByType<T>`、`FindObjectsInactive`、
`FindObjectsSortMode`、`SceneManager.GetActiveScene()` / `Scene.handle` / `Scene.rootCount`。

实机验证清单：

1. 进游戏 → 日志出现 `Loading [Lillia Demosaic x.y.z]` 与 `mosaic renderer:` 行，画面无马赛克；
2. 运行中修改 `Keywords`（删/加 `mosaic`）→ 立即生效，证明线程池回调 → 主线程消费这条路径通；
3. `Enabled = false` → 恢复；改回 `true` → 再次消失；
4. 换场景 → 新实例再打印一次 `mosaic renderer:`（`InstanceID` 变化）；
5. 全程无 `scan failed:` / 无异常 / 无崩溃。

## 发布

版本号在 `src/Plugin.cs` 的 `Version` 常量（同时用于 `[BepInPlugin]` 与日志）。
发布 = 推送 commit + tag + Release（附 `LilliaDemosaic.dll`）。
注意 `gh release create/upload` 走 GraphQL，网络异常时可改用 REST：

```powershell
$json | gh api repos/<owner>/<repo>/releases -X POST --input -
gh api --method POST -H "Content-Type: application/octet-stream" \
  "https://uploads.github.com/repos/<owner>/<repo>/releases/<id>/assets?name=LilliaDemosaic.dll" \
  --input src/bin/Release/LilliaDemosaic.dll
```

README 的「更新记录」与 Release notes 保持同步。
