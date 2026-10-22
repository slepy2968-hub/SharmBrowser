sampler2D surface : register(s0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 original = tex2D(surface, uv);
    float2 position = (uv - 0.5) * float2(9.6774, 1.0);
    float2 nearest = float2(clamp(position.x, -4.3387, 4.3387), 0);
    float2 radial = position - nearest;
    float distance = length(radial);
    float2 normal = radial / max(distance, 0.001);
    float rim = pow(saturate((distance - 0.36) / 0.14), 2.0);
    float2 refracted = uv - normal * float2(0.004, 0.035) * rim;
    float4 glass = tex2D(surface, refracted);
    float sheen = pow(saturate(dot(normal, normalize(float2(-0.6, -0.8)))), 5.0);
    glass.rgb += rim * (0.018 + sheen * 0.13);
    glass.r += rim * 0.007;
    glass.b += rim * 0.014;
    glass.a = original.a;
    return glass;
}
