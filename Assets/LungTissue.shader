// Lung tissue: standard URP physically based shading, plus a fine detail normal
// projected TRIPLANAR in object space.
//
// Why this exists rather than URP/Lit: the lung's UV set is a single cylindrical unwrap
// stretched around the whole organ (873 model units of circumference). At the 512px the
// smoking system is limited to, one texel covers 6.4mm of tissue and the finest noise
// that can be represented without aliasing is ~26mm. Real lung surface detail is
// sub-millimetre, so *no* UV-mapped texture at any affordable resolution can carry it -
// push the frequency up and it aliases into stripes, keep it legal and it is invisible.
//
// Triplanar detail breaks that ceiling: the detail map is projected along the three
// object-space axes at its own tiling, so its resolution is completely independent of
// the unwrap. It also sidesteps two defects of that unwrap for free - the u = 1 -> 0
// seam, and the atan2 singularity along the central axis (which is what speckles the
// trachea, since the trachea sits on that axis).
//
// Cost is three extra texture samples in the fragment shader. Large-scale colour still
// comes from the baked albedo through normal UVs, which is correct: colour variation on
// a lung genuinely is low frequency, so the unwrap is fine for that job.
Shader "BioGears/LungTissue"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Color", Color) = (1,1,1,1)

        _BumpMap("Normal Map (UV)", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 0.55

        _DetailNormal("Detail Normal (triplanar)", 2D) = "bump" {}
        // Repeats per object-space unit. The mesh is ~278 units across and scaled to
        // ~1m, so 0.35 puts one repeat at roughly 12mm of real tissue - fine enough that
        // the tiling is not perceptible, coarse enough to stay above texel noise.
        _DetailScale("Detail Tiling (per object unit)", Float) = 0.35
        _DetailStrength("Detail Strength", Range(0,2)) = 0.55
        // Higher values make each axis projection dominate more sharply; lower values
        // widen the cross-fade between them.
        _TriplanarSharpness("Triplanar Blend Sharpness", Range(1,8)) = 4

        // R = smoothness, G = ambient occlusion. Baked from the same height field as
        // the normal map, so gloss and cavity shading line up with the actual relief.
        _MaskMap("Mask (R smoothness, G occlusion)", 2D) = "white" {}
        _SmoothnessMin("Smoothness - fissures", Range(0,1)) = 0.16
        _SmoothnessMax("Smoothness - pleura", Range(0,1)) = 0.38
        _OcclusionStrength("Occlusion Strength", Range(0,1)) = 0.75

        // --- cheap subsurface -------------------------------------------------------
        // URP/Lit cannot do this at all, which is why it was off the table until we had
        // our own shader. This is a back-scatter approximation, not a real diffusion
        // profile: light arriving from behind the surface leaks toward the viewer,
        // tinted by how flesh filters it. A few instructions, no extra passes.
        [HDR] _TranslucencyColor("Translucency Color", Color) = (0.62, 0.14, 0.11, 1)
        _TranslucencyStrength("Translucency Strength", Range(0,3)) = 0.85
        // How much the surface normal bends the transmission vector. Higher spreads the
        // glow around the silhouette instead of concentrating it dead-behind.
        _TranslucencyDistortion("Translucency Distortion", Range(0,1)) = 0.35
        _TranslucencyPower("Translucency Falloff", Range(1,16)) = 4
        // Softens the light terminator. Real flesh has no hard shadow edge because
        // light scatters a short distance under the surface before re-emerging.
        _DiffuseWrap("Diffuse Wrap", Range(0,1)) = 0.35

        // --- smoking damage ---------------------------------------------------------
        // Each channel of _DamageMask holds a susceptibility field for one kind of
        // damage, and each has its own onset along the exposure axis. Damage therefore
        // grows outward from its own seed regions instead of the whole organ fading to
        // grey together - the difference between pathology and a colour filter.
        _DamageMask("Damage Susceptibility (R pigment, G scar, B inflam, A emphysema)", 2D) = "black" {}
        // 0 = never smoked, 1 = advanced cumulative exposure. Set from pack-years by a
        // saturating curve in C#; deliberately NOT a "percent damaged" figure.
        _Damage01("Cumulative Exposure", Range(0,1)) = 0
        _PigmentColor("Anthracotic Pigment", Color) = (0.075, 0.070, 0.068, 1)
        _ScarColor("Fibrotic Scar", Color) = (0.760, 0.716, 0.640, 1)
        _InflamedColor("Inflamed Tissue", Color) = (0.600, 0.240, 0.220, 1)

        // --- GPU breathing ----------------------------------------------------------
        // The same radial expansion LungMeshDeformer used to compute on the CPU, moved
        // into the vertex shader. Identical maths: normalize(pos - centre), scaled per
        // axis, times the current expansion. Driven by BioGears exactly as before - the
        // only difference is that C# now sets one float per frame instead of rewriting
        // and re-uploading 730k vertices.
        _BreathExpansion("Breath Expansion", Float) = 0
        _BreathCentre("Breath Centre (object space)", Vector) = (0,0,0,0)
        _BreathAxisScale("Breath Axis Scale", Vector) = (1,0.8,1.2,0)

        _Metallic("Metallic", Range(0,1)) = 0
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _BumpScale;
            float  _DetailScale;
            half   _DetailStrength;
            half   _TriplanarSharpness;
            half   _SmoothnessMin;
            half   _SmoothnessMax;
            half   _OcclusionStrength;
            half4  _TranslucencyColor;
            half   _TranslucencyStrength;
            half   _TranslucencyDistortion;
            half   _TranslucencyPower;
            half   _DiffuseWrap;
            half4  _PigmentColor;
            half4  _ScarColor;
            half4  _InflamedColor;
            half   _Damage01;
            float  _BreathExpansion;
            float4 _BreathCentre;
            float4 _BreathAxisScale;
            half   _Metallic;
            half4  _EmissionColor;
        CBUFFER_END

        // Must be applied identically in the forward, shadow and depth passes, or the
        // lung's shadow and depth would sit at the un-inflated position while the
        // visible surface moves.
        float3 ApplyBreathing(float3 positionOS)
        {
            float3 offset = positionOS - _BreathCentre.xyz;
            float len = length(offset);
            if (len < 1e-5) return positionOS;

            float3 dir = (offset / len) * _BreathAxisScale.xyz;
            return positionOS + dir * _BreathExpansion;
        }
        ENDHLSL

        // ------------------------------------------------------------------ forward --
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex LitPassVertex
            #pragma fragment LitPassFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);      SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);      SAMPLER(sampler_BumpMap);
            TEXTURE2D(_DetailNormal); SAMPLER(sampler_DetailNormal);
            TEXTURE2D(_MaskMap);      SAMPLER(sampler_MaskMap);
            TEXTURE2D(_DamageMask);   SAMPLER(sampler_DamageMask);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float4 tangentWS  : TEXCOORD3;
                float3 positionOS : TEXCOORD4;
                float3 normalOS   : TEXCOORD5;
                float  fogFactor  : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings LitPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 breathedOS = ApplyBreathing(input.positionOS.xyz);
                VertexPositionInputs pos = GetVertexPositionInputs(breathedOS);
                VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS   = nrm.normalWS;
                output.tangentWS  = float4(nrm.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv         = TRANSFORM_TEX(input.uv, _BaseMap);
                // Deliberately the REST pose, not breathedOS. The fragment shader uses
                // this to sample triplanar detail, and detail has to stay anchored to
                // the tissue: sampling at the inflated position would make the pores
                // slide across the surface on every breath. Only the rendered position
                // moves - the surface's identity does not.
                output.positionOS = input.positionOS.xyz;
                output.normalOS   = input.normalOS;
                output.fogFactor  = ComputeFogFactor(pos.positionCS.z);
                return output;
            }

            // Whiteout triplanar normal blend. Samples the detail map along the three
            // object-space planes and merges them into a single object-space normal,
            // weighted by how strongly the surface faces each axis.
            float3 TriplanarDetailNormalOS(float3 positionOS, float3 normalOS, float scale)
            {
                float3 n = normalize(normalOS);
                float3 blend = pow(abs(n), _TriplanarSharpness);
                blend /= max(blend.x + blend.y + blend.z, 1e-5);

                float2 uvX = positionOS.zy * scale;
                float2 uvY = positionOS.xz * scale;
                float2 uvZ = positionOS.xy * scale;

                float3 tx = UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, uvX));
                float3 ty = UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, uvY));
                float3 tz = UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, uvZ));

                // Reorient each projection onto its plane, preserving the sign of the
                // surface normal so detail never inverts on back-facing axes.
                tx = float3(tx.xy + n.zy, abs(tx.z) * n.x);
                ty = float3(ty.xy + n.xz, abs(ty.z) * n.y);
                tz = float3(tz.xy + n.xy, abs(tz.z) * n.z);

                return normalize(tx.zyx * blend.x + ty.xzy * blend.y + tz.xyz * blend.z);
            }

            half4 LitPassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                // Base normal from the UV normal map, into world space.
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);

                float3 nWS = normalize(input.normalWS);
                float3 tWS = normalize(input.tangentWS.xyz);
                float3 bWS = normalize(cross(nWS, tWS) * input.tangentWS.w);
                float3 baseNormalWS = normalize(mul(normalTS, half3x3(tWS, bWS, nWS)));

                // Triplanar detail, expressed as a perturbation: subtracting the plain
                // geometric normal leaves only what the detail map added, so it can be
                // layered onto the UV normal without fighting it.
                float3 detailOS = TriplanarDetailNormalOS(input.positionOS, input.normalOS, _DetailScale);
                float3 detailWS = normalize(TransformObjectToWorldNormal(detailOS));
                float3 geomWS   = normalize(TransformObjectToWorldNormal(input.normalOS));
                float3 finalNormalWS = normalize(baseNormalWS + (detailWS - geomWS) * _DetailStrength);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = finalNormalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(finalNormalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                // ---- smoking damage, layered ---------------------------------------
                // Each layer thresholds its own susceptibility field against exposure,
                // with its own onset, so the stages arrive in a plausible order and in
                // different places. smoothstep gives soft ragged edges rather than a
                // hard boundary, and every layer stays partial - the lung never becomes
                // uniformly black, which is both medically wrong and visually cheap.
                half4 dmg = SAMPLE_TEXTURE2D(_DamageMask, sampler_DamageMask, input.uv);
                half exposure = saturate(_Damage01);

                // Inflammation appears first and plateaus - irritated, congested tissue.
                half inflame = smoothstep(1.0h - exposure * 1.30h, 1.0h - exposure * 1.30h + 0.30h, dmg.b)
                             * saturate(exposure * 3.0h) * 0.45h;
                albedo.rgb = lerp(albedo.rgb, _InflamedColor.rgb, inflame);

                // Carbon pigment: the dominant visual change, spreading continuously.
                half pigment = smoothstep(1.0h - exposure * 1.45h, 1.0h - exposure * 1.45h + 0.22h, dmg.r)
                             * saturate(exposure * 2.2h);
                albedo.rgb = lerp(albedo.rgb, _PigmentColor.rgb, pigment * 0.92h);

                // Fibrosis: pale streaky scar tissue, only past moderate exposure.
                half scarOnset = saturate((exposure - 0.35h) / 0.65h);
                half scar = smoothstep(0.62h, 0.90h, dmg.g) * scarOnset * 0.55h;
                albedo.rgb = lerp(albedo.rgb, _ScarColor.rgb, scar);

                // Emphysema: advanced only. Darkens and desaturates a few large regions
                // to suggest air-trapping and lost tissue, without punching holes.
                half emphOnset = saturate((exposure - 0.60h) / 0.40h);
                half emph = smoothstep(0.55h, 0.95h, dmg.a) * emphOnset;
                half3 grey = dot(albedo.rgb, half3(0.299h, 0.587h, 0.114h)).xxx;
                albedo.rgb = lerp(albedo.rgb, grey * 0.62h, emph * 0.55h);

                // Mask: gloss and cavity shading, both keyed to the baked relief so a
                // fissure is simultaneously duller and darker - which is what stops the
                // surface reading as one uniformly polished object.
                half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, input.uv);
                half smoothness = lerp(_SmoothnessMin, _SmoothnessMax, mask.r);
                half occlusion = lerp(1.0h, mask.g, _OcclusionStrength);

                // Pigmented and fibrotic tissue is drier and duller; mucus makes a few
                // inflamed patches glossier instead. Both are local, never global.
                smoothness = lerp(smoothness, smoothness * 0.45h, saturate(pigment + scar));
                smoothness = lerp(smoothness, 0.62h, inflame * 0.55h);
                // Damaged regions read denser, so they occlude a little more.
                occlusion *= lerp(1.0h, 0.80h, saturate(pigment * 0.7h + emph * 0.5h));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo.rgb;
                surfaceData.alpha = 1.0h;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = smoothness;
                surfaceData.normalTS = normalTS;
                surfaceData.emission = _EmissionColor.rgb;
                surfaceData.occlusion = occlusion;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);

                // --- subsurface -----------------------------------------------------
                Light mainLight = GetMainLight(inputData.shadowCoord);
                float3 N = finalNormalWS;
                float3 V = inputData.viewDirectionWS;
                float3 L = mainLight.direction;

                // Transmission vector: the light direction pushed back through the
                // surface and bent by the normal. Looking along it means looking at
                // light that travelled through the tissue rather than off it.
                float3 transDir = normalize(-L - N * _TranslucencyDistortion);
                half back = pow(saturate(dot(V, transDir)), _TranslucencyPower);

                // Thin regions transmit more. Facing ratio is a cheap stand-in for
                // thickness - no thickness map needed, and edges are where it shows.
                half thinness = saturate(1.0h - abs(dot(N, V)));
                half3 shadowedLight = mainLight.color * mainLight.shadowAttenuation;

                color.rgb += shadowedLight * _TranslucencyColor.rgb * albedo.rgb
                           * back * _TranslucencyStrength * (0.35h + 0.65h * thinness) * occlusion
                           * saturate(1.0h - pigment * 0.85h);   // carbon blocks transmission

                // Wrapped diffuse: lifts the terminator so shadows fade warm instead of
                // stopping at a hard line, the way light does under skin and viscera.
                half ndl = dot(N, L);
                half wrapped = saturate((ndl + _DiffuseWrap) / (1.0h + _DiffuseWrap));
                half terminator = saturate(wrapped - saturate(ndl));
                color.rgb += shadowedLight * _TranslucencyColor.rgb * albedo.rgb
                           * terminator * _TranslucencyStrength * 0.5h * occlusion;

                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        // ------------------------------------------------------------- shadow caster --
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(ApplyBreathing(input.positionOS.xyz));
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                output.positionCS = positionCS;
                return output;
            }

            half4 ShadowFragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------- depth only --
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(ApplyBreathing(input.positionOS.xyz));
                return output;
            }

            half4 DepthFragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
