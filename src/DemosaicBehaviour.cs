using System;
using System.Collections.Generic;
using UnityEngine;

namespace LilliaDemosaic
{
    /// <summary>
    /// 每帧把马赛克关掉。
    /// 游戏的开关逻辑通常发生在 Update 阶段, 我们在 LateUpdate 覆盖,
    /// 所以本帧渲染时一定是关闭的 —— 不需要 Harmony 补丁, 也不依赖 IL2CPP 偏移。
    /// </summary>
    public class DemosaicBehaviour : MonoBehaviour
    {
        // Il2CppInterop 要求所有 MonoBehaviour 子类提供 (IntPtr) 构造函数
        public DemosaicBehaviour(IntPtr ptr) : base(ptr) { }

        private readonly List<Renderer> _targets = new List<Renderer>();
        private readonly HashSet<IntPtr> _neutralized = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> _logged = new HashSet<IntPtr>();
        private string[] _keywords = Array.Empty<string>();
        private Shader _clearShader;
        private int _frame;

        private void Awake()
        {
            ParseKeywords(Plugin.CfgKeywords.Value);
            Plugin.CfgKeywords.SettingChanged += (_, __) => ParseKeywords(Plugin.CfgKeywords.Value);
            Refresh();
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

        private bool IsMosaicName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var lower = name.ToLowerInvariant();
            foreach (var k in _keywords)
                if (lower.Contains(k)) return true;
            return false;
        }

        /// <summary>把单个材质换成透明渲染 (只做一次)。</summary>
        private void NeutralizeMaterial(Material m)
        {
            if (m == null) return;
            var ptr = m.Pointer;
            if (ptr == IntPtr.Zero || !_neutralized.Add(ptr)) return;

            if (_clearShader == null) _clearShader = Shader.Find("Sprites/Default");
            if (_clearShader != null) m.shader = _clearShader;
            m.mainTexture = null;
            m.color = new Color(0f, 0f, 0f, 0f);
            Plugin.Logger.LogInfo($"neutralized material: {m.name}");
        }

        /// <summary>重新扫描场景, 更新 "要关掉的 renderer" 与 "要中和的材质"。</summary>
        private void Refresh()
        {
            _targets.Clear();
            var mode = Plugin.CfgMode.Value;
            bool doDedicated = mode == DemosaicMode.Dedicated || mode == DemosaicMode.Both;
            bool doMaterials = mode == DemosaicMode.Materials || mode == DemosaicMode.Both;

            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<Renderer>();

                foreach (var r in all)
                {
                    if (r == null) continue;

                    var mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0 || mats.Length > 8) continue;

                    bool matsAll = true;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || !IsMosaicName(m.name)) { matsAll = false; break; }
                    }

                    // 名字命中 + 只有 1 个材质 → 也视为贴片 (材质名不规范的场合)
                    bool nameHit = Plugin.CfgMatchRendererName.Value && mats.Length == 1 && IsMosaicName(r.name);
                    bool dedicated = matsAll || nameHit;

                    if (dedicated && doDedicated)
                    {
                        _targets.Add(r);
                        if (_logged.Add(r.Pointer))
                            Plugin.Logger.LogInfo($"mosaic renderer: {r.name} ({mats.Length} mat)");
                        continue;   // 整个关掉就不必再改材质
                    }

                    if (doMaterials)
                    {
                        if (nameHit && doDedicated == false)
                            NeutralizeMaterial(mats[0]);
                        else
                            foreach (var m in mats)
                                if (m != null && IsMosaicName(m.name)) NeutralizeMaterial(m);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"scan failed: {e.Message}");
            }
        }

        private void Update()
        {
            if (!Plugin.CfgEnabled.Value) return;
            int every = Math.Max(1, Plugin.CfgRefreshFrames.Value);
            if (++_frame % every == 0) Refresh();
        }

        private void LateUpdate()
        {
            if (!Plugin.CfgEnabled.Value || _targets.Count == 0) return;

            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var r = _targets[i];
                if (r == null)          // Unity 的假 null (对象已销毁)
                {
                    _targets.RemoveAt(i);
                    continue;
                }
                try
                {
                    if (r.enabled) r.enabled = false;
                }
                catch { _targets.RemoveAt(i); }
            }
        }
    }
}
