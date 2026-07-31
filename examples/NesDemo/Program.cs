using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ObjectRT.MonoGame;

namespace ObjectRT.NesDemo;

/// <summary>
/// NES emulator frontend. The window/game loop live here; all emulator
/// driving (load ROM, step frames, read input, upload pixels) is scripted
/// in game.oil through the host bindings:
///
///   call Nes.CreateTexture()
///   call Nes.LoadRom()
///   call Nes.ReadInput()
///   call Nes.StepFrame()
///   call Nes.UploadFrame()
///   call MonoGame.Sprite.DrawScaled(string, float32, float32, float32, float32)
///
/// Usage:
///   dotnet run                      (built-in test ROM: checkerboard)
///   dotnet run -- --rom path.nes    (load any mapper 0/1/2 ROM)
///   dotnet run -- --frames 300      (auto-close after N frames)
///
/// Controls: arrows = D-pad, Z = B, X = A, Enter = Start, Shift = Select, Esc = quit.
/// </summary>
public class NesGameApp : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly string? _romPath;
    private readonly int _maxFrames;
    private int _frames;
    private NesHost? _nes;

    public NesGameApp(string? romPath, int maxFrames)
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 768,
            PreferredBackBufferHeight = 720,
        };
        _romPath = romPath;
        _maxFrames = maxFrames;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "ObjectRT NES Emulator";
    }

    protected override void Initialize()
    {
        MonoGameHost.Attach(this);

        // Direct C# game loop — no ObjectIL dispatch overhead.
        _nes = new NesHost { RomPath = _romPath };

        _nes.CreateTexture();
        _nes.LoadRom();
        _nes.StepFrame();

        Console.WriteLine($"[host] emulator ready (rom: {(_romPath ?? "built-in test ROM")})");
        base.Initialize();
    }

    protected override void Update(GameTime gameTime)
    {
        MonoGameHost.BeginFrame();
        MonoGameHost.UpdateFrame(gameTime);

        _nes!.ReadInput();
        _nes.StepFrame();

        if (Keyboard.GetState().IsKeyDown(Keys.Escape) || (_maxFrames > 0 && ++_frames >= _maxFrames))
            Exit();

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        _nes!.UploadFrame();
        _nes!.Draw(768, 720);
        base.Draw(gameTime);
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string? romPath = null;
        var frames = 0;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--rom" when i + 1 < args.Length:
                    romPath = args[i + 1];
                    i++;
                    break;
                case "--frames" when int.TryParse(args[i + 1], out var f):
                    frames = Math.Max(0, f);
                    i++;
                    break;
            }
        }

        using var game = new NesGameApp(romPath, frames);
        game.Run();
        Console.WriteLine("[host] emulator exited");
    }
}
