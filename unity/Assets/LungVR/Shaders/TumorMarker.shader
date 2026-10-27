// Tumor markers (TumorMarkers.cs): a glowing sphere at each tumor found by the
// LungVision backend. A tumor sits INSIDE the lung, so two passes:
//   * hidden by tissue (ZTest Greater) - a soft, pulsing ghost, so it can always be
//     found without making the lung see-through;
//   * in plain view (ZTest LEqual)     - a bright solid core with a rim glow.
// URP draws the SRPDefaultUnlit and UniversalForward passes of one material in turn.
Shader "LungVision/TumorMarker"
{
    Properties
    {
        _Color("Color", Color) = (1, 0.15, 0.15, 1)
        _HiddenAlpha("Alpha when behind tissue", Range(0, 1)) = 0.45
        _PulseSpeed("Pulse speed", Float) = 2.2
        _Selected("Selected (0/1)", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+10" "RenderType" = "Transparent" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            half  _HiddenAlpha;
            float _PulseSpeed;
            half  _Selected;
        CBUFFER_END

        struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
        struct Varyings   { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 viewWS : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

        Varyings vert(Attributes v)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
            o.positionCS = TransformWorldToHClip(positionWS);
            o.normalWS = TransformObjectToWorldNormal(v.normalOS);
            o.viewWS = GetWorldSpaceViewDir(positionWS);
            return o;
        }

        // 0 at the centre of the disc the sphere makes on screen, 1 at its edge.
        half Rim(Varyings i)
        {
            return 1.0h - saturate(dot(normalize(i.normalWS), normalize(i.viewWS)));
        }

        half Pulse()
        {
            return 0.5h + 0.5h * sin(_Time.y * _PulseSpeed * (1.0 + _Selected));
        }
        ENDHLSL

        // Behind tissue: additive ghost, brightest at the rim.
        Pass
        {
            Name "Hidden"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            ZTest Greater
            ZWrite Off
            Cull Back
            Blend SrcAlpha One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            half4 frag(Varyings i) : SV_Target
            {
                half rim = Rim(i);
                half a = _HiddenAlpha * (0.35h + 0.65h * rim) * lerp(0.55h, 1.0h, Pulse());
                return half4(_Color.rgb * (1.0h + _Selected), saturate(a * (1.0h + _Selected)));
            }
            ENDHLSL
        }

        // In view: solid, glowing core.
        Pass
        {
            Name "Visible"
            Tags { "LightMode" = "UniversalForward" }
            ZTest LEqual
            ZWrite On
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            half4 frag(Varyings i) : SV_Target
            {
                half rim = Rim(i);
                half3 core = _Color.rgb * (0.65h + 0.35h * Pulse());
                half3 glow = lerp(core, half3(1.0h, 0.85h, 0.7h), rim * rim * 0.6h);
                return half4(glow * (1.0h + 0.5h * _Selected), 1.0h);
            }
            ENDHLSL
        }
    }
}
