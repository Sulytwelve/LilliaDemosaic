using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace LilliaDemosaic
{
    /// <summary>处理策略。</summary>
    public enum DemosaicMode
    {
        /// <summary>只关闭"全部材质都是马赛克"的专用贴片 renderer（最安全，默认）。</summary>
        Dedicated = 0,
        /// <summary>只把命中的材质换成透明 shader（适合身体与阴部合批进同一 renderer 的游戏）。</summary>
        Materials = 1,
        /// <summary>两者都做：专用贴片关 renderer，其余命中材质换 shader（覆盖面最广）。</summary>
        Both = 2,
    }

    /// <summary>
    /// Novice Succubus Lillia 去马赛克 (BepInEx 6 / IL2CPP)
    ///
    /// 机制 (逆向自 dump.cs):
    ///   DressController 在 UpdateMosaicVisibility() 里按
    ///   `IsDoingSex || !IsPartsWorn(Panties)` 每帧开关一组 renderer,
    ///   这组 renderer 是贴在身体表面的独立贴片 (Pussy_Mosaic / Sphere),
    ///   材质名为 StrictMosaic。
    ///
    /// 对策:
    ///   在 LateUpdate 里(晚于游戏的 Update)把它们再关掉, 每帧覆盖;
    ///   列表每 2 秒刷新一次, 角色进出场景自动跟上。
    /// </summary>
    [BepInPlugin(Guid, "Lillia Demosaic", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "lillia.demosaic";
        public const string Version = "1.1.0";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<string> CfgKeywords;
        internal static ConfigEntry<int> CfgRefreshFrames;
        internal static ConfigEntry<DemosaicMode> CfgMode;
        internal static ConfigEntry<bool> CfgMatchRendererName;

        public override void Load()
        {
            Logger = Log;

            CfgEnabled = Config.Bind("General", "Enabled", true,
                "开启去马赛克。改动即时生效, 不需要重启游戏。");
            CfgKeywords = Config.Bind("General", "Keywords",
                "mosaic/mozaic/mosic/mosiac/mozaik/mosaik/pixelate/censor/모자이크/モザイク",
                "材质名命中其中任一关键字的 renderer/材质会被当作马赛克。用 / 分隔, 不区分大小写。");
            CfgRefreshFrames = Config.Bind("General", "RefreshFrames", 120,
                new ConfigDescription("多少帧重新扫描一次场景 (120 帧 ≈ 2 秒)。",
                    new AcceptableValueRange<int>(1, 600)));
            CfgMode = Config.Bind("General", "Mode", DemosaicMode.Dedicated,
                "Dedicated = 只关专用贴片 renderer (最安全);\n" +
                "Materials = 只把命中材质换透明 shader (适合合批网格, 但若该材质是本体的一部分会留洞);\n" +
                "Both = 两种都做 (覆盖面最广)。");
            CfgMatchRendererName = Config.Bind("General", "MatchRendererName", true,
                "true = renderer 自身名字命中关键字、且它只有 1 个材质时, 也当作马赛克贴片。\n" +
                "有些游戏材质名不规范 (如 Mosic_Back), 靠这条能命中。");

            AddComponent<DemosaicBehaviour>();
            Logger.LogInfo($"Lillia Demosaic loaded (mode={CfgMode.Value})");
        }
    }
}
