using DOTSUtils.Samples.Pathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DOTSUtils.Pathfinding.EditorTools
{
    /// <summary>
    /// 生成 A* 演示场景：一个场地根对象（<see cref="AStarDemoBootstrap"/>，场地全在运行时生成）
    /// 加一台俯视相机与一盏平行光，存成 <c>Assets/Scenes/AStarDemo.unity</c> 并打开。
    /// </summary>
    internal static class AStarDemoMenu
    {
        private const string k_ScenePath = "Assets/Scenes/AStarDemo.unity";

        [MenuItem("Tools/DOTSUtils/A* 演示场景（新建）")]
        private static void CreateDemoScene()
        {
            var active = EditorSceneManager.GetActiveScene();
            if (active.isDirty &&
                !EditorUtility.DisplayDialog("A* 演示场景",
                    "当前场景有未保存的改动，继续会丢失。要继续吗？", "继续并丢失改动", "取消"))
            {
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(k_ScenePath) != null &&
                !EditorUtility.DisplayDialog("A* 演示场景",
                    k_ScenePath + " 已存在，要覆盖吗？", "覆盖", "取消"))
            {
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("AStarDemo");
            var bootstrap = root.AddComponent<AStarDemoBootstrap>();

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            camera.fieldOfView = 60f;
            camera.farClipPlane = 1000f;

            // 相机位姿按场地大小算：改网格参数后重新生成场景，整片场地仍然在视野内。
            AStarDemoBootstrap.SuggestCameraPose(bootstrap.Width * bootstrap.CellSize,
                                                 bootstrap.Height * bootstrap.CellSize,
                                                 out var cameraPosition, out var cameraRotation);
            cameraObject.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
            cameraObject.AddComponent<AudioListener>();

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            EditorSceneManager.SaveScene(scene, k_ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[AStarDemo] 演示场景已生成：{k_ScenePath} —— 打开它并进 Play 就能看到 " +
                      $"{bootstrap.AgentCount} 个 ECS agent 沿 A* 路径行走并互相避让。");
        }
    }
}
