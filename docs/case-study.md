# 案例研究：马赛克是怎么实现的

记录一次实际逆向过程，说明「为什么本插件的判定规则是这样定的」，也方便你在新游戏上定位问题。

## 1. 先确定是哪一层

Unity 游戏里的马赛克常见有四种：

| 类型 | 特征 | 能否用本插件 |
|---|---|---|
| 独立贴片 mesh | 有自己的 renderer，材质名通常含 mosaic | ✅ 主要目标 |
| 合批网格里的一个子材质 | 一个 renderer 多个材质，其中一个像素化 | ✅ `Mode=Materials/Both` |
| shader 内像素化 | 材质参数/关键字控制，无独立网格 | ⚠️ 只能改材质参数或换 shader |
| 全屏后期 | 屏幕空间后处理 | ❌ 思路不同 |

判断方法：把 IL2CPP 的 `global-metadata.dat` 丢给 [Il2CppDumper](https://github.com/Perfare/Il2CppDumper)，
在生成的 `dump.cs` 里搜 `mosaic / mozaic / censor / pixel`，看命中在哪个类：
`Renderer` 相关 → 独立贴片；`Material/Shader` 参数 → shader 内；`PostProcess` 相关 → 后期。

## 2. 本次案例（Unity 2022.3，metadata v31）

`dump.cs` 里只有两处命中：

```
DressController : MonoBehaviour
    private readonly List<Renderer> _mosaicRenderers;   // 0xE8
    private void UpdateMosaicVisibility();              // 关/开贴片

LIDPenetrator : MonoBehaviour                            // 另一个部位, 与本插件无关
    public bool  enableMosaic;
    public float mosaicRatio;
```

反汇编 `UpdateMosaicVisibility` 还原出：

```csharp
if (!_mosaicScanned || _mosaicRenderers[0] == null) {
    _mosaicRenderers.Clear();
    foreach (var r in targetAvatar.GetComponentsInChildren<Renderer>(true))
        foreach (var m in r.sharedMaterials)
            if (m.name.Contains(<关键字>)) { _mosaicRenderers.Add(r); break; }
}
bool show = IsDoingSex || !IsPartsWorn(Panties);
foreach (var r in _mosaicRenderers) r.enabled = show;   // 每帧都在改
```

实测日志（本插件的 `mosaic renderer:` 行）：

```
mosaic renderer: .../MainBody_Instance/Pussy_Mosaic (1 mat)     ← 材质 StrictMosaic
mosaic renderer: .../Armature/Hips/Hips(Basis_body(B))/Sphere (1 mat)
```

结论：**独立贴片 + 每帧开关**。所以正确做法是「在游戏之后覆盖」，而不是「关一次」。

## 3. 走不通的路（留给后来人）

* **按材质名匹配的现成插件无效**：网络上的 `DumbRendererDemosaic` 一类确实能命中
  `StrictMosaic`，但它所在的 BepInEx 没能初始化（Unity 2022.3 需要新版 BepInEx 6，
  旧版 Cpp2IL 只支持 metadata 23–29，会报 `Unsupported metadata version ... got 31`）。
* **hook 游戏方法**：可行但脆弱，需要维护 RVA/字段偏移；本插件改用 `LateUpdate` 覆盖，
  效果等价且零偏移依赖。
* **另一个部位的像素化是 shader 内做的**：`_LID_MosaicEnabled` 实测为 0、无相关关键字、
  无独立网格，像素化烤死在 `Hidden/LIDPatched/...` shader 或贴图里；
  强行替换 shader 会渲染成品红，**不建议动**。

## 4. 换新游戏时的排查清单

1. 先跑 `Mode=Dedicated`，看日志有没有 `mosaic renderer:`；
2. 有命中但画面没变 → 说明有别的东西在 `LateUpdate` 之后又打开它，
   或贴片不是靠 `enabled` 控制（可能改 `material` 或 `gameObject.SetActive`）；
3. 完全没命中 → 用 dump.cs 确认马赛克所在层级，再决定加关键字还是换 `Mode`；
4. 白色/黑色遮挡块不是马赛克，是游戏自身的 censor 材质，本插件不会处理。
