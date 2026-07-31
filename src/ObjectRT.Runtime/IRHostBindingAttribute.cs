namespace ObjectRT.Runtime;

/// <summary>
/// Marks a C# interface as a host binding contract for ObjectRT scripts.
///
/// The source generator reads this and emits a hardwired (reflection-free)
/// dispatch adapter registered in <see cref="HostDispatchRegistry"/>. Scripts
/// call the host through the <c>callnative</c> opcode using the binding name:
///
/// <code>
/// [IRHostBinding("MonoGame.Screen")]
/// public interface IMonoGameScreen
/// {
///     void Clear(int color);
///     int  Width();
/// }
/// </code>
///
/// then in ObjectIL:
/// <code>
/// callnative MonoGame.Screen.Clear(int32)
/// </code>
///
/// Register an implementation with <c>rt.RegisterHost(new MonoGameScreen(), "MonoGame.Screen")</c>.
/// The generated dispatcher casts directly to the interface, so no reflection
/// is needed at runtime — host bindings keep working under NativeAOT.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class IRHostBindingAttribute : Attribute
{
    /// <summary>Name scripts use to call the host, e.g. "MonoGame.Screen".</summary>
    public string Name { get; }

    /// <param name="name">Binding name for scripts. If null, the interface name is used.</param>
    public IRHostBindingAttribute(string? name = null)
    {
        Name = name ?? string.Empty;
    }
}
