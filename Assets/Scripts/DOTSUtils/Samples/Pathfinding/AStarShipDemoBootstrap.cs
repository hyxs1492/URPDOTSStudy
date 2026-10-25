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

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// A* 船群演示的入口（挂在场景里的一个根对象上，场地是场景里真实的 GameObject）：
    /// <list type="number">
    /// <item>读 <see cref="GridAsset"/>——由编辑器工具从场景烘焙出来的网格资产——把它的参数与掩码推给
    /// <see cref="AStarSettings"/>，可走 / 阻挡完全来自烘焙数据，不在运行时拼掩码；</item>
    /// <item>配置 Rvo 与 Spatial 参数，建一个只装演示所需模块的 World，
    /// 再用 <c>DotsWorldManager.AddModule</c> 装配演示模块；</item>
    /// <item>用 benchmark 的纯 ECS 生成路径，从 <see cref="SourceShip"/>（默认 TestShip.prefab）摆出
    /// <see cref="AgentCount"/> 艘船（渲染走 Entities Graphics，不为每艘船建 GameObject）；</item>
    /// <item><see cref="Update"/> 里在主线程把鼠标在地面上的落点写进 <see cref="AStarDemoCursorTarget"/> 单例，
    /// 跟路径系统据此让整群船追鼠标；落点只按「本组件上一次写进去的点」判断有没有移动，
    /// 因此外部脚本写进去的目标不会被鼠标覆盖。</item>
    /// </list>
    /// 网格掩码的阻挡边界会取成闭合多边形，再按环拆成静态障碍物线段写进 Rvo（<see cref="SyncObstacles"/>）。
    /// 演示自建 World 并接管 PlayerLoop（与 benchmark 示例同一做法），所以默认 World 会暂停更新。
    /// </summary>
    [AddComponentMenu("DOTSUtils/Samples/A* Ship Demo Bootstrap")]
    public class AStarShipDemoBootstrap : MonoBehaviour
    {
        /// <summary>演示自建 World 的名字。</summary>
        public const string WorldName = "AStarShipDemoWorld";

        /// <summary>演示模块名（<see cref="DotsAStarDemoModule"/>，与 1 万实体演示共用）。</summary>
        private const string k_ModuleName = "AStarDemo";

        /// <summary>鼠标落点由鼠标写入时记在 <c>Writer</c> 上的标记。</summary>
        private const int k_MouseWriterMark = 0;

        /// <summary>鼠标落点由 <see cref="SetCursorTarget"/>（外部脚本 / 调试面板）写入时记在 <c>Writer</c> 上的标记。</summary>
        private const int k_ExternalWriterMark = 1;

        /// <summary>烘焙出来的网格资产；为空时本演示不生成 agent。</summary>
        [Header("网格资产（编辑器工具烘焙）")]
        public AStarGridAsset GridAsset;

        /// <summary>用哪种算法求解：A* 或 Dijkstra。</summary>
        [Tooltip("用哪种算法求解：A* 或 Dijkstra。会覆盖网格资产里的算法。")]
        public PathfindingAlgorithm Algorithm = PathfindingAlgorithm.AStar;

        /// <summary>船的数量（每艘船是一个 ECS 实体，不是 GameObject）。</summary>
        [Header("Agent")]
        [Range(1, 100000)] public int AgentCount = 400;

        /// <summary>船的巡航速度（世界单位/秒）。</summary>
        [Range(0.1f, 50f)] public float AgentSpeed = 5f;

        /// <summary>船的 Rvo 碰撞半径。</summary>
        [Range(0.05f, 5f)] public float AgentRadius = 0.45f;

        /// <summary>船的水平最大边长目标值（世界单位）：按渲染源尺寸等比缩放，约一格（1 世界单位）长。</summary>
        [Range(0.05f, 20f)] public float ShipFootprint = 0.9f;

        /// <summary>船所在水平面的高度（y）；船底略高于地面。</summary>
        public float AgentPlaneHeight = 0.15f;

        /// <summary>首次请求路径前的随机延迟上限（秒）：不要让整群船在同一帧一起请求路径。</summary>
        [Range(0f, 30f)] public float InitialRequestDelay = 1.5f;

        /// <summary>随机目标与当前位置的最小距离（世界单位）：只在鼠标跟随关掉时用到。</summary>
        [Range(0f, 400f)] public float MinGoalDistance = 20f;

        /// <summary>随机种子（0 按 1 处理）。</summary>
        public uint RandomSeed = 20240928u;

        /// <summary>Rvo 的时间视野 τ（秒），越大越早开始避让。</summary>
        [Header("Rvo")]
        [Range(0.1f, 10f)] public float RvoTimeHorizon = 2f;

        /// <summary>Rvo 的邻居判定距离（世界单位）。</summary>
        [Range(0f, 64f)] public float RvoNeighborDistance = 4f;

        /// <summary>每艘船最多参与求解的邻居数（内核上限 64）。</summary>
        [Range(0, 64)] public int RvoMaxNeighbors = 8;

        /// <summary>是否把网格掩码的阻挡边界取成闭合多边形、再按环拆成线段喂给 Rvo（关掉 = 不做静态避障）。</summary>
        public bool UseRvoObstacles = true;

        /// <summary>静态障碍物线段的时间视野 τ_obst（秒）；默认值与 <c>RvoConfig.Default.TimeHorizonObst</c> 相同。</summary>
        [Range(0.1f, 10f)] public float ObstacleTimeHorizon = 2f;

        /// <summary>视口外的船是否跳过 Rvo 求解；关掉 = 全部船每帧都参与避障。</summary>
        public bool CullOutsideView;

        /// <summary>视口裁剪的判定余量（世界单位）。</summary>
        [Range(0f, 20f)] public float CullMargin = 1f;

        /// <summary>空间索引的最大深度（上限 7）。</summary>
        [Header("Spatial 索引")]
        [Range(1, 7)] public int SpatialMaxDepth = 6;

        /// <summary>单个叶节点最多装多少元素，超出就分裂。</summary>
        [Range(1, 64)] public int SpatialMaxLeafElements = 16;

        /// <summary>索引范围在场地外多留的余量（世界单位），避免被挤到场地边的船越界丢索引。</summary>
        [Range(0f, 200f)] public float SpatialPadding = 8f;

        /// <summary>船的渲染源（默认 TestShip.prefab）；为空时不生成 agent。</summary>
        [Header("渲染")]
        public GameObject SourceShip;

        /// <summary>船的材质列表（每艘随机取一个）；留空时按 <see cref="MaterialVariants"/> 生成。</summary>
        public Material[] Materials;

        /// <summary>自动生成材质时的变体数。</summary>
        [Range(1, 32)] public int MaterialVariants = 8;

        /// <summary>用 Debug.DrawLine 画路径的船数（0 = 不画）。</summary>
        [Header("可视化")]
        [Range(0, 256)] public int DebugPathCount = 8;

        /// <summary>是否让整群船以鼠标在地面上的落点为目标（关掉 = 随机目标）。</summary>
        [Header("鼠标跟随")]
        public bool FollowCursor = true;

        /// <summary>鼠标目标周围随机偏移的半径（世界单位）；0 = 所有船精确指向同一点。</summary>
        [Range(0f, 64f)] public float CursorSpreadRadius = 10f;

        /// <summary>鼠标目标换版本后的重规划错峰窗口（帧）：整群船摊到这个窗口里响应。</summary>
        [Range(1, 512)] public int CursorRepathStride = 64;

        /// <summary>是否画出鼠标目标标记（十字 + 目标格方框 + 竖线 + 偏移半径圈）。</summary>
        public bool DebugCursorMarker = true;

        /// <summary>是否每帧汇总 <see cref="AStarDemoStats"/> 单例。</summary>
        [Header("统计")]
        public bool StatsEnabled = true;

        /// <summary>是否在屏幕左上角画统计面板（实跑时直接看到改道进度）。</summary>
        public bool ShowStatsOverlay = true;

        /// <summary>是否把地面与障碍物的渲染器换成下面两个材质资产（按对象角色：地面用 <see cref="FloorMaterial"/>，其余用 <see cref="WallMaterial"/>）。</summary>
        [Header("场地外观")]
        public bool ApplySceneMaterials = true;

        /// <summary>地面材质。</summary>
        public Material FloorMaterial;

        /// <summary>障碍物材质。</summary>
        public Material WallMaterial;

        /// <summary>自建 World；未创建时为 <c>null</c>。</summary>
        private World m_World;

        /// <summary>取鼠标射线用的主相机；为 <c>null</c> 时重新取一次。</summary>
        private Camera m_Camera;

        /// <summary>鼠标目标单例实体；World 重建后重新取。</summary>
        private Entity m_CursorEntity;

        /// <summary>本组件写到单例上的目标版本计数。</summary>
        private int m_CursorVersion;

        /// <summary>本组件上一次写进去的落点；为空表示还没写过。</summary>
        private float3 m_LastWritten;

        /// <summary>本组件是否已经写过鼠标落点。</summary>
        private bool m_HasWritten;

        private RenderMeshPool m_Pool = new();
        private Material[] m_Materials = new Material[0];
        private readonly List<Entity> m_Agents = new();
        private bool m_OwnMaterials;

        /// <summary>统计面板的文本样式，第一次画的时候建一次。</summary>
        private GUIStyle m_OverlayStyle;

        /// <summary>上一次写静态障碍物线段时用的网格建表版本（<c>int.MinValue</c> = 还没写过）。</summary>
        private int m_ObstacleBuildVersion = int.MinValue;

        /// <summary>当前写进 Rvo 的静态障碍物线段条数。</summary>
        private int m_ObstacleSegmentCount;

        /// <summary>拿不到障碍物写入目标的警告是否已经打过（避免每帧刷屏）。</summary>
        private bool m_ObstacleWarningIssued;

        /// <summary>本演示自建的 World；未创建时为 <c>null</c>。</summary>
        public World DemoWorld => m_World;

        /// <summary>网格资产里可走格数（烘焙结果），资产为空时为 0。</summary>
        public int GridWalkableCount => GridAsset != null ? GridAsset.WalkableCount : 0;

        /// <summary>网格资产里的总格数，资产为空时为 0。</summary>
        public int GridCellCount => GridAsset != null ? GridAsset.CellCount : 0;

        /// <summary>本组件上一次写进单例的鼠标落点。</summary>
        public Vector3 LastWrittenGround => m_LastWritten;

        /// <summary>
        /// 把一个世界坐标点写成鼠标目标（同时把版本加一）；外部脚本 / 调试面板用它指定目标而不用碰 ECS。
        /// 点会先按网格资产的掩码吸附到最近的可走格中心（网格外或压在障碍上都能落到可走格），
        /// 没有可走格时按传入的点写入。返回写入后的版本号，World 未创建时返回 0。
        /// 写进去的点会同时成为「本组件上一次写进去的点」，因此鼠标不移开时不会被覆盖。
        /// </summary>
        public int SetCursorTarget(Vector3 worldPoint)
        {
            if (!EnsureCursorTarget())
            {
                return 0;
            }

            var em = m_World.EntityManager;
            var cursor = em.GetComponentData<AStarDemoCursorTarget>(m_CursorEntity);
            var raw = new float3(worldPoint.x, AgentPlaneHeight, worldPoint.z);
            var ground = TrySnapToGrid(raw, out var snapped) ? snapped : raw;
            m_CursorVersion = math.max(m_CursorVersion, cursor.Version) + 1;
            m_LastWritten = ground;
            m_HasWritten = true;
            cursor.Position = ground;
            cursor.Version = m_CursorVersion;
            cursor.Valid = 1;
            cursor.Writer = k_ExternalWriterMark;
            cursor.WriteCount++;
            em.SetComponentData(m_CursorEntity, cursor);
            return m_CursorVersion;
        }

        /// <summary>
        /// 俯视相机的建议位姿：把宽 <paramref name="fieldWidth"/>、深 <paramref name="fieldDepth"/> 的场地
        /// 整片框进视野（生成场景菜单用它摆相机）。
        /// </summary>
        public static void SuggestCameraPose(float fieldWidth, float fieldDepth,
                                             out Vector3 position, out Quaternion rotation)
            => AStarDemoBootstrap.SuggestCameraPose(fieldWidth, fieldDepth, out position, out rotation);

        private void Awake()
        {
            if (GridAsset == null)
            {
                Debug.LogError("[AStarShipDemo] 没有指定网格资产：先用编辑器工具从场景烘焙一份（菜单 " +
                               "Tools/DOTSUtils/A* 寻路网格），再把它指到本组件上。");
                return;
            }

            if (SourceShip == null)
            {
                Debug.LogError("[AStarShipDemo] 没有指定船的渲染源（TestShip.prefab）：无法生成 agent。");
                return;
            }

            // 资产自带尺寸与掩码：先核对掩码长度，再推给 AStarSettings（A* 模块按这份数据建网格）；
            // 算法用本组件的选择。
            GridAsset.ResizeIfNeeded();
            var config = GridAsset.ToConfig();
            config.Algorithm = Algorithm;
            AStarSettings.Configure(config, GridAsset.Mask != null ? (byte[])GridAsset.Mask.Clone() : null);

            var size = GridAsset.Size;
            var mask = GridAsset.Mask;
            var components = AStarDemoGridMask.CountComponents(mask, size.x, size.y);
            Debug.Log($"[AStarShipDemo] 网格资产 {GridAsset.name}：{size.x}×{size.y} 格、边长 {config.CellSize}，" +
                      $"可走 {GridAsset.WalkableCount}/{GridAsset.CellCount} 格、4 邻连通分量 {components}。" +
                      (components == 1 ? string.Empty : "（不是 1 说明有船走不到的区域）"));

            ApplySettings(in config);
            CreateWorldIfNeeded();

            m_ObstacleBuildVersion = int.MinValue;
            m_ObstacleSegmentCount = 0;
            m_ObstacleWarningIssued = false;

            if (ApplySceneMaterials)
            {
                ApplySceneMaterialsNow();
            }

            if (m_World != null && m_World.IsCreated)
            {
                EnsureCursorTarget();
                SpawnAgents(in config, mask, size);
                VerifyGrid();
                SyncObstacles();
            }
        }

        /// <summary>
        /// 按对象角色把 <see cref="FloorMaterial"/> / <see cref="WallMaterial"/> 直接赋给场景里地面与障碍方块的渲染器，
        /// 颜色写在材质资产上。
        /// </summary>
        private void ApplySceneMaterialsNow()
        {
            var renderers = GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                var material = renderer.gameObject.name == "Ground" ? FloorMaterial : WallMaterial;
                if (material != null)
                {
                    renderer.sharedMaterial = material;
                }
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
        }

        /// <summary>
        /// 主线程每帧把鼠标在地面上的落点写进 <see cref="AStarDemoCursorTarget"/> 单例：
        /// 主相机从 <c>Input.mousePosition</c> 发一条射线，取它与 <c>y = AgentPlaneHeight</c> 水平面的交点，
        /// 再把交点吸附到网格里最近的可走格中心。
        /// 只有落点相对**本组件上一次写进去的点**移动超过一格时才写，并把版本加一，
        /// 跟路径 job 据此错峰重规划；射线打不到地面（鼠标指向地平线以上）时保留上一次的落点。
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

            var cellSize = GridAsset != null ? math.max(GridAsset.CellSize, 1e-3f) : 1f;
            var moved = !m_HasWritten || math.distance(
                            new float2(ground.x, ground.z),
                            new float2(m_LastWritten.x, m_LastWritten.z)) >= cellSize;
            if (!moved)
            {
                return;
            }

            var em = m_World.EntityManager;
            var cursor = em.GetComponentData<AStarDemoCursorTarget>(m_CursorEntity);

            // 版本只增不减：外部脚本先写过更大的版本时接着往下走，不要回退。
            m_CursorVersion = math.max(m_CursorVersion, cursor.Version) + 1;
            m_LastWritten = ground;
            m_HasWritten = true;
            cursor.Position = ground;
            cursor.Version = m_CursorVersion;
            cursor.Valid = 1;
            cursor.Writer = k_MouseWriterMark;
            cursor.WriteCount++;
            em.SetComponentData(m_CursorEntity, cursor);
        }

        /// <summary>
        /// 屏幕左上角的统计面板：直接显示 <see cref="AStarDemoStats"/> 里的关键计数
        /// （多少艘船还没跟上新目标、本帧改道了多少、目标挑到了哪一类、求解有没有超时）。
        /// </summary>
        private void OnGUI()
        {
            if (!ShowStatsOverlay || m_World == null || !m_World.IsCreated)
            {
                return;
            }
            if (!AStarDemoStatsAccess.TryGet(m_World, out var stats))
            {
                return;
            }

            if (m_OverlayStyle == null)
            {
                m_OverlayStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    wordWrap = false,
                    richText = false,
                };
                m_OverlayStyle.normal.textColor = Color.white;
            }

            var obstacleLines = RvoAccess.TryGetStatus(m_World, out var rvo) ? rvo.ObstacleCount.ToString() : "?";

            var text =
                $"A* 船群演示（World：{WorldName}）\n" +
                $"agent {stats.AgentCount}    有可跟路径 {stats.PathAgents}    等待求解 {stats.PendingAgents}（最长 {stats.MaxPendingFrames} 帧）\n" +
                $"鼠标目标版本 {stats.CursorVersion}（写入 {stats.CursorWriteCount} 次，来源 {(stats.CursorWriter == k_MouseWriterMark ? "鼠标" : "外部")}）\n" +
                $"还没跟上新目标 {stats.StaleAgents}    本帧改道 {stats.RepathedLastFrame}（错峰窗口 {stats.RepathWindow} 帧）\n" +
                $"目标类型：鼠标附近 {stats.CursorGoalAgents} / 退回随机 {stats.RandomGoalAgents} / 挑不到 {stats.NoGoalAgents}\n" +
                $"静态障碍物 写入 {m_ObstacleSegmentCount} 条    系统读到 {obstacleLines} 条\n" +
                $"求解超时累计 {stats.PendingTimeouts}    帧号 {stats.FrameIndex}";

            GUI.Box(new Rect(8f, 8f, 620f, 172f), GUIContent.none);
            GUI.Label(new Rect(16f, 12f, 604f, 164f), text, m_OverlayStyle);
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

            // 单例是新建的还是捡回来的都从头开始：本组件写的版本从当前值往上加。
            m_CursorVersion = em.GetComponentData<AStarDemoCursorTarget>(m_CursorEntity).Version;
            m_HasWritten = false;
            return true;
        }

        /// <summary>
        /// 取鼠标在主相机视线下与 <c>y = AgentPlaneHeight</c> 水平面的交点，再吸附到网格里最近的可走格中心；
        /// 视线与地面平行 / 背向地面、或网格里没有可走格时返回 false。场景里的地面是纯视觉方块，
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

        /// <summary>把地面落点吸附到网格资产里最近的可走格中心；资产为空或没有可走格时返回 false。</summary>
        private bool TrySnapToGrid(float3 point, out float3 snapped)
        {
            snapped = default;
            if (GridAsset == null ||
                !AStarDemoCursor.TrySnapToWalkableCell(point, GridAsset, out var cellCenter))
            {
                return false;
            }

            snapped = new float3(cellCenter.x, AgentPlaneHeight, cellCenter.z);
            return true;
        }

        /// <summary>
        /// 把 Inspector 上的参数交给各模块的装配入口。
        /// 网格参数与掩码在 <see cref="Awake"/> 里已经由网格资产推给 <see cref="AStarSettings"/>，
        /// 这里只补 Rvo / Spatial / Visibility 与演示自己的运行期参数。
        /// </summary>
        private void ApplySettings(in AStarConfig gridConfig)
        {
            var bounds = gridConfig.Bounds;

            // ① 空间索引（Rvo 的邻居来源）：范围取场地 + 余量，叶节点粒度才与船的数量匹配。
            var tree = SpatialTreeConfig.Default;
            tree.Bounds = new AABB
            {
                Center = bounds.Center,
                Extents = bounds.Extents + new float3(SpatialPadding, 16f, SpatialPadding),
            };
            tree.MaxDepth = SpatialMaxDepth;
            tree.MaxLeafElements = (short)SpatialMaxLeafElements;
            tree.Mode = SpatialUpdateMode.Incremental;
            tree.WarnOnOutOfBounds = true;

            // ② Rvo 参数（顺带把 Spatial 参数一起给上）。
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

            // ③ 可见性：关掉裁剪时所有船每帧都参与 Rvo 求解。
            var visibility = VisibilityConfig.Default;
            visibility.Enabled = CullOutsideView;
            visibility.Margin = CullMargin;
            VisibilitySettings.Configure(visibility);

            // ④ 演示自己的运行期参数（跟路径系统与调试画线系统每帧读）。
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
                Debug.LogWarning($"[AStarShipDemo] 模块 \"{k_ModuleName}\" 装配失败：船不会移动" +
                                 "（原因见上方 DotsWorld 的警告）。");
            }
        }

        private void SpawnAgents(in AStarConfig gridConfig, byte[] mask, int2 size)
        {
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
                Source = SourceShip,
                Materials = m_Materials,
                Count = AgentCount,
                Mask = mask,
                Width = size.x,
                Height = size.y,
                CellSize = gridConfig.CellSize,
                // 掩码第 0 格的最小角：与角色摆放、格中心的换算一致。
                OriginX = gridConfig.Bounds.Center.x - gridConfig.Bounds.Extents.x,
                OriginZ = gridConfig.Bounds.Center.z - gridConfig.Bounds.Extents.z,
                PlaneHeight = AgentPlaneHeight,
                AgentRadius = AgentRadius,
                ShipFootprint = ShipFootprint,
                Speed = AgentSpeed,
                InitialDelayMax = InitialRequestDelay,
                Seed = RandomSeed,
                DebugPathCount = DebugPathCount,
                RepathStride = CursorRepathStride,
            };

            var spawned = AStarDemoSpawner.Spawn(m_World, request, m_Pool, m_Agents);
            Debug.Log($"[AStarShipDemo] 生成 {spawned}/{AgentCount} 艘 ECS 船：源 {SourceShip.name}、" +
                      $"目标边长 {ShipFootprint}、Rvo 半径 {AgentRadius}、速度 {AgentSpeed}、错峰窗口 {CursorRepathStride} 帧。");
        }

        /// <summary>核对 A* 模块真的按烘焙资产建好了网格；没有网格时船不会动，这里直接报到 Console。</summary>
        private void VerifyGrid()
        {
            if (AStarAccess.TryGetSingletonData(m_World, out var data) && data.Grid.IsCreated)
            {
                Debug.Log($"[AStarShipDemo] A* 网格已就绪：{data.Grid.Size.x}×{data.Grid.Size.y} 格、" +
                          $"可走 {data.Grid.WalkableCount} 格、建表版本 {data.BuildVersion}。");
                return;
            }

            Debug.LogError("[AStarShipDemo] A* 模块没有建出网格：船的寻路请求不会被求解（船不会移动）。");
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
                WarnObstaclesOnce("[AStarShipDemo] 拿不到 A* 网格：静态障碍物这次没有写入 Rvo（船照常跑）。");
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
                Debug.LogWarning("[AStarShipDemo] 网格里没有抽出阻挡边界：静态障碍物为 0（船照常跑）。");
                return;
            }

            // Rvo 侧收的就是「顶点 + 每个多边形的顶点数」：几何本身就是闭合环，直接写进去（不再拆成独立线段）。
            var polygonVertexCounts = new int[polygons.Length];
            for (var i = 0; i < polygons.Length; i++)
            {
                polygonVertexCounts[i] = polygons[i].Count;
            }

            if (!RvoAccess.SetObstacles(m_World, vertices, polygonVertexCounts))
            {
                WarnObstaclesOnce("[AStarShipDemo] 障碍物单例暂时不可写：静态障碍物下一帧重试（船照常跑）。");
                return;
            }

            m_ObstacleBuildVersion = data.BuildVersion;
            m_ObstacleSegmentCount = vertices.Length;
            Debug.Log($"[AStarShipDemo] 写入静态障碍物 {count} 个闭环 / {vertices.Length} 个顶点；" +
                      $"内侧 = 顶点序（A→B）左侧、τ_obst {math.max(0.1f, ObstacleTimeHorizon)} 秒、网格建表版本 {data.BuildVersion}）。");
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

        /// <summary>生成默认材质变体（与 benchmark 用同一套工厂，只改 _BaseColor）。</summary>
        private Material[] CreateDefaultMaterials()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Custom/CustomLit");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            if (shader == null)
            {
                Debug.LogWarning("[AStarShipDemo] 找不到可用的 agent shader，无法生成材质。");
                return new Material[0];
            }

            return BenchmarkMaterialFactory.Create(MaterialVariants, shader);
        }
    }
}
