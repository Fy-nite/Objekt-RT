using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ObjectRT.MonoGame;
using ObjectRTRuntime = ObjectRT.Runtime.Runtime;

namespace ObjectRT.MonoGameDemo;

/// <summary>
/// Host shell for the ObjectRT x MonoGame demo. The C# side only owns the
/// window, the game loop and the Runtime; all rendering, input and game
/// logic lives in game.oil (ObjectIL) and talks to MonoGame via
/// callnative MonoGame.* bindings.
///
/// Usage:
///   dotnet run                          (until the window closes / Esc)
///   dotnet run -- --frames 300          (auto-close after N frames)
/// </summary>
public class GameApp : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly int _maxFrames;
    private int _frames;
    private ObjectRTRuntime? _rt;

    public GameApp(int maxFrames)
    {
        _graphics = new GraphicsDeviceManager(this);
        _maxFrames = maxFrames;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.Title = "ObjectRT x MonoGame";
    }

    protected override void Initialize()
    {
        MonoGameHost.Attach(this);

        _rt = new ObjectRTRuntime();

        // Prove the host bindings work through the SOURCE-GENERATED dispatch
        // adapters only — no reflection. This is the NativeAOT configuration:
        // if the generator weren't wired up, every callnative would fail here.
        _rt.HostResolver.AllowReflection = false;

        MonoGameHost.Register(_rt);
        _rt.LoadModuleFile(Path.Combine(AppContext.BaseDirectory, "game.oil"));
        _rt.CallMethod<object?>("Game.Init");

        Console.WriteLine("[host] module loaded, Game.Init complete");
        base.Initialize();
    }

    protected override void Update(GameTime gameTime)
    {
        MonoGameHost.BeginFrame();
        MonoGameHost.UpdateFrame(gameTime);

        _rt?.CallMethod<object?>("Game.Update", (float)gameTime.ElapsedGameTime.TotalSeconds);

        if (Keyboard.GetState().IsKeyDown(Keys.Escape) || (_maxFrames > 0 && ++_frames >= _maxFrames))
            Exit();

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        // The script owns the frame: it clears, begins the sprite batch,
        // draws, and ends it — all through callnative MonoGame.* calls.
        _rt?.CallMethod<object?>("Game.Draw");
        base.Draw(gameTime);
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var frames = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var f))
                frames = Math.Max(0, f);
        }

        using var game = new GameApp(frames);
        game.Run();
        Console.WriteLine("[host] game exited");
    }
}
