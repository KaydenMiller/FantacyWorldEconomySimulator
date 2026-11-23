// Sub-tile smoothing (the CPU renderer's BlendSink.Pick): per pixel the class key holding the most barycentric weight
// wins (ties: first vertex, then second); categorical layers take the winning key's colour, continuous layers blend the
// colours of the vertices with the winning key by their weights.

uniform int uMode;          // GpuLayerMode
uniform vec3 uCellColour;   // the block's globe cell colour (mode 0)
uniform float uShade;
uniform int uTint;

in vec3 vWeights;
flat in uvec4 vClassA;
flat in uvec4 vClassB;
flat in uvec4 vClassC;
flat in ivec4 vValueA;
flat in ivec4 vValueB;
flat in ivec4 vValueC;

out vec4 fragColour;

#include "palette.glsl"

void main()
{
    vec3 ca = texelColour(uMode, vClassA, vValueA, uCellColour);
    vec3 cb = texelColour(uMode, vClassB, vValueB, uCellColour);
    vec3 cc = texelColour(uMode, vClassC, vValueC, uCellColour);
    uint ka = texelKey(uMode, vClassA, ca);
    uint kb = texelKey(uMode, vClassB, cb);
    uint kc = texelKey(uMode, vClassC, cc);
    float wa = vWeights.x;
    float wb = vWeights.y;
    float wc = vWeights.z;
    float sa = wa + (kb == ka ? wb : 0.0) + (kc == ka ? wc : 0.0);
    float sb = wb + (ka == kb ? wa : 0.0) + (kc == kb ? wc : 0.0);
    float sc = wc + (ka == kc ? wa : 0.0) + (kb == kc ? wb : 0.0);
    uint win = (sa >= sb && sa >= sc) ? ka : (sb >= sc ? kb : kc);
    vec3 col = win == ka ? ca : (win == kb ? cb : cc);
    if (isContinuous(uMode))
    {
        vec3 sum = vec3(0.0);
        float w = 0.0;
        if (ka == win && wa > 0.0) { sum += ca * wa; w += wa; }
        if (kb == win && wb > 0.0) { sum += cb * wb; w += wb; }
        if (kc == win && wc > 0.0) { sum += cc * wc; w += wc; }
        if (w > 0.0) col = floor(sum / w * 255.0 + 0.5) / 255.0;
    }

    col *= uShade;
    if (uTint != 0) col = mix(col, vec3(1.0, 200.0 / 255.0, 40.0 / 255.0), 0.55);
    fragColour = vec4(col, 1.0);
}
