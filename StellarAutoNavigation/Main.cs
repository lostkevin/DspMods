﻿#define QUICK_INDICATOR

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace AutoNavigate
{
    [BepInPlugin(__GUID__, __NAME__, "1.09")]
    public class AutoNavigate : BaseUnityPlugin
    {
        public const string __NAME__ = "StellarAutoNavigation";
        public const string __GUID__ = "0x.plugins.dsp." + __NAME__;

        public static AutoNavigate __this;
        public static AutoStellarNavigation s_NavigateInstance;
        public static bool s_IsHistoryNavigated = false;
        public static ConfigEntry<double> s_NavigateMinEnergy;
        private static double lastDist;

        private Player player = null;  // Maybe null if controller is not inited.

        private void Start()
        {
            ModDebug.SetLogger(Logger);

            __this = this;
            s_NavigateInstance = new AutoStellarNavigation(GetNavigationConfig());

            new Harmony(__GUID__).PatchAll();
        }

        private void Update()
        {
            if (player != null)
            {
                //导航开关  (K键可能会在将来版本弃用)
                if (Input.GetKeyDown(KeyCode.Keypad0) || Input.GetKeyDown(KeyCode.K))
                {
                    lastDist = double.MaxValue;
                    s_NavigateInstance.ToggleNavigate(player);
                }
            }
        }

        private AutoStellarNavigation.NavigationConfig GetNavigationConfig()
        {
            var config = new AutoStellarNavigation.NavigationConfig();

            s_NavigateMinEnergy = Config.Bind<double>(
                "AutoStellarNavigation",
                "minAutoNavEnergy",
                50000000.0,
                "开启自动导航最低能量(最低50m)"
                );
            config.speedUpEnergylimit = Config.Bind<double>(
                "AutoStellarNavigation",
                "SpeedUpEnergylimit",
                50000000.0,
                "开启加速最低能量(默认50m)"
                );
            config.wrapEnergylimit = Config.Bind<double>(
                "AutoStellarNavigation",
                "WrapEnergylimit",
                800000000,
                "开启曲率最低能量(默认800m)"
                );
            config.enableLocalWrap = Config.Bind<bool>(
                "AutoStellarNavigation",
                "EnableLocalWrap",
                true,
                "是否开启本地行星曲率飞行"
                );
            config.localWrapMinDistance = Config.Bind<double>(
                "AutoStellarNavigation",
                "LocalWrapMinDistance",
                100000.0,
                "本地行星曲率飞行最短距离"
                );

            if (s_NavigateMinEnergy.Value < 50000000.0)
                s_NavigateMinEnergy.Value = 50000000.0;

            return config;
        }

        /// <summary>
        /// 安全模式，确保不会出现一些未知错误
        /// </summary>
        private class SafeMode
        {
            public static void Reset()
            {
                s_IsHistoryNavigated = false;
                s_NavigateInstance.Reset();
                s_NavigateInstance.target.Reset();
                __this.player = null;
            }

            [HarmonyPatch(typeof(GameMain), "OnDestroy")]
            public class SafeDestroy
            {
                private static void Prefix() =>
                    Reset();
            }

            [HarmonyPatch(typeof(GameMain), "Pause")]
            public class SafePause
            {
                public static void Prefix() =>
                    s_NavigateInstance.Pause();
            }

            [HarmonyPatch(typeof(GameMain), "Resume")]
            public class SafeResume
            {
                public static void Prefix() =>
                    s_NavigateInstance.Resume();
            }
        }

        [HarmonyPatch(typeof(PlayerController), "Init")]
        private class PlayerControllerInit
        {
            private static void Postfix(PlayerController __instance) =>
                __this.player = __instance.player;
        }

        /// <summary>
        /// Navigate Tips
        /// </summary>
        [HarmonyPatch(typeof(UIGeneralTips), "_OnUpdate")]
        private class NavigateTips
        {
            //Tip position offest
            private static Vector2 anchoredPosition = new Vector2(0.0f, 160.0f);

            public static void Postfix(UIGeneralTips __instance)
            {
                if (!s_NavigateInstance.enable || !s_NavigateInstance.target.TargetSanity)
                    return;

                Text modeText = Traverse.Create((object)__instance).Field("modeText").GetValue<Text>();

                modeText.gameObject.SetActive(true);
                modeText.rectTransform.anchoredPosition = anchoredPosition;
                s_NavigateInstance.modeText = modeText;
                s_NavigateInstance.modeText.text = string.Format("{0} Auto Navigation", s_NavigateInstance.GetText()).LocalText();

            }
        }

        /// <summary>
        /// Sail speed up
        /// </summary>
        [HarmonyPatch(typeof(VFInput), "_sailSpeedUp", MethodType.Getter)]
        private class SailSpeedUp
        {
            /// <summary>
            /// Force VFInput._sailSpeedUp to be True if we need to speed up
            /// </summary>
            /// <param name="__result"></param>
            private static void Postfix(ref bool __result)
            {
                if (!s_NavigateInstance.enable)
                    return;

                if (s_NavigateInstance.sailSpeedUp)
                    __result = true;
            }
        }

        /// <summary>
        /// Sail mode
        /// </summary>
        [HarmonyPatch(typeof(PlayerMove_Sail), "GameTick")]
        private class SailMode_AutoNavigate
        {
            private static VectorLF3 oTargetURot;
            

            private static void Prefix(PlayerMove_Sail __instance)
            {
                if (!s_NavigateInstance.enable)
                    return;

                if (!__instance.player.sailing && !__instance.player.warping)
                    return;

                ++__instance.controller.input0.y;
                oTargetURot = __instance.controller.fwdRayUDir;

                if (__instance.player.warping)
                {
                    var cdist = s_NavigateInstance.target.GetDistance(__instance.player);

                    if (lastDist < cdist)
                    {
                        ModDebug.Trace($"Dropping out of warp, passed target - ld:{lastDist} cd:{cdist}");
                        //We passed the target, drop out of warp, retarget
                        AutoStellarNavigation.Warp.TryLeaveWarp(__instance, false);
                    }
                    lastDist = cdist;
                }

                s_NavigateInstance.Navigation(__instance);
            }

            private static void Postfix(PlayerMove_Sail __instance)
            {
                // In PlayerMove_Sail.GameTick, the DSP will always try to accelerate the mecha when we have enough energy
                // because we set sailSpeedUp to be True. Recover the real value after hacking by set  sailSpeedUp to be False.
                s_NavigateInstance.sailSpeedUp = false;

                if (!s_NavigateInstance.enable)
                    return;

                __instance.controller.fwdRayUDir = oTargetURot;
                // Stop Navigation if there is any input
                s_NavigateInstance.HandlePlayerInput();
            }
        }

        /// <summary>
        /// Fly mode
        /// </summary>
        [HarmonyPatch(typeof(PlayerMove_Fly), "GameTick")]
        private class FlyToSail
        {
            private static float sailMinAltitude = 49.0f;

            /// <summary>
            /// Fly --> Sail or Arrive
            /// </summary>
            private static void Prefix(PlayerMove_Fly __instance)
            {
                //ModDebug.Trace("Begin FlyToSail.PreFix");
                if (!s_NavigateInstance.enable)
                {
                    //ModDebug.Trace("No navigation, exiting");
                    return;
                }

                if (__instance.player.movementState != EMovementState.Fly)
                {
                    //ModDebug.Trace("Movement != fly");
                    return;
                }

                if (s_NavigateInstance.DetermineArrive(__instance.player))
                {
                    //ModDebug.Log("FlyModeArrive");
                    s_NavigateInstance.Arrive();
                }
                else if (__instance.mecha.thrusterLevel < 2)
                {
                    //ModDebug.Trace("Thruster level too low");
                    s_NavigateInstance.Arrive("Thruster Level Too Low".LocalText());
                }
                else if (__instance.player.mecha.coreEnergy < s_NavigateMinEnergy.Value)
                {
                    //ModDebug.Trace("Mecha energy too low");
                    s_NavigateInstance.Arrive("Mecha Energy Too Low".LocalText());
                }
                else
                {
                    //ModDebug.Trace("In else");
                    ++__instance.controller.input1.y;

                    if (__instance.currentAltitude > sailMinAltitude)
                    {
                        //ModDebug.Trace("Switch to sail");
                        AutoStellarNavigation.Fly.TrySwtichToSail(__instance);
                    }
                }
                //ModDebug.Trace("End FlyToSail.PreFix");
            }
        }

        /// <summary>
        /// WalkOrDrift mode
        /// </summary>
        [HarmonyPatch(typeof(PlayerMove_Walk), "UpdateJump")]
        private class WalkToFLy
        {
            /// <summary>
            /// WalkOrDrift --> Fly or Arrive
            /// </summary>
            private static void Postfix(PlayerMove_Walk __instance, ref bool __result)
            {
                ModDebug.Trace("Begin WalkToFLy.PostFix");

                if (!s_NavigateInstance.enable)
                {
                    ModDebug.Trace("No navigation, exiting");
                    return;
                }

                if (s_NavigateInstance.DetermineArrive(__instance.player))
                {
                    ModDebug.Log("WalkModeArrive");
                    s_NavigateInstance.Arrive();
                }
                else if (__instance.mecha.thrusterLevel < 1)
                {
                    ModDebug.Trace("Thruster level too low");
                    s_NavigateInstance.Arrive("Thruster Level Too Low".LocalText());
                }
                else if (__instance.player.mecha.coreEnergy < s_NavigateMinEnergy.Value)
                {
                    ModDebug.Trace("Mecha energy too low");
                    s_NavigateInstance.Arrive("Mecha Energy Too Low".LocalText());
                }
                else
                {
                    ModDebug.Trace("Switching to fly mode");
                    AutoStellarNavigation.WalkOrDrift.TrySwitchToFly(__instance);
                    //切换至Fly Mode 中对 UpdateJump 方法进行拦截
                    __result = true;
                }

                ModDebug.Trace("End WalkToFLy.PostFix");
            }
        }

        /// <summary>
        /// Drift mode
        /// </summary>
        [HarmonyPatch(typeof(PlayerMove_Drift), "UpdateJump")]
        private class DriftToFly
        {
            /// <summary>
            /// WalkOrDrift --> Fly or Arrive
            /// </summary>
            private static void Postfix(PlayerMove_Drift __instance)
            {
                ModDebug.Trace("Begin DriftToFly.PostFix");

                if (!s_NavigateInstance.enable)
                {
                    ModDebug.Trace("No navigation, exiting");
                    return;
                }

                if (s_NavigateInstance.DetermineArrive(__instance.player))
                {
                    ModDebug.Log("DriftModeArrive");
                    s_NavigateInstance.Arrive();
                }
                else if (__instance.mecha.thrusterLevel < 1)
                {
                    ModDebug.Trace("Thruster level too low");
                    s_NavigateInstance.Arrive("Thruster Level Too Low".LocalText());
                }
                else if (__instance.player.mecha.coreEnergy < s_NavigateMinEnergy.Value)
                {
                    ModDebug.Trace("Mecha energy too low");
                    s_NavigateInstance.Arrive("Mecha Energy Too Low".LocalText());
                }
                else
                {
                    ModDebug.Trace("Switching to fly mode");
                    AutoStellarNavigation.WalkOrDrift.TrySwitchToFly(__instance);
                }

                ModDebug.Trace("End DriftToFly.PostFix");
            }
        }

        /// --------------------------
        /// Starmap Indicator   游戏内置星球导航指示标
        /// --------------------------
        [HarmonyPatch(typeof(UIStarmap), "OnCursorFunction3Click")]
        private class OnSetIndicatorAstro
        {
            /// <summary>
            /// 根据 Indicator (导航指示标) 设置导航目标
            /// </summary>
            private static void Prefix(UIStarmap __instance)
            {
                if ((UnityEngine.Object)__instance.focusPlanet != (UnityEngine.Object)null &&
                    __instance.focusPlanet.planet != null)
                {
                    s_NavigateInstance.target.SetTarget(__instance.focusPlanet.planet);
                    return;
                }

                if ((UnityEngine.Object)__instance.focusStar != (UnityEngine.Object)null &&
                    __instance.focusStar.star != null)
                {
                    s_NavigateInstance.target.SetTarget(__instance.focusStar.star);
                    return;
                }

                if ((UnityEngine.Object)__instance.focusHive != (UnityEngine.Object)null &&
                    __instance.focusHive.hive != null)
                {
                    s_NavigateInstance.target.SetTarget(__instance.focusHive.hive);
                    return;
                }
            }
        }

        [HarmonyPatch(typeof(UIStarmap), "OnTinderClick")]
        private class OnSetTinderIndicatorAstro
        {
            /// <summary>
            /// 根据 Indicator (导航指示标) 设置导航目标
            /// </summary>
            private static void Postfix(UIStarmap __instance)
            {
                int enemyId = GameMain.mainPlayer.navigation.indicatorEnemyId;
                if (enemyId < 0 || enemyId >= __instance.spaceSector.enemyPool.Length)
                    enemyId = 0;
                else if (__instance.spaceSector.enemyPool[enemyId].id == 0 || __instance.spaceSector.enemyPool[enemyId].dfTinderId == 0)
                    enemyId = 0;
                if (enemyId != 0)
                {
                    s_NavigateInstance.target.SetTarget(__instance.spaceSector, enemyId);
                    return;
                }
            }
        }

#if QUICK_INDICATOR

        /// --------------------------
        /// 快速设置导航标识
        /// --------------------------
        [HarmonyPatch(typeof(UIStarmap), "UpdateCursorView")]
        private class UIStarmap_UpdateCursorView
        {
            private static void Postfix(UIStarmap __instance)
            {
                //if (!s_NavigateInstance.enable)
                //    return;

                //Indicator 开关
                if (!Input.GetKeyDown(KeyCode.LeftControl))
                    return;

                if (__instance.mouseHoverStar != null && __instance.mouseHoverStar.star != null)
                {
                    __instance.focusStar = __instance.mouseHoverStar;
                    __instance.focusPlanet = null;
                    __instance.OnCursorFunction3Click(0);

                    //s_NavigateInstance.target.SetTarget(__instance.mouseHoverStar.star);
                    return;
                }

                if (__instance.mouseHoverPlanet != null && __instance.mouseHoverPlanet.planet != null)
                {
                    __instance.focusPlanet = __instance.mouseHoverPlanet;
                    __instance.focusStar = null;
                    __instance.OnCursorFunction3Click(0);

                    //s_NavigateInstance.target.SetTarget(__instance.mouseHoverPlanet.planet);
                    return;
                }
            }
        }

#endif

#if FAST_SWITH_TARGET
        /// --------------------------
        /// 自动导航时鼠标单击目标即可切换导航目标
        /// --------------------------
        [HarmonyPatch(typeof(UIStarmap), "OnStarClick")]
        private class UIStarmap_OnStarClick
        {
            private static void Prefix(UIStarmap __instance, ref UIStarmapStar star)
            {
                if (!s_NavigateInstance.enable)
                    return;

                if (star == null || star.star == null)
                    return;

                s_NavigateInstance.target.SetTarget(star.star);

                __instance.focusStar = star;
                __instance.OnCursorFunction3Click(0);
            }
        }

        [HarmonyPatch(typeof(UIStarmap), "OnPlanetClick")]
        private class UIStarmap_OnPlanetClick
        {
            private static void Prefix(UIStarmap __instance, ref UIStarmapPlanet planet)
            {
                if (!s_NavigateInstance.enable)
                    return;

                if (planet == null || planet.planet == null)
                    return;

                s_NavigateInstance.target.SetTarget(planet.planet);

                __instance.focusPlanet = planet;
                __instance.OnCursorFunction3Click(0);
            }
        }
#endif
    }
}