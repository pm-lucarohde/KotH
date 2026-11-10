// Leaves for the kit's scene view: Standard lighting, alpha cut, both sides (the game's Leaves
// template draws both too). Same property names as Standard, so the exporter reads it alike.
Shader "KotH/Leaves (two-sided)"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _Glossiness ("Smoothness", Range(0, 1)) = 0.2
        _Metallic ("Metallic", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" }
        Cull Off
        CGPROGRAM
        #pragma surface surf Standard alphatest:_Cutoff addshadow
        #pragma target 3.0
        sampler2D _MainTex;
        fixed4 _Color;
        half _Glossiness, _Metallic;
        struct Input
        {
            float2 uv_MainTex;
            float facing : VFACE;
        };
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Alpha = c.a;
            o.Smoothness = _Glossiness;
            o.Metallic = _Metallic;
            // The back face lit as its own front.
            o.Normal = float3(0, 0, IN.facing > 0 ? 1 : -1);
        }
        ENDCG
    }
    FallBack "Legacy Shaders/Transparent/Cutout/VertexLit"
}
