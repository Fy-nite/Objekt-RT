using System.Linq;
using ObjektRT.Core.Hosting;

namespace ObjectRT.Runtime.Reflection;

/// <summary>
/// Stock <see cref="IReflectHost"/> backed by a <see cref="Runtime"/> and its
/// <see cref="ModuleReflector"/>. Resolves names through the loaded module
/// (short or qualified), then invokes through the runtime.
/// </summary>
public sealed class RuntimeReflectHost : IReflectHost
{
    private readonly Runtime _runtime;

    public RuntimeReflectHost(Runtime runtime) => _runtime = runtime;

    private ModuleReflector? Reflector => _runtime.GetReflector();

    public string[] Types()
        => Reflector?.GetTypes().Select(t => t.Name).ToArray() ?? Array.Empty<string>();

    public bool HasType(string typeName)
    {
        var refl = Reflector;
        if (refl == null) return false;
        if (refl.GetType(typeName) != null) return true;
        var shortName = ResolveShort(typeName);
        return shortName != null && refl.GetType(shortName) != null;
    }

    public string[] Methods(string typeName)
    {
        var t = FindType(typeName);
        return t?.GetMethods().Select(m => m.QualifiedName).ToArray() ?? Array.Empty<string>();
    }

    public string[] Fields(string typeName)
    {
        var t = FindType(typeName);
        return t?.GetFields().Select(f => f.QualifiedName).ToArray() ?? Array.Empty<string>();
    }

    public string BaseType(string typeName)
        => FindType(typeName)?.BaseType?.Name ?? "";

    public string ModuleName()
        => Reflector?.ModuleName ?? "";

    public string Kind(string typeName)
        => FindType(typeName)?.Kind.ToString() ?? "";

    public bool IsClass(string typeName)
        => FindType(typeName)?.IsClass ?? false;

    public bool IsInterface(string typeName)
        => FindType(typeName)?.IsInterface ?? false;

    public bool IsStruct(string typeName)
        => FindType(typeName)?.IsStruct ?? false;

    public bool IsEnum(string typeName)
        => FindType(typeName)?.IsEnum ?? false;

    public bool IsAbstract(string typeName)
        => FindType(typeName)?.IsAbstract ?? false;

    public bool IsSealed(string typeName)
        => FindType(typeName)?.IsSealed ?? false;

    public string Access(string typeName)
        => FindType(typeName)?.Access.ToString() ?? "";

    public string[] Interfaces(string typeName)
        => FindType(typeName)?.Interfaces.Select(i => i?.Name ?? "").ToArray() ?? Array.Empty<string>();

    public string[] AllInterfaces(string typeName)
        => FindType(typeName)?.GetInterfaces().Select(i => i.Name).ToArray() ?? Array.Empty<string>();

    public string[] Hierarchy(string typeName)
        => FindType(typeName)?.GetHierarchy().Select(t => t.Name).ToArray() ?? Array.Empty<string>();

    public bool IsSubclassOf(string typeName, string baseTypeName)
    {
        var t = FindType(typeName);
        var baseType = FindType(baseTypeName);
        return t != null && baseType != null && t.IsSubclassOf(baseType);
    }

    public bool IsAssignableFrom(string typeName, string otherTypeName)
    {
        var t = FindType(typeName);
        var other = FindType(otherTypeName);
        return t != null && other != null && t.IsAssignableFrom(other);
    }

    public string Resolve(string qualifiedMethodName)
    {
        int dot = qualifiedMethodName.LastIndexOf('.');
        if (dot <= 0 || dot >= qualifiedMethodName.Length - 1) return "";
        var typeName = qualifiedMethodName[..dot];
        var methodName = qualifiedMethodName[(dot + 1)..];
        return FindType(typeName)?.FindMethod(methodName)?.QualifiedName ?? "";
    }

    public string[] DeclaredMethods(string typeName)
        => FindType(typeName)?.GetDeclaredMethods().Select(m => m.QualifiedName).ToArray() ?? Array.Empty<string>();

    public string[] DeclaredFields(string typeName)
        => FindType(typeName)?.GetDeclaredFields().Select(f => f.QualifiedName).ToArray() ?? Array.Empty<string>();

    public string[] Attributes(string typeName)
        => FindType(typeName)?.GetAttributes().Select(a => a.ToString()).ToArray() ?? Array.Empty<string>();

    public string[] MethodAttributes(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.GetAttributes().Select(a => a.ToString()).ToArray() ?? Array.Empty<string>();

    public string MethodReturn(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.ReturnTypeName ?? "";

    public string[] MethodParams(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.GetParameters().Select(p => p.ToString()).ToArray() ?? Array.Empty<string>();

    public bool MethodStatic(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.IsStatic ?? false;

    public bool MethodVirtual(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.IsVirtual ?? false;

    public bool MethodOverride(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.IsOverride ?? false;

    public bool MethodAbstract(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.IsAbstract ?? false;

    public string MethodDeclaringType(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.DeclaringType.Name ?? "";

    public string MethodBase(string typeName, string methodName)
        => FindMethod(typeName, methodName)?.GetBaseDefinition()?.QualifiedName ?? "";

    public string FieldType(string typeName, string fieldName)
        => FindField(typeName, fieldName)?.TypeName ?? "";

    public bool FieldStatic(string typeName, string fieldName)
        => FindField(typeName, fieldName)?.IsStatic ?? false;

    public string FieldDeclaringType(string typeName, string fieldName)
        => FindField(typeName, fieldName)?.DeclaringType.Name ?? "";

    public object? Invoke(string typeName, string methodName, object? receiver, object?[] args)
    {
        var method = FindMethod(typeName, methodName);
        if (method == null) return null;
        return method.Invoke(_runtime, method.IsStatic ? null : receiver, args);
    }

    public object? GetStatic(string typeName, string fieldName)
    {
        var t = FindType(typeName);
        var field = t?.GetField(fieldName);
        if (field == null || !field.IsStatic) return null;
        return _runtime.GetStaticField(field.QualifiedName);
    }

    public void SetStatic(string typeName, string fieldName, object? value)
    {
        var t = FindType(typeName);
        var field = t?.GetField(fieldName);
        if (field == null || !field.IsStatic) return;
        _runtime.SetStaticField(field.QualifiedName, value);
    }

    public object? Call(string typeName, string methodName, object?[] args)
    {
        var t = FindType(typeName);
        var method = t?.GetMethod(methodName);
        if (method == null || !method.IsStatic) return null;
        return _runtime.CallMethod<object?>(method.QualifiedName, args);
    }

    private TypeInfo? FindType(string typeName)
    {
        var refl = Reflector;
        if (refl == null) return null;
        var direct = refl.GetType(typeName);
        if (direct != null) return direct;
        var shortName = ResolveShort(typeName);
        return shortName != null ? refl.GetType(shortName) : null;
    }

    /// <summary>Finds a method by name on a type, walking the base chain (most-derived wins).</summary>
    private MethodInfo? FindMethod(string typeName, string methodName)
        => FindType(typeName)?.FindMethod(methodName);

    /// <summary>Finds a field by name on a type, walking the base chain.</summary>
    private FieldInfo? FindField(string typeName, string fieldName)
        => FindType(typeName)?.GetField(fieldName);

    /// <summary>Finds a short name's qualified form ("Geo" → "com.lib.Geo") in the loaded module.</summary>
    private string? ResolveShort(string shortName)
    {
        if (shortName.Contains('.')) return null;
        return Reflector?.GetTypes().FirstOrDefault(t =>
            t.Name == shortName || t.Name.EndsWith("." + shortName, StringComparison.Ordinal))?.Name;
    }
}
