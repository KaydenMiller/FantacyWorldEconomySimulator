// Mountain symbols (GpuGlyphs): one triangle per symbol, corner = gl_VertexID % 3, sized like the CPU renderer's
// EmitGlyphs: 0.75 x the cell's projected radius (inset at Globe View, group-scaled at Region View); hidden under MinPx.

uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uCentre;
uniform float uScale;
uniform float uFlat;
uniform float uMaxAngle;
uniform vec2 uViewport;
uniform float uEdgeInset;   // Globe View: the cell inset (gap / 2); 0 at Region View
uniform float uMinPx;
uniform int uGroup;         // 1: scale about the projected group (globe cell) centre, as Region View
uniform highp sampler2D uCellGeom; // RGBA32F, 2 texels per globe cell: centre, first corner

layout(location = 0) in vec3 aCellCentre;
layout(location = 1) in vec3 aCellCorner;
layout(location = 2) in uint aGroup;

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

void main()
{
    vec3 c = project(aCellCentre);
    float radius = length(project(aCellCorner).xy - c.xy);
    vec2 centre = c.xy;
    if (uGroup != 0)
    {
        int g = int(aGroup);
        vec2 gc = project(cellTexel(g * 2)).xy;
        float gr = length(project(cellTexel(g * 2 + 1)).xy - gc);
        float gs = gr <= 0.0 ? 1.0 : clamp(1.0 - 1.5 / (0.866 * gr), 0.5, 1.0);
        centre = gc + (centre - gc) * gs;
        radius *= gs;
    }

    if (c.z < 0.5 || radius < uMinPx)
    {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }

    float inset = (radius > 0.0 && uEdgeInset > 0.0) ? clamp(1.0 - uEdgeInset / (0.866 * radius), 0.3, 1.0) : 1.0;
    float s = radius * inset * 0.75;
    int corner = gl_VertexID % 3;
    vec2 offset = corner == 0 ? vec2(0.0, -0.6) : corner == 1 ? vec2(-0.55, 0.4) : vec2(0.55, 0.4);
    vec2 q = centre + offset * s;
    gl_Position = vec4(q.x / uViewport.x * 2.0 - 1.0, 1.0 - q.y / uViewport.y * 2.0, 0.0, 1.0);
}
