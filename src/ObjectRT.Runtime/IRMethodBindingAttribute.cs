namespace ObjectRT.Runtime;

/// <summary>
/// Optional attribute to override the ObjectRT method name for an interface method.
/// When not specified, the C# method name is used as-is.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class IRMethodBindingAttribute : Attribute
{
    /// <summary>The name of the method in the ObjectRT module.</summary>
    public string FunctionName { get; }

    public IRMethodBindingAttribute(string? functionName = null)
    {
        FunctionName = functionName ?? string.Empty;
    }
}
