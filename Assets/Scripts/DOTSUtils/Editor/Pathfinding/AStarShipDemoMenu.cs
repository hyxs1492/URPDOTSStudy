using DOTSUtils.Samples.Pathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DOTSUtils.Pathfinding.EditorTools
{
    /// <summary>
    /// 演示场景里的一块矩形障碍（俯视只给中心与尺寸，顶面统一在 <see cref="AStarShipDemoMenu.WallHeight"/>）。
    /// </summary>
    internal struct AStarShipDemoObstacle
    {
        /// <summary>对象名。</summary>
        public string Name;

        /// <summary>中心（x, z）。</summary>
        public Vector2 Center;

        /// <summary>尺寸（x, z）。</summary>
        public Vector2 Size;
    }

    /// <summary>
    /// 生成「A* 船群演示」场景：一块带 Collider 的地面 + 一段带缺口的障碍走廊（真实的 Cube，
    /// 供编辑器工具射线烘焙）+ 俯视相机 + 平行光，再加一个挂 <see cref="AStarShipDemoBootstrap"/>
    /// 与 <see cref="AStarGridSource"/> 的根对象。
    ///
    /// 建完场景后会按 <see cref="AStarShipDemoBootstrap"/> 用的那套射线参数实际烘焙一次，
    /// 把结果写进 <c>Assets/Pathfinding/AStarShipGrid.asset</c>。
    /// </summary>
    internal static class AStarShipDemoMenu
    {
        /// <summary>场景资产路径。</summary>
        public const string ScenePath = "Assets/Scenes/AStarShipDemo.unity";

        /// <summary>网格资产路径。</summary>
        public const string GridAssetPath = "Assets/Pathfinding/AStarShipGrid.asset";

        /// <summary>船源预制体路径。</summary>
        public const string ShipPrefabPath = "Assets/Res/Prefabs/Ship/TestShip.prefab";

        /// <summary>场景里地面用的材质资产（URP Lit，颜色写在资产上）。</summary>
        public const string FloorMaterialPath = "Assets/Pathfinding/Materials/Floor.mat";

        /// <summary>场景里障碍物用的材质资产（URP Lit，颜色写在资产上）。</summary>
        public const string WallMaterialPath = "Assets/Pathfinding/Materials/Wall.mat";

        /// <summary>材质资产所在目录。</summary>
        public const string MaterialFolder = "Assets/Pathfinding/Materials";

        /// <summary>地面颜色（暗灰蓝）。</summary>
        public static readonly Color FloorColor = new Color(0.16f, 0.17f, 0.2f);

        /// <summary>障碍物颜色（偏亮）。</summary>
        public static readonly Color WallColor = new Color(0.32f, 0.34f, 0.4f);

        /// <summary>场地宽（世界单位，对应 x）。</summary>
        public const float FieldWidth = 60f;

        /// <summary>场地深（世界单位，对应 z）。</summary>
        public const float FieldDepth = 40f;

        /// <summary>格子边长（世界单位）。</summary>
        public const float CellSize = 1f;

        /// <summary>障碍物高度（世界单位）；顶面在 y = WallHeight。</summary>
        public const float WallHeight = 3f;

        /// <summary>烘焙射线起点高度（相对网格中心）。</summary>
        public const float BakeStartHeight = 5f;

        /// <summary>烘焙最大落差。</summary>
        public const float BakeMaxDrop = 2.5f;

        /// <summary>烘焙是否把「命中」算作可走；本场景用「落差带内命中算阻挡」。</summary>
        public const bool BakeHitWalkable = false;

        /// <summary>
        /// 障碍布局：外圈一圈墙 + 三道带缺口的横墙 + 四段两端留通道的竖墙。
        /// 缺口与竖墙两端错开，整片场地 4 邻连通（可走 / 阻挡格数见烘焙日志）。
        /// </summary>
        private static readonly AStarShipDemoObstacle[] s_Obstacles =
        {
            // 外圈
            new AStarShipDemoObstacle { Name = "Wall_South", Center = new Vector2(30f, 0.5f), Size = new Vector2(60f, 1f) },
            new AStarShipDemoObstacle { Name = "Wall_North", Center = new Vector2(30f, 39.5f), Size = new Vector2(60f, 1f) },
            new AStarShipDemoObstacle { Name = "Wall_West", Center = new Vector2(0.5f, 20f), Size = new Vector2(1f, 40f) },
            new AStarShipDemoObstacle { Name = "Wall_East", Center = new Vector2(59.5f, 20f), Size = new Vector2(1f, 40f) },

            // 横墙 1（z ≈ 9.5）：缺口在 x ∈ (20, 26)
            new AStarShipDemoObstacle { Name = "Hall_R1_A", Center = new Vector2(10.5f, 9.5f), Size = new Vector2(19f, 1f) },
            new AStarShipDemoObstacle { Name = "Hall_R1_B", Center = new Vector2(42.5f, 9.5f), Size = new Vector2(33f, 1f) },

            // 横墙 2（z ≈ 19.5）：缺口在 x ∈ (33, 39)
            new AStarShipDemoObstacle { Name = "Hall_R2_A", Center = new Vector2(17f, 19.5f), Size = new Vector2(32f, 1f) },
            new AStarShipDemoObstacle { Name = "Hall_R2_B", Center = new Vector2(49f, 19.5f), Size = new Vector2(20f, 1f) },

            // 横墙 3（z ≈ 29.5）：缺口在 x ∈ (14, 20) 与 x ∈ (48, 54)
            new AStarShipDemoObstacle { Name = "Hall_R3_A", Center = new Vector2(7.5f, 29.5f), Size = new Vector2(13f, 1f) },
            new AStarShipDemoObstacle { Name = "Hall_R3_B", Center = new Vector2(34f, 29.5f), Size = new Vector2(28f, 1f) },
            new AStarShipDemoObstacle { Name = "Hall_R3_C", Center = new Vector2(56.5f, 29.5f), Size = new Vector2(5f, 1f) },

            // 竖墙：夹在两道横墙之间，两端各留两格通道（船的 Rvo 直径约 0.9 世界单位，通道不能只有一格）
            new AStarShipDemoObstacle { Name = "Vcol_C1", Center = new Vector2(14.5f, 14.5f), Size = new Vector2(1f, 5f) },
            new AStarShipDemoObstacle { Name = "Vcol_C2", Center = new Vector2(44.5f, 24.5f), Size = new Vector2(1f, 5f) },
            new AStarShipDemoObstacle { Name = "Vcol_C3", Center = new Vector2(30.5f, 34.5f), Size = new Vector2(1f, 5f) },
            new AStarShipDemoObstacle { Name = "Vcol_C4", Center = new Vector2(48.5f, 34.5f), Size = new Vector2(1f, 5f) },
        };

        [MenuItem("Tools/DOTSUtils/A* 船群演示场景（新建）")]
        private static void CreateShipDemoScene()
        {
            var active = EditorSceneManager.GetActiveScene();
            if (active.isDirty &&
                !EditorUtility.DisplayDialog("A* 船群演示场景",
                    "当前场景有未保存的改动，继续会丢失。要继续吗？", "继续并丢失改动", "取消"))
            {
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null &&
                !EditorUtility.DisplayDialog("A* 船群演示场景",
                    ScenePath + " 已存在，要覆盖吗？", "覆盖", "取消"))
            {
                return;
            }

            var shipPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ShipPrefabPath);
            if (shipPrefab == null)
            {
                Debug.LogError($"[AStarShipDemo] 找不到船源预制体 {ShipPrefabPath}，无法生成场景。");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var floorMaterial = EnsureMaterial(FloorMaterialPath, "Floor", FloorColor);
            var wallMaterial = EnsureMaterial(WallMaterialPath, "Wall", WallColor);

            var root = new GameObject("AStarShipDemo");
            var bootstrap = root.AddComponent<AStarShipDemoBootstrap>();
            bootstrap.SourceShip = shipPrefab;
            bootstrap.FloorMaterial = floorMaterial;
            bootstrap.WallMaterial = wallMaterial;

            BuildGround(root.transform, floorMaterial);
            BuildObstacles(root.transform, wallMaterial);
            BuildLight();
            BuildCamera();

            // 网格资产：不存在就按场地参数新建一份，然后用射线实际烘焙一次。
            var asset = LoadOrCreateGridAsset();
            if (asset == null)
            {
                return;
            }

            var gridSource = root.AddComponent<AStarGridSource>();
            gridSource.GridAsset = asset;
            bootstrap.GridAsset = asset;

            var layers = ~0;
            if (AStarBakeUtility.BakeActiveSceneAndApply(asset, layers, BakeStartHeight, BakeMaxDrop,
                                                         BakeHitWalkable, out var bake))
            {
                Debug.Log($"[AStarShipDemo] 网格已烘焙并保存：{bake.Width}×{bake.Height} 格、" +
                          $"可走 {bake.WalkableCount}/{bake.CellCount}（阻挡 {bake.BlockedCount}）。");
            }
            AssetDatabase.SaveAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Selection.activeGameObject = root;

            Debug.Log($"[AStarShipDemo] 场景已生成：{ScenePath} —— 进 Play 后整群船会追鼠标" +
                      $"（{bootstrap.AgentCount} 艘，错峰窗口 {bootstrap.CursorRepathStride} 帧）。");
        }

        /// <summary>地面：顶面正好在 y = 0 的方块，带 Collider（烘焙靠它）。</summary>
        private static void BuildGround(Transform parent, Material material)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(parent, false);
            ground.transform.localPosition = new Vector3(FieldWidth * 0.5f, -0.5f, FieldDepth * 0.5f);
            ground.transform.localScale = new Vector3(FieldWidth, 1f, FieldDepth);
            ApplySceneMaterial(ground, material);
        }

        /// <summary>障碍：外圈墙 + 内部横竖墙，每块都是带 Collider 的 Cube。</summary>
        private static void BuildObstacles(Transform parent, Material material)
        {
            var root = new GameObject("Obstacles");
            root.transform.SetParent(parent, false);

            for (var i = 0; i < s_Obstacles.Length; i++)
            {
                var obstacle = s_Obstacles[i];
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = obstacle.Name;
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = new Vector3(obstacle.Center.x, WallHeight * 0.5f, obstacle.Center.y);
                cube.transform.localScale = new Vector3(obstacle.Size.x, WallHeight, obstacle.Size.y);
                ApplySceneMaterial(cube, material);
            }
        }

        private static void BuildLight()
        {
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static void BuildCamera()
        {
            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            camera.fieldOfView = 60f;
            camera.farClipPlane = 1000f;

            AStarShipDemoBootstrap.SuggestCameraPose(FieldWidth, FieldDepth,
                                                     out var position, out var rotation);
            cameraObject.transform.SetPositionAndRotation(position, rotation);
            cameraObject.AddComponent<AudioListener>();
        }

        /// <summary>取网格资产；不存在时按场地参数新建一份（范围正好盖住地面，格边长 1）。</summary>
        private static AStarGridAsset LoadOrCreateGridAsset()
        {
            var asset = AssetDatabase.LoadAssetAtPath<AStarGridAsset>(GridAssetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<AStarGridAsset>();
                AssetDatabase.CreateAsset(asset, GridAssetPath);
            }

            asset.ApplyConfig(new AStarConfig
            {
                Bounds = new AABB
                {
                    Center = new Vector3(FieldWidth * 0.5f, 0f, FieldDepth * 0.5f),
                    Extents = new Vector3(FieldWidth * 0.5f, 1f, FieldDepth * 0.5f),
                },
                CellSize = CellSize,
                Neighbourhood = AStarNeighbourhood.Eight,
                AllowCornerCutting = false,
                HeuristicWeight = 1f,
                MaxVisitedNodes = 0,
                SmoothPath = true,
            });
            asset.ResizeIfNeeded();
            return asset;
        }

        /// <summary>给方块赋对应角色的材质资产。</summary>
        private static void ApplySceneMaterial(GameObject go, Material material)
        {
            var renderer = go.GetComponent<Renderer>();
            if (material == null || renderer == null)
            {
                return;
            }
            renderer.sharedMaterial = material;
        }

        /// <summary>重建（或补齐）场景用的两个材质资产：地面与障碍物各一个 URP Lit 材质，颜色写在资产上。</summary>
        [MenuItem("Tools/DOTSUtils/A* 船群演示材质（重建）")]
        private static void RebuildSceneMaterials()
        {
            var floor = EnsureMaterial(FloorMaterialPath, "Floor", FloorColor);
            var wall = EnsureMaterial(WallMaterialPath, "Wall", WallColor);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (floor == null || wall == null)
            {
                Debug.LogError("[AStarShipDemo] 材质重建失败：找不到 URP Lit shader。");
                return;
            }

            Debug.Log($"[AStarShipDemo] 材质已就绪：{FloorMaterialPath}、{WallMaterialPath}。");
        }

        /// <summary>
        /// 取材质资产；不存在时新建一份 URP Lit 材质，并把颜色写进资产（<c>_BaseColor</c> / <c>_Color</c>）。
        /// </summary>
        private static Material EnsureMaterial(string path, string materialName, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[AStarShipDemo] 找不到 URP Lit shader，无法建材质。");
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                if (!AssetDatabase.IsValidFolder(MaterialFolder))
                {
                    AssetDatabase.CreateFolder("Assets/Pathfinding", "Materials");
                }

                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.name = materialName;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }

            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
