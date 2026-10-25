Shader "Custom/CustomLit"
{
    Properties
    {
        // ---- 基础 ----
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor]   _BaseColor("Color", Color) = (1, 1, 1, 1)

        // ---- PBR: MRO 打包贴图 (R:Metallic G:Roughness B:Occlusion) ----
        [NoScaleOffset] _MROMap("MRO Map (R:Metallic G:Roughness B:Occlusion)", 2D) = "white" {}
        _Metallic("Metallic (R)", Range(0, 1)) = 0.0
        _Roughness("Roughness (G)", Range(0, 1)) = 0.5
        _OcclusionStrength("Occlusion Strength (B)", Range(0, 1)) = 1.0

        // ---- 法线 ----
        [Normal] [NoScaleOffset] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Range(0, 4)) = 1.0

        // ---- Detail Inputs（与 URP Lit 对齐）----
        [NoScaleOffset] _DetailMask("Detail Mask (A)", 2D) = "white" {}
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "gray" {}
        [Normal][NoScaleOffset] _DetailNormalMap("Detail Normal Map", 2D) = "bump" {}
        _DetailNormalMapScale("Detail Normal Scale", Range(0, 4)) = 1.0
        _DetailAlbedoMapScale("Detail Albedo Scale", Range(0.0, 2.0)) = 1.0

        // ---- 自发光 ----
        [Toggle(_EMISSION)] _EmissionOn("Emission", Float) = 0.0
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "black" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0, 0, 0, 1)

        // ---- 外发光 (Rim) ----
        [Toggle(_RIM)] _RimOn("Rim Glow", Float) = 0.0
        [HDR] _RimColor("Rim Color", Color) = (0.5, 0.7, 1.0, 1.0)
        _RimPower("Rim Power", Range(0.25, 8)) = 3.0
        _RimIntensity("Rim Intensity", Range(0, 16)) = 1.0

        // ---- 溶解 (Dissolve) ----
        // 方向由 GUI 映射为关键字：
        //   0 -> _DISSOLVE_RADIAL / 1 -> _DISSOLVE_AXIS_X / 2 -> _DISSOLVE_AXIS_Y / 3 -> _DISSOLVE_AXIS_Z
        [Enum(Radial, 0, X, 1, Y, 2, Z, 3)] _DissolveDirectionMode("Dissolve Direction", Float) = 3.0
        [Toggle(_DISSOLVE_ON)] _DissolveOn("Dissolve", Float) = 0.0
        // 0 = 全部溶解（空），1 = 完全可见
        _DissolveAmount("Dissolve Amount (0=Empty 1=Visible)", Range(0, 1)) = 1.0
        _DissolveCenter("Dissolve Origin (Object Space)", Vector) = (0, 0, 0, 0)
        _DissolveRadius("Dissolve Radius (Object Units)", Float) = 1.0
        [NoScaleOffset] _DissolveNoiseMap("Dissolve Noise Map (R)", 2D) = "gray" {}
        _DissolveNoiseStrength("Noise Strength", Range(0, 1)) = 0.25
        _DissolveNoiseScale("Noise Scale", Range(0.01, 32)) = 1.0
        _DissolveCutoff("Cutoff (Bias)", Range(-1, 1)) = 0.0
        [HDR] _DissolveEdgeColor("Dissolve Edge Color", Color) = (2, 0.5, 0.1, 1)
        _DissolveEdgeWidth("Dissolve Edge Width", Range(0.001, 0.5)) = 0.08

        // 旧版本属性名，避免已有材质丢引用
        [HideInInspector] _DissolveDirection("__dissolveDirection", Vector) = (0, 0, 0, 0)

        // ---- 描边 (Outline) ----
        // 世界空间外扩 + 距离补偿：宽度以像素计、任何朝向的轮廓都被均匀加粗。
        [Toggle(_OUTLINE_ON)] _OutlineOn("Outline", Float) = 0.0
        _OutlineColor("Outline Color", Color) = (0, 0, 0, 1)
        _OutlineWidthPixels("Outline Width (Pixels)", Range(0, 32)) = 3.0
        // 剪影模式：ZTest Always + ZWrite Off，让描边先铺底、再被主体盖掉，
        // 结果只保留外轮廓（去掉内部细节线）。由 GUI 按模式自动写入。
        // 0=Disabled 1=Never 2=Less 3=Equal 4=LessEqual 5=Greater 6=NotEqual 7=GreaterEqual 8=Always
        [HideInInspector] _OutlineZTest("Outline ZTest", Float) = 4.0
        [HideInInspector] _OutlineZWrite("Outline ZWrite", Float) = 1.0
        // 平滑法线：用烘焙进 UV3 的"位置平均法线"外扩，修硬边处的描边缺口。
        [Toggle(_OUTLINE_SMOOTH_NORMALS)] _OutlineSmoothNormals("Smooth Normals", Float) = 0.0

        // ================================================================
        //  Surface Options（由 CustomLitShaderGUI 绘制）
        // ================================================================

        // Workflow: 0 = Specular, 1 = Metallic（默认 Metallic，与 URP Lit 一致）
        [Enum(Specular, 0, Metallic, 1)] _WorkflowMode("Workflow Mode", Float) = 1.0

        // Surface Type: 0 = Opaque, 1 = Transparent
        [Enum(Opaque, 0, Transparent, 1)] _Surface("Surface Type", Float) = 0.0
        // Blending Mode: 0 = Alpha, 1 = Premultiply, 2 = Additive, 3 = Multiply
        [Enum(Alpha, 0, Premultiply, 1, Additive, 2, Multiply, 3)] _Blend("Blending Mode", Float) = 0.0
        [ToggleUI] _BlendModePreserveSpecular("Preserve Specular Lighting", Float) = 0.0
        [Enum(Off, 0, On, 1)] _ZWrite("ZWrite", Float) = 1.0
        // 混合因子由 GUI 写入（Opaque = One/Zero）
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0

        // Render Face: 0 = Both, 1 = Back, 2 = Front
        [Enum(Both, 0, Back, 1, Front, 2)] _Cull("Render Face", Float) = 2.0

        [Toggle(_ALPHATEST_ON)] _AlphaClip("Alpha Clipping", Float) = 0.0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0

        // ObsoleteProperties (2022.x 没有 Meta pass，保留以免旧材质报错)
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _GlossMapScale("Smoothness", Float) = 0.5
        [HideInInspector] _Glossiness("Smoothness", Float) = 0.5
        [HideInInspector] _SpecColor("Specular", Color) = (1, 1, 1)
        [HideInInspector] _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    }

    SubShader
    {
        // UniversalMaterialType = Lit 让 SSAO / 贴花把本材质当作 Lit 表面处理
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
            "Queue" = "Geometry"
        }
        LOD 300

        // ------------------------------------------------------------------
        // Forward Lit（PBR 主渲染）
        // ------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend[_SrcBlend][_DstBlend]
            ZWrite[_ZWrite]
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            // -------------------------------------
            // Material keywords
            #pragma shader_feature_local _NORMALMAP
            // Detail Inputs：URP 约定用 _DETAIL +（_DETAIL_MULX2 / _DETAIL_SCALED）
            #pragma shader_feature_local_fragment _DETAIL
            #pragma shader_feature_local_fragment _DETAIL_MULX2 _DETAIL_SCALED
            // 溶解：四选一模式 + 总开关
            #pragma shader_feature_local _ _DISSOLVE_RADIAL _DISSOLVE_AXIS_X _DISSOLVE_AXIS_Y _DISSOLVE_AXIS_Z
            #pragma shader_feature_local _DISSOLVE_ON
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _MRO_MAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _RIM
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF

            // 透明相关关键字（URP 约定）
            #pragma multi_compile _ _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local_fragment _ALPHAMODULATE_ON

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma vertex CustomLitPassVertex
            #pragma fragment CustomLitPassFragment

            #include "CustomLitInput.hlsl"
            #include "CustomLitEffects.hlsl"
            #include "CustomLitForwardPass.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // Outline（反转外壳描边，SRPDefaultUnlit）
        // ------------------------------------------------------------------
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite[_OutlineZWrite]
            ZTest[_OutlineZTest]

            HLSLPROGRAM
            #pragma target 2.0

            #pragma shader_feature_local _ _OUTLINE_ON
            #pragma shader_feature_local _OUTLINE_SMOOTH_NORMALS
            // 溶解：四选一模式 + 总开关
            #pragma shader_feature_local _ _DISSOLVE_RADIAL _DISSOLVE_AXIS_X _DISSOLVE_AXIS_Y _DISSOLVE_AXIS_Z
            #pragma shader_feature_local _DISSOLVE_ON
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma vertex CustomLitOutlinePassVertex
            #pragma fragment CustomLitOutlinePassFragment

            #include "CustomLitInput.hlsl"
            #include "CustomLitEffects.hlsl"
            #include "CustomLitOutlinePass.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // ShadowCaster
        // ------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            Cull[_Cull]
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0

            #pragma shader_feature_local _ALPHATEST_ON
            // 溶解：四选一模式 + 总开关
            #pragma shader_feature_local _ _DISSOLVE_RADIAL _DISSOLVE_AXIS_X _DISSOLVE_AXIS_Y _DISSOLVE_AXIS_Z
            #pragma shader_feature_local _DISSOLVE_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma vertex CustomLitShadowPassVertex
            #pragma fragment CustomLitShadowPassFragment

            #include "CustomLitInput.hlsl"
            #include "CustomLitEffects.hlsl"
            #include "CustomLitDepthPasses.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // DepthOnly（深度预处理）
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            #pragma shader_feature_local _ALPHATEST_ON
            // 溶解：四选一模式 + 总开关
            #pragma shader_feature_local _ _DISSOLVE_RADIAL _DISSOLVE_AXIS_X _DISSOLVE_AXIS_Y _DISSOLVE_AXIS_Z
            #pragma shader_feature_local _DISSOLVE_ON
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma vertex CustomLitDepthOnlyVertex
            #pragma fragment CustomLitDepthOnlyFragment

            #include "CustomLitInput.hlsl"
            #include "CustomLitEffects.hlsl"
            #include "CustomLitDepthPasses.hlsl"
            ENDHLSL
        }

        // ------------------------------------------------------------------
        // DepthNormals（法线预处理，供 SSAO 等使用）
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            #pragma shader_feature_local _ALPHATEST_ON
            // 溶解：四选一模式 + 总开关
            #pragma shader_feature_local _ _DISSOLVE_RADIAL _DISSOLVE_AXIS_X _DISSOLVE_AXIS_Y _DISSOLVE_AXIS_Z
            #pragma shader_feature_local _DISSOLVE_ON
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #pragma vertex CustomLitDepthNormalsVertex
            #pragma fragment CustomLitDepthNormalsFragment

            #include "CustomLitInput.hlsl"
            #include "CustomLitEffects.hlsl"
            #include "CustomLitDepthPasses.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "CustomLitShaderGUI"
}
