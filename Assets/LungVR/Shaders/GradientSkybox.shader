// Minimal vertical gradient skybox for the medical VR console environment.
// Two lerped colours and a pow() - no textures, no cubemap sampling - so it costs
// essentially nothing on Quest while avoiding the flat, dead look of a solid clear
// colour. Stereo macros are present so it renders correctly per-eye under
// single-pass instanced XR.
Shader "BioGears/GradientSkybox"
{
    Properties
    {
        _TopColor    ("Top Color",    Color) = (0.16, 0.17, 0.19, 1)
        _BottomColor ("Bottom Color", Color) = (0.015, 0.015, 0.02, 1)
        _Exponent    ("Falloff",      Range(0.2, 4)) = 1.3
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
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
                float3 directionOS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _TopColor;
            float4 _BottomColor;
            float  _Exponent;

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.directionOS = input.positionOS.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Remap the view direction's height from [-1,1] to [0,1], then bias it
                // so the darker tone occupies more of the lower hemisphere.
                float h = normalize(input.directionOS).y * 0.5 + 0.5;
                h = pow(saturate(h), _Exponent);
                return lerp(_BottomColor, _TopColor, h);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
