using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LilliaDemosaic
{
    /// <summary>
    /// 把马赛克关掉 —— 算法优化版 (v2)。
    ///
    /// 机制不变: 游戏在 Update 阶段开关贴片, 我们在更晚的时机覆盖,
    /// 不需要 Harmony 补丁, 也不依赖 IL2CPP 偏移。
    ///
    /// 相对 v1 的算法改动 (详见审计文档):
    ///  1. 发现: 每帧做一次"脏探测"(活动场景 handle + 根对象数量, 2 次 interop 调用),
    ///     变化了才全量重扫; 周期扫描仅作兜底, 不再盲扫。
    ///  2. 扫描: FindObjectsByType(Include, 不排序) 取代 FindObjectsOfType ——
    ///     不排序显著更快 (官方文档), 且能发现未激活的贴片 (着装状态下贴片常为 inactive)。
    ///  3. 判定: renderer instanceID → 结论缓存; 名字 → 命中结论缓存;
    ///     大小写不敏感匹配零字符串分配 (不再每个名字 ToLowerInvariant 一次)。
    ///  4. 执行: Renderer.forceRenderingOff 一次置位, 游戏之后随便翻 enabled 都画不出来,
    ///     逐帧 LateUpdate 对抗循环整个不再走; 每 30 帧低频复核补位。
    ///     forceRenderingOff 不可用 (异常) 时自动退回 v1 的逐帧覆盖逻辑。
    ///  5. 去重键由 native Pointer 换成 InstanceID, 消除指针复用导致的误判。
    /// </summary>
    public class DemosaicBehaviour : MonoBehaviour
    {
        // Il2CppInterop 要求所有 MonoBehaviour 子类提供 (IntPtr) 构造函数
        public DemosaicBehaviour(IntPtr ptr) : base(ptr) { }

        // ---- 调节参数 ----
        private const int MaxMats = 8;          // 材质数超过视为身体部件 (沿用 v1 阈值)
        private const int DirtyCooldown = 15;   // 脏探测触发重扫的最小间隔 (帧), 防抖
        private const int VerifyFrames = 30;    // 复核 forceRenderingOff 的间隔 (帧)
        private const int CacheSweepEvery = 10; // 每 K 次兜底扫描清一次 renderer 判定缓存 (收窄材质替换的陈旧窗口)

        // ---- 关键字 (已预转小写) ----
        private string[] _keywords = Array.Empty<string>();

        // ---- 判定缓存 ----
        private readonly Dictionary<string, bool> _nameCache =
            new Dictionary<string, bool>();                       // 名字 → 是否命中 (材质名高度重复, 命中率极高)
        private readonly Dictionary<int, bool> _rendererCache =
            new Dictionary<int, bool>();                          // renderer instanceID → 是否贴片

        // ---- 运行状态 ----
        private readonly List<Renderer> _targets = new List<Renderer>();
        private readonly HashSet<int> _neutralized = new HashSet<int>();   // material instanceID
        private readonly HashSet<int> _logged = new HashSet<int>();        // renderer instanceID
        private Shader _clearShader;

        private int _frame;
        private int _fallbackScans;               // 兜底扫描计数 (缓存淘汰用)
        private int _lastDirty = -1000;           // 上次脏触发重扫的帧号
        private int _lastRootCount = int.MinValue;
        private int _lastSceneHandle = int.MinValue;
        private bool _probeOk = true;             // 脏探测可用性 (首次异常即停用, 退回纯周期)
        private bool _forceOffOk = true;          // forceRenderingOff 可用性 (false → 逐帧覆盖回退)
        private volatile bool _refreshPending;    // 配置变更请求重扫 (由主线程消费)
        private volatile bool _restorePending;    // 配置关闭请求恢复 (由主线程消费)
        private volatile bool _configDirty;       // 关键字/模式变更: 需重建关键字表并清缓存
        private volatile string _pendingKeywords; // 回调线程写入的新关键字 (引用赋值原子)

        /// <summary>
        /// 配置变更可能发生在任意线程 (BepInEx 的文件监听会在线程池线程触发 SettingChanged)。
        /// Unity API 与托管字典都不是线程安全的, 所以回调里只做"记录", 全部处理搬到主线程。
        /// </summary>
        private void RequestRefresh() => _refreshPending = true;

        private void Awake()
        {
            ParseKeywords(Plugin.CfgKeywords.Value);
            Plugin.CfgKeywords.SettingChanged += (_, __) =>
            {
                _pendingKeywords = Plugin.CfgKeywords.Value;   // 仅记录, 不碰缓存/不解析
                _configDirty = true;
            };
            Plugin.CfgMode.SettingChanged += (_, __) => _configDirty = true;
            Plugin.CfgMatchRendererName.SettingChanged += (_, __) => _configDirty = true;
            Plugin.CfgEnabled.SettingChanged += (_, __) => OnEnabledChanged();
            OnEnabledChanged();
        }

        /// <summary>主线程消费配置变更: 重建关键字表、清缓存、重扫。</summary>
        private void ApplyPendingConfig()
        {
            _configDirty = false;
            string kw = _pendingKeywords;
            if (kw != null)
            {
                _pendingKeywords = null;
                ParseKeywords(kw);
                _nameCache.Clear();       // 名字→命中 的结论依赖关键字
            }
            _rendererCache.Clear();       // 判定依赖 Mode / MatchRendererName
            Refresh();
        }

        private void OnEnabledChanged()
        {
            if (Plugin.CfgEnabled.Value) RequestRefresh();
            else _restorePending = true;
        }

        private void ParseKeywords(string raw)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (var part in raw.Split('/'))
                {
                    var s = part.Trim().ToLowerInvariant();
                    if (s.Length > 0) list.Add(s);
                }
            }
            _keywords = list.ToArray();
        }

        // ------------------------------------------------------------------
        // 名字匹配 (缓存 + 零分配)
        // ------------------------------------------------------------------

        /// <summary>名字是否命中关键字 (结论缓存; 大小写不敏感; 不分配临时字符串)。</summary>
        private bool IsMosaicName(string name)
        {
            if (string.IsNullOrEmpty(name) || _keywords.Length == 0) return false;

            if (_nameCache.TryGetValue(name, out bool hit)) return hit;
            hit = ContainsKeyword(name);
            _nameCache[name] = hit;
            return hit;
        }

        private bool ContainsKeyword(string name)
        {
            foreach (var k in _keywords)
                if (ContainsIgnoreCase(name, k)) return true;
            return false;
        }

        /// <summary>
        /// 大小写不敏感子串搜索, 零分配。关键字已预转小写,
        /// 被搜字符串逐 char 用 char.ToLowerInvariant(char) 比较 (无字符串分配)。
        /// </summary>
        private static bool ContainsIgnoreCase(string s, string k)
        {
            int n = s.Length, m = k.Length;
            if (m == 0) return true;
            if (n < m) return false;

            for (int i = 0; i <= n - m; i++)
            {
                int j = 0;
                while (j < m)
                {
                    if (char.ToLowerInvariant(s[i + j]) != k[j]) break;
                    j++;
                }
                if (j == m) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 扫描 / 判定
        // ------------------------------------------------------------------

        /// <summary>重新扫描场景, 更新 "要关掉的 renderer" 与 "要中和的材质"。</summary>
        private void Refresh()
        {
            // 先在本地收集, 扫描成功后才替换 _targets。
            // 否则中途抛异常会把旧目标丢掉 (它们仍处于 forceRenderingOff, 之后无人恢复/复核)。
            var found = new List<Renderer>();

            var mode = Plugin.CfgMode.Value;
            bool doDedicated = mode == DemosaicMode.Dedicated || mode == DemosaicMode.Both;
            bool doMaterials = mode == DemosaicMode.Materials || mode == DemosaicMode.Both;
            bool matchName = Plugin.CfgMatchRendererName.Value;

            try
            {
                var all = FindAllRenderers();

                foreach (var r in all)
                {
                    if (r == null) continue;

                    var mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0 || mats.Length > MaxMats) continue;

                    int id = r.GetInstanceID();

                    // 判定结果缓存: 同一 renderer 不再重复做材质名匹配
                    if (!_rendererCache.TryGetValue(id, out bool dedicated))
                    {
                        dedicated = ClassifyDedicated(r, mats, matchName);
                        _rendererCache[id] = dedicated;
                    }

                    if (dedicated && doDedicated)
                    {
                        found.Add(r);
                        if (_logged.Add(id))
                            Plugin.Logger.LogInfo($"mosaic renderer: {r.name} ({mats.Length} mat)");
                        DisableRenderer(r);
                        continue;   // 整个关掉就不必再改材质
                    }

                    if (doMaterials)
                    {
                        // 名字命中 + 只有 1 个材质 → 直接中和这个材质 (材质名不规范的场合)
                        bool nameHit = matchName && mats.Length == 1 && IsMosaicName(r.name);
                        if (nameHit && !doDedicated)
                        {
                            NeutralizeMaterial(mats[0]);
                        }
                        else
                        {
                            for (int i = 0; i < mats.Length; i++)
                            {
                                var m = mats[i];
                                if (m != null && IsMosaicName(m.name)) NeutralizeMaterial(m);
                            }
                        }
                    }
                }

                // 扫描完整走完才替换目标列表
                _targets.Clear();
                _targets.AddRange(found);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"scan failed: {e.Message}");   // 保留旧目标, 不丢状态
            }
        }

        /// <summary>这个 renderer 是不是"马赛克专用贴片": 全部材质命中, 或名字命中且单材质。</summary>
        private bool ClassifyDedicated(Renderer r, Material[] mats, bool matchName)
        {
            bool all = true;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || !IsMosaicName(m.name)) { all = false; break; }
            }
            if (all) return true;

            // 名字命中 + 只有 1 个材质 → 也视为贴片 (材质名不规范的场合)
            return matchName && mats.Length == 1 && IsMosaicName(r.name);
        }

        /// <summary>
        /// 枚举场景 Renderer。Unity 2021.3+: FindObjectsByType 不排序
        /// (官方文档明确比会排序的 FindObjectsOfType 显著快), 且包含未激活对象
        /// (角色着装时贴片常为 inactive, 只扫 active 会漏检)。
        /// </summary>
        private static Renderer[] FindAllRenderers()
        {
            try
            {
                return UnityEngine.Object.FindObjectsByType<Renderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
            }
            catch
            {
                // 老版本 Unity 回退 (不包含 inactive)
                return UnityEngine.Object.FindObjectsOfType<Renderer>();
            }
        }

        // ------------------------------------------------------------------
        // 执行: 关 renderer
        // ------------------------------------------------------------------

        /// <summary>
        /// 首选 forceRenderingOff: 一次置位, 游戏之后每帧翻 enabled 也画不出来,
        /// 不需要每帧对抗。不可用 (异常) 时回退到 v1 的 enabled=false + 逐帧覆盖。
        /// </summary>
        private void DisableRenderer(Renderer r)
        {
            if (_forceOffOk)
            {
                try { r.forceRenderingOff = true; return; }
                catch { _forceOffOk = false; }
            }
            try { r.enabled = false; } catch { }
        }

        /// <summary>低频复核: 若有东西复位了 forceRenderingOff 就补上。</summary>
        private void VerifyTargets()
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var r = _targets[i];
                if (r == null) { _targets.RemoveAt(i); continue; }   // Unity 的假 null (已销毁)

                try { if (!r.forceRenderingOff) r.forceRenderingOff = true; }
                catch { _forceOffOk = false; return; }
            }
        }

        /// <summary>Enabled=false 时交还控制权。enabled 不碰: 游戏自己的开关逻辑会接管。</summary>
        private void RestoreRenderers()
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var r = _targets[i];
                if (r == null) { _targets.RemoveAt(i); continue; }
                try { r.forceRenderingOff = false; } catch { }
            }
            _targets.Clear();
        }

        // ------------------------------------------------------------------
        // 材质中和 (Materials/Both 模式, 语义同 v1)
        // ------------------------------------------------------------------

        /// <summary>把单个材质换成透明渲染 (只做一次, 以 InstanceID 去重)。</summary>
        private void NeutralizeMaterial(Material m)
        {
            if (m == null) return;
            if (!_neutralized.Add(m.GetInstanceID())) return;

            try
            {
                if (_clearShader == null) _clearShader = Shader.Find("Sprites/Default");
                if (_clearShader != null) m.shader = _clearShader;
                m.mainTexture = null;
                m.color = new Color(0f, 0f, 0f, 0f);
                Plugin.Logger.LogInfo($"neutralized material: {m.name}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"neutralize failed on {m.name}: {e.Message}");
            }
        }

        // ------------------------------------------------------------------
        // 帧循环
        // ------------------------------------------------------------------

        private void Update()
        {
            // 配置变更只能在主线程落地 (SettingChanged 可能来自线程池)
            if (_restorePending) { _restorePending = false; RestoreRenderers(); }
            if (!Plugin.CfgEnabled.Value) return;
            if (_configDirty) ApplyPendingConfig();
            if (_refreshPending) { _refreshPending = false; Refresh(); }

            _frame++;

            // 1) 脏探测 (每帧 2 次 interop 调用, 远轻于一次全量扫描):
            //    场景切换 / 根对象数量变化 (换装、角色进出场景) → 立即重扫。
            //    限频 DirtyCooldown 帧防抖; 冷却窗口内的变化会被下一次探测补上。
            if (_probeOk && _frame - _lastDirty >= DirtyCooldown)
            {
                try
                {
                    var scene = SceneManager.GetActiveScene();
                    bool sceneChanged = scene.handle != _lastSceneHandle;
                    if (sceneChanged)
                    {
                        // 换场景: 旧 instanceID 全部失效, 清判定缓存
                        _rendererCache.Clear();
                    }
                    if (sceneChanged || scene.rootCount != _lastRootCount)
                    {
                        _lastSceneHandle = scene.handle;
                        _lastRootCount = scene.rootCount;
                        _lastDirty = _frame;
                        Refresh();
                    }
                }
                catch { _probeOk = false; }
            }

            // 2) 兜底: 周期性全量重扫 (覆盖脏探测够不到的情况,
            //    如角色作为已有根对象的子物体生成)。RefreshFrames 语义不变。
            //    顺带每 CacheSweepEvery 次兜底清一次 renderer 判定缓存:
            //    游戏运行时替换材质时 (审计 R3), 陈旧结论最多存活 K 个兜底周期。
            //    只清 renderer 级缓存; 名字级缓存与场景无关, 且 String 匹配开销已被它吸收。
            int every = Math.Max(1, Plugin.CfgRefreshFrames.Value);
            if (_frame % every == 0)
            {
                if (++_fallbackScans % CacheSweepEvery == 0) _rendererCache.Clear();
                Refresh();
            }

            // 3) 低频复核 forceRenderingOff (仅主路径需要; 回退路径走 LateUpdate)
            if (_forceOffOk && _frame % VerifyFrames == 0) VerifyTargets();
        }

        /// <summary>
        /// 回退路径: forceRenderingOff 不可用时, 保留 v1 "游戏开、我们关"的每帧对抗。
        /// 主路径下此函数直接返回, 逐帧成本为零。
        /// </summary>
        private void LateUpdate()
        {
            if (_forceOffOk || !Plugin.CfgEnabled.Value) return;

            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var r = _targets[i];
                if (r == null) { _targets.RemoveAt(i); continue; }
                try { if (r.enabled) r.enabled = false; }
                catch { _targets.RemoveAt(i); }
            }
        }
    }
}
