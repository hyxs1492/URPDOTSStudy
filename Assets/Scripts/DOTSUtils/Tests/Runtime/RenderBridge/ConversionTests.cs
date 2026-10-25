using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    public class ConversionTests
    {
        GameObject m_Root;
        GameObject m_Child1;
        GameObject m_Child2;
        MeshRenderer m_Renderer1;
        MeshRenderer m_Renderer2;
        Mesh m_Mesh;
        Material m_Material;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();
            // Mesh/Material 由 TestRenderAssets 统一登记，用例之间绝不销毁（会被持久池的 RenderMeshArray 引用）。
            m_Mesh = TestRenderAssets.NewMesh("conv-mesh", 1);
            m_Material = TestRenderAssets.NewMaterial();

            // 父物体挂标记，两个带 MeshRenderer 的 Child 填进 MeshRenderers。
            m_Root = new GameObject("root");

            m_Child1 = new GameObject("child1");
            m_Child1.transform.SetParent(m_Root.transform);
            m_Child1.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Renderer1 = m_Child1.AddComponent<MeshRenderer>();
            m_Renderer1.sharedMaterial = m_Material;

            m_Child2 = new GameObject("child2");
            m_Child2.transform.SetParent(m_Root.transform);
            m_Child2.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Renderer2 = m_Child2.AddComponent<MeshRenderer>();
            m_Renderer2.sharedMaterial = m_Material;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Root != null) Object.DestroyImmediate(m_Root);
            DotsRenderConversionQueue.Clear();
            DestroyAllBridgedEntities();
            ReclaimRegistrySlots();

            // 兜底：确保 registry 不再持有本用例已销毁对象的槽位。
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                var conversionSystem = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
                conversionSystem?.Registry.PruneDestroyedTransforms();
            }
        }

        /// <summary>给父物体挂标记并默认把两个 Child 的 Renderer 填进 MeshRenderers。</summary>
        DotsRenderAuthoring MarkDefault()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer1);
            authoring.MeshRenderers.Add(m_Renderer2);
            return authoring;
        }

        static EntityManager Em => World.DefaultGameObjectInjectionWorld.EntityManager;

        internal static void DestroyAllBridgedEntities()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<DotsRenderLink>());
            if (!query.IsEmptyIgnoreFilter) em.DestroyEntity(query);
            query.Dispose();
        }

        /// <summary>显式回收注册表槽位：TearDown 的注销请求会被随后的队列 Clear 丢弃，条目会跨用例残留，绝对计数断言需要确定起点。</summary>
        static void ReclaimRegistrySlots()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;

            var system = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
            if (system == null) return;

            // MarkDead 会改动注册表底层容器，而两个 job 都持有它的视图，先完成依赖。
            world.GetExistingSystemManaged<DotsRenderTransformSyncSystem>()?.PendingJob.Complete();
            world.GetExistingSystemManaged<DotsRenderTransformPushSystem>()?.PendingJob.Complete();

            var registry = system.Registry;
            var entities = registry.Entities; // NativeArray<Entity> 视图，MarkDead 会改动底层，先做快照
            var snapshot = new Entity[entities.Length];
            for (var i = 0; i < snapshot.Length; i++) snapshot[i] = entities[i];
            foreach (var entity in snapshot)
                if (entity != Entity.Null) registry.MarkDead(entity);
        }

        /// <summary>恰好 1 个父实体：有 <see cref="DotsRenderLink"/>、无 <see cref="Parent"/>。</summary>
        internal static Entity GetParentEntity(EntityManager em)
        {
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<DotsRenderLink>(),
                ComponentType.Exclude<Parent>());
            var entities = query.ToEntityArray(Allocator.Temp);
            var count = entities.Length;
            var result = count > 0 ? entities[0] : Entity.Null;
            entities.Dispose();
            query.Dispose();
            Assert.AreEqual(1, count, "期望恰好 1 个父实体（有 DotsRenderLink、无 Parent）");
            return result;
        }

        /// <summary>所有子渲染实体：有 <see cref="DotsRenderLink"/> 且有 <see cref="Parent"/>。</summary>
        internal static List<Entity> GetChildEntities(EntityManager em)
        {
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<DotsRenderLink>(),
                ComponentType.ReadOnly<Parent>());
            var entities = query.ToEntityArray(Allocator.Temp);
            var result = new List<Entity>(entities.Length);
            for (var i = 0; i < entities.Length; i++) result.Add(entities[i]);
            entities.Dispose();
            query.Dispose();
            return result;
        }

        /// <summary>按 <see cref="DotsRenderLink.Target"/> 的 GameObject 名找到子渲染实体。</summary>
        internal static Entity FindChildByLinkName(EntityManager em, string name)
        {
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<DotsRenderLink>(),
                ComponentType.ReadOnly<Parent>());
            var entities = query.ToEntityArray(Allocator.Temp);
            var found = Entity.Null;
            for (var i = 0; i < entities.Length; i++)
            {
                var link = em.GetComponentData<DotsRenderLink>(entities[i]);
                var go = link.Target.Value;
                if (go != null && go.name == name) { found = entities[i]; break; }
            }
            entities.Dispose();
            query.Dispose();
            return found;
        }

        internal static int CountBridgedEntities(EntityManager em)
        {
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<DotsRenderLink>());
            var count = query.CalculateEntityCount();
            query.Dispose();
            return count;
        }

        [UnityTest]
        public IEnumerator 父实体与两个子渲染实体在下一帧建立()
        {
            MarkDefault();

            yield return null;

            var em = Em;
            var parent = GetParentEntity(em);
            var children = GetChildEntities(em);

            Assert.AreEqual(2, children.Count, "两个 Child MeshRenderer 应各生成一个子渲染实体");
            Assert.IsFalse(em.HasComponent<MaterialMeshInfo>(parent), "父实体不应有渲染组件");
            Assert.IsTrue(em.HasComponent<LocalTransform>(parent), "父实体缺少 LocalTransform");
            Assert.IsTrue(em.HasComponent<LocalToWorld>(parent), "父实体缺少 LocalToWorld");
            Assert.IsTrue(em.HasComponent<DotsRenderLink>(parent), "父实体缺少 DotsRenderLink");

            foreach (var child in children)
            {
                Assert.AreEqual(parent, em.GetComponentData<Parent>(child).Value, "子实体必须挂在父实体下");
                Assert.IsTrue(em.HasComponent<MaterialMeshInfo>(child), "缺少 MaterialMeshInfo");
                Assert.IsTrue(em.HasComponent<RenderMeshArray>(child), "缺少 RenderMeshArray 共享组件");
                Assert.IsTrue(em.HasComponent<RenderFilterSettings>(child), "缺少 RenderFilterSettings 共享组件");
                Assert.IsTrue(em.HasComponent<LocalToWorld>(child), "缺少 LocalToWorld");
                Assert.IsTrue(em.HasComponent<LocalTransform>(child), "缺少 LocalTransform");
                Assert.IsTrue(em.HasComponent<RenderBounds>(child), "缺少 RenderBounds");
            }
        }

        [UnityTest]
        public IEnumerator 转换时写入父世界变换与子局部变换并关闭原绘制()
        {
            m_Root.transform.position = new Vector3(1.5f, -2f, 3.25f);
            m_Root.transform.rotation = Quaternion.Euler(0f, 30f, 0f);
            m_Root.transform.localScale = new Vector3(2f, 2f, 2f);

            m_Child1.transform.localPosition = new Vector3(0.5f, 0f, 0f);
            m_Child1.transform.localScale = new Vector3(3f, 3f, 3f);
            m_Child2.transform.localPosition = new Vector3(-0.5f, 1f, 0f);
            m_Child2.transform.localScale = new Vector3(1f, 1f, 1f);

            MarkDefault();

            yield return null;

            var em = Em;
            var parent = GetParentEntity(em);
            var parentLt = em.GetComponentData<LocalTransform>(parent);

            Assert.AreEqual(1.5f, parentLt.Position.x, 0.001f);
            Assert.AreEqual(-2f, parentLt.Position.y, 0.001f);
            Assert.AreEqual(3.25f, parentLt.Position.z, 0.001f);
            var euler = math.degrees(math.Euler(parentLt.Rotation));
            Assert.AreEqual(30f, euler.y, 0.01f);
            Assert.AreEqual(2f, parentLt.Scale, 0.001f, "父实体 Scale 应取 lossyScale.x");

            var child1 = FindChildByLinkName(em, "child1");
            var c1 = em.GetComponentData<LocalTransform>(child1);
            Assert.AreEqual(0.5f, c1.Position.x, 0.001f, "子实体存相对父的局部位置");
            Assert.AreEqual(0f, c1.Position.y, 0.001f);
            Assert.AreEqual(0f, c1.Position.z, 0.001f);
            Assert.AreEqual(3f, c1.Scale, 0.001f, "子实体 Scale 应取 localScale.x");

            var child2 = FindChildByLinkName(em, "child2");
            var c2 = em.GetComponentData<LocalTransform>(child2);
            Assert.AreEqual(-0.5f, c2.Position.x, 0.001f);
            Assert.AreEqual(1f, c2.Position.y, 0.001f);
            Assert.AreEqual(0f, c2.Position.z, 0.001f);
            Assert.AreEqual(1f, c2.Scale, 0.001f);

            Assert.IsTrue(m_Renderer1.forceRenderingOff, "原 Renderer1 必须停止绘制");
            Assert.IsTrue(m_Renderer2.forceRenderingOff, "原 Renderer2 必须停止绘制");
        }

        /// <summary>目标是深层后代（Root → Node → DeepRenderer）时，子渲染实体的 LocalTransform 必须相对 owner（父实体）合成，而非相对直接父物体 Node。</summary>
        [UnityTest]
        public IEnumerator 深层后代目标的位置相对owner合成()
        {
            var node = new GameObject("node");
            node.transform.SetParent(m_Root.transform);
            node.transform.localPosition = new Vector3(1f, 2f, 3f);

            var deep = new GameObject("deep");
            deep.transform.SetParent(node.transform);
            deep.transform.localPosition = new Vector3(4f, 5f, 6f);
            deep.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var deepRenderer = deep.AddComponent<MeshRenderer>();
            deepRenderer.sharedMaterial = m_Material;

            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(deepRenderer);

            yield return null;

            var em = Em;
            var parent = GetParentEntity(em);
            var child = FindChildByLinkName(em, "deep");
            Assert.AreNotEqual(Entity.Null, child, "深层后代应生成子渲染实体");

            var lt = em.GetComponentData<LocalTransform>(child);
            Assert.AreEqual(5f, lt.Position.x, 0.001f, "应为相对 owner 的合成位置 x（1+4）");
            Assert.AreEqual(7f, lt.Position.y, 0.001f, "应为相对 owner 的合成位置 y（2+5）");
            Assert.AreEqual(9f, lt.Position.z, 0.001f, "应为相对 owner 的合成位置 z（3+6）");
            Assert.AreEqual(parent, em.GetComponentData<Parent>(child).Value, "Parent 应指向父实体");
        }

        [UnityTest]
        public IEnumerator 同一个对象重复标记不会产生重复实体()
        {
            var authoring = MarkDefault();
            yield return null;

            authoring.enabled = false;
            authoring.enabled = true;
            yield return null;

            var em = Em;
            Assert.AreEqual(3, CountBridgedEntities(em), "重复启用不应重复建实体（1 父 + 2 子）");
        }

        [UnityTest]
        public IEnumerator 两个列表都为空时标记对象被跳过并警告()
        {
            var bare = new GameObject("bare");
            bare.AddComponent<DotsRenderAuthoring>();
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("两个列表都为空"));

            yield return null;

            var em = Em;
            Assert.AreEqual(0, CountBridgedEntities(em), "两个列表都为空不应创建任何实体");
            Object.DestroyImmediate(bare);
        }

        [UnityTest]
        public IEnumerator Disposal_DisableComponents_转换后原渲染器被禁用_注销后恢复()
        {
            var authoring = MarkDefault();
            // AddComponent 已触发 OnEnable 入队；Disposal 在同一帧内设置，系统下一帧才消费队列，读到的是这里的值。
            authoring.Disposal = OriginalRendererDisposal.DisableComponents;

            yield return null; // 转换完成
            yield return null; // 让结构性变更稳定后再断言

            Assert.IsFalse(m_Renderer1.enabled, "DisableComponents 模式下原 Renderer 应被禁用");
            Assert.IsFalse(m_Renderer2.enabled, "DisableComponents 模式下原 Renderer 应被禁用");
            Assert.IsFalse(m_Renderer1.forceRenderingOff, "DisableComponents 模式不应改 forceRenderingOff");

            var em = Em;
            Assert.AreEqual(3, CountBridgedEntities(em), "实体结构不应受影响（1 父 + 2 子）");

            authoring.enabled = false; // 注销
            yield return null;
            yield return null;

            Assert.IsTrue(m_Renderer1.enabled, "注销后必须恢复组件启用状态");
            Assert.IsTrue(m_Renderer2.enabled, "注销后必须恢复组件启用状态");

            authoring.enabled = true;
        }

        [UnityTest]
        public IEnumerator Disposal_ForceRenderingOff_保持默认语义()
        {
            var authoring = MarkDefault();
            authoring.Disposal = OriginalRendererDisposal.ForceRenderingOff;

            yield return null;
            yield return null;

            Assert.IsTrue(m_Renderer1.forceRenderingOff, "默认模式仍用 forceRenderingOff");
            Assert.IsTrue(m_Renderer1.enabled, "默认模式不改组件启用状态");
        }

        [UnityTest]
        public IEnumerator Disposal_DestroyComponents_转换后原渲染器被销毁_注销不恢复()
        {
            var authoring = MarkDefault();
            authoring.Disposal = OriginalRendererDisposal.DestroyComponents;

            yield return null; // 系统消费队列并调用 Destroy
            yield return null; // Object.Destroy 帧末生效

            Assert.IsTrue(m_Renderer1 == null, "DestroyComponents 模式应销毁原 Renderer");
            Assert.IsTrue(m_Renderer2 == null, "DestroyComponents 模式应销毁原 Renderer");

            var em = Em;
            Assert.AreEqual(3, CountBridgedEntities(em), "实体结构不应受影响（1 父 + 2 子）");

            authoring.enabled = false; // 注销必须是安全的（组件已被销毁）
            yield return null;
            yield return null;

            Assert.IsTrue(m_Renderer1 == null, "DestroyComponents 注销后不应还原渲染");
            Assert.AreEqual(0, CountBridgedEntities(em), "注销后桥接实体应全部销毁");

            authoring.enabled = true;
        }

        [UnityTest]
        public IEnumerator Renderer设置被如实映射到RenderFilterSettings()
        {
            m_Renderer1.shadowCastingMode = ShadowCastingMode.Off;
            m_Renderer1.receiveShadows = true;
            m_Renderer1.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            m_Renderer1.renderingLayerMask = 5u;
            m_Child1.layer = 8;

            MarkDefault();
            yield return null;

            var em = Em;
            var child = FindChildByLinkName(em, "child1");
            var settings = em.GetSharedComponent<RenderFilterSettings>(child);

            Assert.AreEqual(ShadowCastingMode.Off, settings.ShadowCastingMode);
            Assert.IsTrue(settings.ReceiveShadows);
            Assert.AreEqual(MotionVectorGenerationMode.ForceNoMotion, settings.MotionMode);
            Assert.AreEqual(5u, settings.RenderingLayerMask);
            Assert.AreEqual(8, settings.Layer, "Layer 应取该 Renderer 所在 GameObject 的层");
        }

        [UnityTest]
        public IEnumerator RenderBounds来自Mesh包围盒()
        {
            m_Mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 3f, 4f));
            MarkDefault();
            yield return null;

            var em = Em;
            var child = FindChildByLinkName(em, "child1");
            var bounds = em.GetComponentData<RenderBounds>(child);

            Assert.AreEqual(2f, bounds.Value.Extents.x * 2f, 0.01f);
            Assert.AreEqual(3f, bounds.Value.Extents.y * 2f, 0.01f);
            Assert.AreEqual(4f, bounds.Value.Extents.z * 2f, 0.01f);
        }

        [UnityTest]
        public IEnumerator 多材质槽映射为连续索引范围()
        {
            var twoSlotMesh = TestRenderAssets.NewMesh("two-slot", 2);
            var matA = TestRenderAssets.NewMaterial();
            var matB = TestRenderAssets.NewMaterial();
            m_Child1.GetComponent<MeshFilter>().sharedMesh = twoSlotMesh;
            m_Renderer1.sharedMaterials = new[] { matA, matB };

            MarkDefault();
            yield return null;

            var em = Em;
            var child = FindChildByLinkName(em, "child1");
            var info = em.GetComponentData<MaterialMeshInfo>(child);
            var array = em.GetSharedComponentManaged<RenderMeshArray>(child);

            Assert.IsTrue(info.HasMaterialMeshIndexRange, "多材质槽必须走 range 路径");
            var range = info.MaterialMeshIndexRange;
            Assert.AreEqual(2, range.length);
            Assert.AreEqual(0, array.MaterialMeshIndices[range.start].SubMeshIndex);
            Assert.AreEqual(1, array.MaterialMeshIndices[range.start + 1].SubMeshIndex);
            Assert.AreSame(matA, array.MaterialReferences[array.MaterialMeshIndices[range.start].MaterialIndex].Value);
            Assert.AreSame(matB, array.MaterialReferences[array.MaterialMeshIndices[range.start + 1].MaterialIndex].Value);
        }

        [UnityTest]
        public IEnumerator DestroyComponents后再次Enable能用快照重建实体()
        {
            var authoring = MarkDefault();
            // MarkDefault 的 AddComponent 已入队；Disposal 在同一帧内设置，系统下一帧才消费队列。
            authoring.Disposal = OriginalRendererDisposal.DestroyComponents;

            yield return null; // 转换
            yield return null; // Object.Destroy 帧末生效

            Assert.IsTrue(m_Renderer1 == null, "前提：DestroyComponents 已销毁原 Renderer");

            var em = Em;
            var firstParent = GetParentEntity(em);
            var firstChildren = GetChildEntities(em);
            Assert.AreEqual(2, firstChildren.Count, "首次转换应产生 2 个子渲染实体");

            // 关掉再打开：注销会销毁实体，而组件已被销毁，只有快照还留着转换输入。
            authoring.enabled = false;
            yield return null;
            yield return null;
            Assert.AreEqual(0, CountBridgedEntities(em), "注销后桥接实体应全部销毁");

            authoring.enabled = true;
            yield return null;
            yield return null;

            var secondParent = GetParentEntity(em);
            var secondChildren = GetChildEntities(em);

            Assert.AreNotEqual(Entity.Null, secondParent, "组件已销毁，但快照仍在 —— 应能用快照重建父实体");
            Assert.AreNotEqual(firstParent, secondParent, "应是新建的父实体");
            Assert.AreEqual(firstChildren.Count, secondChildren.Count, "重建后的渲染实体数应与首次一致");
            Assert.AreEqual(3, CountBridgedEntities(em), "1 父 + 2 子");

            authoring.enabled = true;
        }

        [UnityTest]
        public IEnumerator 禁用标记组件后全部实体被销毁并恢复原绘制()
        {
            var authoring = MarkDefault();
            yield return null;
            var em = Em;
            Assert.AreEqual(3, CountBridgedEntities(em), "禁用前应有 1 父 + 2 子");

            authoring.enabled = false;
            yield return null;

            Assert.AreEqual(0, CountBridgedEntities(em), "禁用后全部实体必须被销毁");
            Assert.IsFalse(m_Renderer1.forceRenderingOff, "原绘制必须恢复");
            Assert.IsFalse(m_Renderer2.forceRenderingOff, "原绘制必须恢复");
        }

        [UnityTest]
        public IEnumerator 销毁GameObject后实体被销毁()
        {
            MarkDefault();
            yield return null;

            Object.DestroyImmediate(m_Root);
            m_Root = null;   // 避免 TearDown 二次销毁
            yield return null;

            var em = Em;
            Assert.AreEqual(0, CountBridgedEntities(em), "销毁后实体必须被清理");
        }

        [UnityTest]
        public IEnumerator MeshRenderers含空槽重复项与非层级对象时逐项警告且只转换合法目标()
        {
            var outsider = new GameObject("outsider");
            outsider.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var outsiderRenderer = outsider.AddComponent<MeshRenderer>();
            outsiderRenderer.sharedMaterial = m_Material;

            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer1);        // 合法
            authoring.MeshRenderers.Add(null);               // 空槽
            authoring.MeshRenderers.Add(m_Renderer1);        // 重复
            authoring.MeshRenderers.Add(outsiderRenderer);   // 非子层级

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("含空槽"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("重复项"));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("不在"));

            yield return null;

            var em = Em;
            Assert.AreEqual(1, GetChildEntities(em).Count, "只有合法的 child1 被转换");
            Assert.AreEqual(2, CountBridgedEntities(em), "1 父 + 1 子");
            Assert.IsTrue(m_Renderer1.forceRenderingOff, "合法目标被关闭原绘制");
            Assert.IsFalse(outsiderRenderer.forceRenderingOff, "非层级目标不应被转换");

            Object.DestroyImmediate(outsider);
        }

        [UnityTest]
        public IEnumerator 大量注销后注册表被压缩且不残留索引()
        {
            var objects = new List<GameObject>();
            for (var i = 0; i < 70; i++)
            {
                var go = new GameObject($"bulk-{i}");
                go.transform.position = new Vector3(i, 0f, 0f);

                var child = new GameObject("child");
                child.transform.SetParent(go.transform);
                child.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
                var renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = m_Material;

                var authoring = go.AddComponent<DotsRenderAuthoring>();
                authoring.MeshRenderers.Add(renderer);
                objects.Add(go);
            }
            yield return null;

            var em = Em;
            Assert.AreEqual(140, CountBridgedEntities(em), "70 个父物体各 1 父 + 1 子");

            var system = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<DotsRenderConversionSystem>();
            Assert.AreEqual(70, system.Registry.Count, "同步表条目数 == 父物体数（不是 Renderer 数）");

            foreach (var go in objects) Object.DestroyImmediate(go);
            yield return null;
            yield return null;

            Assert.AreEqual(0, CountBridgedEntities(em), "全部实体必须被清理");
            Assert.AreEqual(0, system.Registry.Count, "注销后注册表不应残留任何槽位");
        }
    }
}
