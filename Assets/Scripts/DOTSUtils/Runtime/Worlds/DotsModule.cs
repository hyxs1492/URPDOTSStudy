using System;
using System.Collections.Generic;

namespace DOTSUtils.Worlds
{
    /// <summary>
    /// 模块的声明式定义。**任何程序集里继承它的具体类型都会被自动发现并登记**（见
    /// <see cref="DotsWorldRegistry"/>）。装配顺序由 <see cref="SortKey"/> 决定，与扫描顺序无关。
    /// 需要运行时动态构造、或不想被自动发现的场合，仍可用 <see cref="DotsModules.Register"/> 显式登记；
    /// IL2CPP 下若模块类型没有被任何静态代码引用，建议加 <c>[UnityEngine.Scripting.Preserve]</c> 防止裁剪。
    /// </summary>
    public abstract class DotsModule
    {
        /// <summary>
        /// 模块名（注册表键）。重名时先到者胜。
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// 装配顺序键：同一个 World 内按 (SortKey, Name) 升序装配。默认 0。
        /// </summary>
        public virtual int SortKey => 0;

        /// <summary>
        /// 要自动加入的 World 模板名。默认空 = 不自动加入，只能由模板显式列出或
        /// <see cref="DotsWorldManager.AddModule"/> 装配。
        /// </summary>
        public virtual IReadOnlyList<string> Templates => Array.Empty<string>();

        /// <summary>
        /// 本模块**依赖**的其它模块名（默认无依赖）。装配时若依赖缺失，
        /// <c>DotsWorldManager.AddModule</c> 会<b>拒绝装配</b>并给出警告。
        /// </summary>
        public virtual IReadOnlyList<string> Requires => Array.Empty<string>();

        /// <summary>
        /// 该模块包含的组（列表顺序即组内装配顺序）。
        /// </summary>
        public abstract IReadOnlyList<DotsModuleGroupDescriptor> Groups { get; }

        /// <summary>
        /// 转成注册表用的数据描述。
        /// </summary>
        public DotsModuleDescriptor ToDescriptor()
        {
            var groups = new DotsModuleGroupDescriptor[Groups.Count];
            for (var i = 0; i < groups.Length; i++)
            {
                groups[i] = Groups[i];
            }
            return new DotsModuleDescriptor(Name, SortKey, Requires, groups)
            {
                ModuleType = GetType(),   // 供模板按 Type 引用本模块
            };
        }
    }
}
