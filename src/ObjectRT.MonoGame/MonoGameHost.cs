using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ObjectRT.Runtime;
using ObjectRTRuntime = ObjectRT.Runtime.Runtime;

namespace ObjectRT.MonoGame;

/// <summary>
/// Host façade that connects an ObjectRT <see cref="Runtime"/> to a MonoGame
/// <see cref="Game"/>. Attach a game, register the bindings, and ObjectIL
/// scripts can drive rendering, input and timing via the <c>call</c> opcode
/// (which resolves module functions first, then host bindings like these).
///
/// Wiring (in your Game subclass):
/// <code>
/// var rt = new Runtime();
/// MonoGameHost.Attach(this);   // this = your Game (call in Initialize)
/// MonoGameHost.Register(rt);   // exposes the "MonoGame.*" namespace to scripts
/// rt.LoadModuleFile("game.oil");
/// </code>
///
/// Call <see cref="MonoGameHost.BeginFrame"/> at the top of <c>Update</c> and
/// <see cref="MonoGameHost.UpdateFrame"/> with the <see cref="GameTime"/> so
/// edge-triggered input and the script-visible timing values work.
///
/// The binding surface is deliberately primitive-shaped (ints, floats,
/// strings, packed ARGB colors) because those are the only value types the
/// VM can pass across the boundary today. Position (x, y) arguments for
/// sprite draws are treated as the CENTER of the texture.
/// </summary>
public static class MonoGameHost
{
    /// <summary>The attached game, or null before <see cref="Attach"/>.</summary>
    public static Game? Game { get; private set; }

    /// <summary>The game's graphics device, or null before <see cref="Attach"/>.</summary>
    public static GraphicsDevice? GraphicsDevice { get; private set; }

    /// <summary>A shared sprite batch created by <see cref="Attach"/>.</summary>
    public static SpriteBatch? SpriteBatch { get; private set; }

    private static readonly Dictionary<string, Texture2D> s_textures = new(StringComparer.Ordinal);

    private static KeyboardState _prevKeys;
    private static MouseState _prevMouse;
    private static double _totalTime;
    private static double _deltaTime;

    /// <summary>Total seconds elapsed since the game started (script-visible).</summary>
    public static double TotalTime => _totalTime;

    /// <summary>Seconds elapsed since the last frame (script-visible).</summary>
    public static double DeltaTime => _deltaTime;

    // ── Host wiring ─────────────────────────────────────────────────

    /// <summary>
    /// Attach a running MonoGame game. Call from <c>Game.Initialize</c> after
    /// the GraphicsDevice is ready.
    /// </summary>
    public static void Attach(Game game)
    {
        Game = game;
        GraphicsDevice = game.GraphicsDevice;
        SpriteBatch = new SpriteBatch(game.GraphicsDevice);
    }

    /// <summary>
    /// Register all binding groups with an ObjectRT runtime as host objects
    /// behind interface contracts. Scripts call them as
    /// <c>call MonoGame.Screen.Clear(int32)</c> and so on.
    /// The interfaces are marked [IRHostBinding], so the source generator
    /// hardwires dispatch — no reflection, NativeAOT-safe.
    /// </summary>
    public static void Register(ObjectRTRuntime rt)
    {
        rt.RegisterHost("MonoGame.Screen", Screen);
        rt.RegisterHost("MonoGame.Sprite", Sprite);
        rt.RegisterHost("MonoGame.Texture", Texture);
        rt.RegisterHost("MonoGame.Input", Input);
        rt.RegisterHost("MonoGame.Color", Color);
        rt.RegisterHost("MonoGame.Math", MathB);
        rt.RegisterHost("MonoGame.Log", Log);
    }

    // ── Host instances ─────────────────────────────────────────────

    /// <summary>Screen (window/timing) host instance.</summary>
    public static IMonoGameScreen Screen { get; } = new ScreenHost();

    /// <summary>Sprite (SpriteBatch drawing) host instance.</summary>
    public static IMonoGameSprite Sprite { get; } = new SpriteHost();

    /// <summary>Procedural texture host instance.</summary>
    public static IMonoGameTexture Texture { get; } = new TextureHost();

    /// <summary>Keyboard / mouse input host instance.</summary>
    public static IMonoGameInput Input { get; } = new InputHost();

    /// <summary>Packed ARGB color helpers host instance.</summary>
    public static IMonoGameColor Color { get; } = new ColorHost();

    /// <summary>Random helpers host instance.</summary>
    public static IMonoGameMath MathB { get; } = new MathHost();

    /// <summary>Console logging host instance.</summary>
    public static IMonoGameLog Log { get; } = new LogHost();

    /// <summary>
    /// Capture input state at the start of a frame so edge-triggered
    /// queries (KeyPressed, MousePressed) work. Call from <c>Game.Update</c>.
    /// </summary>
    public static void BeginFrame()
    {
        _prevKeys = Keyboard.GetState();
        _prevMouse = Mouse.GetState();
    }

    /// <summary>Update the timing values exposed to scripts. Call from <c>Game.Update</c>.</summary>
    public static void UpdateFrame(GameTime time)
    {
        _deltaTime = time.ElapsedGameTime.TotalSeconds;
        _totalTime = time.TotalGameTime.TotalSeconds;
    }

    // ── Internals shared by the binding groups ─────────────────────

    internal static Texture2D? FindTexture(string name)
        => s_textures.TryGetValue(name, out var tex) ? tex : null;

    internal static void StoreTexture(string name, Texture2D tex) => s_textures[name] = tex;

    /// <summary>
    /// Register a texture under a script-visible name so it can be drawn with
    /// the MonoGame.Sprite bindings (e.g. an emulator framebuffer).
    /// </summary>
    public static void SetTexture(string name, Texture2D texture) => s_textures[name] = texture;

    /// <summary>Unpack a packed ARGB int into a MonoGame color.</summary>
    internal static Color Unpack(int argb) => new(
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF),
        (byte)((argb >> 24) & 0xFF));

    /// <summary>Keyboard state captured by the last <see cref="BeginFrame"/>.</summary>
    internal static KeyboardState PrevKeys => _prevKeys;

    /// <summary>Mouse state captured by the last <see cref="BeginFrame"/>.</summary>
    internal static MouseState PrevMouse => _prevMouse;
}

// ── Host contracts ─────────────────────────────────────────────────────
// Each interface is the binding surface ObjectRT scripts see. The source
// generator reads [IRHostBinding] and hardwires dispatch — no reflection.

/// <summary>Window / backbuffer / timing queries and the clear color.</summary>
[IRHostBinding("MonoGame.Screen")]
public interface IMonoGameScreen
{
    void Clear(int color);
    int  Width();
    int  Height();
    float DeltaTime();
    float TotalTime();
}

/// <summary>SpriteBatch drawing. Draw methods center the texture at (x, y).</summary>
[IRHostBinding("MonoGame.Sprite")]
public interface IMonoGameSprite
{
    void Begin();
    void End();
    void Draw(string texture, float x, float y);
    void DrawColored(string texture, float x, float y, int color);
    void DrawScaled(string texture, float x, float y, float scaleX, float scaleY);
    void DrawScaledColored(string texture, float x, float y, float scaleX, float scaleY, int color);
    void DrawRotated(string texture, float x, float y, float rotation, int color);
    void FillRect(float x, float y, float w, float h, int color);
}

/// <summary>Procedural texture creation — no content pipeline needed.</summary>
[IRHostBinding("MonoGame.Texture")]
public interface IMonoGameTexture
{
    void CreateSolid(string name, int width, int height, int color);
    void CreateChecker(string name, int width, int height, int colorA, int colorB);
    void Remove(string name);
}

/// <summary>Keyboard / mouse state. Buttons: 0 = left, 1 = right, 2 = middle.</summary>
[IRHostBinding("MonoGame.Input")]
public interface IMonoGameInput
{
    bool KeyDown(int key);
    bool KeyPressed(int key);
    int  MouseX();
    int  MouseY();
    bool MouseDown(int button);
    bool MousePressed(int button);
}

/// <summary>Packed ARGB color helpers. Colors are int32s in scripts.</summary>
[IRHostBinding("MonoGame.Color")]
public interface IMonoGameColor
{
    int Argb(int a, int r, int g, int b);
    int Rgb(int r, int g, int b);
    int White();
    int Black();
    int Red();
    int Green();
    int Blue();
    int Yellow();
    int Cyan();
    int Magenta();
    int Orange();
    int Gray();
}

/// <summary>Random helpers. State lives on the host.</summary>
[IRHostBinding("MonoGame.Math")]
public interface IMonoGameMath
{
    float RandomFloat();
    float RandomRange(float min, float max);
    int   RandomInt(int max);
}

/// <summary>Console output for scripts (goes to the host's stdout).</summary>
[IRHostBinding("MonoGame.Log")]
public interface IMonoGameLog
{
    void Log(string message);
    void LogInt(int value);
    void LogFloat(float value);
}

// ── Host implementations ────────────────────────────────────────────────

internal sealed class ScreenHost : IMonoGameScreen
{
    public void Clear(int color) => MonoGameHost.GraphicsDevice?.Clear(MonoGameHost.Unpack(color));
    public int Width() => MonoGameHost.GraphicsDevice?.Viewport.Width ?? 0;
    public int Height() => MonoGameHost.GraphicsDevice?.Viewport.Height ?? 0;
    public float DeltaTime() => (float)MonoGameHost.DeltaTime;
    public float TotalTime() => (float)MonoGameHost.TotalTime;
}

internal sealed class SpriteHost : IMonoGameSprite
{
    public void Begin() => MonoGameHost.SpriteBatch?.Begin();
    public void End() => MonoGameHost.SpriteBatch?.End();

    public void Draw(string texture, float x, float y)
        => DrawImpl(texture, x, y, 1f, 1f, 0f, Color.White);

    public void DrawColored(string texture, float x, float y, int color)
        => DrawImpl(texture, x, y, 1f, 1f, 0f, MonoGameHost.Unpack(color));

    public void DrawScaled(string texture, float x, float y, float scaleX, float scaleY)
        => DrawImpl(texture, x, y, scaleX, scaleY, 0f, Color.White);

    public void DrawScaledColored(string texture, float x, float y, float scaleX, float scaleY, int color)
        => DrawImpl(texture, x, y, scaleX, scaleY, 0f, MonoGameHost.Unpack(color));

    public void DrawRotated(string texture, float x, float y, float rotation, int color)
        => DrawImpl(texture, x, y, 1f, 1f, rotation, MonoGameHost.Unpack(color));

    public void FillRect(float x, float y, float w, float h, int color)
    {
        var batch = MonoGameHost.SpriteBatch;
        if (batch == null || MonoGameHost.GraphicsDevice == null) return;

        var pixel = MonoGameHost.FindTexture("__pixel");
        if (pixel == null)
        {
            pixel = new Texture2D(MonoGameHost.GraphicsDevice, 1, 1);
            pixel.SetData(new[] { Color.White });
            MonoGameHost.StoreTexture("__pixel", pixel);
        }

        batch.Draw(pixel, new Rectangle((int)x, (int)y, (int)w, (int)h), MonoGameHost.Unpack(color));
    }

    private static void DrawImpl(string texture, float x, float y, float sx, float sy, float rotation, Color color)
    {
        var batch = MonoGameHost.SpriteBatch;
        var tex = MonoGameHost.FindTexture(texture);
        if (batch == null || tex == null) return;

        var origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
        batch.Draw(tex, new Vector2(x, y), null, color, rotation, origin,
                   new Vector2(sx, sy), SpriteEffects.None, 0f);
    }
}

internal sealed class TextureHost : IMonoGameTexture
{
    public void CreateSolid(string name, int width, int height, int color)
    {
        var device = MonoGameHost.GraphicsDevice;
        if (device == null) return;

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var tex = new Texture2D(device, width, height);
        var data = new Color[width * height];
        Array.Fill(data, MonoGameHost.Unpack(color));
        tex.SetData(data);
        MonoGameHost.StoreTexture(name, tex);
    }

    public void CreateChecker(string name, int width, int height, int colorA, int colorB)
    {
        var device = MonoGameHost.GraphicsDevice;
        if (device == null) return;

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var ca = MonoGameHost.Unpack(colorA);
        var cb = MonoGameHost.Unpack(colorB);
        var data = new Color[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                data[y * width + x] = ((x + y) % 2 == 0) ? ca : cb;
        var tex = new Texture2D(device, width, height);
        tex.SetData(data);
        MonoGameHost.StoreTexture(name, tex);
    }

    public void Remove(string name) => MonoGameHost.StoreTexture(name, null!);
}

internal sealed class InputHost : IMonoGameInput
{
    public bool KeyDown(int key) => Keyboard.GetState().IsKeyDown((Keys)key);

    public bool KeyPressed(int key)
    {
        var cur = Keyboard.GetState();
        return cur.IsKeyDown((Keys)key) && !MonoGameHost.PrevKeys.IsKeyDown((Keys)key);
    }

    public int MouseX() => Mouse.GetState().X;
    public int MouseY() => Mouse.GetState().Y;

    public bool MouseDown(int button) => IsDown(Mouse.GetState(), button);

    public bool MousePressed(int button)
    {
        var cur = Mouse.GetState();
        return IsDown(cur, button) && !IsDown(MonoGameHost.PrevMouse, button);
    }

    private static bool IsDown(MouseState s, int button) => button switch
    {
        0 => s.LeftButton == ButtonState.Pressed,
        1 => s.RightButton == ButtonState.Pressed,
        2 => s.MiddleButton == ButtonState.Pressed,
        _ => false,
    };
}

internal sealed class ColorHost : IMonoGameColor
{
    public int Argb(int a, int r, int g, int b) => (a << 24) | (r << 16) | (g << 8) | b;
    public int Rgb(int r, int g, int b) => (255 << 24) | (r << 16) | (g << 8) | b;

    public int White()   => unchecked((int)0xFFFFFFFF);
    public int Black()   => unchecked((int)0xFF000000);
    public int Red()     => unchecked((int)0xFFFF0000);
    public int Green()   => unchecked((int)0xFF00FF00);
    public int Blue()    => unchecked((int)0xFF0000FF);
    public int Yellow()  => unchecked((int)0xFFFFFF00);
    public int Cyan()    => unchecked((int)0xFF00FFFF);
    public int Magenta() => unchecked((int)0xFFFF00FF);
    public int Orange()  => unchecked((int)0xFFFFA500);
    public int Gray()    => unchecked((int)0xFF808080);
}

internal sealed class MathHost : IMonoGameMath
{
    private static readonly Random s_random = new();

    public float RandomFloat() => (float)s_random.NextDouble();
    public float RandomRange(float min, float max) => min + (float)s_random.NextDouble() * (max - min);
    public int RandomInt(int max) => s_random.Next(Math.Max(1, max));
}

internal sealed class LogHost : IMonoGameLog
{
    public void Log(string message) => Console.WriteLine($"[script] {message}");
    public void LogInt(int value) => Console.WriteLine($"[script] {value}");
    public void LogFloat(float value) => Console.WriteLine($"[script] {value:F4}");
}
