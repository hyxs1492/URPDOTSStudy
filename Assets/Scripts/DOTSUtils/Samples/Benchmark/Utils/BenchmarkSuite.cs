using System.Collections.Generic;
using DOTSUtils.RenderBridge;
using DOTSUtils.Rvo;
using DOTSUtils.Spatial;
using DOTSUtils.Visibility;
using DOTSUtils.Worlds;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace DOTSUtils.Benchmark
{
    /// <summary>benchmark 用的生成路径。</summary>
    public enum BenchmarkSpawnMode
    {
        /// <summary>不经过桥：直接建「组实体 + 每层实体」的完整 LOD 层级。</summary>
        PureEcs,

        /// <summary>经过桥：Instantiate 源对象，父实体的变换由实体侧驱动。</summary>
        ThroughBridge,
    }

    /// <summary>
    /// Benchmark 示例（不是模块）：自己开一个 World，往里装 RenderBridge + Visibility + Rvo + Benchmark
    /// 模块（Spatial 是 Rvo 的硬依赖，由框架自动装配），用 <c>TestShip</c> 预制体批量生成船，
    /// 对比两条生成路径的批次数与帧时间。
    ///
    /// 用法：Play 模式 → Inspector 调参 → Spawn；换 <see cref="Mode"/> 再 Spawn 即可对比。
    /// Clear 只清船，Destroy World 拆掉整个世界。自建 World 会接管 PlayerLoop（期间默认 World 暂停更新），
    /// 销毁时自动还给默认 World。
    /// </summary>
    [AddComponentMenu("DOTS Render/Benchmark Sample")]
    public sealed class BenchmarkSuite : MonoBehaviour
    {
        [Header("生成")]
        [Tooltip("源对象：需含 LODGroup。留空时自动加载 TestShip 预制体。")]
        public GameObject SourceShip;

        [Range(1, 500000)] public int ShipCount = 200;

        [Range(1, 32)] public int MaterialVariants = 8;

        public BenchmarkSpawnMode Mode = BenchmarkSpawnMode.PureEcs;

        /// <summary>
        /// 活动范围（立方体半边长），单位：世界单位。船的初始位置、移动边界、Spatial 索引范围都用它，
        /// 三者必须一致；范围是立方体（x=y=z），四叉树投影到 xz 之后也是正方形。
        /// </summary>
        [Tooltip("活动范围（立方体半边长）。船的生成位置、移动边界、Spatial 索引范围都用它。")]
        [Range(1f, 2000f)] public float SpawnRadius = 200f;

        /// <summary>船的 RVO 碰撞半径（&gt; 0），所有船共用；只在生成/转换时写进 <c>RvoAgent</c>，运行时改需重新生成才生效。</summary>
        [Tooltip("船的 RVO 碰撞半径（> 0）。运行时改动需重新生成船只才生效。")]
        [Range(0.1f, 20f)] public float ShipRadius = 1f;

        [Header("移动")]
        [Range(0f, 200f)] public float MoveSpeed = 10f;

        /// <summary>转向角速度，单位：度/秒。越小转得越慢。</summary>
        [Range(0f, 720f)] public float TurnSpeedDegPerSec = 120f;

        /// <summary>两次换向之间的间隔范围，单位：秒（填反了会自动交换）。</summary>
        [Range(0.1f, 60f)] public float MinRetargetSeconds = 1f;
        [Range(0.1f, 60f)] public float MaxRetargetSeconds = 10f;

        [Header("Spatial 模块参数")]
        [Tooltip("用哪棵树：四叉树只看 xz，八叉树看完整 xyz。")]
        public SpatialIndexKind SpatialIndex = SpatialIndexKind.Quadtree;

        // 索引范围直接用 SpawnRadius，与船的活动范围保持一致。
        [Tooltip("树的最大深度（上限 7，四叉树与八叉树共用同一套布局）。实践里 6 足够。")]
        [Range(1, 7)] public int SpatialMaxDepth = 6;

        [Tooltip("单个叶节点最多装多少元素，超出就分裂。")]
        [Range(1, 64)] public int SpatialMaxLeafElements = 8;

        [Tooltip("Bulk = 每帧清空重建；Incremental = 只增量应用新增/移动/销毁。")]
        public SpatialUpdateMode SpatialTreeUpdateMode = SpatialUpdateMode.Incremental;

        [Tooltip("首次出现越界元素时打一次 warning（不是每帧打）。")]
        public bool SpatialWarnOnOutOfBounds = true;

        [Header("Rvo 模块参数")]
        [Tooltip("时间视野 τ，单位：秒。越大越早开始避让。")]
        [Range(0.1f, 10f)] public float RvoTimeHorizon = 2f;

        [Tooltip("邻居判定距离，单位：世界单位。")]
        [Range(0f, 64f)] public float RvoNeighborDist = 15f;

        [Tooltip("每个 agent 最多参与求解的邻居数（内核上限 64）。")]
        [Range(0, 64)] public int RvoMaxNeighbors = 10;

        [Tooltip("关掉 = 不查邻居（本帧不避障），用于对照。")]
        public bool RvoUseSpatialIndex = true;

        [Header("视口裁剪")]
        [Tooltip("相机视锥外的船跳过 RVO 求解（只降级：位置仍按上次速度连续推进，不冻结）。关掉用于对照测量。")]
        public bool CullOutsideView = true;

        [Tooltip("判定余量（世界单位）：贴着视口边缘的实体提前算可见，减少求解量的抖变。")]
        [Range(0f, 20f)] public float CullMargin = 1f;

        [Header("显示")]
        public bool ShowOverlay = true;

        /// <summary>自建 World 的名字。</summary>
        public const string WorldName = "BenchmarkWorld";

        /// <summary>自建 World 用的模板名（只含这个例子需要的模块）。</summary>
        private const string k_TemplateName = "BenchmarkSample";

        private const string k_DefaultShipPath = "Assets/Res/Prefabs/Ship/TestShip.prefab";
        private const string kShaderName = "Custom/CustomLit";

        private World m_World;
        private RenderMeshPool m_Pool = new();
        private readonly List<Entity> m_SpawnedEntities = new();
        private readonly List<GameObject> m_SpawnedObjects = new();
        private Material[] m_Materials = new Material[0];

        public int SpawnedShipCount { get; private set; }

        /// <summary>当前自建 World；未创建时为 <c>null</c>。</summary>
        public World SampleWorld => m_World;

        // ------------------------------------------------------------------
        //  World 生命周期
        // ------------------------------------------------------------------

        /// <summary>开一个只装这套例子需要的 World：RenderBridge + Visibility + Rvo + Benchmark。已存在就复用；创建后接管 PlayerLoop。</summary>
        [ContextMenu("Create World")]
        public void CreateWorldIfNeeded()
        {
            if (m_World != null && m_World.IsCreated)
            {
                return;
            }

            // 参数必须在建 World 之前给：Spatial 那份会被 SpatialBuildSystem 在 OnCreate 快照走。
            ApplySettings();

            // 模板只列想要的模块（Rvo 依赖的 Spatial 由框架自动装配）；列出顺序无所谓，装配先按依赖递归、再按 SortKey。
            DotsWorldTemplates.Register(new DotsWorldTemplate(
                k_TemplateName,
                typeof(DotsRenderBridgeModule),
                typeof(DotsVisibilityModule),
                typeof(DotsRvoModule),            // 它依赖的 Spatial 会自动装配
                typeof(DotsBenchmarkModule)));
            m_World = DotsWorldManager.CreateWorld(WorldName, k_TemplateName);
        }

        /// <summary>
        /// 把 Inspector 上的参数一次性交给装配入口：走 <see cref="RvoSettings.Configure"/>，它会连带把
        /// Rvo 依赖的 Spatial 的索引参数一起设好。
        ///
        /// Spatial 那份是装配时参数：改了要 Destroy World 再 Spawn 才生效。
        /// </summary>
        public void ApplySettings()
        {
            var tree = SpatialTreeConfig.Default;
            // 索引范围 = 活动范围（同一个 SpawnRadius）。
            tree.Bounds = new AABB { Center = float3.zero, Extents = new float3(SpawnRadius) };
            tree.MaxDepth = SpatialMaxDepth;
            tree.MaxLeafElements = (short)SpatialMaxLeafElements;
            tree.Mode = SpatialTreeUpdateMode;
            tree.WarnOnOutOfBounds = SpatialWarnOnOutOfBounds;

            var config = RvoConfig.Default;
            config.TimeHorizon = RvoTimeHorizon;
            config.NeighborDist = RvoNeighborDist;
            config.MaxNeighbors = RvoMaxNeighbors;
            // 位置不交给 Rvo 积分：本示例让 BenchmarkRandomMoveSystem 当唯一写入方。
            config.IntegratePosition = false;
            config.UseSpatialIndex = RvoUseSpatialIndex;

            RvoSettings.Configure(config, SpatialIndex, tree);

            // 可见性模块的参数（把「谁在相机视锥里」标成 VisibilityCulledTag）。
            var visibility = VisibilityConfig.Default;
            visibility.Enabled = CullOutsideView;
            visibility.Margin = CullMargin;
            VisibilitySettings.Configure(visibility);
        }

        /// <summary>拆掉自建 World，并把 PlayerLoop 的更新权还给默认 World。</summary>
        [ContextMenu("Destroy World")]
        public void DestroyWorld()
        {
            Clear();

            if (m_World == null)
            {
                return;
            }

            if (m_World.IsCreated)
            {
                DotsWorldManager.DestroyWorld(m_World);
            }
            m_World = null;

            // 自建 World 接管过 PlayerLoop，这里还回去。
            var fallback = DotsWorldManager.DefaultWorld;
            if (fallback != null && fallback.IsCreated)
            {
                DotsWorldManager.SetActiveWorld(fallback);
            }
        }

        private void OnDestroy() => DestroyWorld();

        // ------------------------------------------------------------------
        //  生成 / 清理
        // ------------------------------------------------------------------

        [ContextMenu("Spawn")]
        public void Spawn()
        {
            Clear();

            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Benchmark] 需要进入 Play 模式后生成（生成的是 ECS 实体）。");
                return;
            }

            if (SourceShip == null)
            {
                SourceShip = LoadDefaultShip();
            }
            if (SourceShip == null)
            {
                Debug.LogWarning($"[Benchmark] 请指定 SourceShip，或确认 {k_DefaultShipPath} 存在。");
                return;
            }

            CreateWorldIfNeeded();
            ApplySettings();

            m_Materials = BenchmarkMaterialFactory.Create(MaterialVariants, Shader.Find(kShaderName));

            if (Mode == BenchmarkSpawnMode.PureEcs)
            {
                SpawnedShipCount = BenchmarkShipFactory.SpawnPureEcs(
                    m_World, SourceShip, ShipCount, m_Materials, SpawnRadius, RandomSeed(), m_Pool, m_SpawnedEntities);
            }
            else
            {
                m_SpawnedObjects.AddRange(BenchmarkShipFactory.SpawnThroughBridge(
                    SourceShip, m_Materials, ShipCount, SpawnRadius, RandomSeed()));
                SpawnedShipCount = m_SpawnedObjects.Count;
                // 组件由实例上的 ShipController 在实体创建成功时挂上，这里不需要再补标。
            }

            Debug.Log($"[Benchmark] Spawn 完成：mode={Mode} ships={SpawnedShipCount} " +
                      $"materials={m_Materials.Length} entities={m_SpawnedEntities.Count}");
        }

        [ContextMenu("Clear")]
        public void Clear()
        {
            if (m_World != null && m_World.IsCreated)
            {
                BenchmarkShipFactory.DestroyEntities(m_World.EntityManager, m_SpawnedEntities);
            }

            foreach (var go in m_SpawnedObjects)
            {
                if (go == null)
                {
                    continue;
                }
                if (Application.isPlaying)
                {
                    Destroy(go);
                }
                else
                {
                    DestroyImmediate(go);
                }
            }
            m_SpawnedObjects.Clear();

            BenchmarkMaterialFactory.DestroyAll(m_Materials);
            m_Materials = new Material[0];

            // 材质已销毁，重建池（池里缓存的 RenderMeshArray 引用的是旧材质）。
            m_Pool = new RenderMeshPool();
            SpawnedShipCount = 0;
        }

        private void Update()
        {
            // 两条路径共用同一套移动参数。
            BenchmarkRandomMoveSystem.MoveSpeed = MoveSpeed;
            BenchmarkShipComponents.ShipRadius = ShipRadius;
            // 移动边界 = 活动范围（与 Spatial 的索引范围是同一个值）。
            BenchmarkRandomMoveSystem.Bounds = SpawnRadius;
            BenchmarkRandomMoveSystem.TurnSpeedDegPerSec = TurnSpeedDegPerSec;
            // 允许把上下限填反：这里归一化。
            BenchmarkRandomMoveSystem.MinRetargetSeconds = Mathf.Min(MinRetargetSeconds, MaxRetargetSeconds);
            BenchmarkRandomMoveSystem.MaxRetargetSeconds = Mathf.Max(MinRetargetSeconds, MaxRetargetSeconds);
        }

        private void OnGUI()
        {
            if (!ShowOverlay)
            {
                return;
            }

            var world = m_World != null && m_World.IsCreated ? m_World : null;
            var snapshot = BenchmarkMetrics.Read(world);
            var entityCount = world != null ? world.EntityManager.UniversalQuery.CalculateEntityCount() : 0;

            // 全部用固定坐标绘制。
            const float x = 12f;
            const float y = 12f;
            const float width = 540f;
            const float lineHeight = 20f;
            GUI.Box(new Rect(x, y, width, 176f), string.Empty);

            if (GUI.Button(new Rect(x + 10f, y + 8f, 72f, 24f), "Spawn"))
            {
                Spawn();
            }
            if (GUI.Button(new Rect(x + 88f, y + 8f, 72f, 24f), "Clear"))
            {
                Clear();
            }
            if (GUI.Button(new Rect(x + 166f, y + 8f, 110f, 24f), "Destroy World"))
            {
                DestroyWorld();
            }

            var line = y + 40f;
            GUI.Label(new Rect(x + 10f, line, width - 20f, lineHeight),
                $"World={(world != null ? m_World.Name : "未创建")}   (默认 World 在自建 World 存活期间暂停)");
            line += lineHeight;
            GUI.Label(new Rect(x + 10f, line, width - 20f, lineHeight),
                $"Mode={Mode}  Ships={SpawnedShipCount}  Materials={m_Materials.Length}  Speed={MoveSpeed:0.#}");
            line += lineHeight;
            GUI.Label(new Rect(x + 10f, line, width - 20f, lineHeight),
                $"Entities={entityCount}  (spawned={m_SpawnedEntities.Count})");
            line += lineHeight;
            GUI.Label(new Rect(x + 10f, line, width - 20f, lineHeight), snapshot.StatsAvailable
                ? $"Batches={snapshot.BatchCount}  DrawCmds={snapshot.DrawCommandCount}  " +
                  $"Rendered={snapshot.RenderedInstanceCount}  ChunksAnyLod={snapshot.ChunkCountAnyLod}"
                : "Batches=n/a（仅编辑器内可读 EntitiesGraphicsStats）");
            line += lineHeight;
            GUI.Label(new Rect(x + 10f, line, width - 20f, lineHeight),
                $"Frame {snapshot.FrameMilliseconds:0.0} ms ({snapshot.Fps:0} FPS)");
        }

        private static uint RandomSeed() => (uint)UnityEngine.Random.Range(1, int.MaxValue);

        /// <summary>Inspector 没指定时退回 TestShip 预制体。</summary>
        private static GameObject LoadDefaultShip()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(k_DefaultShipPath);
#else
            return null;
#endif
        }
    }
}
