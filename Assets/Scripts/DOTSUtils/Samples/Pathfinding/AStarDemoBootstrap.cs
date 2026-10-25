using System.Collections.Generic;
using DOTSUtils.Benchmark;
using DOTSUtils.Pathfinding;
using DOTSUtils.RenderBridge;
using DOTSUtils.Rvo;
using DOTSUtils.Spatial;
using DOTSUtils.Visibility;
using DOTSUtils.Worlds;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// A* 演示的入口（挂在场景里的一个根对象上，场地与 agent 全在运行时生成）：
    /// <list type="number">
    /// <item>按内置布局建可走掩码，并复核整片场地只有一个连通分量；</item>
    /// <item>配置 A* 网格、Rvo 与 Spatial 参数，建一个只装演示所需模块的 World，
    /// 再用 <c>DotsWorldManager.AddModule</c> 装配演示模块；</item>
    /// <item>复用 benchmark 的纯 ECS 生成路径摆出 <see cref="AgentCount"/> 个 agent 实体
    /// （渲染走 Entities Graphics，不为每个 agent 建 GameObject）；</item>
    /// <item>生成地板与墙体这些纯视觉的 GameObject；</item>
    /// <item><see cref="Update"/> 里在主线程把鼠标在地面上的落点写进
    /// <see cref="AStarDemoCursorTarget"/> 单例，跟路径系统据此让蜂群追鼠标。</item>
    /// </list>
    /// 网格掩码的阻挡边界会取成闭合多边形，再按环拆成静态障碍物线段写进 Rvo（<see cref="SyncObstacles"/>）。
    /// 演示自建 World 并接管 PlayerLoop（与 benchmark 示例同一做法），所以默认 World 会暂停更新。
    /// </summary>
    [AddComponentMenu("DOTSUtils/Samples/A* Demo Bootstrap")]
    public class AStarDemoBootstrap : MonoBehaviour
    {
        /// <summary>演示自建 World 的名字。</summary>
        public const string WorldName = "AStarDemoWorld";

        /// <summary>演示模块名（<see cref="DotsAStarDemoModule"/>）。</summary>
        private const string k_ModuleName = "AStarDemo";

        /// <summary>网格宽（格数，对应世界 x）。</summary>
        [Header("网格")]
        public int Width = 96;

        /// <summary>网格高（格数，对应世界 z）。</summary>
        public int Height = 64;

        /// <summary>格子边长（世界单位）；96×64 格、边长 2 ⇒ 场地 192×128。</summary>
        public float CellSize = 2f;

        /// <summary>4 邻还是 8 邻。</summary>
        public AStarNeighbourhood Neighbourhood = AStarNeighbourhood.Eight;

        /// <summary>8 邻时是否允许贴角穿过。</summary>
        public bool AllowCornerCutting;

        /// <summary>用哪种算法求解：A* 或 Dijkstra。</summary>
        [Tooltip("用哪种算法求解：A* 或 Dijkstra。")]
        public PathfindingAlgorithm Algorithm = PathfindingAlgorithm.AStar;

        /// <summary>是否对路径做视线拉直。</summary>
        public bool SmoothPath = true;

        /// <summary>agent 数量（每个 agent 是一个 ECS 实体，不是 GameObject）。</summary>
        [Header("Agent")]
        [Range(1, 100000)] public int AgentCount = 10000;

        /// <summary>agent 巡航速度（世界单位/秒）。</summary>
        [Range(0.1f, 50f)] public float AgentSpeed = 4f;

        /// <summary>agent 的 Rvo 碰撞半径。</summary>
        [Range(0.05f, 5f)] public float AgentRadius = 0.4f;

        /// <summary>agent 所在水平面的高度（y）；默认让球正好站在地板上。</summary>
        public float AgentPlaneHeight = 0.4f;

        /// <summary>首次请求路径前的随机延迟上限（秒）：一万个 agent 不要在同一帧一起请求路径，网格大时 A* 会卡。</summary>
        [Range(0f, 30f)] public float InitialRequestDelay = 3f;

        /// <summary>新目标与当前位置的最小距离（世界单位）：避免挑到脚底下的格子。</summary>
        [Range(0f, 400f)] public float MinGoalDistance = 24f;

        /// <summary>随机种子（0 按 1 处理）。</summary>
        public uint RandomSeed = 20240501u;

        /// <summary>Rvo 的时间视野 τ（秒），越大越早开始避让。</summary>
        [Header("Rvo")]
        [Range(0.1f, 10f)] public float RvoTimeHorizon = 2f;

        /// <summary>Rvo 的邻居判定距离（世界单位）。</summary>
        [Range(0f, 64f)] public float RvoNeighborDistance = 5f;

        /// <summary>每个 agent 最多参与求解的邻居数（内核上限 64）。</summary>
        [Range(0, 64)] public int RvoMaxNeighbors = 8;

        /// <summary>是否把网格掩码的阻挡边界取成闭合多边形、再按环拆成线段喂给 Rvo（关掉 = 不做静态避障）。</summary>
        public bool UseRvoObstacles = false;   // 暂不开：等 Rvo 内核接上多边形障碍物之后一起打开

        /// <summary>静态障碍物线段的时间视野 τ_obst（秒）；默认值与 <c>RvoConfig.Default.TimeHorizonObst</c> 相同。</summary>
        [Range(0.1f, 10f)] public float ObstacleTimeHorizon = 2f;

        /// <summary>视口外的 agent 是否跳过 Rvo 求解；关掉 = 全部 agent 每帧都参与避障。</summary>
        public bool CullOutsideView;

        /// <summary>视口裁剪的判定余量（世界单位）。</summary>
        [Range(0f, 20f)] public float CullMargin = 1f;

        /// <summary>空间索引的最大深度（上限 7）。</summary>
        [Header("Spatial 索引")]
        [Range(1, 7)] public int SpatialMaxDepth = 6;

        /// <summary>单个叶节点最多装多少元素，超出就分裂。</summary>
        [Range(1, 64)] public int SpatialMaxLeafElements = 16;

        /// <summary>索引范围在场地外多留的余量（世界单位），避免被挤到场地边的 agent 越界丢索引。</summary>
        [Range(0f, 200f)] public float SpatialPadding = 16f;

        /// <summary>agent 的渲染源对象；留空时运行时生成一个球。</summary>
        [Header("渲染")]
        public GameObject SourceAgent;

        /// <summary>agent 的材质列表（每个 agent 随机取一个）；留空时按 <see cref="MaterialVariants"/> 生成。</summary>
        public Material[] Materials;

        /// <summary>自动生成材质时的变体数。</summary>
        [Range(1, 32)] public int MaterialVariants = 8;

        /// <summary>用 Debug.DrawLine 画路径的 agent 个数（0 = 不画）。</summary>
        [Header("可视化")]
        [Range(0, 256)] public int DebugPathCount = 16;

        /// <summary>是否让全体 agent 以鼠标在地面上的落点为目标（关掉 = 原来的随机游走）。</summary>
        [Header("鼠标跟随")]
        public bool FollowCursor = true;

        /// <summary>鼠标目标周围随机偏移的半径（世界单位）；0 = 所有 agent 精确指向同一点。</summary>
        [Range(0f, 64f)] public float CursorSpreadRadius = 8f;

        /// <summary>鼠标目标换版本后的重规划错峰步长：每帧只让约 1/Stride 的 agent 响应。</summary>
        [Range(1, 512)] public int CursorRepathStride = 64;

        /// <summary>是否画出鼠标目标标记（十字 + 目标格方框 + 竖线 + 偏移半径圈）。</summary>
        public bool DebugCursorMarker = true;

        /// <summary>是否每帧汇总 <see cref="AStarDemoStats"/> 单例（一万个 agent 时可关掉省一次主线程拷贝）。</summary>
        public bool StatsEnabled = true;

        /// <summary>是否生成地板与墙体的视觉对象（它们不参与寻路与避障，只是给人看的）。</summary>
        [Header("场地外观")]
        public bool BuildVisuals = true;

        /// <summary>自建 World；未创建时为 <c>null</c>。</summary>
        private World m_World;

        /// <summary>取鼠标射线用的主相机；为 <c>null</c> 时重新取一次。</summary>
        private Camera m_Camera;

        /// <summary>鼠标目标单例实体；World 重建后重新取。</summary>
        private Entity m_CursorEntity;

        /// <summary>鼠标目标版本计数，只在落点移动超过一格时加一。</summary>
        private int m_CursorVersion;

        /// <summary>运行时建的可走掩码；鼠标落点靠它吸附到可走格。</summary>
        private byte[] m_Mask;

        private RenderMeshPool m_Pool = new();
        private Material[] m_Materials = new Material[0];
        private Material m_FloorMaterial;
        private Material m_WallMaterial;
        private readonly List<Entity> m_Agents = new();
        private GameObject m_SourceObject;
        private bool m_OwnSourceObject;
        private bool m_OwnMaterials;

        /// <summary>上一次写静态障碍物线段时用的网格建表版本（<c>int.MinValue</c> = 还没写过）。</summary>
        private int m_ObstacleBuildVersion = int.MinValue;

        /// <summary>当前写进 Rvo 的静态障碍物线段条数。</summary>
        private int m_ObstacleSegmentCount;

        /// <summary>拿不到障碍物写入目标的警告是否已经打过（避免每帧刷屏）。</summary>
        private bool m_ObstacleWarningIssued;

        /// <summary>
        /// 内置布局的可走掩码（外圈是墙，内部三道带缺口的墙）。
        /// 行主序（<c>index = z * width + x</c>），1 = 可走。
        /// </summary>
        public static byte[] BuildMask(int width, int height) => AStarDemoGridMask.Build(width, height);

        /// <summary>当前写进 Rvo 的静态障碍物线段条数（0 = 还没写或网格里没有阻挡边界）。</summary>
        public int RvoObstacleSegmentCount => m_ObstacleSegmentCount;

        /// <summary>生成一个带颜色的材质（URP Lit，退回 Standard / Sprites）。</summary>
        public static Material MakeMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            if (shader == null)
            {
                return null;
            }

            var material = new Material(shader);
            // URP Lit 的主色是 _BaseColor，内置管线是 _Color：谁存在就设谁。
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
            return material;
        }

        /// <summary>
        /// 俯视相机的建议位姿：把宽 <paramref name="fieldWidth"/>、深 <paramref name="fieldDepth"/> 的场地
        /// 整片框进视野（菜单生成演示场景时用它摆相机）。
        /// </summary>
        public static void SuggestCameraPose(float fieldWidth, float fieldDepth,
                                             out Vector3 position, out Quaternion rotation)
        {
            const float pitchDegrees = 65f;
            const float halfFovDegrees = 30f;   // 对应相机默认的 60° 垂直视场
            const float depthPadding = 1.35f;   // 前后各留一点余量

            var farTan = Mathf.Tan(Mathf.Deg2Rad * (pitchDegrees - halfFovDegrees));
            var nearTan = Mathf.Tan(Mathf.Deg2Rad * (pitchDegrees + halfFovDegrees));
            // 每单位机高覆盖的地面纵深（俯角 + 视场决定）。
            var coverage = 1f / farTan - 1f / nearTan;
            var height = fieldDepth * depthPadding / Mathf.Max(coverage, 1e-3f);
            // 覆盖区间中点相对「相机正下方地面点」的偏移。
            var midOffset = 0.5f * height * (1f / farTan + 1f / nearTan);

            position = new Vector3(fieldWidth * 0.5f, height, fieldDepth * 0.5f - midOffset);
            rotation = Quaternion.Euler(pitchDegrees, 0f, 0f);
        }

        private void Awake()
        {
            var mask = AStarDemoGridMask.Build(Width, Height);
            m_Mask = mask;

            // 连通性自检：整片场地必须只有一个可走分量（随机目标才都走得到）。
            var components = AStarDemoGridMask.CountComponents(mask, Width, Height);
            if (components != 1)
            {
                Debug.LogError($"[AStarDemo] 可走掩码有 {components} 个连通分量（应为 1）：agent 会被墙隔断。");
            }

            ApplySettings(mask);
            CreateWorldIfNeeded();

            m_ObstacleBuildVersion = int.MinValue;
            m_ObstacleSegmentCount = 0;
            m_ObstacleWarningIssued = false;

            if (m_World != null && m_World.IsCreated)
            {
                EnsureCursorTarget();
                SpawnAgents(mask);
                SyncObstacles();
            }

            if (BuildVisuals)
            {
                BuildFloor();
                BuildWalls(mask);
            }
        }

        private void OnDestroy()
        {
            m_Agents.Clear();

            if (m_World != null && m_World.IsCreated)
            {
                // 模块组与全部实体随 World 一起回收。
                DotsWorldManager.DestroyWorld(m_World);
            }
            m_World = null;
            m_CursorEntity = Entity.Null;

            // 自建 World 接管过 PlayerLoop，这里把默认 World 放回去。
            var fallback = DotsWorldManager.DefaultWorld;
            if (fallback != null && fallback.IsCreated)
            {
                DotsWorldManager.SetActiveWorld(fallback);
            }

            if (m_OwnMaterials)
            {
                BenchmarkMaterialFactory.DestroyAll(m_Materials);
            }
            m_Materials = new Material[0];
            DestroyMaterial(ref m_FloorMaterial);
            DestroyMaterial(ref m_WallMaterial);

            if (m_OwnSourceObject && m_SourceObject != null)
            {
                Destroy(m_SourceObject);
            }
            m_SourceObject = null;
        }

        /// <summary>
        /// 主线程每帧把鼠标在地面上的落点写进 <see cref="AStarDemoCursorTarget"/> 单例：
        /// 主相机从 <c>Input.mousePosition</c> 发一条射线，取它与 <c>y = AgentPlaneHeight</c> 水平面的交点，
        /// 再把交点吸附到网格里最近的可走格中心。
        /// 落点移动超过一格时把版本加一，跟路径 job 据此错峰重规划。
        /// 射线打不到地面（鼠标指向地平线以上）时保留上一次的落点。
        /// </summary>
        private void Update()
        {
            SyncObstacles();

            if (m_World == null || !m_World.IsCreated || !AStarDemoSettings.Config.FollowCursor)
            {
                return;
            }

            if (!EnsureCursorTarget() || !TryGetCursorGroundPoint(out var ground))
            {
                return;
            }

            var em = m_World.EntityManager;
            var cursor = em.GetComponentData<AStarDemoCursorTarget>(m_CursorEntity);

            var moved = cursor.Valid == 0 || math.distance(
                            new float2(ground.x, ground.z),
                            new float2(cursor.Position.x, cursor.Position.z)) >= math.max(CellSize, 1e-3f);
            if (!moved)
            {
                return;
            }

            m_CursorVersion++;
            cursor.Position = ground;
            cursor.Version = m_CursorVersion;
            cursor.Valid = 1;
            em.SetComponentData(m_CursorEntity, cursor);
        }

        /// <summary>鼠标目标单例：World 里没有就建一个（版本从 0 起算），已存在就取回来。</summary>
        private bool EnsureCursorTarget()
        {
            if (m_World == null || !m_World.IsCreated)
            {
                return false;
            }

            var em = m_World.EntityManager;
            if (m_CursorEntity != Entity.Null && em.Exists(m_CursorEntity) &&
                em.HasComponent<AStarDemoCursorTarget>(m_CursorEntity))
            {
                return true;
            }

            using (var query = em.CreateEntityQuery(ComponentType.ReadOnly<AStarDemoCursorTarget>()))
            {
                if (!query.IsEmptyIgnoreFilter)
                {
                    m_CursorEntity = query.GetSingletonEntity();
                }
                else
                {
                    m_CursorEntity = em.CreateEntity(typeof(AStarDemoCursorTarget));
                    em.SetName(m_CursorEntity, "AStarDemoCursorTarget");
                }
            }

            m_CursorVersion = 0;
            return true;
        }

        /// <summary>
        /// 取鼠标在主相机视线下与 <c>y = AgentPlaneHeight</c> 水平面的交点，再吸附到网格里最近的可走格中心；
        /// 视线与地面平行 / 背向地面、或网格里没有可走格时返回 false。地板是可走掩码之外的纯视觉对象，
        /// 所以直接和平面求交，不做物理射线。
        /// </summary>
        private bool TryGetCursorGroundPoint(out float3 ground)
        {
            ground = default;

            if (m_Camera == null)
            {
                m_Camera = Camera.main;
                if (m_Camera == null)
                {
                    return false;
                }
            }

            var ray = m_Camera.ScreenPointToRay(Input.mousePosition);
            var direction = ray.direction;
            if (direction.y > -1e-4f)
            {
                return false;
            }

            var distance = (AgentPlaneHeight - ray.origin.y) / direction.y;
            if (distance <= 0f)
            {
                return false;
            }

            var point = ray.origin + direction * distance;
            return TrySnapToGrid(new float3(point.x, AgentPlaneHeight, point.z), out ground);
        }

        /// <summary>把地面落点吸附到内置布局网格里最近的可走格中心；没有可走格时返回 false。</summary>
        private bool TrySnapToGrid(float3 point, out float3 snapped)
        {
            snapped = default;

            var field = new float3(Width * CellSize, 0f, Height * CellSize);
            var bounds = new AABB
            {
                Center = field * 0.5f,
                Extents = new float3(field.x * 0.5f, 1f, field.z * 0.5f),
            };

            if (!AStarDemoCursor.TrySnapToWalkableCell(point, bounds, CellSize, m_Mask,
                                                       new int2(Width, Height), out var cellCenter))
            {
                return false;
            }

            snapped = new float3(cellCenter.x, AgentPlaneHeight, cellCenter.z);
            return true;
        }

        /// <summary>
        /// 把 Inspector 上的参数交给各模块的装配入口。
        /// 网格 / 索引参数都是**装配 World 之前**给的：Spatial 那份在系统 OnCreate 时被快照走。
        /// </summary>
        private void ApplySettings(byte[] mask)
        {
            var field = new float3(Width * CellSize, 0f, Height * CellSize);

            // ① A* 网格：掩码 + 参数（改这里的值会让系统重建网格）。
            AStarSettings.Configure(new AStarConfig
            {
                Bounds = new AABB
                {
                    Center = field * 0.5f,
                    Extents = new float3(field.x * 0.5f, 1f, field.z * 0.5f),
                },
                CellSize = CellSize,
                Neighbourhood = Neighbourhood,
                AllowCornerCutting = AllowCornerCutting,
                Algorithm = Algorithm,
                HeuristicWeight = 1f,
                MaxVisitedNodes = 0,
                SmoothPath = SmoothPath,
            }, mask);

            // ② 空间索引（Rvo 的邻居来源）：范围取场地 + 余量，叶节点粒度才与 agent 密度匹配。
            var tree = SpatialTreeConfig.Default;
            tree.Bounds = new AABB
            {
                Center = new float3(field.x * 0.5f, 0f, field.z * 0.5f),
                Extents = new float3(field.x * 0.5f + SpatialPadding, 16f, field.z * 0.5f + SpatialPadding),
            };
            tree.MaxDepth = SpatialMaxDepth;
            tree.MaxLeafElements = (short)SpatialMaxLeafElements;
            tree.Mode = SpatialUpdateMode.Incremental;
            tree.WarnOnOutOfBounds = true;

            // ③ Rvo 参数（顺带把 Spatial 参数一起给上）。
            var rvo = RvoConfig.Default;
            rvo.TimeHorizon = RvoTimeHorizon;
            rvo.NeighborDist = RvoNeighborDistance;
            rvo.MaxNeighbors = RvoMaxNeighbors;
            // 位置由跟路径系统积分，Rvo 只算速度。
            rvo.IntegratePosition = false;
            rvo.UseSpatialIndex = true;
            // 静态障碍物线段：掩码的阻挡边界，建 World 之后由 SyncObstacles 写入。
            rvo.UseObstacles = UseRvoObstacles;
            rvo.TimeHorizonObst = math.max(0.1f, ObstacleTimeHorizon);
            RvoSettings.Configure(rvo, SpatialIndexKind.Quadtree, tree);

            // ④ 可见性：关掉裁剪时所有 agent 每帧都参与 Rvo 求解。
            var visibility = VisibilityConfig.Default;
            visibility.Enabled = CullOutsideView;
            visibility.Margin = CullMargin;
            VisibilitySettings.Configure(visibility);

            // ⑤ 演示自己的运行期参数（跟路径系统与调试画线系统每帧读）。
            var config = AStarDemoConfig.Default;
            config.PlaneHeight = AgentPlaneHeight;
            config.MinGoalDistance = MinGoalDistance;
            config.DebugPathCount = DebugPathCount;
            config.DebugLineHeight = AgentPlaneHeight + 0.3f;
            config.FollowCursor = FollowCursor;
            config.CursorSpreadRadius = math.max(0f, CursorSpreadRadius);
            config.CursorRepathStride = math.max(1, CursorRepathStride);
            config.DebugCursorMarker = DebugCursorMarker;
            config.StatsEnabled = StatsEnabled;
            AStarDemoSettings.Config = config;
        }

        /// <summary>
        /// 建演示用的 World：空模板 + 显式装配演示模块（模块的 Requires 会递归装上
        /// RenderBridge / AStar / Rvo → Spatial + Visibility）。
        /// </summary>
        private void CreateWorldIfNeeded()
        {
            if (m_World != null && m_World.IsCreated)
            {
                return;
            }

            // CreateWorld 不允许重名：同名的 World 先销毁。
            if (DotsWorldManager.TryGetWorld(WorldName, out var stale) && stale != null && stale.IsCreated)
            {
                DotsWorldManager.DestroyWorld(stale);
            }

            m_World = DotsWorldManager.CreateWorld(WorldName, string.Empty);
            if (!DotsWorldManager.AddModule(m_World, k_ModuleName))
            {
                Debug.LogWarning($"[AStarDemo] 模块 \"{k_ModuleName}\" 装配失败：agent 不会移动" +
                                 "（原因见上方 DotsWorld 的警告）。");
            }
        }

        private void SpawnAgents(byte[] mask)
        {
            m_SourceObject = SourceAgent != null ? SourceAgent : CreateDefaultSource();
            m_OwnSourceObject = SourceAgent == null;

            if (Materials != null && Materials.Length > 0)
            {
                m_Materials = Materials;
                m_OwnMaterials = false;
            }
            else
            {
                m_Materials = CreateDefaultMaterials();
                m_OwnMaterials = true;
            }

            // 材质已就位后再建池：池里缓存的 RenderMeshArray 引用材质。
            m_Pool = new RenderMeshPool();

            var request = new AStarDemoSpawnRequest
            {
                Source = m_SourceObject,
                Materials = m_Materials,
                Count = AgentCount,
                Mask = mask,
                Width = Width,
                Height = Height,
                CellSize = CellSize,
                PlaneHeight = AgentPlaneHeight,
                AgentRadius = AgentRadius,
                Speed = AgentSpeed,
                InitialDelayMax = InitialRequestDelay,
                Seed = RandomSeed,
                DebugPathCount = DebugPathCount,
                RepathStride = CursorRepathStride,
            };

            var spawned = AStarDemoSpawner.Spawn(m_World, request, m_Pool, m_Agents);
            Debug.Log($"[AStarDemo] 生成 {spawned}/{AgentCount} 个 ECS agent：" +
                      $"网格 {Width}×{Height}×{CellSize}、Rvo 半径 {AgentRadius}、速度 {AgentSpeed}。");

            // 源对象只是渲染模板：生成完藏起来，场景里不再多画一个球。
            if (m_SourceObject != null)
            {
                m_SourceObject.SetActive(false);
            }
        }

        /// <summary>
        /// 把网格掩码的阻挡边界取成闭合多边形、按环拆成线段写进 Rvo 的静态障碍物：
        /// 网格按 <see cref="AStarSettings.Version"/> 重建、建表版本变化时做一次，已经按当前版本写过就直接返回。
        /// 拿不到网格 / 提取不出多边形 / 障碍物单例不可写时打一条警告并继续。
        /// </summary>
        private void SyncObstacles()
        {
            if (!UseRvoObstacles || m_World == null || !m_World.IsCreated)
            {
                return;
            }

            if (m_ObstacleBuildVersion == AStarSettings.Version)
            {
                return;
            }

            if (!AStarAccess.TryGetSingletonData(m_World, out var data) || !data.Grid.IsCreated)
            {
                WarnObstaclesOnce("[AStarDemo] 拿不到 A* 网格：静态障碍物这次没有写入 Rvo（agent 照常跑）。");
                return;
            }

            if (data.BuildVersion != AStarSettings.Version)
            {
                // 网格还没按当前参数重建：下一帧再看。
                return;
            }

            var count = PathObstacleBuilder.BuildPolygons(in data.Grid, out var vertices, out var polygons);
            if (count <= 0)
            {
                m_ObstacleBuildVersion = data.BuildVersion;
                m_ObstacleSegmentCount = 0;
                RvoAccess.ClearObstacles(m_World);
                Debug.LogWarning("[AStarDemo] 网格里没有抽出阻挡边界：静态障碍物为 0（agent 照常跑）。");
                return;
            }

            // Rvo 的障碍物缓冲当前收线段：按环把每个多边形拆成「顶点 → 下一顶点」的边，每条边只写一次。
            var edgeCount = 0;
            for (var i = 0; i < polygons.Length; i++)
            {
                edgeCount += polygons[i].Count;
            }

            var converted = new RvoObstacleSegment[edgeCount];
            var cursor = 0;
            for (var i = 0; i < polygons.Length; i++)
            {
                var polygon = polygons[i];
                for (var v = 0; v < polygon.Count; v++)
                {
                    var index = polygon.Start + v;
                    converted[cursor++] = new RvoObstacleSegment
                    {
                        A = vertices[index],
                        B = vertices[polygon.NextVertex(index)],
                    };
                }
            }

            if (!RvoAccess.SetObstacles(m_World, converted))
            {
                WarnObstaclesOnce("[AStarDemo] 障碍物单例暂时不可写：静态障碍物下一帧重试（agent 照常跑）。");
                return;
            }

            m_ObstacleBuildVersion = data.BuildVersion;
            m_ObstacleSegmentCount = edgeCount;
            Debug.Log($"[AStarDemo] 写入静态障碍物 {count} 个闭环（{vertices.Length} 个顶点、按环拆成 {edgeCount} 条线段；" +
                      $"内侧 = 顶点序左侧、τ_obst {math.max(0.1f, ObstacleTimeHorizon)} 秒、网格建表版本 {data.BuildVersion}）。");
        }

        /// <summary>障碍物相关警告只打一条（后续同类失败静默重试）。</summary>
        private void WarnObstaclesOnce(string message)
        {
            if (m_ObstacleWarningIssued)
            {
                return;
            }
            m_ObstacleWarningIssued = true;
            Debug.LogWarning(message);
        }

        /// <summary>运行时生成默认渲染源：空根 + 一个球（球的大小由子物体的局部缩放给）。</summary>
        private GameObject CreateDefaultSource()
        {
            var root = new GameObject("AStarDemoAgentSource") { hideFlags = HideFlags.HideAndDontSave };

            var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "Body";
            body.hideFlags = HideFlags.HideAndDontSave;
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = Vector3.zero;
            // 内置球网格半径 0.5，缩放到「半径 = AgentRadius」。
            body.transform.localScale = Vector3.one * (AgentRadius * 2f);

            var collider = body.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var bodyRenderer = body.GetComponent<MeshRenderer>();
            if (bodyRenderer != null)
            {
                // agent 不投影：一万个投影球的影子 pass 开销大。
                bodyRenderer.shadowCastingMode = ShadowCastingMode.Off;
                bodyRenderer.receiveShadows = false;
            }

            return root;
        }

        /// <summary>生成默认材质变体（与 benchmark 用同一套工厂，只改 _BaseColor）。</summary>
        private Material[] CreateDefaultMaterials()
        {
            var shader = Shader.Find("Custom/CustomLit");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            if (shader == null)
            {
                Debug.LogWarning("[AStarDemo] 找不到可用的 agent shader，无法生成材质。");
                return new Material[0];
            }

            return BenchmarkMaterialFactory.Create(MaterialVariants, shader);
        }

        /// <summary>地板：一块顶面正好在 y = 0 的方板，靠边还能看到场地边界。</summary>
        private void BuildFloor()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(transform, false);
            floor.transform.position = new Vector3(Width * CellSize * 0.5f, -0.5f, Height * CellSize * 0.5f);
            floor.transform.localScale = new Vector3(Width * CellSize, 1f, Height * CellSize);
            DestroyCollider(floor);

            m_FloorMaterial = MakeMaterial(new Color(0.16f, 0.17f, 0.2f));
            var floorRenderer = floor.GetComponent<Renderer>();
            if (floorRenderer != null && m_FloorMaterial != null)
            {
                floorRenderer.sharedMaterial = m_FloorMaterial;
            }
        }

        /// <summary>
        /// 墙体：外圈四块长墙 + 内部每个阻挡格一个小方块。
        /// 墙体只是视觉，寻路与避障用的是掩码。
        /// </summary>
        private void BuildWalls(byte[] mask)
        {
            var root = new GameObject("Walls");
            root.transform.SetParent(transform, false);

            m_WallMaterial = MakeMaterial(new Color(0.32f, 0.34f, 0.4f));
            var wallHeight = 0.5f;

            // 外圈：南北两块横墙 + 东西两块纵墙。
            var fieldX = Width * CellSize;
            var fieldZ = Height * CellSize;
            CreateWallCube(root.transform, "Wall_South", new Vector3(fieldX * 0.5f, wallHeight * 0.5f, CellSize * 0.5f),
                           new Vector3(fieldX, wallHeight, CellSize));
            CreateWallCube(root.transform, "Wall_North", new Vector3(fieldX * 0.5f, wallHeight * 0.5f, fieldZ - CellSize * 0.5f),
                           new Vector3(fieldX, wallHeight, CellSize));
            CreateWallCube(root.transform, "Wall_West", new Vector3(CellSize * 0.5f, wallHeight * 0.5f, fieldZ * 0.5f),
                           new Vector3(CellSize, wallHeight, fieldZ));
            CreateWallCube(root.transform, "Wall_East", new Vector3(fieldX - CellSize * 0.5f, wallHeight * 0.5f, fieldZ * 0.5f),
                           new Vector3(CellSize, wallHeight, fieldZ));

            // 内部墙：逐格。
            for (var z = 1; z < Height - 1; z++)
            {
                for (var x = 1; x < Width - 1; x++)
                {
                    if (mask[z * Width + x] != 0)
                    {
                        continue;
                    }

                    CreateWallCube(root.transform, $"Wall_{x}_{z}",
                                   new Vector3((x + 0.5f) * CellSize, wallHeight * 0.5f, (z + 0.5f) * CellSize),
                                   new Vector3(CellSize * 0.98f, wallHeight, CellSize * 0.98f));
                }
            }
        }

        private void CreateWallCube(Transform parent, string name, Vector3 position, Vector3 scale)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.SetParent(parent, false);
            wall.transform.position = position;
            wall.transform.localScale = scale;
            DestroyCollider(wall);

            var wallRenderer = wall.GetComponent<Renderer>();
            if (wallRenderer != null && m_WallMaterial != null)
            {
                wallRenderer.sharedMaterial = m_WallMaterial;
            }
        }

        private static void DestroyCollider(GameObject go)
        {
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
        }

        private static void DestroyMaterial(ref Material material)
        {
            if (material == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Destroy(material);
            }
            else
            {
                DestroyImmediate(material);
            }
            material = null;
        }
    }
}
