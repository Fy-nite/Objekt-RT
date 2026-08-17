using System.Runtime.CompilerServices;
using ObjektRT.Core.Model;

namespace ObjectRT.Runtime.Reflection;

/// <summary>
/// C#-style reflection over an ObjectRT module. Build a reflector from any
/// <see cref="ORBTModule"/> (parse one with <c>OilFileReader</c> /
/// <c>OrbtFileReader</c>, or get it from <see cref="Runtime.GetReflector"/>
/// for the loaded module) and you can enumerate types, methods, fields and
/// attributes, walk inheritance hierarchies, resolve method references —
/// including inherited ones — and invoke them through a <see cref="Runtime"/>.
///
/// Example:
/// <code>
/// var mod = OilFileReader.ParseFile("app.oil");
/// var refl = ModuleReflector.From(mod);
/// var circle = refl.GetType("Circle");
/// var describe = circle?.FindMethod("Describe");          // most-derived override
/// object? receiver = rt.CallMethod&lt;object&gt;("Factory.Make");
/// string? text = describe?.Invoke(rt, receiver) as string;
/// </code>
/// </summary>
public sealed class ModuleReflector
{
    private readonly Dictionary<TypeRecord, TypeInfo> _typesByRecord = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, TypeInfo> _typesByName = new(StringComparer.Ordinal);

    /// <summary>The module being reflected over.</summary>
    public ORBTModule Module { get; }

    public string ModuleName => Module.ModuleName;
    public ModuleVersion Version => Module.Version;
    public byte FormatVersion => Module.FormatVersion;

    public ModuleReflector(ORBTModule module)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        foreach (var record in module.Types)
        {
            var info = new TypeInfo(this, record);
            _typesByRecord[record] = info;
            _typesByName[info.Name] = info;
        }
    }

    public static ModuleReflector From(ORBTModule module) => new(module);

    /// <summary>All types declared in the module, in declaration order.</summary>
    public IReadOnlyList<TypeInfo> GetTypes() => _typesByRecord.Values.ToList();

    /// <summary>Gets a type by its (simple) name, or null when not declared in this module.</summary>
    public TypeInfo? GetType(string name) =>
        _typesByName.TryGetValue(name, out var type) ? type : null;

    /// <summary>Gets the reflective wrapper for a raw <see cref="TypeRecord"/> (reference-identity based).</summary>
    public TypeInfo? GetTypeInfo(TypeRecord record) =>
        _typesByRecord.TryGetValue(record, out var type) ? type : null;

    /// <summary>
    /// Resolves a method reference by qualified name ("Type.Method") — exact
    /// match first, then through the type's base chain. So
    /// <c>FindMethod("Derived.Method")</c> returns an inherited declaration
    /// when <c>Derived</c> doesn't declare it, and an override on a derived
    /// type shadows the base one.
    /// </summary>
    public MethodInfo? FindMethod(string qualifiedName)
    {
        int dot = qualifiedName.LastIndexOf('.');
        if (dot <= 0 || dot >= qualifiedName.Length - 1) return null;
        var type = GetType(qualifiedName[..dot]);
        return type?.FindMethod(qualifiedName[(dot + 1)..]);
    }

    /// <summary>Alias of <see cref="FindMethod(string)"/>.</summary>
    public MethodInfo? GetMethod(string qualifiedName) => FindMethod(qualifiedName);

    /// <summary>Every method declared by every type in the module (flat).</summary>
    public IEnumerable<MethodInfo> EnumerateMethods()
    {
        foreach (var type in _typesByRecord.Values)
            foreach (var method in type.GetDeclaredMethods())
                yield return method;
    }
}

/// <summary>
/// Reflection over a single type: its kind, flags, base type, interfaces,
/// declared/inherited methods and fields, and attributes. Method lookup is
/// inheritance-aware — the most-derived declaration of a name wins, like
/// <c>System.Type.GetMethod</c>.
/// </summary>
public sealed class TypeInfo
{
    private readonly ModuleReflector _reflector;
    private TypeInfo? _base;
    private bool _baseResolved;
    private IReadOnlyList<TypeInfo?>? _interfaces;

    internal TypeInfo(ModuleReflector reflector, TypeRecord record)
    {
        _reflector = reflector;
        Record = record;
        Name = reflector.Module.Resolve(record.NameIndex);
    }

    /// <summary>The raw module record backing this reflection view.</summary>
    public TypeRecord Record { get; }

    public string Name { get; }

    /// <summary>Qualified name — for methods, "Type.Method" keys on this.</summary>
    public string FullName => Name;

    public TypeKind Kind => Record.Kind;
    public MemberAccess Access => Record.Access;

    public bool IsClass => Kind == TypeKind.Class;
    public bool IsInterface => Kind == TypeKind.Interface;
    public bool IsStruct => Kind == TypeKind.Struct;
    public bool IsEnum => Kind == TypeKind.Enum;
    public bool IsAbstract => (Record.Flags & TypeFlags.Abstract) != 0;
    public bool IsSealed => (Record.Flags & TypeFlags.Sealed) != 0;

    /// <summary>
    /// The direct base type, or null when the type has no base or its base is
    /// declared outside this module (external bases are not indexed).
    /// </summary>
    public TypeInfo? BaseType
    {
        get
        {
            if (!_baseResolved)
            {
                _base = Record.BaseTypeIndex >= 0 && Record.BaseTypeIndex < _reflector.Module.Types.Count
                    ? _reflector.GetTypeInfo(_reflector.Module.Types[Record.BaseTypeIndex])
                    : null;
                _baseResolved = true;
            }
            return _base;
        }
    }

    /// <summary>
    /// Interfaces implemented by this type (directly), resolved by name.
    /// Entries are null when an interface is declared outside this module.
    /// </summary>
    public IReadOnlyList<TypeInfo?> Interfaces
    {
        get
        {
            if (_interfaces == null)
            {
                _interfaces = Record.InterfaceIndices
                    .Select(idx => _reflector.GetType(_reflector.Module.Resolve(idx)))
                    .ToList();
            }
            return _interfaces;
        }
    }

    // ── Methods ────────────────────────────────────────────────────

    /// <summary>Methods declared directly on this type (not inherited).</summary>
    public IReadOnlyList<MethodInfo> GetDeclaredMethods() =>
        Record.Methods.Select(m => new MethodInfo(_reflector, this, m)).ToList();

    /// <summary>Declared methods, then each base type's declared methods (most-derived first).</summary>
    public IReadOnlyList<MethodInfo> GetMethods()
    {
        var result = new List<MethodInfo>(GetDeclaredMethods());
        for (var baseType = BaseType; baseType != null; baseType = baseType.BaseType)
            result.AddRange(baseType.GetDeclaredMethods());
        return result;
    }

    /// <summary>Gets a method by name, walking the base chain when this type doesn't declare it (most-derived wins).</summary>
    public MethodInfo? GetMethod(string name) => FindMethod(name);

    /// <summary>Gets a method declared directly on this type, or null.</summary>
    public MethodInfo? GetDeclaredMethod(string name)
    {
        foreach (var record in Record.Methods)
            if (_reflector.Module.Resolve(record.NameIndex) == name)
                return new MethodInfo(_reflector, this, record);
        return null;
    }

    /// <summary>
    /// Finds a method by name through the inheritance chain: this type's own
    /// methods first, then each base type in turn. The most-derived declaration
    /// wins, so an override shadows the base method — matching
    /// <c>System.Type.GetMethod</c> semantics.
    /// </summary>
    public MethodInfo? FindMethod(string name)
    {
        for (var type = this; type != null; type = type.BaseType)
        {
            var method = type.GetDeclaredMethod(name);
            if (method != null) return method;
        }
        return null;
    }

    // ── Fields ─────────────────────────────────────────────────────

    /// <summary>Fields declared directly on this type.</summary>
    public IReadOnlyList<FieldInfo> GetDeclaredFields() =>
        Record.Fields.Select(f => new FieldInfo(_reflector, this, f)).ToList();

    /// <summary>Declared fields, then inherited fields from each base type.</summary>
    public IReadOnlyList<FieldInfo> GetFields()
    {
        var result = new List<FieldInfo>(GetDeclaredFields());
        for (var baseType = BaseType; baseType != null; baseType = baseType.BaseType)
            result.AddRange(baseType.GetDeclaredFields());
        return result;
    }

    /// <summary>Gets a field by name, walking the base chain when needed.</summary>
    public FieldInfo? GetField(string name)
    {
        for (var type = this; type != null; type = type.BaseType)
        {
            var field = type.GetDeclaredField(name);
            if (field != null) return field;
        }
        return null;
    }

    /// <summary>Gets a field declared directly on this type, or null.</summary>
    public FieldInfo? GetDeclaredField(string name)
    {
        foreach (var record in Record.Fields)
            if (_reflector.Module.Resolve(record.NameIndex) == name)
                return new FieldInfo(_reflector, this, record);
        return null;
    }

    // ── Attributes ─────────────────────────────────────────────────

    /// <summary>Attributes applied to this type in the module (e.g. <c>@Author("x")</c>).</summary>
    public IReadOnlyList<AttributeInfo> GetAttributes() =>
        Record.Attributes.Select(a => new AttributeInfo(_reflector, a)).ToList();

    // ── Hierarchy ──────────────────────────────────────────────────

    /// <summary>This type and all its bases, most-derived first (cycle-safe).</summary>
    public IReadOnlyList<TypeInfo> GetHierarchy()
    {
        var chain = new List<TypeInfo>();
        var seen = new HashSet<TypeInfo>();
        for (var type = this; type != null && seen.Add(type); type = type.BaseType)
            chain.Add(type);
        return chain;
    }

    /// <summary>True when this type inherits (directly or transitively) from <paramref name="other"/>.</summary>
    public bool IsSubclassOf(TypeInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var baseType = BaseType; baseType != null; baseType = baseType.BaseType)
            if (baseType == other) return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="other"/> is this type, a subclass of it, or
    /// (when this is an interface) an implementor of it — i.e. an instance of
    /// <paramref name="other"/> can be used where this type is expected.
    /// </summary>
    public bool IsAssignableFrom(TypeInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other == this) return true;
        if (other.IsSubclassOf(this)) return true;
        if (IsInterface)
            foreach (var iface in other.GetInterfaces())
                if (iface == this) return true;
        return false;
    }

    /// <summary>All interfaces implemented by this type, including inherited ones.</summary>
    public IReadOnlyList<TypeInfo> GetInterfaces()
    {
        var result = new List<TypeInfo>();
        var seen = new HashSet<TypeInfo>();
        for (var type = this; type != null; type = type.BaseType)
            foreach (var iface in type.Interfaces)
                if (iface != null && seen.Add(iface))
                    result.Add(iface);
        return result;
    }

    public override string ToString() => Name;

    public override bool Equals(object? obj) => obj is TypeInfo other && ReferenceEquals(Record, other.Record);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(Record);

    public static bool operator ==(TypeInfo? a, TypeInfo? b) =>
        ReferenceEquals(a, b) || (a is not null && a.Equals(b));
    public static bool operator !=(TypeInfo? a, TypeInfo? b) => !(a == b);
}

/// <summary>
/// Reflection over a single method and — importantly — a <em>method reference</em>:
/// it carries the declaring type, signature and attributes, resolves through
/// inheritance (<see cref="GetBaseDefinition"/>), and can be invoked against a
/// <see cref="Runtime"/>.
/// </summary>
public sealed class MethodInfo
{
    private readonly ModuleReflector _reflector;

    internal MethodInfo(ModuleReflector reflector, TypeInfo declaringType, MethodRecord record)
    {
        _reflector = reflector;
        DeclaringType = declaringType;
        Record = record;
        Name = reflector.Module.Resolve(record.NameIndex);
    }

    /// <summary>The raw module record backing this reflection view.</summary>
    public MethodRecord Record { get; }

    /// <summary>The type that declares this method (the base type for inherited lookups).</summary>
    public TypeInfo DeclaringType { get; }

    public string Name { get; }

    /// <summary>The "Type.Method" key used by <see cref="Runtime.CallMethod{T}"/> and the VM's function table.</summary>
    public string QualifiedName => $"{DeclaringType.Name}.{Name}";

    public MemberAccess Access => Record.Access;
    public bool IsStatic => (Record.Flags & MethodFlags.Static) != 0;
    public bool IsVirtual => (Record.Flags & MethodFlags.Virtual) != 0;
    public bool IsOverride => (Record.Flags & MethodFlags.Override) != 0;
    public bool IsAbstract => (Record.Flags & MethodFlags.Abstract) != 0;

    /// <summary>The declared return type name ("int32", "string", "void", ...).</summary>
    public string ReturnTypeName => _reflector.Module.Resolve(Record.SignatureIndex);

    /// <summary>Number of declared parameters (instance methods include 'this' as parameter 0).</summary>
    public int ParameterCount => Record.ParamCount;

    public IReadOnlyList<ParameterInfo> GetParameters() =>
        Record.Params.Select(p => new ParameterInfo(_reflector, p)).ToList();

    /// <summary>Attributes applied to this method in the module.</summary>
    public IReadOnlyList<AttributeInfo> GetAttributes() =>
        Record.Attributes.Select(a => new AttributeInfo(_reflector, a)).ToList();

    /// <summary>
    /// The base definition of this method: walks the base-type chain to the
    /// first (highest) declaration of a method with the same name. For a plain
    /// (non-override) method this is itself, so this mirrors
    /// <c>MethodInfo.GetBaseDefinition</c>.
    /// </summary>
    public MethodInfo? GetBaseDefinition()
    {
        if (!IsOverride) return this;
        MethodInfo? current = this;
        for (var baseType = DeclaringType.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            var declared = baseType.GetDeclaredMethod(Name);
            if (declared != null)
            {
                current = declared;
                if (!declared.IsOverride) break;
            }
        }
        return current;
    }

    /// <summary>
    /// Invokes this method on a runtime. For static methods pass
    /// <paramref name="receiver"/> as null; for instance methods it must be
    /// the object handle returned by a previous call (VM-internal objects
    /// round-trip through <c>CallMethod</c> as raw <c>uint</c> handles).
    /// </summary>
    public object? Invoke(Runtime runtime, object? receiver, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (IsStatic)
        {
            if (receiver != null)
                throw new ArgumentException($"Method '{QualifiedName}' is static and cannot be invoked with a receiver.");
            return runtime.CallMethod<object>(QualifiedName, args);
        }

        var all = new object?[args.Length + 1];
        all[0] = receiver;
        Array.Copy(args, 0, all, 1, args.Length);
        return runtime.CallMethod<object>(QualifiedName, all);
    }

    public override string ToString()
    {
        var parameters = string.Join(", ", GetParameters().Select(p => p.ToString()));
        return $"{ReturnTypeName} {QualifiedName}({parameters})";
    }
}

/// <summary>
/// Reflection over a single field. Note: the wire format's
/// <see cref="FieldRecord"/> carries no static flag, so <see cref="IsStatic"/>
/// is always false.
/// </summary>
public sealed class FieldInfo
{
    private readonly ModuleReflector _reflector;

    internal FieldInfo(ModuleReflector reflector, TypeInfo declaringType, FieldRecord record)
    {
        _reflector = reflector;
        DeclaringType = declaringType;
        Record = record;
        Name = reflector.Module.Resolve(record.NameIndex);
    }

    public FieldRecord Record { get; }
    public TypeInfo DeclaringType { get; }
    public string Name { get; }
    public string TypeName => _reflector.Module.Resolve(Record.TypeIndex);

    /// <summary>The "Type.field" key used by the VM's field table.</summary>
    public string QualifiedName => $"{DeclaringType.Name}.{Name}";

    /// <summary>Whether the field is static (carried by the module's field metadata).</summary>
    public bool IsStatic => Record.IsStatic;

    public override string ToString() => $"{TypeName} {QualifiedName}";
}

/// <summary>Reflection over a method parameter.</summary>
public sealed class ParameterInfo
{
    private readonly ModuleReflector _reflector;

    internal ParameterInfo(ModuleReflector reflector, ParameterRecord record)
    {
        _reflector = reflector;
        Record = record;
        Name = reflector.Module.Resolve(record.NameIndex);
    }

    public ParameterRecord Record { get; }
    public string Name { get; }
    public string TypeName => _reflector.Module.Resolve(Record.TypeIndex);

    public override string ToString() => $"{TypeName} {Name}";
}

/// <summary>Reflection over a module attribute annotation (e.g. <c>@Author("alice")</c>).</summary>
public sealed class AttributeInfo
{
    private readonly ModuleReflector _reflector;

    internal AttributeInfo(ModuleReflector reflector, AttributeRecord record)
    {
        _reflector = reflector;
        Record = record;
        Name = reflector.Module.Resolve(record.NameIndex);
        Arguments = record.ArgIndices.Select(reflector.Module.Resolve).ToList();
    }

    public AttributeRecord Record { get; }
    public string Name { get; }

    /// <summary>Attribute arguments as text (string literals are unquoted by the tokenizer).</summary>
    public IReadOnlyList<string> Arguments { get; }

    public override string ToString() =>
        Arguments.Count > 0 ? $"{Name}({string.Join(", ", Arguments)})" : Name;
}
