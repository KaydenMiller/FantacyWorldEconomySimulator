// Tile block mesh (GpuTileBlock): the triangles between three tile centres, drawn non-indexed so the corner is
// gl_VertexID % 3. Each vertex carries all three tiles' data; the fragment shader blends them (as BlendSink).
// One draw call per block; the block's globe cell gives the Region/Detail group scaling (the CPU renderer's Cluster).

uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uCentre;
uniform float uScale;
uniform float uFlat;
uniform float uMaxAngle;
uniform vec2 uViewport;
uniform int uGroup;          // 1: scale about the projected group centre (gaps between globe cells)
uniform vec3 uGroupCentre;   // the block's globe cell centre
uniform vec3 uGroupCorner;   // its first corner

layout(location = 0) in vec3 aPos;
layout(location = 1) in uvec4 aClassA;
layout(location = 2) in uvec4 aClassB;
layout(location = 3) in uvec4 aClassC;
layout(location = 4) in ivec3 aValueA;
layout(location = 5) in ivec3 aValueB;
layout(location = 6) in ivec3 aValueC;

out vec3 vWeights;
flat out uvec4 vClassA;
flat out uvec4 vClassB;
flat out uvec4 vClassC;
flat out ivec4 vValueA;
flat out ivec4 vValueB;
flat out ivec4 vValueC;

vec2 project(vec3 p)
{
    float x = dot(p, uRight);
    float y = dot(p, uUp);
    float z = dot(p, uCentre);
    float s = sqrt(x * x + y * y);
    float theta = min(atan(s, z), uMaxAngle);
    float r = uScale * ((1.0 - uFlat) * sin(theta) + uFlat * theta);
    vec2 dir = vec2(z > 0.0 ? 0.0 : 1.0, 0.0);
    if (s > 1e-20) dir = vec2(x, y) / s;
    return uViewport * 0.5 + vec2(r * dir.x, -r * dir.y);
}

void main()
{
    int corner = gl_VertexID % 3;
    vWeights = corner == 0 ? vec3(1.0, 0.0, 0.0) : corner == 1 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
    vClassA = aClassA;
    vClassB = aClassB;
    vClassC = aClassC;
    vValueA = ivec4(aValueA, 0);
    vValueB = ivec4(aValueB, 0);
    vValueC = ivec4(aValueC, 0);

    vec2 q = project(aPos);
    if (uGroup != 0)
    {
        vec2 g = project(uGroupCentre);
        float radius = length(project(uGroupCorner) - g);
        float s = radius <= 0.0 ? 1.0 : clamp(1.0 - 1.5 / (0.866 * radius), 0.5, 1.0);
        q = g + (q - g) * s;
    }

    gl_Position = vec4(q.x / uViewport.x * 2.0 - 1.0, 1.0 - q.y / uViewport.y * 2.0, 0.0, 1.0);
}
