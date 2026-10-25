using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Custom/CustomLit 材质 Inspector。
///
/// 分区用 Unity 原生的 <see cref="MaterialHeaderScopeList"/> 绘制（URP 自己用的那套），
/// 每一节都是可折叠 header，展开状态按 bit 记在 MaterialEditor 里。
/// 分区顺序按要求固定为：
///   Surface Options → Surface Inputs → Effects → Detail Inputs → Advanced Options
///
/// 已完成：Surface Options（第 1 步）、Detail Inputs（第 2 步）、可折叠分区（第 5 步）。
/// 待完成：溶解方向与语义（第 3 步）、描边屏幕空间化（第 4 步）。
/// </summary>
public class CustomLitShaderGUI : ShaderGUI
{
    enum SurfaceType { Opaque = 0, Transparent = 1 }
    enum BlendMode { Alpha = 0, Premultiply = 1, Additive = 2, Multiply = 3 }
    enum RenderFace { Both = 0, Back = 1, Front = 2 }

    /// <summary>分区展开状态 bit，必须是 2 的幂且互不重复。</summary>
    static class Expandable
    {
        public const uint SurfaceOptions = 1 << 0;
        public const uint SurfaceInputs = 1 << 1;
        public const uint Effects = 1 << 2;
        public const uint Details = 1 << 3;
        public const uint Advanced = 1 << 4;
    }

    static readonly string[] kSurfaceTypeNames = { "Opaque", "Transparent" };
    static readonly string[] kBlendModeNames = { "Alpha", "Premultiply", "Additive", "Multiply" };
    static readonly string[] kRenderFaceNames = { "Both", "Back", "Front" };
    static readonly string[] kWorkflowModeNames = { "Specular", "Metallic" };

    /// <summary>溶解的 4 个模式关键字，与 shader 的 #pragma shader_feature_local 那组一一对应。</summary>
    static readonly string[] kDissolveModeKeywords =
    {
        "_DISSOLVE_RADIAL", "_DISSOLVE_AXIS_X", "_DISSOLVE_AXIS_Y", "_DISSOLVE_AXIS_Z"
    };

    // ---- 分区标题 ----
    static readonly GUIContent kSurfaceOptionsHeader = EditorGUIUtility.TrTextContent("Surface Options",
        "Controls how URP renders the material on screen.");
    static readonly GUIContent kSurfaceInputsHeader = EditorGUIUtility.TrTextContent("Surface Inputs",
        "These settings describe the look and feel of the surface itself.");
    static readonly GUIContent kEffectsHeader = EditorGUIUtility.TrTextContent("Effects",
        "本 shader 附加的效果：外发光、径向溶解、描边。");
    static readonly GUIContent kDetailHeader = EditorGUIUtility.TrTextContent("Detail Inputs",
        "These settings define the surface details by tiling and overlaying additional maps on the surface.");
    static readonly GUIContent kAdvancedHeader = EditorGUIUtility.TrTextContent("Advanced Options",
        "These settings affect behind-the-scenes rendering and underlying calculations.");

    // ---- 控件文案 ----
    static readonly GUIContent kWorkflowModeText = EditorGUIUtility.TrTextContent("Workflow Mode");
    static readonly GUIContent kSurfaceTypeText = EditorGUIUtility.TrTextContent("Surface Type",
        "Select a surface type for your texture. Choose between Opaque or Transparent.");
    static readonly GUIContent kBlendModeText = EditorGUIUtility.TrTextContent("Blending Mode",
        "Controls how the color of the Transparent surface blends with the Material color in the background.");
    static readonly GUIContent kPreserveSpecularText = EditorGUIUtility.TrTextContent("Preserve Specular Lighting",
        "Preserves specular lighting intensity and size by not applying transparent alpha to the specular light contribution.");
    static readonly GUIContent kZWriteText = EditorGUIUtility.TrTextContent("Depth Write",
        "Controls whether the shader writes depth. Transparent surfaces usually leave this Off.");
    static readonly GUIContent kRenderFaceText = EditorGUIUtility.TrTextContent("Render Face",
        "Specifies which faces to cull from your geometry. Front culls front faces. Back culls backfaces. None means that both sides are rendered.");
    static readonly GUIContent kBaseMapText = EditorGUIUtility.TrTextContent("Base Map", "基础颜色贴图");
    static readonly GUIContent kMroMapText = EditorGUIUtility.TrTextContent("MRO Map",
        "R: Metallic / Roughness   G: Roughness   B: Occlusion");
    static readonly GUIContent kNormalMapText = EditorGUIUtility.TrTextContent("Normal Map");
    static readonly GUIContent kEmissionMapText = EditorGUIUtility.TrTextContent("Emission Map");
    static readonly GUIContent kDetailMaskText = EditorGUIUtility.TrTextContent("Mask",
        "细节遮罩，使用所选贴图的 Alpha 通道；Tiling/Offset 对 Mask 无效。");
    static readonly GUIContent kDetailAlbedoText = EditorGUIUtility.TrTextContent("Base Map",
        "细节颜色贴图（Detail Albedo，sRGB）。");
    static readonly GUIContent kDetailNormalText = EditorGUIUtility.TrTextContent("Normal Map", "细节法线贴图。");
    static readonly GUIContent kDetailScaleInfo = EditorGUIUtility.TrTextContent(
        "Detail Albedo Scale != 1 会走 _DETAIL_SCALED 变体（约多 6 条指令）。");

    // ---- Dissolve 文案 ----
    static readonly string[] kDissolveDirectionNames = { "Radial", "X", "Y", "Z" };
    static readonly GUIContent kDissolveOnText = EditorGUIUtility.TrTextContent("Dissolve (溶解)");
    static readonly GUIContent kDissolveDirectionText = EditorGUIUtility.TrTextContent("Direction",
        "从哪个方向开始溶解：Radial 从原点向外扩散；X / Y / Z 沿对应轴推进。");
    static readonly GUIContent kDissolveAmountText = EditorGUIUtility.TrTextContent("Amount",
        "0 = 全部溶解（空），1 = 完全可见。");
    static readonly GUIContent kDissolveOriginRadialText = EditorGUIUtility.TrTextContent("Origin (Object Space)",
        "径向溶解的球心，物体空间坐标。用 Auto-Fit 会自动填成 mesh 包围盒中心。");
    static readonly GUIContent kDissolveOriginAxisText = EditorGUIUtility.TrTextContent("Origin (Object Space)",
        "轴向溶解的行程中点，物体空间坐标。用 Auto-Fit 会自动填成 mesh 包围盒中心。");
    static readonly GUIContent kDissolveRadiusText = EditorGUIUtility.TrTextContent("Radius (Object Units)",
        "溶解因子从 0 增长到 1 所需距离。\n" +
        "轴向：半径 = 该轴的完整尺寸（Auto-Fit 自动填），这样因子正好覆盖 0..1。\n" +
        "径向：半径 = 到最远角落的距离。");
    static readonly GUIContent kDissolveAutoFitText = EditorGUIUtility.TrTextContent("Auto-Fit To Mesh",
        "按本材质所挂 mesh 的包围盒（物体空间）自动填 Origin 与 Radius：\n" +
        "  Origin = 包围盒中心\n" +
        "  Radius = 当前方向对应轴的尺寸（径向取半对角线）");
    static readonly GUIContent kDissolveNoiseText = EditorGUIUtility.TrTextContent("Noise Map",
        "R 通道噪声；默认 gray(0.5) 表示不加噪声扰动。");

    // ---- Outline 文案 ----
    static readonly string[] kOutlineModeNames = { "Silhouette", "Detail" };
    static readonly GUIContent kOutlineOnText = EditorGUIUtility.TrTextContent("Outline (描边)",
        "世界空间外扩 + 距离补偿的反转外壳描边：宽度以像素计、任何朝向的轮廓都被均匀加粗。");
    static readonly GUIContent kOutlineModeText = EditorGUIUtility.TrTextContent("Mode",
        "Silhouette：只画外轮廓（描边铺底后被主体盖掉，内部细节线消失）。\n" +
        "Detail：保留内部结构线（画面上每条凸起/接缝都会有自己的轮廓）。");
    static readonly GUIContent kOutlineWidthText = EditorGUIUtility.TrTextContent("Width (Pixels)",
        "描边宽度，单位像素（屏幕上恒定，不随距离变化）。");
    static readonly GUIContent kOutlineSmoothText = EditorGUIUtility.TrTextContent("Smooth Normals",
        "用烘焙进 UV3 的平滑法线外扩，修掉硬边模型上描边的缺口/断裂。\n" +
        "需要先用下面的 Bake Smooth Normals 烘焙一次。");
    static readonly GUIContent kOutlineBakeText = EditorGUIUtility.TrTextContent("Bake Smooth Normals",
        "把「按位置平均后的法线」烘焙进选中物体 mesh 的 UV3 通道（物体空间）。\n" +
        "一次性操作；换模型或改模型后需要重新烘。UV3 原本被占用时会提示放弃。");

    MaterialEditor m_Editor;
    MaterialProperty[] m_Properties;

    readonly MaterialHeaderScopeList m_Scopes = new MaterialHeaderScopeList();
    bool m_ScopesInitialized;

    // ------------------------------------------------------------------
    //  ShaderGUI 入口
    // ------------------------------------------------------------------

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        m_Editor = materialEditor;
        m_Properties = properties;

        if (!m_ScopesInitialized)
        {
            RegisterScopes();
            m_ScopesInitialized = true;
        }

        if (m_Editor.targets.Length > 0 && m_Editor.targets[0] is Material mat)
        {
            m_Scopes.DrawHeaders(m_Editor, mat);
        }
        else
        {
            // 多选 / 异常兜底：平铺
            DrawSurfaceOptions(null);
            DrawSurfaceInputs(null);
            DrawEffects(null);
            DrawDetailInputs(null);
            DrawAdvancedOptions(null);
        }

        // 关键字与混合状态必须在属性写入之后再同步
        SyncMaterialKeywords();
        ApplySurfaceOptions();
    }

    void RegisterScopes()
    {
        // 顺序即显示顺序，按要求固定
        m_Scopes.RegisterHeaderScope(kSurfaceOptionsHeader, Expandable.SurfaceOptions, DrawSurfaceOptions);
        m_Scopes.RegisterHeaderScope(kSurfaceInputsHeader, Expandable.SurfaceInputs, DrawSurfaceInputs);
        m_Scopes.RegisterHeaderScope(kEffectsHeader, Expandable.Effects, DrawEffects);
        m_Scopes.RegisterHeaderScope(kDetailHeader, Expandable.Details, DrawDetailInputs);
        m_Scopes.RegisterHeaderScope(kAdvancedHeader, Expandable.Advanced, DrawAdvancedOptions);
    }

    // ------------------------------------------------------------------
    //  Surface Options
    // ------------------------------------------------------------------

    void DrawSurfaceOptions(Material material)
    {
        // 与 URP BaseShaderGUI 一致：label 宽度跟随 Inspector 默认值
        EditorGUIUtility.labelWidth = 0f;

        DrawEnumPopup("_WorkflowMode", kWorkflowModeText, kWorkflowModeNames);

        DrawEnumPopup("_Surface", kSurfaceTypeText, kSurfaceTypeNames);
        bool transparent = Prop("_Surface").floatValue >= 0.5f;

        if (transparent)
        {
            EditorGUI.indentLevel++;
            DrawEnumPopup("_Blend", kBlendModeText, kBlendModeNames);

            BlendMode blendMode = (BlendMode)Mathf.RoundToInt(Prop("_Blend").floatValue);
            bool disabled = blendMode == BlendMode.Multiply || blendMode == BlendMode.Premultiply;
            DrawFloatToggle(kPreserveSpecularText, Prop("_BlendModePreserveSpecular"), disabled);

            m_Editor.ShaderProperty(Prop("_ZWrite"), kZWriteText);
            EditorGUI.indentLevel--;
        }

        DrawEnumPopup("_Cull", kRenderFaceText, kRenderFaceNames);

        m_Editor.ShaderProperty(Prop("_AlphaClip"), "Alpha Clipping");
        if (Prop("_AlphaClip").floatValue != 0.0f)
            m_Editor.ShaderProperty(Prop("_Cutoff"), "  Alpha Cutoff");

        m_Editor.ShaderProperty(Prop("_ReceiveShadows"), "Receive Shadows");
    }

    // ------------------------------------------------------------------
    //  Surface Inputs
    // ------------------------------------------------------------------

    void DrawSurfaceInputs(Material material)
    {
        m_Editor.TexturePropertySingleLine(kBaseMapText, Prop("_BaseMap"), Prop("_BaseColor"));

        m_Editor.TexturePropertySingleLine(kMroMapText, Prop("_MROMap"));
        bool hasMap = Prop("_MROMap").textureValue != null;
        m_Editor.ShaderProperty(Prop("_Metallic"), hasMap ? "Metallic × R" : "Metallic");
        m_Editor.ShaderProperty(Prop("_Roughness"), hasMap ? "Roughness × G" : "Roughness");
        if (hasMap)
            m_Editor.ShaderProperty(Prop("_OcclusionStrength"), "Occlusion × B");

        m_Editor.TexturePropertySingleLine(kNormalMapText, Prop("_BumpMap"), Prop("_BumpScale"));

        m_Editor.ShaderProperty(Prop("_EmissionOn"), "Emission");
        if (Prop("_EmissionOn").floatValue != 0.0f)
        {
            EditorGUI.indentLevel++;
            m_Editor.ShaderProperty(Prop("_EmissionColor"), "Color");
            m_Editor.TexturePropertySingleLine(kEmissionMapText, Prop("_EmissionMap"));
            EditorGUI.indentLevel--;
        }

        m_Editor.TextureScaleOffsetProperty(Prop("_BaseMap"));
    }

    // ------------------------------------------------------------------
    //  Effects
    // ------------------------------------------------------------------

    void DrawEffects(Material material)
    {
        // ---- Rim ----
        m_Editor.ShaderProperty(Prop("_RimOn"), "Rim Glow (外发光)");
        if (Prop("_RimOn").floatValue != 0.0f)
        {
            EditorGUI.indentLevel++;
            m_Editor.ShaderProperty(Prop("_RimColor"), "Color");
            m_Editor.ShaderProperty(Prop("_RimPower"), "Power");
            m_Editor.ShaderProperty(Prop("_RimIntensity"), "Intensity");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space();

        // ---- Dissolve（方向可选：径向 / X / Y / Z；0=全空，1=全显）----
        m_Editor.ShaderProperty(Prop("_DissolveOn"), kDissolveOnText);
        if (Prop("_DissolveOn").floatValue != 0.0f)
        {
            EditorGUI.indentLevel++;

            DrawEnumPopup("_DissolveDirectionMode", kDissolveDirectionText, kDissolveDirectionNames);

            bool radial = Mathf.RoundToInt(Prop("_DissolveDirectionMode").floatValue) == 0;
            m_Editor.ShaderProperty(Prop("_DissolveAmount"), kDissolveAmountText);
            m_Editor.ShaderProperty(Prop("_DissolveCenter"),
                radial ? kDissolveOriginRadialText : kDissolveOriginAxisText);
            m_Editor.ShaderProperty(Prop("_DissolveRadius"), kDissolveRadiusText);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Space(EditorGUI.indentLevel * 15f, false);
            if (GUILayout.Button(kDissolveAutoFitText, GUILayout.Width(150f)))
                AutoFitDissolveToMesh();
            EditorGUILayout.EndHorizontal();

            m_Editor.TexturePropertySingleLine(kDissolveNoiseText, Prop("_DissolveNoiseMap"));
            m_Editor.ShaderProperty(Prop("_DissolveNoiseStrength"), "Noise Strength");
            m_Editor.ShaderProperty(Prop("_DissolveNoiseScale"), "Noise Scale");
            m_Editor.ShaderProperty(Prop("_DissolveCutoff"), "Cutoff (Bias)");

            EditorGUILayout.Space();
            m_Editor.ShaderProperty(Prop("_DissolveEdgeColor"), "Edge Color");
            m_Editor.ShaderProperty(Prop("_DissolveEdgeWidth"), "Edge Width");

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space();

        // ---- Outline ----
        m_Editor.ShaderProperty(Prop("_OutlineOn"), kOutlineOnText);
        if (Prop("_OutlineOn").floatValue != 0.0f)
        {
            EditorGUI.indentLevel++;

            DrawOutlineModePopup();

            m_Editor.ShaderProperty(Prop("_OutlineColor"), "Color");
            m_Editor.ShaderProperty(Prop("_OutlineWidthPixels"), kOutlineWidthText);
            m_Editor.ShaderProperty(Prop("_OutlineSmoothNormals"), kOutlineSmoothText);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Space(EditorGUI.indentLevel * 15f, false);
            if (GUILayout.Button(kOutlineBakeText, GUILayout.Width(170f)))
                OutlineNormalBaker.BakeSelection();
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }
    }

    // ------------------------------------------------------------------
    //  Detail Inputs
    // ------------------------------------------------------------------

    void DrawDetailInputs(Material material)
    {
        m_Editor.TexturePropertySingleLine(kDetailMaskText, Prop("_DetailMask"));

        m_Editor.TexturePropertySingleLine(kDetailAlbedoText, Prop("_DetailAlbedoMap"));

        if (Prop("_DetailAlbedoMapScale").floatValue != 1.0f)
            EditorGUILayout.HelpBox(kDetailScaleInfo.text, MessageType.Info, true);

        m_Editor.TexturePropertySingleLine(kDetailNormalText,
            Prop("_DetailNormalMap"), Prop("_DetailNormalMapScale"));

        m_Editor.ShaderProperty(Prop("_DetailAlbedoMapScale"), "Albedo Scale");

        // Detail 与 BaseMap 共用 UV，这里编辑 Detail 自身的 Tiling/Offset
        m_Editor.TextureScaleOffsetProperty(Prop("_DetailAlbedoMap"));
    }

    // ------------------------------------------------------------------
    //  Advanced Options
    // ------------------------------------------------------------------

    void DrawAdvancedOptions(Material material)
    {
        m_Editor.RenderQueueField();
        m_Editor.EnableInstancingField();
        m_Editor.DoubleSidedGIField();
    }

    // ------------------------------------------------------------------
    //  同步
    // ------------------------------------------------------------------

    void SyncMaterialKeywords()
    {
        bool hasNormalMap = Prop("_BumpMap").textureValue != null;
        bool hasMROMap = Prop("_MROMap").textureValue != null;
        bool detailEnabled = Prop("_DetailAlbedoMap").textureValue != null
                          || Prop("_DetailNormalMap").textureValue != null;
        bool detailScaled = Prop("_DetailAlbedoMapScale").floatValue != 1.0f;

        bool dissolveOn = Prop("_DissolveOn").floatValue >= 0.5f;
        int dissolveMode = Mathf.Clamp(
            Mathf.RoundToInt(Prop("_DissolveDirectionMode").floatValue), 0, kDissolveModeKeywords.Length - 1);

        foreach (Object obj in m_Editor.targets)
        {
            if (!(obj is Material m)) continue;

            SetKeyword(m, "_NORMALMAP", hasNormalMap);
            SetKeyword(m, "_MRO_MAP", hasMROMap);

            SetKeyword(m, "_DETAIL", detailEnabled);
            SetKeyword(m, "_DETAIL_SCALED", detailEnabled && detailScaled);
            SetKeyword(m, "_DETAIL_MULX2", detailEnabled && !detailScaled);

            // 溶解：总开关 + 四选一模式
            SetKeyword(m, "_DISSOLVE_ON", dissolveOn);
            for (int i = 0; i < kDissolveModeKeywords.Length; i++)
                SetKeyword(m, kDissolveModeKeywords[i], dissolveOn && i == dissolveMode);
        }
    }

    /// <summary>Surface Type / Blending Mode → 关键字、Blend 因子、ZWrite、RenderQueue（与 URP 一致）。</summary>
    void ApplySurfaceOptions()
    {
        bool transparent = (SurfaceType)Mathf.RoundToInt(Prop("_Surface").floatValue) == SurfaceType.Transparent;
        BlendMode blendMode = (BlendMode)Mathf.RoundToInt(Prop("_Blend").floatValue);
        bool preserveSpecular = Prop("_BlendModePreserveSpecular").floatValue >= 0.5f;

        int srcBlend, dstBlend;
        bool alphaPremultiply = false, alphaModulate = false;

        if (!transparent)
        {
            srcBlend = (int)UnityEngine.Rendering.BlendMode.One;
            dstBlend = (int)UnityEngine.Rendering.BlendMode.Zero;
        }
        else
        {
            switch (blendMode)
            {
                case BlendMode.Premultiply:
                    srcBlend = (int)UnityEngine.Rendering.BlendMode.One;
                    dstBlend = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
                    break;
                case BlendMode.Additive:
                    srcBlend = (int)UnityEngine.Rendering.BlendMode.SrcAlpha;
                    dstBlend = (int)UnityEngine.Rendering.BlendMode.One;
                    break;
                case BlendMode.Multiply:
                    srcBlend = (int)UnityEngine.Rendering.BlendMode.DstColor;
                    dstBlend = (int)UnityEngine.Rendering.BlendMode.Zero;
                    alphaModulate = true;
                    break;
                default: // Alpha
                    srcBlend = (int)UnityEngine.Rendering.BlendMode.SrcAlpha;
                    dstBlend = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
                    alphaPremultiply = preserveSpecular;
                    break;
            }
        }

        int zWrite = transparent ? Mathf.RoundToInt(Prop("_ZWrite").floatValue) : 1;
        int autoQueue = transparent ? (int)RenderQueue.Transparent : (int)RenderQueue.Geometry;
        bool bothFaces = Mathf.RoundToInt(Prop("_Cull").floatValue) == (int)RenderFace.Both;

        foreach (Object obj in m_Editor.targets)
        {
            if (!(obj is Material m)) continue;

            SetKeyword(m, "_SURFACE_TYPE_TRANSPARENT", transparent);
            SetKeyword(m, "_ALPHAPREMULTIPLY_ON", alphaPremultiply);
            SetKeyword(m, "_ALPHAMODULATE_ON", alphaModulate);

            Prop("_SrcBlend").floatValue = srcBlend;
            Prop("_DstBlend").floatValue = dstBlend;
            Prop("_ZWrite").floatValue = zWrite;
            m.SetInt("_SrcBlend", srcBlend);
            m.SetInt("_DstBlend", dstBlend);
            m.SetInt("_ZWrite", zWrite);
            m.SetOverrideTag("RenderType", transparent ? "Transparent" : "Opaque");

            if (m.renderQueue == (int)RenderQueue.Geometry || m.renderQueue == (int)RenderQueue.Transparent)
                m.renderQueue = autoQueue;

            m.doubleSidedGI = bothFaces;
        }
    }

    // ------------------------------------------------------------------
    //  Auto-Fit：按 mesh 的物体空间包围盒生成 Origin / Radius
    // ------------------------------------------------------------------    /// <summary>
    /// 按当前材质所挂 mesh 的包围盒自动填溶解参数：
    ///   Origin = 包围盒中心（物体空间）
    ///   轴向   : Radius = 该轴的完整尺寸
    ///   径向   : Radius = 包围盒半对角线长度
    /// 同一材质被多个 mesh 共用时，取第一个可用的 mesh（会在 Console 提示）。
    /// </summary>
    void AutoFitDissolveToMesh()
    {
        if (!TryGetSharedMeshBounds(out Bounds bounds, out Mesh mesh))
        {
            Debug.LogWarning("[CustomLit] Auto-Fit 失败：找不到挂有本材质的 MeshRenderer / MeshFilter。");
            return;
        }

        int mode = Mathf.Clamp(
            Mathf.RoundToInt(Prop("_DissolveDirectionMode").floatValue), 0, 3);
        Vector3 size = bounds.size;
        Vector3 center = bounds.center;

        float radius;
        string axisName;
        switch (mode)
        {
            case 1: radius = size.x; axisName = "X"; break;
            case 2: radius = size.y; axisName = "Y"; break;
            case 3: radius = size.z; axisName = "Z"; break;
            default: radius = bounds.extents.magnitude; axisName = "径向"; break;
        }

        m_Editor.RegisterPropertyChangeUndo("Auto-Fit Dissolve");
        Prop("_DissolveCenter").vectorValue = new Vector4(center.x, center.y, center.z, 0f);
        Prop("_DissolveRadius").floatValue = Mathf.Max(radius, 1e-4f);

        Debug.Log(string.Format(
            "[CustomLit] Auto-Fit（{0}）：mesh=\"{1}\" 物体空间 center={2} size={3} -> Origin={4} Radius={5:0.###}",
            axisName, mesh.name, center.ToString("F3"), size.ToString("F3"),
            center.ToString("F3"), radius));
    }

    /// <summary>找到第一个挂有本材质的 Renderer，返回其 mesh 的物体空间包围盒。</summary>
    bool TryGetSharedMeshBounds(out Bounds bounds, out Mesh mesh)
    {
        bounds = default(Bounds);
        mesh = null;
        if (m_Editor.targets.Length == 0) return false;

        var wanted = new HashSet<Material>();
        foreach (Object o in m_Editor.targets)
            if (o is Material mat) wanted.Add(mat);
        if (wanted.Count == 0) return false;

        // 先扫场景对象（含未激活）
        foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
        {
            if (r == null || r.gameObject == null) continue;
            if (r.gameObject.scene.IsValid() == false) continue;          // 排除 prefab 资源本体
            if (!wanted.Contains(r.sharedMaterial)) continue;

            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            bounds = mf.sharedMesh.bounds;
            mesh = mf.sharedMesh;
            return true;
        }

        // 退化到 prefab 资源
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) continue;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!wanted.Contains(r.sharedMaterial)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                bounds = mf.sharedMesh.bounds;
                mesh = mf.sharedMesh;
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    //  工具
    // ------------------------------------------------------------------

    MaterialProperty Prop(string name) => FindProperty(name, m_Properties, true);

    void DrawEnumPopup(string propName, GUIContent label, string[] names)
    {
        MaterialProperty p = FindProperty(propName, m_Properties, false);
        if (p == null) return;

        EditorGUI.showMixedValue = p.hasMixedValue;
        EditorGUI.BeginChangeCheck();
        int v = EditorGUILayout.Popup(label, Mathf.RoundToInt(p.floatValue), names);
        if (EditorGUI.EndChangeCheck())
        {
            p.floatValue = v;
            m_Editor.RegisterPropertyChangeUndo(label.text);
        }
        EditorGUI.showMixedValue = false;
    }

    /// <summary>
    /// 描边模式下拉：Silhouette / Detail。模式由 _OutlineZTest + _OutlineZWrite 表达。
    ///
    ///   Silhouette -> ZTest Always(8) + ZWrite On(1)
    ///       不测深度地铺底并写深度，主体随后盖掉框内部分 -> 只剩外轮廓。
    ///       代价：不测深度，所以会从部件缝隙里透出来（穿模）。
    ///       ⚠️ ZWrite 必须为 On：实测 ZWrite=0 时整条描边会彻底消失。
    ///   Detail     -> ZTest LEqual(4) + ZWrite On(1)
    ///       外壳被深度遮挡，但内部凹陷处的背面仍会画出 -> 保留内部结构线。
    ///
    /// 关于"完整剪影"（无内部线 + 无穿模）：单材质做不到，已实测排除两条路 ——
    ///   · ZTest Greater（含沿视线推远深度）：恒为 0 像素。因为本 pass 在主体之后绘制，
    ///     此时深度缓冲已被主体填满，外壳永远"更近"，Greater 永不通过。
    ///   · stencil 遮罩：ShaderLab 的 #if 拿不到 shader_feature 关键字，
    ///     无法在"关闭时不写 stencil"和"开启时写 stencil"之间切换，会污染 SSAO 的 stencil。
    /// 真要做需要给描边单独一个材质（多一个 renderer），或改用全屏后处理。
    /// </summary>
    void DrawOutlineModePopup()
    {
        MaterialProperty zt = FindProperty("_OutlineZTest", m_Properties, false);
        MaterialProperty zw = FindProperty("_OutlineZWrite", m_Properties, false);
        if (zt == null || zw == null) return;

        int mode = zt.floatValue >= 7.5f ? 0 : 1;   // 8 = Always -> Silhouette

        EditorGUI.showMixedValue = zt.hasMixedValue;
        EditorGUI.BeginChangeCheck();
        int v = EditorGUILayout.Popup(kOutlineModeText, mode, kOutlineModeNames);
        if (EditorGUI.EndChangeCheck())
        {
            m_Editor.RegisterPropertyChangeUndo(kOutlineModeText.text);
            zt.floatValue = (v == 0) ? 8f : 4f;    // Always : LEqual
            zw.floatValue = 1f;                    // 两种模式都必须写深度
        }
        EditorGUI.showMixedValue = false;
    }

    void DrawFloatToggle(GUIContent label, MaterialProperty prop, bool disabled)
    {
        if (prop == null) return;
        EditorGUI.BeginDisabledGroup(disabled);
        EditorGUI.showMixedValue = prop.hasMixedValue;
        EditorGUI.BeginChangeCheck();
        bool v = EditorGUILayout.Toggle(label, prop.floatValue >= 0.5f);
        if (EditorGUI.EndChangeCheck())
        {
            prop.floatValue = v ? 1f : 0f;
            m_Editor.RegisterPropertyChangeUndo(label.text);
        }
        EditorGUI.showMixedValue = false;
        EditorGUI.EndDisabledGroup();
    }

    static void SetKeyword(Material m, string keyword, bool state)
    {
        if (state) m.EnableKeyword(keyword);
        else m.DisableKeyword(keyword);
    }
}
