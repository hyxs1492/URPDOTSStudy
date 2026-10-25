using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CustomLit
{
    /// <summary>
    /// CustomLit 材质参数的便捷入口。
    ///
    /// 解决两个痛点：
    ///   ① 不用手写十几行 AddComponentData —— 一次调用按需加完
    ///   ② 之后新增 shader 参数时，只需在 <see cref="Params"/> 里加一个字段，
    ///      调用点不用改（映射按「字段名 == 组件类型名」自动解析）
    ///
    /// 重要语义（这是 Entities Graphics 的行为，不是本类引入的）：
    ///   **没加某个 MaterialProperty 组件的实体，该属性读到 0** —— 不是材质球的值。
    ///   所以只想覆盖部分参数时，只填那几项；想让某参数沿用材质球配置，就别加它。
    ///
    /// <code>
    /// // 只覆盖溶解进度和基础色（其余参数回落到 0 = shader 默认）
    /// CustomLitMaterial.Add(em, entity, new CustomLitMaterial.Params
    /// {
    ///     DissolveAmount = 1f,
    ///     BaseColor      = Color.white,
    /// });
    ///
    /// // 运行时改值（组件不存在时会自动补上）
    /// CustomLitMaterial.Set(em, entity, new DissolveAmount { Value = 0.5f });
    /// CustomLitMaterial.SetColor(em, entity, new BaseColor { Value = default }, Color.red);
    ///
    /// CustomLitMaterial.Has&lt;DissolveAmount&gt;(em, entity);
    /// CustomLitMaterial.Remove&lt;DissolveAmount&gt;(em, entity);
    /// </code>
    ///
    /// ⚠️ 这是**调用侧便利**，不是运行时桥接层：不加影子组件、不加 job、不翻倍数据。
    ///    实体上仍然是各自独立的 MaterialProperty 组件（Entity 面板字段语义清晰、可带滑条）。
    /// </summary>
    public static class CustomLitMaterial
    {
        // ---------------------------------------------------------------------
        //  参数集合
        //  字段名必须与 CustomLit 的 MaterialProperty 组件类型名一致
        //  （DissolveAmount → CustomLit.DissolveAmount）。数值取自 shader 属性默认值。
        //  新增可覆盖参数时：在 CustomLitMaterialProperties.cs 加组件，
        //  然后在下面加一个同名字段即可，调用点无需改动。
        // ---------------------------------------------------------------------

        /// <summary>
        /// 一次要写入的一组参数。**只填你要覆盖的项**：
        /// 标量默认 0、颜色默认 (0,0,0,0)，这些视为「不覆盖」。
        /// </summary>
        public struct Params
        {
            // ---- 颜色 / 发光 ----
            public Color BaseColor;
            public Color EmissionColor;
            public Color RimColor;
            public float RimIntensity;

            // ---- 描边 ----
            public Color OutlineColor;
            public float OutlineWidthPixels;

            // ---- 溶解 ----
            public float DissolveAmount;
            public float DissolveRadius;
            public float DissolveNoiseStrength;
            public float DissolveNoiseScale;
            public float DissolveCutoff;
            public Color DissolveEdgeColor;
            public float DissolveEdgeWidth;
        }

        // ---------------------------------------------------------------------
        //  加组件 / 写值
        // ---------------------------------------------------------------------

        /// <summary>
        /// 只加上 <paramref name="values"/> 里显式设置过的参数组件。
        /// 判定：标量 != 0、颜色 != (0,0,0,0)。
        /// </summary>
        public static void Add(EntityManager em, Entity entity, Params values)
        {
            foreach (var m in GetMappings())
            {
                object v = m.Field.GetValue(values);
                if (!IsSet(v)) continue;
                Write(em, entity, m, v);
            }
        }

        /// <summary>加上全部 CustomLit 参数组件（含未设置的项，写 0）。</summary>
        public static void AddAll(EntityManager em, Entity entity, Params values)
        {
            foreach (var m in GetMappings())
                Write(em, entity, m, m.Field.GetValue(values));
        }

        /// <summary>把 <see cref="Params"/> 应用到一批实体（实体需已存在）。</summary>
        public static void AddAll(EntityManager em, IReadOnlyList<Entity> entities, Params values)
        {
            foreach (var e in entities) AddAll(em, e, values);
        }

        static void Write(EntityManager em, Entity entity, Mapping m, object value)
        {
            if (!em.HasComponent(entity, m.Type))
                em.AddComponent(entity, m.Type);
            SetBoxed(em, entity, m.Type, m.ToComponent(value));
        }

        // EntityManager 没有非泛型的 SetComponentData(Entity, ComponentType, object)，
        // 用反射走泛型重载。只在 Add/AddAll 里用到，不是热路径。
        static readonly Dictionary<System.Type, MethodInfo> s_SetComponentDataCache = new Dictionary<System.Type, MethodInfo>();

        static void SetBoxed(EntityManager em, Entity entity, ComponentType type, object component)
        {
            var t = type.GetManagedType();
            MethodInfo mi;
            if (!s_SetComponentDataCache.TryGetValue(t, out mi))
            {
                mi = typeof(EntityManager).GetMethod(nameof(EntityManager.SetComponentData),
                    new[] { typeof(Entity), t });
                s_SetComponentDataCache[t] = mi;
            }
            mi.Invoke(em, new[] { (object)entity, component });
        }

        // ---------------------------------------------------------------------
        //  单参数 API —— 全泛型，无反射
        // ---------------------------------------------------------------------

        /// <summary>写入单个 float 参数（组件不存在时自动补上）。</summary>
        public static void Set<T>(EntityManager em, Entity entity, T value)
            where T : unmanaged, IComponentData
        {
            var t = typeof(T);
            if (!em.HasComponent(entity, t)) em.AddComponent<T>(entity);
            em.SetComponentData(entity, value);
        }

        /// <summary>写入单个颜色参数（组件不存在时自动补上）。</summary>
        public static void SetColor<T>(EntityManager em, Entity entity, Color color)
            where T : unmanaged, IComponentData
        {
            var t = typeof(T);
            if (!em.HasComponent(entity, t)) em.AddComponent<T>(entity);
            var o = System.Activator.CreateInstance(t);
            t.GetField("Value").SetValue(o, (float4)new float4(color.r, color.g, color.b, color.a));
            em.SetComponentData(entity, (T)o);
        }

        /// <summary>是否已加该参数组件。</summary>
        public static bool Has<T>(EntityManager em, Entity entity) where T : unmanaged, IComponentData
            => em.HasComponent<T>(entity);

        /// <summary>移除该参数组件（移除后该属性读到 0 = shader 默认）。</summary>
        public static void Remove<T>(EntityManager em, Entity entity) where T : unmanaged, IComponentData
        {
            if (em.HasComponent<T>(entity)) em.RemoveComponent<T>(entity);
        }

        /// <summary>读取该参数组件；不存在返回 false。</summary>
        public static bool TryGet<T>(EntityManager em, Entity entity, out T value)
            where T : unmanaged, IComponentData
        {
            if (!em.HasComponent<T>(entity)) { value = default; return false; }
            value = em.GetComponentData<T>(entity);
            return true;
        }

        // ---------------------------------------------------------------------
        //  字段名 → 组件类型的映射（缓存）
        // ---------------------------------------------------------------------

        sealed class Mapping
        {
            public FieldInfo Field;
            public ComponentType Type;

            public object ToComponent(object v)
            {
                var ct = Type.GetManagedType();
                var o = System.Activator.CreateInstance(ct);
                if (v is float f)
                    ct.GetField("Value").SetValue(o, f);
                else if (v is Color c)
                    ct.GetField("Value").SetValue(o, (float4)new float4(c.r, c.g, c.b, c.a));
                return o;
            }
        }

        static List<Mapping> s_Mappings;

        static List<Mapping> GetMappings()
        {
            if (s_Mappings != null) return s_Mappings;

            s_Mappings = new List<Mapping>();
            var asm = typeof(CustomLitMaterial).Assembly;
            foreach (var f in typeof(Params).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                System.Type compType = null;
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name != f.Name) continue;
                    if (!typeof(IComponentData).IsAssignableFrom(t)) continue;
                    compType = t;
                    break;
                }
                if (compType == null)
                {
                    Debug.LogWarning("[CustomLitMaterial] Params." + f.Name +
                        " 找不到同名 MaterialProperty 组件，已跳过。");
                    continue;
                }
                s_Mappings.Add(new Mapping { Field = f, Type = ComponentType.ReadWrite(compType) });
            }
            return s_Mappings;
        }

        static bool IsSet(object v)
        {
            if (v is float f) return f != 0f;
            if (v is Color c) return c.r != 0f || c.g != 0f || c.b != 0f || c.a != 0f;
            return true;
        }
    }
}
