// The phone camera's image (PhoneCamera) from its two planes, luma (R8) and interleaved chroma
// (RG8, U then V, half size), to RGB with full-range BT.601, as Android's cameras give YUV_420_888,
// turned to stand upright on the screen by _ImageFromScreenX/Y (PhoneCamera.ImageFromScreen). Drawn
// over a unit quad into a texture. _Linearize 1 is for an sRGB target in the linear color space,
// which stores the camera's colors as they came; 0 writes them as they came into a plain target,
// for the hand models.
Shader "CardboardXR/CameraYuv"
{
    Properties
    {
        _LumaTex ("Luma", 2D) = "black" {}
        _ChromaTex ("Chroma", 2D) = "gray" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        Pass
        {
            Name "CameraYuv"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_LumaTex);
            SAMPLER(sampler_LumaTex);
            TEXTURE2D(_ChromaTex);
            SAMPLER(sampler_ChromaTex);
            float4 _ImageFromScreenX;
            float4 _ImageFromScreenY;
            float _Linearize;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            float3 ToLinear(float3 c)
            {
                return lerp(c / 12.92, pow(abs((c + 0.055) / 1.055), 2.4), step(0.04045, c));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 st = float2(dot(_ImageFromScreenX.xy, uv) + _ImageFromScreenX.z,
                                   dot(_ImageFromScreenY.xy, uv) + _ImageFromScreenY.z);
                float y = SAMPLE_TEXTURE2D(_LumaTex, sampler_LumaTex, st).r;
                float2 c = SAMPLE_TEXTURE2D(_ChromaTex, sampler_ChromaTex, st).rg - 0.5;
                float3 rgb = saturate(float3(y + 1.402 * c.y, y - 0.344136 * c.x - 0.714136 * c.y, y + 1.772 * c.x));
                if (_Linearize > 0.5)
                    rgb = ToLinear(rgb);
                return half4(rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
