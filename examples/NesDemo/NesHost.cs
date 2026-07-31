using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NesEmulator;
using ObjectRT.MonoGame;
using ObjectRT.Runtime;

namespace ObjectRT.NesDemo;

/// <summary>
/// Host binding contract for the emulator, exposed to ObjectIL scripts as
/// <c>call Nes.*</c>. The source generator hardwires dispatch (no reflection).
/// </summary>
[IRHostBinding("Nes")]
public interface INesHost
{
    void CreateTexture();
    void LoadRom();
    void Reset();
    void ReadInput();
    void StepFrame();
    void UploadFrame();
}

/// <summary>
/// Wraps a <see cref="NesEmulator.Nes"/> machine for script driving.
/// The script calls LoadRom/StepFrame/ReadInput/UploadFrame each frame;
/// pixels land in a MonoGame texture drawn via the MonoGame.Sprite bindings.
/// </summary>
public sealed class NesHost : INesHost
{
    public NesEmulator.Nes Nes { get; } = new();

    /// <summary>Optional path to an external .nes ROM. Null = built-in test ROM.</summary>
    public string? RomPath { get; set; }

    private Texture2D? _texture;
    private readonly byte[] _bgra = new byte[256 * 240 * 4];

    public void CreateTexture()
    {
        var device = MonoGameHost.GraphicsDevice;
        if (device == null) return;
        _texture = new Texture2D(device, 256, 240);
        MonoGameHost.SetTexture("screen", _texture);
    }

    public void LoadRom()
    {
        var bytes = RomPath is not null && File.Exists(RomPath)
            ? File.ReadAllBytes(RomPath)
            : TestRom.Create();
        Nes.Load(bytes);
        Nes.Reset();
    }

    public void Reset() => Nes.Reset();

    public void ReadInput()
    {
        var c = Nes.Controller1;
        c.Up = MonoGameHost.Input.KeyDown((int)Keys.Up);
        c.Down = MonoGameHost.Input.KeyDown((int)Keys.Down);
        c.Left = MonoGameHost.Input.KeyDown((int)Keys.Left);
        c.Right = MonoGameHost.Input.KeyDown((int)Keys.Right);
        c.A = MonoGameHost.Input.KeyDown((int)Keys.X);
        c.B = MonoGameHost.Input.KeyDown((int)Keys.Z);
        c.Start = MonoGameHost.Input.KeyDown((int)Keys.Enter);
        c.Select = MonoGameHost.Input.KeyDown((int)Keys.RightShift);
    }

    public void StepFrame() => Nes.StepFrame();

    public void UploadFrame()
    {
        if (_texture == null) return;
        var fb = Nes.Frame;
        for (int i = 0; i < fb.Length; i++)
        {
            _bgra[i * 4 + 0] = (byte)(fb[i] & 0xFF);
            _bgra[i * 4 + 1] = (byte)((fb[i] >> 8) & 0xFF);
            _bgra[i * 4 + 2] = (byte)((fb[i] >> 16) & 0xFF);
            _bgra[i * 4 + 3] = 0xFF;
        }
        _texture.SetData(_bgra);
    }

    /// <summary>Draw the framebuffer texture at the given display size.</summary>
    public void Draw(int screenW, int screenH)
    {
        var batch = MonoGameHost.SpriteBatch;
        var device = MonoGameHost.GraphicsDevice;
        if (batch == null || device == null || _texture == null) return;
        device.Clear(Microsoft.Xna.Framework.Color.Black);
        batch.Begin();
        batch.Draw(_texture, new Microsoft.Xna.Framework.Rectangle(0, 0, screenW, screenH),
                   Microsoft.Xna.Framework.Color.White);
        batch.End();
    }
}
