// Soft glowing dot for the airflow particles.
//
// ZTest Always: the air travels INSIDE the trachea, bronchi and lungs, all of which are
// opaque, so a normal depth test would hide every particle. Drawing on top gives an
// X-ray style view of the airflow. Additive, so overlapping particles brighten where
// the flow is dense, and no sorting is needed.
//
// The dot is computed from the quad's UVs rather than a texture, so there is nothing
// to import and nothing to mip-blur at a distance.
Shader "BioGears/AirflowParticle"
{
    Properties
    {
        _Intensity("Intensity", Float) = 1.6

        // Tube mode, for the glowing bronchial tree drawn with LineRenderers: falloff
        // across the line's width only, plus bands of light that travel along it.
        // _FlowPhase is driven from C# by the breath, so the bands move in on inhale
        // and back out on exhale, in step with the air.
        [Toggle] _LineMode("Line Mode", Float) = 0
        _BandCount("Bands Along Line", Float) = 6
        _FlowPhase("Flow Phase", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+10"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Airflow"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _Intensity;
                half _LineMode;
                half _BandCount;
                float _FlowPhase;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half falloff;
                if (_LineMode > 0.5h)
                {
                    // Tube: bright along the centre line, soft to the edges, with
                    // travelling bands (uv.x runs 0..1 along the line).
                    float across = input.uv.y * 2.0 - 1.0;
                    falloff = saturate(1.0h - (half)(across * across));
                    float band = 0.5 + 0.5 * sin((input.uv.x * _BandCount - _FlowPhase) * 6.2831853);
                    falloff *= lerp(0.45h, 1.35h, (half)(band * band));
                }
                else
                {
                    // Dot (or streak, when the particle is stretched by its velocity):
                    // bright core, soft falloff to zero at the quad's edge.
                    float2 d = input.uv * 2.0 - 1.0;
                    falloff = saturate(1.0h - (half)dot(d, d));
                    falloff *= falloff;
                }
                return half4(input.color.rgb * input.color.a * falloff * _Intensity, 0.0h);
            }
            ENDHLSL
        }
    }
}
