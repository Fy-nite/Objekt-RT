namespace ObjectRT.Runtime;

/// <summary>
/// Marks an interface as a strongly-typed proxy for an ObjectRT module class.
/// The source generator reads this and produces a proxy class that implements
/// the interface by delegating calls to <see cref="Runtime.CallMethod{T}"/>.
/// At runtime, <c>Runtime.Bind&lt;T&gt;()</c> returns the generated proxy.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class IRClassBindingAttribute : Attribute
{
    /// <summary>Name of the class in the ObjectRT module (e.g. "Calculator").</summary>
    public string ClassName { get; }

    /// <param name="className">
    /// Name of the class in the ObjectRT module.
    /// If null, the interface name is used.
    /// </param>
    public IRClassBindingAttribute(string? className = null)
    {
        ClassName = className ?? string.Empty;
    }
}
