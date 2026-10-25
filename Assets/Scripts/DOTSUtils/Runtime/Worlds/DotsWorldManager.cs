using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.Worlds
{
    /// <summary>
    /// 概念运行态 World 的管理器：按模板创建 World、装配/卸载模块、切换活跃渲染 World。
    /// 所有公开入口都先确保内置登记已执行。
    /// </summary>
    public static class DotsWorldManager
    {
        private static readonly Dictionary<string, World> s_Worlds = new();

        /// <summary>
        /// 每个 World 已装配的模块名，**按实际装配顺序**（依赖先于依赖方）。
        /// </summary>
        private static readonly Dictionary<World, List<string>> s_ModulesOf = new();

        /// <summary>
        /// 当前唯一活跃（挂在 PlayerLoop 里更新 + 渲染）的 World；可能为 <c>null</c>。
        /// </summary>
        public static World ActiveWorld { get; private set; }

        /// <summary>
        /// Unity 的默认 World（前端桥接目标）。
        /// </summary>
        public static World DefaultWorld => World.DefaultGameObjectInjectionWorld;

        /// <summary>
        /// 当前应当使用的 World：优先 <see cref="ActiveWorld"/>，没有就退回默认 World。
        /// 场景 / MonoBehaviour 侧（authoring、spawner、编辑器窗口）应该用它，
        /// 不要写死 <c>World.DefaultGameObjectInjectionWorld</c>。
        /// </summary>
        public static World CurrentWorld => ActiveWorld ?? DefaultWorld;

        /// <summary>
        /// 当前登记在册、且仍存活的 World。
        /// </summary>
        public static IReadOnlyList<World> Worlds
        {
            get
            {
                var list = new List<World>(s_Worlds.Count);
                foreach (var world in s_Worlds.Values)
                    if (world != null && world.IsCreated)
                    {
                        list.Add(world);
                    }
                return list;
            }
        }

        /// <summary>
        /// 进入 Play 前重置静态运行态。本工程关闭了域重载与场景重载，静态字段会跨 Play 会话保留。
        /// 时序：<c>RuntimeInitializeOnLoadMethod(SubsystemRegistration)</c> 早于 <c>ICustomBootstrap.Initialize</c>。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState() => ResetStaticStateForTests();

        /// <summary>
        /// 清空静态运行态：World 表、模块表、活跃 World、**非内置模板**，并让模块登记重新跑。
        /// **故意是 public**：编辑器（非 Play）也需要这个入口。
        /// 注意**不**清 <c>DotsModules</c>；也不 Dispose 这些 World（Dispose 由调用方负责）。
        /// </summary>
        public static void ResetStaticStateForTests()
        {
            s_Worlds.Clear();
            s_ModulesOf.Clear();
            ActiveWorld = null;
            DotsWorldTemplates.ClearCustom();
            DotsWorldRegistry.Reset();
        }

        // ------------------------------------------------------------------
        //  生命周期
        // ------------------------------------------------------------------

        /// <summary>
        /// 创建命名 World：完整引导（标准组 + Transform + Entities Graphics 等默认系统）→ 挂进 PlayerLoop
        /// → 按模板装配模块。<paramref name="worldName"/> 已存在时抛 <see cref="ArgumentException"/>。
        /// </summary>
        public static World CreateWorld(string worldName, string templateName, bool makeActive = true)
        {
            DotsWorldRegistry.EnsureInitialized();

            if (string.IsNullOrEmpty(worldName))
                throw new ArgumentException("World 名不能为空。", nameof(worldName));
            if (s_Worlds.ContainsKey(worldName))
                throw new ArgumentException($"已存在同名 World：{worldName}", nameof(worldName));

            var world = new World(worldName);

            // 完整引导：模块组与模块 System 都带 [DisableAutoCreation]，不会出现在这个列表里。
            // 不要 Dispose 这个列表。
            var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.Default);
            DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(world, systems);

            // 记住切走前的活跃 World（通常是默认 World）：装配失败时要把它重新挂回 PlayerLoop。
            var previousActive = ActiveWorld;

            // 挂 PlayerLoop 只由 SetActiveWorld 负责。
            if (makeActive)
            {
                SetActiveWorld(world);
            }

            s_ModulesOf[world] = new List<string>();

            try
            {
                if (!string.IsNullOrEmpty(templateName) && DotsWorldTemplates.TryGet(templateName, out var template))
                {
                    // 按 (SortKey, Name) 升序装配 —— 这不等于每帧的执行顺序（见 SortModules 的说明）。
                    foreach (var moduleType in SortModules(template.Modules))
                    {
                        // 不静默吞掉：装配失败必须打警告（具体原因由 AddModule 内部打印）。
                        if (!AddModule(world, moduleType))
                            Debug.LogWarning(
                                $"[DotsWorld] 模板 \"{templateName}\" 中的模块 \"{moduleType.Name}\" 未能装配" +
                                "（原因见上方 AddModule 的警告；常见：未登记 / 已装配 / 依赖缺失或成环）。");
                    }
                }
            }
            catch
            {
                // 装配中途失败 → 在这里回收半初始化的 World（它还没进 s_Worlds）。
                s_ModulesOf.Remove(world);
                // 与 DestroyWorld 同一不变式：Dispose 之前先摘掉 ActiveWorld。
                if (ReferenceEquals(ActiveWorld, world))
                {
                    ActiveWorld = null;
                }
                if (ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(world))
                    ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(world);
                world.Dispose();
                // 恢复此前的活跃 World。
                if (previousActive != null && previousActive.IsCreated)
                {
                    SetActiveWorld(previousActive);
                }
                throw;
            }

            // 登记放在最后。
            s_Worlds.Add(worldName, world);
            return world;
        }

        public static bool TryGetWorld(string worldName, out World world)
        {
            world = null;
            if (string.IsNullOrEmpty(worldName))
            {
                return false;
            }
            if (!s_Worlds.TryGetValue(worldName, out var found))
            {
                return false;
            }
            if (found == null || !found.IsCreated)
            {
                return false;
            }
            world = found;
            return true;
        }

        public static World GetWorld(string worldName)
        {
            if (!TryGetWorld(worldName, out var world))
                throw new ArgumentException($"未找到 World：{worldName}", nameof(worldName));
            return world;
        }

        public static bool DestroyWorld(World world)
        {
            var name = NameOf(world);
            if (name == null)
            {
                return false;
            }

            var wasActive = ReferenceEquals(ActiveWorld, world);
            if (wasActive)
            {
                ActiveWorld = null;
            }

            s_Worlds.Remove(name);
            s_ModulesOf.Remove(world);

            // 必须先用 IsCreated 守卫：World.Dispose() 对已销毁的 World 会抛 ArgumentException。
            if (!world.IsCreated)
            {
                if (wasActive)
                {
                    RestoreDefaultWorldToPlayerLoop();
                }
                return false;
            }

            if (ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(world))
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(world);

            // World.Dispose() 会级联销毁全部系统（含模块组与模块 System）。
            world.Dispose();

            // 销毁活跃 World 后把默认 World 放回 PlayerLoop；这里不改 ActiveWorld（仍为 null）。
            if (wasActive)
            {
                RestoreDefaultWorldToPlayerLoop();
            }
            return true;
        }

        /// <summary>
        /// 把默认 World 挂回 PlayerLoop（若它存在、已创建、且当前不在里面）。
        /// 用于「活跃 World 被销毁之后」。
        /// </summary>
        private static void RestoreDefaultWorldToPlayerLoop()
        {
            var fallback = DefaultWorld;
            if (fallback != null && fallback.IsCreated &&
                !ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(fallback))
            {
                ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(fallback);
            }
        }

        public static bool DestroyWorld(string worldName)
        {
            if (!TryGetWorld(worldName, out var world))
            {
                return false;
            }
            return DestroyWorld(world);
        }

        private static string NameOf(World world)
        {
            if (world == null)
            {
                return null;
            }
            foreach (var pair in s_Worlds)
                if (ReferenceEquals(pair.Value, world))
                {
                    return pair.Key;
                }
            return null;
        }

        /// <summary>
        /// 切换到指定 World：把旧的活跃 World 整个从 PlayerLoop 摘下（逻辑与渲染都不再 update），
        /// 再把目标 World 挂上。同一时刻只有一个活跃渲染 World。
        /// </summary>
        public static void SetActiveWorld(World world)
        {
            if (world != null && !world.IsCreated)
                throw new ArgumentException("不能把已销毁的 World 设为活跃。", nameof(world));
            if (ReferenceEquals(ActiveWorld, world))
            {
                return;
            }

            if (ActiveWorld != null && ActiveWorld.IsCreated &&
                ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(ActiveWorld))
            {
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(ActiveWorld);
            }

            if (world != null && !ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(world))
                ScriptBehaviourUpdateOrder.AppendWorldToCurrentPlayerLoop(world);

            ActiveWorld = world;
        }

        public static void SetActiveWorld(string worldName) => SetActiveWorld(GetWorld(worldName));

        // ------------------------------------------------------------------
        //  模块热插拔（装配）
        // ------------------------------------------------------------------

        /// <summary>
        /// 按模块<b>类型</b>装配（推荐）：<c>AddModule(world, typeof(DotsRvoModule))</c>。
        /// 它依赖的模块会被<b>自动装配</b>（依赖优先），调用方不必自己列出依赖。
        /// </summary>
        public static bool AddModule(World world, Type moduleType)
        {
            DotsWorldRegistry.EnsureInitialized();

            if (world == null || !world.IsCreated)
            {
                return false;
            }
            if (!DotsModules.TryGet(moduleType, out var module))
            {
                return false;
            }
            return AddModuleInternal(world, module, new HashSet<string>());
        }

        /// <summary>
        /// 按模块名装配（低级 API，供运行时动态构造用）。同样会自动装配依赖。
        /// </summary>
        public static bool AddModule(World world, string moduleName)
        {
            DotsWorldRegistry.EnsureInitialized();

            if (world == null || !world.IsCreated)
            {
                return false;
            }
            if (!DotsModules.TryGet(moduleName, out var module))
            {
                return false;
            }
            return AddModuleInternal(world, module, new HashSet<string>());
        }

        /// <summary>
        /// 装配一个模块：<b>先递归装配它声明的依赖</b>（<see cref="DotsModuleDescriptor.Requires"/>），
        /// 再创建自己的每个组、把组挂到 <c>[UpdateInGroup]</c> 指定的父组、装配组内 System。
        /// </summary>
        /// <param name="inProgress">
        /// 当前装配路径上的模块名，用来检测依赖环。
        /// </param>
        private static bool AddModuleInternal(World world, DotsModuleDescriptor module, HashSet<string> inProgress)
        {
            if (!s_ModulesOf.TryGetValue(world, out var installed))
            {
                installed = new List<string>();
                s_ModulesOf[world] = installed;
            }
            if (installed.Contains(module.Name))
            {
                return false; // 已装配（幂等）
            }
            if (!inProgress.Add(module.Name))
            {
                // 依赖成环：环上的模块一律不装配。
                Debug.LogWarning(
                    $"[DotsWorld] 模块依赖成环（\"{module.Name}\" 已在装配路径上）→ 环上的模块一律不装配。" +
                    "循环依赖是配置错误，静默装上去只会掩盖它。");
                return false;
            }

            // 记录本次成功挂上去的组，供异常时**逆序回滚**（见下方 catch）。
            var addedParents = new List<ComponentSystemGroup>();
            var addedGroups = new List<ComponentSystemGroup>();

            try
            {
                // 依赖优先：缺什么就递归装什么。返回值必须接住，否则依赖错误会静默降级。
                for (var i = 0; i < module.Requires.Count; i++)
                {
                    var requiredName = module.Requires[i];
                    if (installed.Contains(requiredName))
                    {
                        continue;
                    }
                    if (!DotsModules.TryGet(requiredName, out var required))
                    {
                        Debug.LogWarning(
                            $"[DotsWorld] 模块 \"{module.Name}\" 依赖 \"{requiredName}\"，但该模块未登记 → 拒绝装配。");
                        return false;
                    }
                    if (!AddModuleInternal(world, required, inProgress))
                    {
                        Debug.LogWarning(
                            $"[DotsWorld] 模块 \"{module.Name}\" 的依赖 \"{requiredName}\" 装配失败 → 拒绝装配 " +
                            $"\"{module.Name}\"（否则会留下一个依赖不全却自称已装配的模块）。");
                        return false;
                    }
                }

                foreach (var groupDesc in module.Groups)
                {
                    if (world.GetOrCreateSystemManaged(groupDesc.GroupType) is not ComponentSystemGroup group)
                        throw new InvalidOperationException($"{groupDesc.GroupType} 不是 ComponentSystemGroup。");

                    var parentType = ResolveParentGroupType(groupDesc.GroupType);
                    if (world.GetOrCreateSystemManaged(parentType) is not ComponentSystemGroup parent)
                        throw new InvalidOperationException($"{parentType} 不是 ComponentSystemGroup。");

                    parent.AddSystemToUpdateList(group);   // 幂等
                    addedParents.Add(parent);
                    addedGroups.Add(group);

                    foreach (var systemType in groupDesc.Systems)
                    {
                        // ComponentSystemBase（class）可走托管版 API；ISystem（struct，无托管实例）必须用
                        // GetOrCreateSystem(Type)（返回 SystemHandle）。传非系统类型时它直接抛 ArgumentException，
                        // 因此不需要 SystemHandle.Null 守卫。
                        var handle = world.GetOrCreateSystem(systemType);
                        group.AddSystemToUpdateList(handle);
                    }

                    // 显式重排，让组自己的 [UpdateInGroup]（OrderFirst/OrderLast）立即生效。
                    parent.SortSystems();
                }

                // 全部装配成功后才登记。
                installed.Add(module.Name);
                return true;
            }
            catch
            {
                // 装配中途失败 → 把本次已经挂上去的组**逆序摘掉**。
                // 只回滚**本次调用**挂上的组；已装配成功的依赖模块保持装配。
                for (var i = addedGroups.Count - 1; i >= 0; i--)
                {
                    addedParents[i].RemoveSystemFromUpdateList(addedGroups[i]);
                    addedParents[i].SortSystems();
                }
                throw;
            }
            finally
            {
                inProgress.Remove(module.Name);
            }
        }

        /// <summary>
        /// 卸载模块（热插拔「禁用」）：把模块的每个组从父组更新列表摘除。
        /// 组与 System 实例**保留在 World**（状态不丢），再次 <see cref="AddModule"/> 即恢复。
        /// </summary>
        public static bool RemoveModule(World world, string moduleName)
        {
            DotsWorldRegistry.EnsureInitialized();

            if (world == null || !world.IsCreated)
            {
                return false;
            }

            // 先**尽力摘除**，再用「本来是否登记为已装配」决定返回值（未装配 → false）。
            var wasInstalled = s_ModulesOf.TryGetValue(world, out var installed) && installed.Contains(moduleName);
            if (!DotsModules.TryGet(moduleName, out var module))
            {
                return false;
            }

            foreach (var groupDesc in module.Groups)
            {
                if (world.GetExistingSystemManaged(groupDesc.GroupType) is not ComponentSystemGroup group)
                {
                    // 只在「本该在」的时候告警。
                    if (wasInstalled)
                    {
                        Debug.LogWarning($"[DotsWorld] 卸载模块 \"{moduleName}\" 时找不到组实例 {groupDesc.GroupType}，跳过摘除。");
                    }
                    continue;
                }

                var parentType = ResolveParentGroupType(groupDesc.GroupType);
                if (world.GetExistingSystemManaged(parentType) is ComponentSystemGroup parent)
                {
                    parent.RemoveSystemFromUpdateList(group);   // 幂等
                    parent.SortSystems();
                }
            }

            if (wasInstalled)
            {
                WarnIfDependentsRemain(world, moduleName, "卸载");
                installed.Remove(moduleName);
            }
            return wasInstalled;
        }

        /// <summary>
        /// 找出「已装配、且声明依赖 <paramref name="moduleName"/>」的模块名。
        /// </summary>
        private static List<string> FindInstalledDependents(World world, string moduleName)
        {
            var dependents = new List<string>();
            if (!s_ModulesOf.TryGetValue(world, out var installed))
            {
                return dependents;
            }
            for (var i = 0; i < installed.Count; i++)
            {
                var other = installed[i];
                if (other == moduleName || !DotsModules.TryGet(other, out var descriptor))
                {
                    continue;
                }
                var requires = descriptor.Requires;
                for (var r = 0; r < requires.Count; r++)
                {
                    if (requires[r] == moduleName)
                    {
                        dependents.Add(other);
                        break;
                    }
                }
            }
            return dependents;
        }

        /// <summary>
        /// 卸载/销毁一个**仍被依赖**的模块时告警：<see cref="DotsModule.Requires"/> 只在装配时校验，
        /// 卸载/销毁时不会阻止，而被依赖方缺失会让依赖模块**静默降级**。
        /// </summary>
        private static void WarnIfDependentsRemain(World world, string moduleName, string operation)
        {
            var dependents = FindInstalledDependents(world, moduleName);
            if (dependents.Count == 0)
            {
                return;
            }
            Debug.LogWarning(
                $"[DotsWorld] {operation} 模块 \"{moduleName}\"，但它仍被已装配的模块依赖：" +
                $"{string.Join(", ", dependents)} —— 那些模块会**静默降级**（例如 Spatial 缺失时 Rvo " +
                "退化成不避障且不报错）。建议先卸载/销毁依赖方。");
        }

        /// <summary>
        /// 销毁模块：先按 <see cref="RemoveModule"/> 摘除，再逐个释放组内 System 与组本身。
        /// 注意 <see cref="ComponentSystemGroup"/> 的销毁**不会**级联销毁子 System，必须显式逐个释放。
        /// </summary>
        public static bool DestroyModule(World world, string moduleName)
        {
            if (world == null || !world.IsCreated)
            {
                return false;
            }
            if (!DotsModules.TryGet(moduleName, out var module))
            {
                return false;
            }

            // 幂等尽力摘除：当前不处于已装态（例如先 RemoveModule 再 DestroyModule）也继续销毁残留实例。
            WarnIfDependentsRemain(world, moduleName, "销毁");
            RemoveModule(world, moduleName);

            foreach (var groupDesc in module.Groups)
            {
                foreach (var systemType in groupDesc.Systems)
                {
                    // 同样用 handle 版：ISystem 没有托管实例，GetExistingSystemManaged 会漏掉它。
                    var handle = world.GetExistingSystem(systemType);
                    if (handle != SystemHandle.Null)
                    {
                        world.DestroySystem(handle);
                    }
                }

                var groupHandle = world.GetExistingSystem(groupDesc.GroupType);
                if (groupHandle != SystemHandle.Null)
                {
                    world.DestroySystem(groupHandle);
                }
            }

            return true;
        }

        /// <summary>
        /// 该 World 是否已装配某模块。
        /// </summary>
        public static bool HasModule(World world, string moduleName) =>
            world != null && world.IsCreated &&
            s_ModulesOf.TryGetValue(world, out var installed) && installed.Contains(moduleName);

        /// <summary>
        /// 该 World 已装配的模块名，**按实际装配顺序**（依赖先于依赖方）。返回快照副本。
        /// </summary>
        public static IReadOnlyList<string> GetModules(World world)
        {
            if (world == null || !world.IsCreated || !s_ModulesOf.TryGetValue(world, out var installed))
                return new List<string>();
            return new List<string>(installed);
        }

        /// <summary>
        /// 按 (SortKey, Name) 升序排模块类型。
        ///
        /// 这里的顺序只决定**装配（AddSystemToUpdateList）的先后**，**不**决定系统每帧的执行顺序：
        /// 跨模块的执行顺序必须由模块组上的 <c>[UpdateAfter]</c>/<c>[UpdateBefore]</c> 声明。
        /// </summary>
        private static List<Type> SortModules(IReadOnlyList<Type> moduleTypes)
        {
            var sorted = new List<Type>(moduleTypes);
            sorted.Sort(CompareModules);
            return sorted;
        }

        private static int CompareModules(Type a, Type b)
        {
            var hasA = DotsModules.TryGet(a, out var da);
            var hasB = DotsModules.TryGet(b, out var db);
            var keyA = hasA ? da.SortKey : int.MaxValue;
            var keyB = hasB ? db.SortKey : int.MaxValue;
            var byKey = keyA.CompareTo(keyB);
            if (byKey != 0)
            {
                return byKey;
            }

            // 平局用**模块名**。
            var nameA = hasA ? da.Name : a.Name;
            var nameB = hasB ? db.Name : b.Name;
            return string.CompareOrdinal(nameA, nameB);
        }

        /// <summary>
        /// 读 <paramref name="groupType"/> 上的 <c>[UpdateInGroup]</c>，返回它要挂到的父组类型。
        /// </summary>
        internal static Type ResolveParentGroupType(Type groupType)
        {
            var attribute = (UpdateInGroupAttribute)Attribute.GetCustomAttribute(
                groupType, typeof(UpdateInGroupAttribute));
            if (attribute == null)
                throw new InvalidOperationException($"模块组 {groupType} 缺少 [UpdateInGroup]。");
            return attribute.GroupType;
        }
    }
}
