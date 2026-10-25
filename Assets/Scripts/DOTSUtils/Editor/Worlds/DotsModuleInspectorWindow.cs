using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DOTSUtils.Worlds;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

namespace DOTSUtils.Worlds.EditorTools
{
    /// <summary>
    /// 模块查看器：把 <see cref="DotsModules"/> 注册表、各 World 实际装了哪些模块，
    /// 以及模块之间的依赖 / 模板 / 执行顺序关系摊在一个窗口里。
    ///
    /// 左栏列出模块并标出已装配 / 未装配；右栏显示模板、Requires / 被依赖（两端可点击跳转）
    /// 以及组与系统的执行顺序声明（<c>UpdateInGroup</c> / <c>UpdateAfter</c> / <c>UpdateBefore</c>）。
    ///
    /// Play 模式会列出 <c>World.All</c> 里的每个 World 及其实时装配情况；非运行时只有注册表可看。
    /// </summary>
    public sealed class DotsModuleInspectorWindow : EditorWindow
    {
        private const string k_MenuPath = "Tools/DOTS World/Module Inspector";
        private const string k_AutoRefreshKey = "DOTSUtils.ModuleInspector.AutoRefresh";
        private const string k_HideInternalKey = "DOTSUtils.ModuleInspector.HideInternalWorlds";
        private const string k_ShowTestsKey = "DOTSUtils.ModuleInspector.ShowTests";

        /// <summary>左栏宽度。</summary>
        private const float k_ListWidth = 210f;

        /// <summary>底部模板区占窗口高度的比例（夹在最小/最大值之间）。</summary>
        private const float k_TemplateAreaRatio = 0.22f;
        private const float k_TemplateAreaMin = 72f;
        private const float k_TemplateAreaMax = 220f;

        /// <summary>左栏选中的模块名；null 表示没选。</summary>
        private string m_SelectedModule;

        /// <summary>当前查看哪个 World：0 = 不动用 World（只看注册表），否则是 <c>World.All</c> 的索引 + 1。</summary>
        private int m_WorldChoice;

        private bool m_AutoRefresh = true;

        /// <summary>隐藏 DOTS 内部的 World（<c>LoadingWorld*</c>）—— 它们对看模块没有意义。</summary>
        private bool m_HideInternalWorlds = true;

        /// <summary>显示测试程序集里声明的模块与模板。默认关闭。</summary>
        private bool m_ShowTests = false;
        private Vector2 m_ListScroll;
        private Vector2 m_DetailScroll;
        private Vector2 m_TemplateScroll;

        private readonly List<World> m_Worlds = new List<World>();

        [MenuItem(k_MenuPath)]
        private static void Open()
        {
            var window = GetWindow<DotsModuleInspectorWindow>("DOTS Modules");
            window.Show();
        }

        private void OnEnable()
        {
            // minSize 放在 OnEnable 里设：已经开着的窗口不会重新走 Open。
            minSize = new Vector2(880f, 520f);

            m_AutoRefresh = EditorPrefs.GetBool(k_AutoRefreshKey, true);
            m_HideInternalWorlds = EditorPrefs.GetBool(k_HideInternalKey, true);
            m_ShowTests = EditorPrefs.GetBool(k_ShowTestsKey, false);
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            // 只在运行时自动刷新。
            if (m_AutoRefresh && Application.isPlaying && !EditorApplication.isCompiling)
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            // 显式初始化一次（幂等，有 s_Initialized 守卫），让非运行时也能看到模块清单。
            DotsWorldRegistry.EnsureInitialized();

            RefreshWorlds();
            DrawToolbar();

            // 详情区吃掉工具栏与底部模板区之间的全部剩余高度。
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            DrawModuleList();
            DrawDetail();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);
            DrawTemplates();
        }

        // ───────────────────────── 取数 ─────────────────────────

        private void RefreshWorlds()
        {
            m_Worlds.Clear();
            m_Worlds.Add(null);   // 第 0 项：不动用 World
            foreach (var world in World.All)
            {
                // 跳过 DOTS 内部的临时加载 World。
                if (m_HideInternalWorlds &&
                    world.Name.StartsWith("LoadingWorld", StringComparison.Ordinal))
                {
                    continue;
                }
                m_Worlds.Add(world);
            }
            m_WorldChoice = Mathf.Clamp(m_WorldChoice, 0, m_Worlds.Count - 1);
        }

        private World SelectedWorld => m_Worlds.Count == 0 ? null : m_Worlds[Mathf.Clamp(m_WorldChoice, 0, m_Worlds.Count - 1)];

        /// <summary>类型所在程序集是否以 <c>.Tests</c> 结尾（测试程序集）。</summary>
        private static bool IsTestAssembly(Type type)
        {
            if (type == null)
            {
                return false;
            }
            var name = type.Assembly.GetName().Name;
            return name.EndsWith(".Tests", StringComparison.Ordinal) ||
                   name.EndsWith("Tests", StringComparison.Ordinal);
        }

        private bool Visible(DotsModuleDescriptor module) => m_ShowTests || !IsTestAssembly(module.ModuleType);

        private static List<DotsModuleDescriptor> SortedModules()
        {
            var list = new List<DotsModuleDescriptor>(DotsModules.All);
            list.Sort((a, b) =>
            {
                var byKey = a.SortKey.CompareTo(b.SortKey);
                return byKey != 0 ? byKey : string.CompareOrdinal(a.Name, b.Name);
            });
            return list;
        }

        /// <summary>拼出「模块名 → 它声明要加入的模板」的反查表（靠模板表反推，不看模块自报）。</summary>
        private static Dictionary<string, List<string>> BuildTemplateMembership()
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var template in DotsWorldTemplates.All)
            {
                foreach (var moduleType in template.Modules)
                {
                    if (!DotsModules.TryGet(moduleType, out var descriptor))
                    {
                        continue;   // 模板列了但没登记的模块
                    }
                    if (!map.TryGetValue(descriptor.Name, out var list))
                    {
                        list = new List<string>();
                        map[descriptor.Name] = list;
                    }
                    list.Add(template.Name);
                }
            }
            return map;
        }

        /// <summary>谁声明依赖了 <paramref name="moduleName"/>（反向依赖）。</summary>
        private static List<string> FindDependents(string moduleName)
        {
            var result = new List<string>();
            foreach (var module in DotsModules.All)
            {
                if (module.Requires == null)
                {
                    continue;
                }
                foreach (var required in module.Requires)
                {
                    if (required == moduleName)
                    {
                        result.Add(module.Name);
                        break;
                    }
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>反射读 <c>ModuleType</c> 上的实例属性，取模块自报的 <c>Templates</c>。</summary>
        private static IReadOnlyList<string> SelfDeclaredTemplates(DotsModuleDescriptor module)
        {
            if (module.ModuleType == null)
            {
                return Array.Empty<string>();
            }
            try
            {
                if (Activator.CreateInstance(module.ModuleType) is DotsModule instance)
                {
                    return instance.Templates ?? Array.Empty<string>();
                }
            }
            catch (Exception)
            {
                // 拿不到就返回空。
            }
            return Array.Empty<string>();
        }

        // ───────────────────────── 工具栏 ─────────────────────────

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField("World", GUILayout.Width(40f));
            var names = new string[m_Worlds.Count];
            for (var i = 0; i < m_Worlds.Count; i++)
            {
                var world = m_Worlds[i];
                if (world == null)
                {
                    names[i] = "（不看 World）";
                    continue;
                }
                var tag = "";
                if (world == DotsWorldManager.ActiveWorld)
                {
                    tag += " ★活跃";
                }
                if (world == DotsWorldManager.DefaultWorld)
                {
                    tag += " ◆默认";
                }
                names[i] = world.Name + tag;
            }
            m_WorldChoice = EditorGUILayout.Popup(m_WorldChoice, names, EditorStyles.toolbarPopup,
                                                 GUILayout.MinWidth(220f));

            GUILayout.FlexibleSpace();

            var tests = GUILayout.Toggle(m_ShowTests, "显示测试模块",
                                         EditorStyles.toolbarButton, GUILayout.Width(110f));
            if (tests != m_ShowTests)
            {
                m_ShowTests = tests;
                EditorPrefs.SetBool(k_ShowTestsKey, tests);
            }

            var hide = GUILayout.Toggle(m_HideInternalWorlds, "隐藏内部 World",
                                        EditorStyles.toolbarButton, GUILayout.Width(110f));
            if (hide != m_HideInternalWorlds)
            {
                m_HideInternalWorlds = hide;
                EditorPrefs.SetBool(k_HideInternalKey, hide);
            }

            var auto = GUILayout.Toggle(m_AutoRefresh, "自动刷新", EditorStyles.toolbarButton, GUILayout.Width(70f));
            if (auto != m_AutoRefresh)
            {
                m_AutoRefresh = auto;
                EditorPrefs.SetBool(k_AutoRefreshKey, auto);
            }
            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(50f)))
            {
                Repaint();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ───────────────────────── 左栏：模块列表 ─────────────────────────

        private void DrawModuleList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(k_ListWidth), GUILayout.ExpandHeight(true));
            var world = SelectedWorld;
            var modules = SortedModules();
            var shown = new List<DotsModuleDescriptor>();
            foreach (var module in modules)
            {
                if (Visible(module))
                {
                    shown.Add(module);
                }
            }

            var header = m_ShowTests
                ? $"模块（{shown.Count}）"
                : $"模块（{shown.Count}／共 {modules.Count}，已滤掉测试）";
            EditorGUILayout.LabelField(header, EditorStyles.boldLabel);

            m_ListScroll = EditorGUILayout.BeginScrollView(m_ListScroll);

            foreach (var module in shown)
            {
                var installed = world != null && world.IsCreated && DotsWorldManager.HasModule(world, module.Name);
                var missing = MissingRequires(module, world);
                var selected = module.Name == m_SelectedModule;

                var marker = installed ? "● " : "○ ";
                var warn = missing.Count > 0 ? "  ⚠" : "";
                var label = $"{marker}{module.Name}  ({module.SortKey}){warn}";

                var style = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = selected ? Color.cyan : (installed ? Color.white : Color.gray) },
                    fontStyle = selected ? FontStyle.Bold : FontStyle.Normal,
                };

                if (GUILayout.Button(label, style))
                {
                    m_SelectedModule = module.Name;
                }
            }

            EditorGUILayout.EndScrollView();

            if (world != null && world.IsCreated)
            {
                EditorGUILayout.LabelField("● 已装配   ○ 未装配", EditorStyles.miniLabel);
                if (MissingAcrossWorld(world, out var report))
                {
                    EditorGUILayout.HelpBox(report, MessageType.Warning);
                }
            }
            else
            {
                EditorGUILayout.LabelField("● / ○ 需要选一个 World", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>返回模块依赖里「在当前查看的 World 中缺失」的那些名字。</summary>
        private static List<string> MissingRequires(DotsModuleDescriptor module, World world)
        {
            var missing = new List<string>();
            if (module.Requires == null)
            {
                return missing;
            }
            foreach (var required in module.Requires)
            {
                if (!DotsModules.TryGet(required, out _))
                {
                    missing.Add(required + "（未登记）");
                }
                else if (world != null && world.IsCreated && !DotsWorldManager.HasModule(world, required))
                {
                    missing.Add(required + "（本 World 未装配）");
                }
            }
            return missing;
        }

        private static bool MissingAcrossWorld(World world, out string report)
        {
            var builder = new StringBuilder();
            foreach (var module in DotsModules.All)
            {
                if (!DotsWorldManager.HasModule(world, module.Name) || module.Requires == null)
                {
                    continue;
                }
                foreach (var required in module.Requires)
                {
                    if (DotsModules.TryGet(required, out var requiredModule) &&
                        !DotsWorldManager.HasModule(world, requiredModule.Name))
                    {
                        builder.AppendLine($"{module.Name} 依赖 {required}，但后者没装。");
                    }
                }
            }
            report = builder.ToString();
            return report.Length > 0;
        }

        // ───────────────────────── 右栏：模块详情 ─────────────────────────

        private void DrawDetail()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            m_DetailScroll = EditorGUILayout.BeginScrollView(m_DetailScroll);

            if (string.IsNullOrEmpty(m_SelectedModule) ||
                !DotsModules.TryGet(m_SelectedModule, out var module))
            {
                EditorGUILayout.HelpBox("在左边选一个模块。", MessageType.Info);
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField(module.Name, EditorStyles.boldLabel);

            DrawKeyValue("模块类型", module.ModuleType != null
                ? $"{module.ModuleType.FullName}   （{module.ModuleType.Assembly.GetName().Name}）"
                : "（未登记类型）");
            DrawKeyValue("装配排序 SortKey", module.SortKey.ToString());
            DrawKeyValue("程序集", module.ModuleType?.Assembly.GetName().Name ?? "-");

            // ---- 模板 ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("模板", EditorStyles.boldLabel);
            var membership = BuildTemplateMembership();
            if (membership.TryGetValue(module.Name, out var templates) && templates.Count > 0)
            {
                foreach (var template in templates)
                {
                    EditorGUILayout.LabelField($"    • {template}（模板里列了它）");
                }
            }
            var selfDeclared = SelfDeclaredTemplates(module);
            foreach (var template in selfDeclared)
            {
                if (templates == null || !templates.Contains(template))
                {
                    EditorGUILayout.LabelField($"    • {template}（模块自报）");
                }
            }
            if ((templates == null || templates.Count == 0) && selfDeclared.Count == 0)
            {
                EditorGUILayout.LabelField("    （不属于任何模板 —— 只能显式装配）", EditorStyles.miniLabel);
            }

            // ---- 依赖 ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("依赖（Requires）", EditorStyles.boldLabel);
            if (module.Requires == null || module.Requires.Count == 0)
            {
                EditorGUILayout.LabelField("    （无）", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var required in module.Requires)
                {
                    var known = DotsModules.TryGet(required, out var requiredModule);
                    var extra = known ? $"  ({requiredModule.SortKey})" : "  ⚠ 未登记";
                    if (GUILayout.Button($"    → {required}{extra}", EditorStyles.label) && known)
                    {
                        m_SelectedModule = requiredModule.Name;
                    }
                }
                EditorGUILayout.LabelField("    装配时会递归补上（见 DotsWorldManager.AddModuleInternal）",
                                           EditorStyles.miniLabel);
            }

            // ---- 被依赖 ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("被依赖（谁 Requires 了它）", EditorStyles.boldLabel);
            var dependents = FindDependents(module.Name);
            if (!m_ShowTests)
            {
                dependents.RemoveAll(name => DotsModules.TryGet(name, out var d) && IsTestAssembly(d.ModuleType));
            }
            if (dependents.Count == 0)
            {
                EditorGUILayout.LabelField("    （无）", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var dependent in dependents)
                {
                    if (GUILayout.Button($"    ← {dependent}", EditorStyles.label) &&
                        DotsModules.TryGet(dependent, out var dependentModule))
                    {
                        m_SelectedModule = dependentModule.Name;
                    }
                }
            }

            // ---- 组与系统 ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("模块组与系统", EditorStyles.boldLabel);
            if (module.Groups == null || module.Groups.Count == 0)
            {
                EditorGUILayout.LabelField("    （没有组 —— 模块只声明依赖/模板）", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var group in module.Groups)
                {
                    DrawGroup(module, group);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawGroup(DotsModuleDescriptor module, DotsModuleGroupDescriptor group)
        {
            var world = SelectedWorld;
            var created = world != null && world.IsCreated &&
                          world.GetExistingSystemManaged(group.GroupType) != null;

            EditorGUILayout.LabelField($"    {group.GroupType.Name}{(created ? "  ●" : "")}", EditorStyles.boldLabel);

            var parent = ReadUpdateInGroup(group.GroupType);
            EditorGUILayout.LabelField($"        挂在 {parent?.Name ?? "（缺 [UpdateInGroup]）"}");
            DrawOrdering("        组顺序", group.GroupType);

            foreach (var systemType in group.Systems)
            {
                EditorGUILayout.LabelField($"        └ {systemType.Name}");
                DrawOrdering("            系统顺序", systemType);
            }
        }

        private static void DrawOrdering(string prefix, Type type)
        {
            foreach (var line in ReadOrdering(type))
            {
                EditorGUILayout.LabelField(prefix + "  " + line, EditorStyles.miniLabel);
            }
        }

        // ───────────────────────── 底部：模板 ─────────────────────────

        private void DrawTemplates()
        {
            EditorGUILayout.LabelField($"模板（{DotsWorldTemplates.All.Count}）", EditorStyles.boldLabel);

            // 高度跟着窗口走。
            var templateHeight = Mathf.Clamp(position.height * k_TemplateAreaRatio,
                                             k_TemplateAreaMin, k_TemplateAreaMax);
            m_TemplateScroll = EditorGUILayout.BeginScrollView(
                m_TemplateScroll, GUILayout.Height(templateHeight), GUILayout.ExpandWidth(true));

            foreach (var template in DotsWorldTemplates.All)
            {
                var modules = new List<string>();
                var skippedTests = 0;
                foreach (var moduleType in template.Modules)
                {
                    var isTest = IsTestAssembly(moduleType);
                    if (isTest && !m_ShowTests)
                    {
                        skippedTests++;
                        continue;
                    }
                    modules.Add(DotsModules.TryGet(moduleType, out var descriptor)
                        ? descriptor.Name
                        : moduleType.Name + "（未登记）");
                }

                // 整条都是测试模块的模板就不用列。
                if (modules.Count == 0 && skippedTests > 0)
                {
                    continue;
                }

                var inUse = false;
                foreach (var world in m_Worlds)
                {
                    if (world != null && world.IsCreated && WorldUsesTemplate(world, modules))
                    {
                        inUse = true;
                        break;
                    }
                }

                var suffix = skippedTests > 0 ? $"（另有 {skippedTests} 个测试模块已滤掉）" : "";
                EditorGUILayout.LabelField(
                    $"    {template.Name}{(inUse ? "  ●使用中" : "")}：{string.Join(" → ", modules)}{suffix}");
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>World 装的模块集合是否与模板的模块集合一致（两边都按同一规则过滤测试模块）。</summary>
        private bool WorldUsesTemplate(World world, List<string> templateModules)
        {
            var installed = new List<string>();
            foreach (var name in DotsWorldManager.GetModules(world))
            {
                if (!m_ShowTests && DotsModules.TryGet(name, out var descriptor) &&
                    IsTestAssembly(descriptor.ModuleType))
                {
                    continue;
                }
                installed.Add(name);
            }

            if (installed.Count != templateModules.Count)
            {
                return false;
            }
            foreach (var name in templateModules)
            {
                if (!installed.Contains(name))
                {
                    return false;
                }
            }
            return true;
        }

        // ───────────────────────── 反射读 attribute ─────────────────────────

        private static void DrawKeyValue(string key, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(key, GUILayout.Width(150f));
            EditorGUILayout.SelectableLabel(value, EditorStyles.label,
                                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }

        private static Type ReadUpdateInGroup(Type type)
        {
            var attribute = (UpdateInGroupAttribute)Attribute.GetCustomAttribute(type, typeof(UpdateInGroupAttribute));
            return attribute?.GroupType;
        }

        /// <summary>读出类型上的 UpdateAfter / UpdateBefore（含 OrderFirst / OrderLast / UpdateInGroup 的排序标记）。</summary>
        private static List<string> ReadOrdering(Type type)
        {
            var lines = new List<string>();
            foreach (var attribute in type.GetCustomAttributes(typeof(UpdateInGroupAttribute), false))
            {
                var typed = (UpdateInGroupAttribute)attribute;
                var flags = new List<string>();
                if (typed.OrderFirst)
                {
                    flags.Add("OrderFirst");
                }
                if (typed.OrderLast)
                {
                    flags.Add("OrderLast");
                }
                if (flags.Count > 0)
                {
                    lines.Add($"UpdateInGroup({typed.GroupType.Name}) [{string.Join(", ", flags)}]");
                }
            }
            foreach (var attribute in type.GetCustomAttributes(typeof(UpdateAfterAttribute), false))
            {
                lines.Add($"UpdateAfter({((UpdateAfterAttribute)attribute).SystemType.Name})");
            }
            foreach (var attribute in type.GetCustomAttributes(typeof(UpdateBeforeAttribute), false))
            {
                lines.Add($"UpdateBefore({((UpdateBeforeAttribute)attribute).SystemType.Name})");
            }
            if (lines.Count == 0)
            {
                lines.Add("（无额外顺序声明 —— 组内按系统名排序）");
            }
            return lines;
        }
    }
}
