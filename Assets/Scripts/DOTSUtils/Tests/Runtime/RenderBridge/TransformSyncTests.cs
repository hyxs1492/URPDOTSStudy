using System.Collections;
using DOTSUtils.Visibility;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary>测试用的帧内实体写入器：回写路径无法用 MonoBehaviour 侧代码观察，只能在帧初之后、帧末回写之前改实体，因此用测试 system 加静态字段下发一次工作。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    // 排序约束必须指向组（RenderBridgeInputGroup / RenderBridgeOutputGroup）；指向组内系统会被 Entities 判为非法并每帧刷警告。
    [UpdateAfter(typeof(RenderBridgeInputGroup))]
    [UpdateBefore(typeof(RenderBridgeOutputGroup))]
    public partial class TestTransformWriterSystem : SystemBase
    {
        public static Entity Target;
        public static LocalTransform Value;
        public static bool HasWork;

        protected override void OnUpdate()
        {
            if (!HasWork) return;
            HasWork = false;

            if (EntityManager.Exists(Target))
                EntityManager.SetComponentData(Target, Value);
        }
    }

    /// <summary>探针：在帧初同步系统之后，检查目标实体的 <see cref="LocalTransform"/> chunk 本帧有没有被打上「已变更」标记（这件事无法从数据值看出来，只能查 change version）。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    // 同 TestTransformWriterSystem：排序约束指向组（同步系统在 RenderBridgeInputGroup 内部）。
    [UpdateAfter(typeof(RenderBridgeInputGroup))]
    public partial class TestTransformChangeProbeSystem : SystemBase
    {
        public static Entity Target;
        public static bool Enabled;
        public static bool TargetChunkChanged;

        protected override void OnUpdate()
        {
            TargetChunkChanged = false;
            if (!Enabled || Target == Entity.Null || !EntityManager.Exists(Target))
            {
                return;
            }

            // DidChange 的 typeHandle 必须按 ref 传（非 ref 重载已 obsolete）。
            var handle = GetComponentTypeHandle<LocalTransform>(isReadOnly: true);
            var chunk = EntityManager.GetChunk(Target);
            TargetChunkChanged = chunk.DidChange(ref handle, LastSystemVersion);
        }
    }

    public class TransformSyncTests
    {
        GameObject m_Root;
        GameObject m_Parent;
        GameObject m_Child;
        MeshRenderer m_Renderer;
        Mesh m_Mesh;
        Material m_Material;

        /// <summary>用例临时关掉的 Visibility 求解系统（TearDown 里恢复）。</summary>
        VisibilitySystem m_VisibilitySystem;

        /// <summary>关掉 Visibility 求解，好让用例手工把实体标成「已裁剪」；否则它每帧会把手标刷回「可见」。</summary>
        void DisableVisibilitySystem()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return;
            }
            m_VisibilitySystem = world.GetExistingSystemManaged<VisibilitySystem>();
            if (m_VisibilitySystem != null)
            {
                m_VisibilitySystem.Enabled = false;
            }
        }

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();
            TestTransformWriterSystem.HasWork = false;
            TestTransformChangeProbeSystem.Enabled = false;
            TestTransformChangeProbeSystem.Target = Entity.Null;
            // Mesh/Material 由 TestRenderAssets 统一登记，用例之间绝不销毁。
            m_Mesh = TestRenderAssets.NewMesh("sync-mesh", 1);
            m_Material = TestRenderAssets.NewMaterial();

            // 父物体挂标记，一个 Child 填进 MeshRenderers。
            m_Root = new GameObject("root");
            m_Child = new GameObject("child");
            m_Child.transform.SetParent(m_Root.transform);
            m_Child.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Renderer = m_Child.AddComponent<MeshRenderer>();
            m_Renderer.sharedMaterial = m_Material;
        }

        [TearDown]
        public void TearDown()
        {
            TestTransformWriterSystem.HasWork = false;
            TestTransformWriterSystem.Target = Entity.Null;
            TestTransformChangeProbeSystem.Enabled = false;
            TestTransformChangeProbeSystem.Target = Entity.Null;

            // 恢复被用例关掉的 Visibility 求解。
            if (m_VisibilitySystem != null)
            {
                m_VisibilitySystem.Enabled = true;
                m_VisibilitySystem = null;
            }

            if (m_Root != null) Object.DestroyImmediate(m_Root);
            if (m_Parent != null) Object.DestroyImmediate(m_Parent);
            DotsRenderConversionQueue.Clear();
            ConversionTests.DestroyAllBridgedEntities();

            // 兜底：本用例销毁的对象可能在 registry 里残留已销毁的 Transform，直接 Prune 掉。
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                var conversionSystem = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
                conversionSystem?.Registry.PruneDestroyedTransforms();
            }
        }

        void MarkDefault()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);
        }

        LocalTransform ParentTransform()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var entity = ConversionTests.GetParentEntity(em);
            return em.GetComponentData<LocalTransform>(entity);
        }

        /// <summary>让 <see cref="TestTransformWriterSystem"/> 在本帧的帧初同步之后改一次父实体。</summary>
        void PushEntityThisFrame(LocalTransform value)
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            TestTransformWriterSystem.Target = ConversionTests.GetParentEntity(em);
            TestTransformWriterSystem.Value = value;
            TestTransformWriterSystem.HasWork = true;
        }

        // ------------------------------------------------------------------
        //  GameObject → 实体（帧初）
        // ------------------------------------------------------------------

        /// <summary>视口外的对象，同步与回写两个方向都不该动它。</summary>
        [UnityTest]
        public IEnumerator 视口外的对象_同步方向不该用旧位置覆盖实体()
        {
            MarkDefault();
            yield return null;
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var parent = ConversionTests.GetParentEntity(em);

            DisableVisibilitySystem();

            // 手工把父实体标成「已裁剪」（启用 = 不可见）。
            em.AddComponent<VisibilityCulledTag>(parent);
            em.SetComponentEnabled<VisibilityCulledTag>(parent, true);

            // 把 GO 挪到别处。
            m_Root.transform.position = new Vector3(5f, 0f, 0f);

            // 模拟实体侧推进。
            PushEntityThisFrame(LocalTransform.FromPosition(new float3(9f, 0f, 0f)));

            yield return null;   // 帧 N  ：同步(剔除→跳过) → 写入器(实体=9) → 回写(剔除→跳过)
            yield return null;   // 帧 N+1：同步必须仍然跳过

            Assert.AreEqual(9f, em.GetComponentData<LocalTransform>(parent).Position.x, 0.0001f,
                "视口外的实体被帧初同步用 GO 的旧位置覆盖了 —— 这正是「视口外不动」的根因");
        }

        /// <summary>同步方向只写真的变了的对象；本用例同时用反向对照覆盖「动了就必须标脏」。</summary>
        [UnityTest]
        public IEnumerator 静止对象不该被每帧标脏LocalTransform()
        {
            MarkDefault();
            yield return null;
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            TestTransformChangeProbeSystem.Target = ConversionTests.GetParentEntity(em);

            // ① 对象保持静止：本帧同步阶段不该写 LocalTransform。
            TestTransformChangeProbeSystem.Enabled = true;
            yield return null;

            Assert.IsFalse(TestTransformChangeProbeSystem.TargetChunkChanged,
                "静止对象被写 LocalTransform 会推进 chunk change version，" +
                "使 LocalToWorldSystem 的 change filter 快路径与 Entities Graphics 的 chunk 早退失效（每帧白算）");

            // ② 反向对照：真的移动了就必须写。
            m_Root.transform.position = new Vector3(7f, 0f, 0f);
            yield return null;

            Assert.IsTrue(TestTransformChangeProbeSystem.TargetChunkChanged,
                "对象移动后同步阶段必须写 LocalTransform（这条同时证明探针本身有效）");
        }

        [UnityTest]
        public IEnumerator 位置旋转被同步到父实体_缩放只在转换时写一次()
        {
            MarkDefault();
            yield return null;

            m_Root.transform.position = new Vector3(4f, -1f, 2.5f);
            m_Root.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            m_Root.transform.localScale = new Vector3(3f, 3f, 3f);
            yield return null;

            var lt = ParentTransform();
            Assert.AreEqual(4f, lt.Position.x, 0.001f);
            Assert.AreEqual(-1f, lt.Position.y, 0.001f);
            Assert.AreEqual(2.5f, lt.Position.z, 0.001f);
            Assert.AreEqual(90f, math.degrees(math.Euler(lt.Rotation)).y, 0.01f);

            // 运行时不同步缩放：Scale 保持转换那一刻写下的值（转换时 m_Root 的 lossyScale 是 1）。
            Assert.AreEqual(1f, lt.Scale, 0.001f, "Scale 只在转换时写一次，运行时不同步");
        }

        [UnityTest]
        public IEnumerator 带父级对象同步的是世界变换()
        {
            m_Parent = new GameObject("parent");
            m_Root.transform.SetParent(m_Parent.transform);
            m_Root.transform.localPosition = new Vector3(1f, 2f, 3f);
            MarkDefault();
            yield return null;

            m_Parent.transform.position = new Vector3(10f, 0f, 0f);
            yield return null;

            var lt = ParentTransform();
            Assert.AreEqual(11f, lt.Position.x, 0.001f, "父实体应存世界变换（父级 10 + 局部 1）");
            Assert.AreEqual(2f, lt.Position.y, 0.001f);
            Assert.AreEqual(3f, lt.Position.z, 0.001f);
        }

        [UnityTest]
        public IEnumerator 移动父物体后子实体LocalToWorld跟随()
        {
            MarkDefault();
            yield return null;
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var child = ConversionTests.GetChildEntities(em)[0];
            var ltBefore = em.GetComponentData<LocalTransform>(child);
            var worldBefore = em.GetComponentData<LocalToWorld>(child).Value.c3;

            m_Root.transform.position = new Vector3(10f, 0f, 0f);
            yield return null;
            yield return null;

            var ltAfter = em.GetComponentData<LocalTransform>(child);
            var worldAfter = em.GetComponentData<LocalToWorld>(child).Value.c3;

            Assert.AreEqual(ltBefore.Position.x, ltAfter.Position.x, 0.0001f, "子实体局部变换不应被改写（只在转换时写一次）");
            Assert.AreEqual(ltBefore.Position.y, ltAfter.Position.y, 0.0001f);
            Assert.AreEqual(ltBefore.Position.z, ltAfter.Position.z, 0.0001f);
            Assert.AreEqual(worldBefore.x + 10f, worldAfter.x, 0.05f, "子实体世界位置应跟随父实体平移 +10（无需重新同步子实体）");
        }

        [UnityTest]
        public IEnumerator 多个父物体各自同步到自己的父实体()
        {
            var secondRoot = new GameObject("second-root");
            var secondChild = new GameObject("second-child");
            secondChild.transform.SetParent(secondRoot.transform);
            secondChild.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var secondRenderer = secondChild.AddComponent<MeshRenderer>();
            secondRenderer.sharedMaterial = m_Material;

            m_Root.transform.position = new Vector3(5f, 0f, 0f);
            secondRoot.transform.position = new Vector3(-5f, 0f, 0f);

            MarkDefault();
            var authoring2 = secondRoot.AddComponent<DotsRenderAuthoring>();
            authoring2.MeshRenderers.Add(secondRenderer);

            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<DotsRenderLink>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.Exclude<Parent>());          // 只有逻辑父实体没有 ECS 父级
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            Assert.AreEqual(2, entities.Length, "两个父物体应生成两个父实体");

            var found = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                if (Mathf.Abs(transforms[i].Position.x - 5f) < 0.001f) found++;
                if (Mathf.Abs(transforms[i].Position.x + 5f) < 0.001f) found++;
            }
            query.Dispose();
            Assert.AreEqual(2, found, "两个父实体应分别位于 +5 与 -5");
            Object.DestroyImmediate(secondRoot);
        }

        // ------------------------------------------------------------------
        //  实体 → GameObject（帧末回写）
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator 帧内改动实体后帧末回写到父物体()
        {
            MarkDefault();
            yield return null;
            yield return null;

            PushEntityThisFrame(LocalTransform.FromPositionRotationScale(
                new float3(7f, 3f, -2f), quaternion.Euler(0f, math.radians(45f), 0f), 1f));
            yield return null;   // 本帧：writer 改实体 → 帧末回写
            yield return null;

            Assert.AreEqual(7f, m_Root.transform.position.x, 0.001f, "实体位置应回写到 GameObject");
            Assert.AreEqual(3f, m_Root.transform.position.y, 0.001f);
            Assert.AreEqual(-2f, m_Root.transform.position.z, 0.001f);
            Assert.AreEqual(45f, m_Root.transform.rotation.eulerAngles.y, 0.1f, "实体旋转应回写到 GameObject");
        }

        [UnityTest]
        public IEnumerator 回写让渲染子物体跟随父物体()
        {
            MarkDefault();
            yield return null;
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var child = ConversionTests.GetChildEntities(em)[0];
            var childLocalBefore = m_Child.transform.localPosition;

            PushEntityThisFrame(LocalTransform.FromPositionRotationScale(
                new float3(6f, 0f, 0f), quaternion.identity, 1f));
            yield return null;
            yield return null;

            Assert.AreEqual(6f, m_Root.transform.position.x, 0.001f, "父物体应被回写");
            Assert.AreEqual(childLocalBefore, m_Child.transform.localPosition, "子物体局部位置不变（靠 Unity 层级跟随）");
            Assert.AreEqual(6f + childLocalBefore.x, m_Child.transform.position.x, 0.001f, "子物体世界位置应跟随父物体");
        }

        [UnityTest]
        public IEnumerator 回写不改动GameObject的缩放()
        {
            m_Root.transform.localScale = new Vector3(3f, 3f, 3f);
            MarkDefault();
            yield return null;
            yield return null;

            PushEntityThisFrame(LocalTransform.FromPositionRotationScale(
                new float3(1f, 1f, 1f), quaternion.identity, 5f));   // 实体侧改了 Scale
            yield return null;
            yield return null;

            Assert.AreEqual(1f, m_Root.transform.position.x, 0.001f, "位置照常回写");
            Assert.AreEqual(3f, m_Root.transform.localScale.x, 0.001f,
                "缩放不回写（TransformAccess 拿不到 lossyScale，无法把世界尺度折算回局部尺度）");
        }

        [UnityTest]
        public IEnumerator TransformDrivenByEntity的对象不参与回写()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);
            authoring.TransformDrivenByEntity = true;
            yield return null;
            yield return null;

            m_Root.transform.position = new Vector3(2f, 0f, 0f);
            yield return null;

            PushEntityThisFrame(LocalTransform.FromPositionRotationScale(
                new float3(9f, 0f, 0f), quaternion.identity, 1f));
            yield return null;
            yield return null;

            Assert.AreEqual(2f, m_Root.transform.position.x, 0.001f,
                "勾了 TransformDrivenByEntity 的对象既不读也不回写，GameObject 保持原样");
        }
    }
}
