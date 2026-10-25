using System;
using System.Collections.Generic;
using UnityEngine;

namespace DOTSUtils.Worlds
{
    /// <summary>
    /// World 描述：名字 + 要装配的模块类型列表。
    /// 用 <see cref="Type"/> 引用模块；依赖的模块（<see cref="DotsModule.Requires"/>）不需要写进来，
    /// 装配时会自动补齐。
    /// </summary>
    public sealed class DotsWorldTemplate
    {
        private readonly List<Type> m_Modules = new();

        public string Name { get; }

        /// <summary>本模板显式列出的模块类型（依赖由框架补齐，不在其中）。</summary>
        public IReadOnlyList<Type> Modules => m_Modules;

        /// <summary>
        /// 用模块类型构造，例如
        /// <c>new DotsWorldTemplate("MyWorld", typeof(DotsRenderBridgeModule), typeof(DotsRvoModule))</c>。
        /// </summary>
        public DotsWorldTemplate(string name, params Type[] modules)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("模板名不能为空。", nameof(name));
            }
            Name = name;
            if (modules != null)
            {
                for (var i = 0; i < modules.Length; i++)
                {
                    AddModule(modules[i]);
                }
            }
        }

        /// <summary>
        /// 按模块类型追加（推荐；幂等：null/重复忽略）。
        /// </summary>
        public void AddModule(Type moduleType)
        {
            if (moduleType == null || m_Modules.Contains(moduleType))
            {
                return;
            }
            // 这里不校验「是否已登记」：模板是**声明**，合法性由装配时判定。
            m_Modules.Add(moduleType);
        }

        /// <summary>
        /// 按模块名追加（低级 API，供运行时动态构造用）。名字不是已登记模块时忽略。
        /// </summary>
        public void AddModule(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName))
            {
                return;
            }
            if (!DotsModules.TryGet(moduleName, out var descriptor) || descriptor.ModuleType == null)
            {
                Debug.LogWarning($"[DotsWorld] 模块名 \"{moduleName}\" 未登记或没有类型信息，已忽略。");
                return;
            }
            AddModule(descriptor.ModuleType);
        }
    }

    /// <summary>
    /// World 模板注册表（代码显式登记）。重名时首次注册胜出（幂等）。
    /// </summary>
    public static class DotsWorldTemplates
    {
        private static readonly Dictionary<string, DotsWorldTemplate> s_Templates = new();

        public static void Register(DotsWorldTemplate template)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }
            if (s_Templates.ContainsKey(template.Name))
            {
                return; // 幂等：不覆盖
            }
            s_Templates.Add(template.Name, template);
        }

        public static bool TryGet(string name, out DotsWorldTemplate template)
        {
            if (string.IsNullOrEmpty(name))
            {
                template = null;
                return false;
            }
            return s_Templates.TryGetValue(name, out template);
        }

        /// <summary>
        /// 活字典视图。枚举期间不要注册新模板（会抛 "Collection was modified"），
        /// 那种场合用 <see cref="Snapshot"/>。
        /// </summary>
        public static IReadOnlyCollection<DotsWorldTemplate> All => s_Templates.Values;

        /// <summary>
        /// 注册表的**快照**（新列表）。
        /// </summary>
        public static IReadOnlyList<DotsWorldTemplate> Snapshot() =>
            new List<DotsWorldTemplate>(s_Templates.Values);

        /// <summary>
        /// 清空注册表（含内置的 "Default"；恢复需同时调 <c>DotsWorldRegistry.Reset()</c>）。
        /// 只想去掉自定义模板请用 <see cref="ClearCustom"/>。
        /// </summary>
        public static void Clear() => s_Templates.Clear();

        /// <summary>
        /// 内置模板名（由 <see cref="DotsWorldRegistry"/> 在初始化时登记）。
        /// </summary>
        public const string BuiltInDefaultTemplateName = "Default";

        /// <summary>
        /// 只清**非内置**模板，保留 "Default"（内置若已丢失则补回空壳，并让登记重新发现，
        /// 使各模块自报的 <see cref="DotsModule.Templates"/> 重新写进来）。
        /// </summary>
        public static void ClearCustom()
        {
            var names = new List<string>(s_Templates.Keys);
            for (var i = 0; i < names.Count; i++)
            {
                if (names[i] != BuiltInDefaultTemplateName)
                {
                    s_Templates.Remove(names[i]);
                }
            }

            if (!s_Templates.ContainsKey(BuiltInDefaultTemplateName))
            {
                s_Templates.Add(BuiltInDefaultTemplateName, new DotsWorldTemplate(BuiltInDefaultTemplateName));
                DotsWorldRegistry.Reset();   // 让模块自报的 Templates 重新写回来
            }
        }
    }
}
