using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DOTSUtils.Worlds
{
    /// <summary>
    /// 内置模板登记 + **模块自动发现**：扫描已加载程序集里所有具体的 <see cref="DotsModule"/> 子类并登记。
    /// 发现发生在 <see cref="EnsureInitialized"/>，即任何 CreateWorld 之前。
    /// </summary>
    public static class DotsWorldRegistry
    {
        private static bool s_Initialized;

        /// <summary>
        /// 已扫描到的模块类型缓存；<c>null</c> 表示尚未扫描。
        /// </summary>
        private static List<Type> s_ModuleTypes;

        public static void EnsureInitialized()
        {
            if (s_Initialized)
            {
                return;
            }

            RegisterBuiltInTemplates();
            DiscoverModules();

            s_Initialized = true;
        }

        /// <summary>
        /// 内置模板：只建空壳，成员由模块自己用 <see cref="DotsModule.Templates"/> 声明。
        /// </summary>
        private static void RegisterBuiltInTemplates()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("Default"));
        }

        private static void DiscoverModules()
        {
            foreach (var type in GetModuleTypes())
            {
                DotsModule module;
                try
                {
                    module = (DotsModule)Activator.CreateInstance(type);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[DotsWorld] 无法实例化模块类型 {type.FullName}（需要公共无参构造）：{e.Message}");
                    continue;
                }

                DotsModules.Register(module.ToDescriptor());

                var templates = module.Templates;
                if (templates == null)
                {
                    continue;
                }
                for (var i = 0; i < templates.Count; i++)
                {
                    var templateName = templates[i];
                    if (DotsWorldTemplates.TryGet(templateName, out var template))
                        template.AddModule(type);   // 按类型加
                    else
                        Debug.LogWarning(
                            $"[DotsWorld] 模块 \"{module.Name}\" 声明加入模板 \"{templateName}\"，但该模板尚未登记。");
                }
            }

            // 自检：本程序集自带各模块，一个都没扫到说明类型扫描或程序集加载出了问题
            //（IL2CPP 剥离、asmdef 变更…）。
            if (DotsModules.All.Count == 0)
            {
                Debug.LogError(
                    "[DotsWorld] 模块自动发现扫到 0 个模块 —— 默认 World 会是空的。" +
                    "请检查程序集是否被剥离（IL2CPP 下考虑 [Preserve]）或 asmdef 引用是否断掉。");
            }
        }

        /// <summary>
        /// 遍历已加载程序集找出所有具体的 <see cref="DotsModule"/> 子类。
        /// IL2CPP 下若模块类型无任何静态引用，建议加 <c>[Preserve]</c> 防裁剪。
        /// </summary>
        private static List<Type> GetModuleTypes()
        {
            if (s_ModuleTypes != null)
            {
                return s_ModuleTypes;
            }

            var found = new List<Type>();
            var skipped = 0;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;   // 部分类型加载失败时，仍拿得到的那些
                    skipped++;
                }
                catch (Exception)
                {
                    // 取类型失败的程序集：记数并在末尾汇总。
                    skipped++;
                    continue;
                }

                if (types == null)
                {
                    continue;
                }
                for (var i = 0; i < types.Length; i++)
                {
                    var type = types[i];
                    if (type == null || type.IsAbstract || type.IsInterface)
                    {
                        continue;
                    }
                    if (!typeof(DotsModule).IsAssignableFrom(type))
                    {
                        continue;
                    }
                    found.Add(type);
                }
            }

            s_ModuleTypes = found;

            if (skipped > 0)
            {
                Debug.LogWarning(
                    $"[DotsWorld] 模块扫描：有 {skipped} 个程序集取类型失败并已跳过（共扫 " +
                    $"{assemblies.Length} 个）。若某个模块「消失」，先查这里。");
            }

            return s_ModuleTypes;
        }

        /// <summary>
        /// 允许重新登记（供测试隔离）。类型扫描结果会被缓存，只重建注册表内容。
        /// </summary>
        public static void Reset() => s_Initialized = false;

        /// <summary>
        /// 连**类型扫描缓存**一起丢掉：下次 <see cref="EnsureInitialized"/> 会重新遍历已加载程序集。
        /// 运行时新加载的程序集（插件、Assembly.Load）里的模块需要靠它才会被发现。
        /// </summary>
        public static void ResetIncludingTypeCache()
        {
            s_Initialized = false;
            s_ModuleTypes = null;
        }
    }
}
