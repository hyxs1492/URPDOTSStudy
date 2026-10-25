using System.Collections.Generic;
using DOTSUtils.RenderBridge;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DOTSUtils.RenderBridge.EditorTools
{
    /// <summary>
    /// 使用 <see cref="OriginalRendererDisposal.DestroyComponents"/> 的安全网：进入 Play 与保存场景时，
    /// 若场景重载已关闭、且场景里有对象用了该模式，就打一条警告。
    ///
    /// 只警告、不阻断。
    /// </summary>
    [InitializeOnLoad]
    public static class DotsRenderDisposalPlayModeGuard
    {
        static DotsRenderDisposalPlayModeGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode)
            {
                return;
            }
            Check(SceneManager.GetActiveScene(), "即将进入 Play");
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            Check(scene, $"即将保存场景 \"{path}\"");
        }

        /// <summary>场景重载是否被关闭。</summary>
        public static bool IsSceneReloadDisabled =>
            EditorSettings.enterPlayModeOptionsEnabled &&
            (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableSceneReload) != 0;

        /// <summary>列出该场景里用了 <c>DestroyComponents</c> 的 <see cref="DotsRenderAuthoring"/> 对象名（含未激活对象）。</summary>
        public static List<string> FindRiskyAuthorings(Scene scene)
        {
            var risky = new List<string>();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return risky;
            }

            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var authorings = roots[i].GetComponentsInChildren<DotsRenderAuthoring>(true);
                for (var a = 0; a < authorings.Length; a++)
                {
                    var authoring = authorings[a];
                    if (authoring == null ||
                        authoring.Disposal != OriginalRendererDisposal.DestroyComponents)
                    {
                        continue;
                    }
                    risky.Add($"{authoring.gameObject.name}(active={authoring.gameObject.activeSelf})");
                }
            }
            return risky;
        }

        private static void Check(Scene scene, string when)
        {
            if (!IsSceneReloadDisabled)
            {
                return;
            }

            var risky = FindRiskyAuthorings(scene);
            if (risky.Count == 0)
            {
                return;
            }

            Debug.LogWarning(
                $"[DotsRender] ⚠️ {when}：场景重载已关闭（EnterPlayModeOptions 含 DisableSceneReload），" +
                $"而该场景有 {risky.Count} 个对象用了 Disposal = DestroyComponents：{string.Join("、", risky)}。\n" +
                "  · 运行时会**真的销毁这些场景对象的 MeshRenderer / LODGroup / MeshFilter**；\n" +
                "  · 退出 Play **不会**恢复（场景没有被重新加载）；\n" +
                "  · 保存场景会把销毁写进资产 —— **不可逆**。\n" +
                "  要安全实验：先备份场景，或把这些对象的 Disposal 临时改成 ForceRenderingOff。");
        }
    }
}
