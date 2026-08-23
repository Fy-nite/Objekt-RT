using System.Linq;
using ObjektRT.Core.Attributes;
using ObjectRT.Abstractions;

namespace ObjectRT.Runtime.Reflection;

/// <summary>
/// In-language reflection binding: <c>Reflect.Types()</c>, <c>Reflect.Methods("Foo")</c>,
/// <c>Reflect.GetStatic(...)</c>, <c>Reflect.Call(...)</c>, <c>Reflect.Invoke(...)</c>,
/// <c>Reflect.Hierarchy("Foo")</c>, ... — runtime introspection over the module
/// loaded into a <see cref="ObjectRT.Runtime.Runtime"/>. The runtime attaches
/// its host automatically on load (<see cref="Attach"/>); without one every
/// call returns empty/false/null.
/// </summary>
[ClassBinding("Reflect")]
public static class ReflectBinding
{
    /// <summary>The host providing module metadata + invocation. Set via <see cref="Attach"/>.</summary>
    public static IReflectHost? Host { get; set; }

    /// <summary>
    /// Points the binding at <paramref name="runtime"/>'s loaded module.
    /// The last runtime to load a module wins — with several runtimes in one
    /// process, call this again after switching between them.
    /// </summary>
    public static void Attach(Runtime runtime) => Host = new RuntimeReflectHost(runtime);

    /// <summary>Every type in the loaded module (qualified wire names).</summary>
    [MethodBinding]
    public static string[] Types() => Host?.Types() ?? Array.Empty<string>();

    /// <summary>True when a type with this (short or qualified) name exists.</summary>
    [MethodBinding]
    public static bool HasType(string typeName) => Host?.HasType(typeName) ?? false;

    /// <summary>Qualified names ("Type.Method") of a type's methods, including inherited.</summary>
    [MethodBinding]
    public static string[] Methods(string typeName) => Host?.Methods(typeName) ?? Array.Empty<string>();

    /// <summary>Qualified names ("Type.field") of a type's fields, including inherited.</summary>
    [MethodBinding]
    public static string[] Fields(string typeName) => Host?.Fields(typeName) ?? Array.Empty<string>();

    /// <summary>The direct base type's wire name, or "" when none.</summary>
    [MethodBinding]
    public static string Base(string typeName) => Host?.BaseType(typeName) ?? "";

    /// <summary>The loaded module's name, or "" when nothing is loaded.</summary>
    [MethodBinding]
    public static string ModuleName() => Host?.ModuleName() ?? "";

    /// <summary>The type's kind: "Class" / "Interface" / "Struct" / "Enum", or "" when unknown.</summary>
    [MethodBinding]
    public static string Kind(string typeName) => Host?.Kind(typeName) ?? "";

    /// <summary>True when the type is a class (TypeKind.Class).</summary>
    [MethodBinding]
    public static bool IsClass(string typeName) => Host?.IsClass(typeName) ?? false;

    /// <summary>True when the type is an interface (TypeKind.Interface).</summary>
    [MethodBinding]
    public static bool IsInterface(string typeName) => Host?.IsInterface(typeName) ?? false;

    /// <summary>True when the type is a struct (TypeKind.Struct).</summary>
    [MethodBinding]
    public static bool IsStruct(string typeName) => Host?.IsStruct(typeName) ?? false;

    /// <summary>True when the type is an enum (TypeKind.Enum).</summary>
    [MethodBinding]
    public static bool IsEnum(string typeName) => Host?.IsEnum(typeName) ?? false;

    /// <summary>True when the type is abstract (IR TypeFlags.Abstract).</summary>
    [MethodBinding]
    public static bool IsAbstract(string typeName) => Host?.IsAbstract(typeName) ?? false;

    /// <summary>True when the type is sealed (IR TypeFlags.Sealed).</summary>
    [MethodBinding]
    public static bool IsSealed(string typeName) => Host?.IsSealed(typeName) ?? false;

    /// <summary>The type's declared access: "Public" / "Private" / "Protected" / "Internal".</summary>
    [MethodBinding]
    public static string Access(string typeName) => Host?.Access(typeName) ?? "";

    /// <summary>Direct interfaces implemented by the type, by name (external ones as "").</summary>
    [MethodBinding]
    public static string[] Interfaces(string typeName) => Host?.Interfaces(typeName) ?? Array.Empty<string>();

    /// <summary>All interfaces implemented by the type, including inherited ones.</summary>
    [MethodBinding]
    public static string[] AllInterfaces(string typeName) => Host?.AllInterfaces(typeName) ?? Array.Empty<string>();

    /// <summary>This type and all its bases, most-derived first.</summary>
    [MethodBinding]
    public static string[] Hierarchy(string typeName) => Host?.Hierarchy(typeName) ?? Array.Empty<string>();

    /// <summary>True when typeName transitively inherits from baseTypeName.</summary>
    [MethodBinding]
    public static bool IsSubclassOf(string typeName, string baseTypeName) => Host?.IsSubclassOf(typeName, baseTypeName) ?? false;

    /// <summary>True when otherTypeName is typeName, a subclass of it, or (for interfaces) an implementor of it.</summary>
    [MethodBinding]
    public static bool IsAssignableFrom(string typeName, string otherTypeName) => Host?.IsAssignableFrom(typeName, otherTypeName) ?? false;

    /// <summary>Resolves "Type.Method" through inheritance — most-derived wins — to its canonical "DeclaringType.Method" form, or "" when unresolvable.</summary>
    [MethodBinding]
    public static string Resolve(string qualifiedMethodName) => Host?.Resolve(qualifiedMethodName) ?? "";

    /// <summary>Methods declared on the type itself (not inherited), as "Type.Method".</summary>
    [MethodBinding]
    public static string[] DeclaredMethods(string typeName) => Host?.DeclaredMethods(typeName) ?? Array.Empty<string>();

    /// <summary>Fields declared on the type itself (not inherited), as "Type.field".</summary>
    [MethodBinding]
    public static string[] DeclaredFields(string typeName) => Host?.DeclaredFields(typeName) ?? Array.Empty<string>();

    /// <summary>Attributes applied to the type, as "Name(arg, ...)" strings.</summary>
    [MethodBinding]
    public static string[] Attributes(string typeName) => Host?.Attributes(typeName) ?? Array.Empty<string>();

    /// <summary>Attributes applied to a method, as "Name(arg, ...)" strings.</summary>
    [MethodBinding]
    public static string[] MethodAttributes(string typeName, string methodName) => Host?.MethodAttributes(typeName, methodName) ?? Array.Empty<string>();

    /// <summary>The method's declared return type name ("int32", "string", "void", ...), or "" when unknown.</summary>
    [MethodBinding]
    public static string MethodReturn(string typeName, string methodName) => Host?.MethodReturn(typeName, methodName) ?? "";

    /// <summary>The method's parameters as "type name" strings ("int32 x"), or empty when unknown. Instance methods include "this" as parameter 0.</summary>
    [MethodBinding]
    public static string[] MethodParams(string typeName, string methodName) => Host?.MethodParams(typeName, methodName) ?? Array.Empty<string>();

    /// <summary>True when the method is static.</summary>
    [MethodBinding]
    public static bool MethodStatic(string typeName, string methodName) => Host?.MethodStatic(typeName, methodName) ?? false;

    /// <summary>True when the method is virtual (IR MethodFlags.Virtual).</summary>
    [MethodBinding]
    public static bool MethodVirtual(string typeName, string methodName) => Host?.MethodVirtual(typeName, methodName) ?? false;

    /// <summary>True when the method is an override (IR MethodFlags.Override).</summary>
    [MethodBinding]
    public static bool MethodOverride(string typeName, string methodName) => Host?.MethodOverride(typeName, methodName) ?? false;

    /// <summary>True when the method is abstract (IR MethodFlags.Abstract).</summary>
    [MethodBinding]
    public static bool MethodAbstract(string typeName, string methodName) => Host?.MethodAbstract(typeName, methodName) ?? false;

    /// <summary>The type that declares the method (the base type for inherited lookups), or "" when unknown.</summary>
    [MethodBinding]
    public static string MethodDeclaringType(string typeName, string methodName) => Host?.MethodDeclaringType(typeName, methodName) ?? "";

    /// <summary>The base definition of the method — the "Type.Method" root of an override chain — or "" when unknown.</summary>
    [MethodBinding]
    public static string MethodBase(string typeName, string methodName) => Host?.MethodBase(typeName, methodName) ?? "";

    /// <summary>The field's declared type name ("int32", ...), or "" when unknown.</summary>
    [MethodBinding]
    public static string FieldType(string typeName, string fieldName) => Host?.FieldType(typeName, fieldName) ?? "";

    /// <summary>True when the field is static.</summary>
    [MethodBinding]
    public static bool FieldStatic(string typeName, string fieldName) => Host?.FieldStatic(typeName, fieldName) ?? false;

    /// <summary>The type that declares the field, or "" when unknown.</summary>
    [MethodBinding]
    public static string FieldDeclaringType(string typeName, string fieldName) => Host?.FieldDeclaringType(typeName, fieldName) ?? "";

    /// <summary>Reads a static field by type name + field name.</summary>
    [MethodBinding]
    public static object? GetStatic(string typeName, string fieldName) => Host?.GetStatic(typeName, fieldName);

    /// <summary>Writes a static field by type name + field name.</summary>
    [MethodBinding]
    public static void SetStatic(string typeName, string fieldName, object? value) => Host?.SetStatic(typeName, fieldName, value);

    /// <summary>Invokes a static method by type name + method name with args.</summary>
    [MethodBinding]
    public static object? Call(string typeName, string methodName, object?[] args) => Host?.Call(typeName, methodName, args);

    /// <summary>
    /// Invokes a method by type name + method name with a receiver and args.
    /// For static methods the receiver is ignored; for instance methods it
    /// must be the handle returned by a previous call (e.g. from
    /// <c>Reflect.Call</c>).
    /// </summary>
    [MethodBinding]
    public static object? Invoke(string typeName, string methodName, object? receiver, object?[] args) => Host?.Invoke(typeName, methodName, receiver, args);
}

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
