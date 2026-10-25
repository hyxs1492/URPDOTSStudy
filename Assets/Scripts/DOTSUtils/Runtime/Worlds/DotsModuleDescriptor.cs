using System;
using System.Collections.Generic;
using Unity.Entities;

namespace DOTSUtils.Worlds
{

    /// <summary>
    /// 一个可热插拔的模块：类型 + 名字 + 若干组。
    /// </summary>
    public sealed class DotsModuleDescriptor
    {
        /// <summary>
        /// 模块的声明类型（<see cref="DotsModule"/> 子类）。模板按类型引用模块时用它查表。
        /// </summary>
        public Type ModuleType { get; internal set; }

        public string Name { get; }

        /// <summary>
        /// 装配顺序键：同一个 World 内按 (SortKey, Name) 升序装配。
        /// </summary>
        public int SortKey { get; }

        public IReadOnlyList<DotsModuleGroupDescriptor> Groups { get; }

        /// <summary>
        /// 该模块依赖的其它模块名。装配时会递归自动装配它们（依赖优先）。
        /// </summary>
        public IReadOnlyList<string> Requires { get; }

        public DotsModuleDescriptor(string name, params DotsModuleGroupDescriptor[] groups)
            : this(name, 0, groups)
        {
        }

        public DotsModuleDescriptor(string name, int sortKey, params DotsModuleGroupDescriptor[] groups)
            : this(name, sortKey, null, groups)
        {
        }

        public DotsModuleDescriptor(string name, int sortKey, IReadOnlyList<string> requires,
                                    params DotsModuleGroupDescriptor[] groups)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("模块名不能为空。", nameof(name));
            }
            Name = name;
            SortKey = sortKey;
            Groups = groups ?? Array.Empty<DotsModuleGroupDescriptor>();
            Requires = requires ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// 模块内的一个组：组类型 + 组内 System 类型（顺序即初始装配顺序）。
    /// </summary>
    public sealed class DotsModuleGroupDescriptor
    {
        public Type GroupType { get; }
        public IReadOnlyList<Type> Systems { get; }

        public DotsModuleGroupDescriptor(Type groupType, params Type[] systems)
        {
            GroupType = groupType ?? throw new ArgumentNullException(nameof(groupType));

            // 早失败：组类型必须是 ComponentSystemGroup。
            if (!typeof(ComponentSystemGroup).IsAssignableFrom(GroupType))
            {
                throw new ArgumentException(
                    $"模块组 {GroupType} 必须派生自 ComponentSystemGroup（实际基类：{GroupType.BaseType}）。",
                    nameof(groupType));
            }

            Systems = systems ?? Array.Empty<Type>();

            // 早失败：Systems 里不允许 null。
            for (var i = 0; i < Systems.Count; i++)
            {
                if (Systems[i] == null)
                {
                    throw new ArgumentException(
                        $"模块组 {GroupType} 的 Systems[{i}] 是 null。", nameof(systems));
                }
            }
        }
    }

    /// <summary>
    /// 模块注册表（代码显式登记）。重名时首次注册胜出（幂等）。
    /// 同时维护“名字 → 描述符”与“类型 → 描述符”两张索引，后者供模板按 <see cref="Type"/> 引用模块。
    /// </summary>
    public static class DotsModules
    {
        private static readonly Dictionary<string, DotsModuleDescriptor> s_Modules = new();
        private static readonly Dictionary<Type, DotsModuleDescriptor> s_ByType = new();

        public static void Register(DotsModuleDescriptor descriptor)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }
            if (s_Modules.TryGetValue(descriptor.Name, out var existing))
            {
                // 幂等：不覆盖已登记的内容；新描述符带类型信息而旧的没有时补上。
                if (existing.ModuleType == null && descriptor.ModuleType != null)
                {
                    existing.ModuleType = descriptor.ModuleType;
                    s_ByType[descriptor.ModuleType] = existing;
                }
                return;
            }
            s_Modules.Add(descriptor.Name, descriptor);
            if (descriptor.ModuleType != null)
            {
                s_ByType[descriptor.ModuleType] = descriptor;
            }
        }

        public static bool TryGet(string name, out DotsModuleDescriptor descriptor)
        {
            if (string.IsNullOrEmpty(name))
            {
                descriptor = null;
                return false;
            }
            return s_Modules.TryGetValue(name, out descriptor);
        }

        /// <summary>
        /// 按模块类型查（模板的推荐路径：<c>typeof(DotsRvoModule)</c>）。
        /// </summary>
        public static bool TryGet(Type moduleType, out DotsModuleDescriptor descriptor)
        {
            descriptor = null;
            return moduleType != null && s_ByType.TryGetValue(moduleType, out descriptor);
        }

        /// <summary>
        /// 活字典视图。枚举期间不要注册新模块（会抛 "Collection was modified"）；
        /// 遍历期间可能触发注册的场合请用 <see cref="Snapshot"/>。
        /// </summary>
        public static IReadOnlyCollection<DotsModuleDescriptor> All => s_Modules.Values;

        /// <summary>
        /// 注册表的**快照**（新列表）。给「遍历期间可能触发注册」的调用方用。
        /// </summary>
        public static IReadOnlyList<DotsModuleDescriptor> Snapshot() =>
            new List<DotsModuleDescriptor>(s_Modules.Values);

        /// <summary>
        /// 清空注册表。
        /// </summary>
        public static void Clear()
        {
            s_Modules.Clear();
            s_ByType.Clear();
        }
    }

}
