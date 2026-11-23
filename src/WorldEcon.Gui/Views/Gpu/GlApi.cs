using Avalonia.OpenGL;

namespace WorldEcon.Gui.Views.Gpu;

/// <summary>
/// OpenGL ES 3.0 / OpenGL 3.3 entry points that Avalonia's <see cref="GlInterface"/> does not wrap, loaded with
/// <see cref="GlInterface.GetProcAddress"/>, plus the enum values the map backend uses. Every function used here is in
/// both OpenGL ES 3.0 (WebGL2's base) and desktop OpenGL 3.3, so a WebGL2 front end calls the same set.
/// </summary>
internal sealed unsafe class GlApi
{
    public const int Triangles = 0x0004;
    public const int UnsignedByte = 0x1401;
    public const int Short = 0x1402;
    public const int UnsignedInt = 0x1405;
    public const int Float = 0x1406;
    public const int ArrayBuffer = 0x8892;
    public const int ElementArrayBuffer = 0x8893;
    public const int StaticDraw = 0x88E4;
    public const int Texture2D = 0x0DE1;
    public const int Texture2DArray = 0x8C1A;
    public const int Texture0 = 0x84C0;
    public const int TextureMinFilter = 0x2801;
    public const int TextureMagFilter = 0x2800;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int Nearest = 0x2600;
    public const int ClampToEdge = 0x812F;
    public const int Rgba = 0x1908;
    public const int Rgba8 = 0x8058;
    public const int Rgba8Ui = 0x8D7C;
    public const int Rgba16I = 0x8D88;
    public const int Rgba32F = 0x8814;
    public const int R8Ui = 0x8232;
    public const int RgbaInteger = 0x8D99;
    public const int RedInteger = 0x8D94;
    public const int UnpackAlignment = 0x0CF5;
    public const int PackAlignment = 0x0D05;
    public const int Blend = 0x0BE2;
    public const int One = 1;
    public const int OneMinusSrcAlpha = 0x0303;
    public const int DepthTest = 0x0B71;
    public const int CullFace = 0x0B44;
    public const int ScissorTest = 0x0C11;
    public const int Framebuffer = 0x8D40;
    public const int VertexShader = 0x8B31;
    public const int FragmentShader = 0x8B30;
    public const int NoError = 0;

    private readonly delegate* unmanaged<int> _getError;
    private readonly delegate* unmanaged<int, float, float, void> _uniform2f;
    private readonly delegate* unmanaged<int, float, float, float, void> _uniform3f;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, int, void*, void> _texImage3D;
    private readonly delegate* unmanaged<int, int, void> _pixelStorei;
    private readonly delegate* unmanaged<int, int, void> _blendFunc;
    private readonly delegate* unmanaged<int, int, int, int, nint, void> _vertexAttribIPointer;
    private readonly delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> _texSubImage2D;

    public GlApi(GlInterface gl)
    {
        Gl = gl;
        nint Load(string name)
        {
            var p = gl.GetProcAddress(name);
            return p != 0 ? p : throw new InvalidOperationException($"OpenGL function {name} is not available.");
        }

        _getError = (delegate* unmanaged<int>)Load("glGetError");
        _uniform2f = (delegate* unmanaged<int, float, float, void>)Load("glUniform2f");
        _uniform3f = (delegate* unmanaged<int, float, float, float, void>)Load("glUniform3f");
        _texImage3D = (delegate* unmanaged<int, int, int, int, int, int, int, int, int, void*, void>)Load("glTexImage3D");
        _pixelStorei = (delegate* unmanaged<int, int, void>)Load("glPixelStorei");
        _blendFunc = (delegate* unmanaged<int, int, void>)Load("glBlendFunc");
        _vertexAttribIPointer = (delegate* unmanaged<int, int, int, int, nint, void>)Load("glVertexAttribIPointer");
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)Load("glReadPixels");
        _texSubImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)Load("glTexSubImage2D");
    }

    /// <summary>The functions Avalonia wraps (buffers, programs, 2D textures, draw calls).</summary>
    public GlInterface Gl { get; }

    public int GetError() => _getError();

    public void Uniform2f(int location, float x, float y) => _uniform2f(location, x, y);

    public void Uniform3f(int location, float x, float y, float z) => _uniform3f(location, x, y, z);

    public void TexImage3D(int target, int level, int internalFormat, int width, int height, int depth, int format, int type, void* data) =>
        _texImage3D(target, level, internalFormat, width, height, depth, 0, format, type, data);

    public void PixelStorei(int name, int value) => _pixelStorei(name, value);

    public void BlendFunc(int src, int dst) => _blendFunc(src, dst);

    public void VertexAttribIPointer(int index, int size, int type, int stride, nint offset) => _vertexAttribIPointer(index, size, type, stride, offset);

    public void ReadPixels(int x, int y, int width, int height, int format, int type, void* data) => _readPixels(x, y, width, height, format, type, data);

    public void TexSubImage2D(int target, int level, int x, int y, int width, int height, int format, int type, void* data) =>
        _texSubImage2D(target, level, x, y, width, height, format, type, data);

    /// <summary>Throws when the GL error flag is set (checked after uploads, not per frame).</summary>
    public void Check(string what)
    {
        var e = GetError();
        if (e != NoError) throw new InvalidOperationException($"OpenGL error 0x{e:X4} after {what}.");
    }
}
