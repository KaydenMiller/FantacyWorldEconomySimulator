// Globe/Region View river pieces (GpuRivers): each quad covers one piece's round-capped stroke.

uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uCentre;
uniform float uScale;
uniform float uFlat;
uniform float uMaxAngle;
uniform vec2 uViewport;
uniform float uPixelRatio;
uniform float uUnitPx;       // region cell angle x scale (the CPU renderer's width unit)
uniform float uMinFrac;
uniform float uAddFrac;
uniform float uMaxPx;
uniform float uMinLengthPx;  // rivers shorter on screen are skipped
uniform int uGroup;          // 1: apply the Region View globe-cell group scaling
uniform float uMinLogFlow;   // pieces whose downstream end is below this are skipped (log10 flow)
uniform float uMaxLogFlow;   // pieces whose upstream end reaches this are skipped (left to the major rivers)
uniform highp sampler2D uCellGeom; // RGBA32F, 2 texels per cell: centre, first corner

layout(location = 0) in vec3 aA;
layout(location = 1) in vec3 aB;
layout(location = 2) in vec2 aCorner;
layout(location = 3) in float aLevel;
layout(location = 4) in float aLengthRad;
layout(location = 5) in uvec2 aCells;
layout(location = 6) in vec2 aLogFlow; // log10 flow at end A (upstream) and end B (downstream)

out vec2 vLocal;
flat out float vLen;
flat out float vHalf;
flat out float vLevel;

vec3 project(vec3 p)
{
    float x = dot(p, uRight);
    float y = dot(p, uUp);
    float z = dot(p, uCentre);
    float s = sqrt(x * x + y * y);
    float theta = atan(s, z);
    float visible = theta <= uMaxAngle ? 1.0 : 0.0;
    theta = min(theta, uMaxAngle);
    float r = uScale * ((1.0 - uFlat) * sin(theta) + uFlat * theta);
    vec2 dir = vec2(z > 0.0 ? 0.0 : 1.0, 0.0);
    if (s > 1e-20) dir = vec2(x, y) / s;
    else if (z <= 0.0) visible = 0.0;
    return vec3(uViewport * 0.5 + vec2(r * dir.x, -r * dir.y), visible);
}

vec3 cellTexel(int t)
{
    return texelFetch(uCellGeom, ivec2(t % 2048, t / 2048), 0).xyz;
}

// The CPU renderer's Cluster + Apply: scale about the projected group centre so the group edge moves in by 1.5 px.
vec2 grouped(uint cell, vec2 p)
{
    if (uGroup == 0) return p;
    vec2 g = project(cellTexel(int(cell) * 2)).xy;
    vec2 k = project(cellTexel(int(cell) * 2 + 1)).xy;
    float radius = length(k - g);
    float s = radius <= 0.0 ? 1.0 : clamp(1.0 - 1.5 / (0.866 * radius), 0.5, 1.0);
    return g + (p - g) * s;
}

void main()
{
    vLevel = aLevel;
    vec3 a = project(aA);
    vec3 b = project(aB);
    if (aLengthRad * uScale < uMinLengthPx || a.z < 0.5 || b.z < 0.5 || aLogFlow.y < uMinLogFlow || aLogFlow.x >= uMaxLogFlow)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        vLocal = vec2(0.0);
        vLen = 0.0;
        vHalf = 0.0;
        return;
    }

    vec2 pa = grouped(aCells.x, a.xy);
    vec2 pb = grouped(aCells.y, b.xy);
    float width = clamp(uUnitPx * (uMinFrac + uAddFrac * aLevel / 31.0), 1.0, uMaxPx);
    float halfWidth = width * 0.5;
    float pad = halfWidth + 1.0 / uPixelRatio; // room for the anti-aliased edge
    vec2 d = pb - pa;
    float len = length(d);
    vec2 dir = len > 0.0 ? d / len : vec2(1.0, 0.0);
    vec2 n = vec2(-dir.y, dir.x);
    vec2 pos = mix(pa, pb, aCorner.x) + dir * ((aCorner.x * 2.0 - 1.0) * pad) + n * (aCorner.y * pad);
    vLocal = vec2(dot(pos - pa, dir), dot(pos - pa, n));
    vLen = len;
    vHalf = halfWidth;
    gl_Position = vec4(pos.x / uViewport.x * 2.0 - 1.0, 1.0 - pos.y / uViewport.y * 2.0, 0.0, 1.0);
}
