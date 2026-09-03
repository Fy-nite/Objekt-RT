using System.Globalization;
using ObjektRT.Core.Model;

namespace ObjectRT.VM;

/// <summary>
/// Compiles an ORBTModule (tooling representation) into a CompiledModule
/// (VM-friendly flat representation with resolved indices).
/// </summary>
public class ModuleCompiler
{
    // ── Resolution tables ──────────────────────────────────────────

    private readonly Dictionary<string, uint> _typeMap = new();
    private readonly Dictionary<string, uint> _fieldMap = new();
    private readonly List<ResolvedFunction> _resolvedFuncs = new();
    private readonly Dictionary<string, uint> _funcMap = new();

    /// <summary>
    /// For each type (in <c>src.Types</c> order), the resolved-function index
    /// where its method block starts. Filled by <see cref="BuildResolutionTables"/>
    /// (methods are appended contiguously per type), so a type's methods occupy
    /// <c>[_typeMethodStart[i], _typeMethodStart[i] + MethodCount)</c> in the flat
    /// function table. This is the single source of truth for
    /// <c>VMType.MethodOffset</c> — a name-keyed map cannot be used here because
    /// overloaded methods (e.g. three constructors) share the same <c>FullName</c>
    /// and last-wins under a dictionary.
    /// </summary>
    private readonly List<uint> _typeMethodStart = new();

    private struct ResolvedFunction
    {
        public string FullName;
        public uint OldMethodIdx;
        public uint NewIndex;
        public int TypeIdx;
        public int MethodIdx;
    }

    // ── Per-function compilation state ─────────────────────────────

    private class CompileState
    {
        public List<byte> Code { get; } = new();
        public uint MaxStackDepth;
        public uint CurrentDepth;
        public string? Error;

        public void Reset()
        {
            Code.Clear();
            MaxStackDepth = 0;
            CurrentDepth = 0;
            Error = null;
        }
    }

    // ── Public API ─────────────────────────────────────────────────

    public Result<CompiledModule> Compile(ORBTModule src)
    {
        var mod = new CompiledModule();

        // Materialize generic instantiations (Box<int>) from their @Generic
        // definitions, lazily, at first use. This mutates src: concrete
        // specialized classes are appended before the resolution tables are
        // built, so everything below treats them as ordinary types.
        MaterializeReferencedGenerics(src);

        BuildResolutionTables(src);

        // 2. Compile types
        mod.Types.Capacity = src.Types.Count;
        mod.Fields.Capacity = Math.Max(_fieldMap.Count, 1);
        mod.Functions.Capacity = Math.Max(_resolvedFuncs.Count, 1);
        mod.FunctionMap.Clear();

        uint fieldIdx = 0;

        // Inherited fields must live at the START of a derived instance, so a
        // base method reading `Animal::name` on a Dog finds the same slot the
        // Dog's constructor wrote. InstanceSize accumulates base sizes and a
        // type's own fields start at its base's total size. Bases may appear
        // AFTER their derived types in the list, so compute sizes up front with
        // memoized recursion over BaseTypeIndex.
        var ownOffset = new uint[src.Types.Count];
        var sizeMemo = new int[src.Types.Count];
        Array.Fill(sizeMemo, -1);

        uint TypeInstanceSize(int typeIdx)
        {
            if (sizeMemo[typeIdx] >= 0) return (uint)sizeMemo[typeIdx];
            var t = src.Types[typeIdx];
            uint size = 0;
            if (t.BaseTypeIndex >= 0 && t.BaseTypeIndex < src.Types.Count)
                size = TypeInstanceSize(t.BaseTypeIndex);
            ownOffset[typeIdx] = size;
            size += (uint)t.FieldCount * VmConstants.FieldSlotSize;
            sizeMemo[typeIdx] = (int)size;
            return size;
        }
        for (int i = 0; i < src.Types.Count; i++)
            TypeInstanceSize(i);

        for (int typeIdx = 0; typeIdx < src.Types.Count; typeIdx++)
        {
            var srcType = src.Types[typeIdx];
            // A @Generic definition is a template: its real fields/methods only
            // exist on the materialized clones later in src.Types (the templates
            // themselves are emitted with no fields/methods, matching
            // BuildResolutionTables which registers nothing for them). Zeroing
            // MethodCount keeps BuildVTables from walking a method block that
            // was never appended, and zeroing FieldCount/InstanceSize keeps the
            // template from being instantiable (it never is).
            bool genericTemplate = TryGetGenericParams(src, srcType, out _);
            var vmt = new VMType
            {
                DebugName = src.Resolve(srcType.NameIndex),
                Kind = (VMTypeKind)(byte)srcType.Kind,
                BaseType = srcType.BaseTypeIndex,
                FieldOffset = fieldIdx,
                FieldCount = genericTemplate ? 0u : srcType.FieldCount,
                MethodCount = genericTemplate ? 0u : srcType.MethodCount,
                InstanceSize = genericTemplate ? 0 : (uint)sizeMemo[typeIdx],
            };

            if (genericTemplate)
            {
                mod.Types.Add(vmt);
                continue;
            }

            if (srcType.FieldCount > 0)
            {
                var fieldTypes = new string[srcType.Fields.Count];
                for (int i = 0; i < srcType.Fields.Count; i++)
                    fieldTypes[i] = src.Resolve(srcType.Fields[i].TypeIndex);
                vmt.FieldTypeNames = fieldTypes;
            }

            // Interface names survive into the compiled module so virtual
            // dispatch can relate receivers to the call's named type through
            // `implements` even when the base chain misses it.
            if (srcType.InterfaceIndices is { Count: > 0 })
            {
                vmt.InterfaceNames = srcType.InterfaceIndices
                    .Select(ix => src.Resolve(ix))
                    .ToArray();
            }

            // Find method offset in the function table: methods of each type
            // occupy a contiguous block in _resolvedFuncs, starting at the
            // index recorded during BuildResolutionTables.
            vmt.MethodOffset = typeIdx < _typeMethodStart.Count ? _typeMethodStart[typeIdx] : 0;

            mod.Types.Add(vmt);

            // Collect fields — offsets are relative to the START of the heap
            // buffer, so a derived type's own fields sit after its base's.
            for (int fi = 0; fi < srcType.Fields.Count; fi++)
            {
                var srcField = srcType.Fields[fi];
                var vmf = new VMField
                {
                    DebugName = src.Resolve(srcField.NameIndex),
                    TypeIndex = 0,
                    Offset = ownOffset[typeIdx] + (uint)fi * VmConstants.FieldSlotSize,
                };
                mod.Fields.Add(vmf);
                fieldIdx++;
            }
        }

        // 3. Compile functions
        for (int rfi = 0; rfi < _resolvedFuncs.Count; rfi++)
        {
            var rf = _resolvedFuncs[rfi];
            bool found = false;

            if (rf.TypeIdx >= 0 && rf.TypeIdx < src.Types.Count
                && rf.MethodIdx >= 0 && rf.MethodIdx < src.Types[rf.TypeIdx].Methods.Count)
            {
                var srcType = src.Types[rf.TypeIdx];
                var srcMethod = srcType.Methods[rf.MethodIdx];
                var cfResult = CompileMethod(src, srcType, srcMethod, rf.FullName);
                if (cfResult.IsError)
                    return new VmError(VmErrorKind.UnresolvedField,
                        $"compilation of '{rf.FullName}' failed: {cfResult.Error.Message}");

                var cf = cfResult.Value;
                cf.SelfIndex = rf.NewIndex;
                mod.Functions.Add(cf);
                mod.FunctionMap[rf.FullName] = rf.NewIndex;
                found = true;

                // Record a constructor overload so `new T(...)` can dispatch by
                // arg count (overloaded ctors share the `..ctor` full-name in
                // FunctionMap, which is last-wins).
                if (rf.FullName.EndsWith("..ctor", StringComparison.Ordinal))
                {
                    string typeName = rf.FullName[..^"..ctor".Length];
                    var overload = (Func: rf.NewIndex, ArgCount: (uint)srcMethod.ParamCount);
                    if (mod.CtorOverloads.TryGetValue(typeName, out var existing))
                    {
                        Array.Resize(ref existing, existing.Length + 1);
                        existing[^1] = overload;
                    }
                    else
                    {
                        existing = new[] { overload };
                    }
                    mod.CtorOverloads[typeName] = existing;
                }
            }

            if (!found)
            {
                // Fallback — empty function that just returns
                var cf = new CompiledFunction
                {
                    DebugName = rf.FullName,
                    SelfIndex = rf.NewIndex,
                    Code = new byte[] { (byte)Opcode.Ret },
                };
                mod.Functions.Add(cf);
                mod.FunctionMap[rf.FullName] = rf.NewIndex;
            }
        }

        // 4. Copy string table
        mod.Strings = new List<string>(src.StringPool.Strings);

        // 5. Set entry point — look for any "*.Main" function
        string entryName = $"{src.ModuleName}.Main";
        if (mod.FunctionMap.TryGetValue(entryName, out var entryIdx))
            mod.EntryFunction = entryIdx;
        else if (mod.FunctionMap.TryGetValue("Main", out entryIdx))
            mod.EntryFunction = entryIdx;
        else {
            // Search for any function named "*.Main"
            foreach (var kv in mod.FunctionMap)
            {
                var name = kv.Key;
                if (name.EndsWith(".Main") || name == "Main")
                {
                    mod.EntryFunction = kv.Value;
                    goto found;
                }
            }
            // Fallback: first function
            if (mod.Functions.Count > 0)
                mod.EntryFunction = 0;
            found:;
        }

        // Copy the field name → index map for static-field reflection.
        mod.FieldMap = new Dictionary<string, uint>(_fieldMap);

        // Build the O(1) type-name → index map (must happen after Types are populated).
        mod.BuildTypeNameMap();

        // Build per-type vtables for O(1) virtual dispatch.
        mod.BuildVTables();

        return mod;
    }

    // ── Resolution table builder ───────────────────────────────────

    private void BuildResolutionTables(ORBTModule src)
    {
        _typeMap.Clear();
        _fieldMap.Clear();
        _resolvedFuncs.Clear();
        _funcMap.Clear();
        _typeMethodStart.Clear();

        foreach (var type in src.Types)
        {
            string tname = src.Resolve(type.NameIndex);
            int typeIdx = _typeMap.Count;
            _typeMap[tname] = (uint)typeIdx;

            // A @Generic definition is a template: its method bodies reference
            // the raw type params (e.g. `isinst T`), which cannot be compiled
            // standalone. Only the materialized instantiations (which carry
            // concrete type args instead) are callable. Register the TYPE so
            // indices stay aligned with src.Types, but declare no fields or
            // methods for the template itself.
            if (TryGetGenericParams(src, type, out _))
            {
                _typeMethodStart.Add((uint)_resolvedFuncs.Count);
                continue;
            }

            _typeMethodStart.Add((uint)_resolvedFuncs.Count);

            foreach (var field in type.Fields)
            {
                string fname = FieldFullName(src, type, field);
                _fieldMap[fname] = (uint)_fieldMap.Count;
            }

            for (int mi = 0; mi < type.Methods.Count; mi++)
            {
                var method = type.Methods[mi];
                var rf = new ResolvedFunction
                {
                    FullName = MethodFullName(src, type, method),
                    OldMethodIdx = (uint)_resolvedFuncs.Count,
                    NewIndex = (uint)_resolvedFuncs.Count,
                    TypeIdx = typeIdx,
                    MethodIdx = mi,
                };
                _funcMap[rf.FullName] = rf.NewIndex;
                _resolvedFuncs.Add(rf);
            }
        }
    }

    // ── Generic materialization (@Generic, lazy, at first use) ───────
    //
    // A generic contract (contract Box<T>) is emitted as a "definition": the
    // class keeps its name, carries an @Generic(T, ...) attribute listing its
    // type parameters, and its body references those parameters literally
    // (field value: T, method get() -> T). Call sites reference concrete
    // instantiations (newobj Box<int>, call Box<int>.get() -> int).
    //
    // At compile time we materialize each referenced instantiation by cloning
    // the definition and substituting the type parameters (and the class's own
    // name) in every type position, re-interning the resulting strings. This
    // happens lazily — only instantiations the code actually references are
    // materialized — and mirrors how the compiler itself resolves the
    // signatures: Box<int> becomes a real, specialized class with its own
    // fields, methods, and function-table entries.

    /// <summary>A @Generic definition: the template TypeRecord plus its type-parameter names.</summary>
    private sealed class GenericDef
    {
        public TypeRecord Type;
        public string[] Params;
        public GenericDef(TypeRecord type, string[] pars)
        {
            Type = type;
            Params = pars;
        }
    }

    /// <summary>Names already materialized in this compilation (or present in the module).</summary>
    private readonly HashSet<string> _materializedNames = new(StringComparer.Ordinal);

    private void MaterializeReferencedGenerics(ORBTModule src)
    {
        _materializedNames.Clear();

        // Collect @Generic definitions (the template types).
        var defs = new Dictionary<string, GenericDef>(StringComparer.Ordinal);
        foreach (var t in src.Types)
        {
            if (TryGetGenericParams(src, t, out var pars))
                defs[src.Resolve(t.NameIndex)] = new GenericDef(t, pars);
        }
        if (defs.Count == 0) return;

        // Worklist of concrete instantiations to materialize.
        var queue = new Queue<(GenericDef Def, string Name)>();
        foreach (var t in src.Types)
            ScanForGenericRefs(src, t, defs, queue);

        while (queue.Count > 0)
        {
            var (def, name) = queue.Dequeue();
            if (_materializedNames.Contains(name)) continue;
            if (src.Types.Any(t => src.Resolve(t.NameIndex) == name)) continue; // already present
            _materializedNames.Add(name);
            var clone = Materialize(src, def, name);
            // The clone may reference further instantiations (nested generics).
            ScanForGenericRefs(src, clone, defs, queue);
        }
    }

    /// <summary>Reads the @Generic(T, U, ...) attribute args, if present.</summary>
    private static bool TryGetGenericParams(ORBTModule src, TypeRecord t, out string[] pars)
    {
        foreach (var attr in t.Attributes)
        {
            if (src.Resolve(attr.NameIndex).Equals("Generic", StringComparison.Ordinal))
            {
                pars = attr.ArgIndices.Select(src.Resolve).ToArray();
                return true;
            }
        }
        pars = Array.Empty<string>();
        return false;
    }

    /// <summary>Scans a type's methods for references to generic instantiations and queues them.</summary>
    private static void ScanForGenericRefs(ORBTModule src, TypeRecord t, Dictionary<string, GenericDef> defs, Queue<(GenericDef, string)> queue)
    {
        foreach (var m in t.Methods)
        {
            var instrs = EnsureDecoded(src, m);
            foreach (var instr in instrs)
            {
                switch (instr.Operand)
                {
                    // newobj Box<int> / newarr element types.
                    case OperandString os when instr.Opcode is Opcode.Newobj or Opcode.Newarr:
                        EnqueueGenericRef(src, defs, src.Resolve(os.StringIndex), queue);
                        break;
                    // castclass / isinst / conv Box<int>.
                    case OperandTypeRef ot:
                        EnqueueGenericRef(src, defs, src.Resolve(ot.StringIndex), queue);
                        break;
                    // ldfld Box<int>::value — the declaring type precedes "::".
                    case OperandFieldRef of:
                        EnqueueGenericRef(src, defs, DeclaringTypeOf(src.Resolve(of.StringIndex), true), queue);
                        break;
                    // call Box<int>.get / Box<int>..ctor — the declaring type precedes ".".
                    case OperandNativeCall nc:
                        EnqueueGenericRef(src, defs, DeclaringTypeOf(src.Resolve(nc.StringIndex), false), queue);
                        break;
                }
            }
        }
    }

    /// <summary>Queues a materialization when the reference names a known generic instantiation.</summary>
    private static void EnqueueGenericRef(ORBTModule src, Dictionary<string, GenericDef> defs, string typeName, Queue<(GenericDef, string)> queue)
    {
        if (!TrySplitGeneric(typeName, out var baseName, out var args)) return;
        if (!defs.TryGetValue(baseName, out var def)) return;
        if (args.Length != def.Params.Length) return;   // malformed — normal resolution will error
        queue.Enqueue((def, typeName));
    }

    /// <summary>Splits "Box&lt;int&gt;" into ("Box", ["int"]). Args may carry [] suffixes.</summary>
    private static bool TrySplitGeneric(string s, out string baseName, out string[] args)
    {
        baseName = s;
        args = Array.Empty<string>();
        int lt = s.IndexOf('<');
        if (lt <= 0 || s[^1] != '>') return false;
        baseName = s[..lt];
        args = SplitTopLevelArgs(s[(lt + 1)..^1]);
        return true;
    }

    private static string[] SplitTopLevelArgs(string inner)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        foreach (var ch in inner)
        {
            switch (ch)
            {
                case '<': depth++; break;
                case '>': depth = Math.Max(0, depth - 1); break;
                case ',' when depth == 0:
                    parts.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) parts.Add(sb.ToString().Trim());
        return parts.Where(p => p.Length > 0).ToArray();
    }

    /// <summary>The declaring type of a qualified reference: "Box::field" or
    /// "Box.method" / "Box..ctor". The member sits after the LAST separator, so
    /// namespaced or materialized generic declaring types (""thing.List&lt;uint8&gt;.Get"",
    /// ""ns.Box&lt;int32&gt;..ctor"") are recovered whole rather than truncated
    /// at the first dot.</summary>
    private static string DeclaringTypeOf(string refStr, bool fieldRef)
    {
        if (fieldRef)
        {
            int sc = refStr.IndexOf("::", StringComparison.Ordinal);
            if (sc > 0) return refStr[..sc];
        }
        int idx = refStr.LastIndexOf('.');
        if (idx <= 0) return refStr;
        // "ns.Type..ctor" — the ".ctor" member follows "..", so the declaring
        // type ends right before the two dots.
        if (refStr[idx - 1] == '.')
            return refStr[..(idx - 1)];
        return refStr[..idx];
    }

    /// <summary>Decodes a method's raw bytecode into Instructions, caching on the record.</summary>
    private static List<Instruction> EnsureDecoded(ORBTModule src, MethodRecord m)
    {
        if (m.Instructions.Count == 0 && m.RawInstructionData.Length > 0)
        {
            try
            {
                m.Instructions = DecodeRawBytecode(m.RawInstructionData, src);
            }
            catch
            {
                // Leave empty; the compile loop will fall back to passthrough.
            }
        }
        return m.Instructions;
    }

    /// <summary>
    /// Clones a @Generic definition for the concrete instantiation
    /// <paramref name="name"/> (e.g. "Box&lt;int&gt;"), substituting the type
    /// parameters (and the class's own name) in every type position, and
    /// appends the specialized class to the module.
    /// </summary>
    private TypeRecord Materialize(ORBTModule src, GenericDef def, string name)
    {
        string defName = src.Resolve(def.Type.NameIndex);
        if (!TrySplitGeneric(name, out _, out var args) || args.Length != def.Params.Length)
            return def.Type;   // malformed instantiation — leave the module untouched

        var paramMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < def.Params.Length; i++)
            paramMap[def.Params[i]] = args[i];

        string Substitute(string s) => SubstituteTypeText(s, paramMap, defName, name);

        var clone = new TypeRecord
        {
            Kind = def.Type.Kind,
            Access = def.Type.Access,
            Flags = def.Type.Flags,
            BaseTypeIndex = def.Type.BaseTypeIndex,
            NamespaceIndex = def.Type.NamespaceIndex,
            NameIndex = src.StringPool.Add(name),
        };
        clone.InterfaceIndices.AddRange(def.Type.InterfaceIndices);
        clone.InterfaceCount = def.Type.InterfaceCount;

        // Copy attributes except @Generic — the clone is a concrete class, not
        // a template (otherwise it would be treated as a definition again).
        foreach (var attr in def.Type.Attributes)
        {
            if (src.Resolve(attr.NameIndex).Equals("Generic", StringComparison.Ordinal)) continue;
            clone.Attributes.Add(attr);
        }

        foreach (var f in def.Type.Fields)
        {
            clone.Fields.Add(new FieldRecord(
                f.NameIndex,
                src.StringPool.Add(Substitute(src.Resolve(f.TypeIndex))),
                f.IsStatic));
            clone.FieldCount++;
        }

        foreach (var m in def.Type.Methods)
        {
            var cm = new MethodRecord
            {
                Access = m.Access,
                Flags = m.Flags,
                NameIndex = m.NameIndex,
                ParamCount = m.ParamCount,
                LocalCount = m.LocalCount,
                LabelCount = m.LabelCount,
                SignatureIndex = src.StringPool.Add(Substitute(src.Resolve(m.SignatureIndex))),
            };
            foreach (var p in m.Params)
                cm.Params.Add(new ParameterRecord(p.NameIndex, src.StringPool.Add(Substitute(src.Resolve(p.TypeIndex)))));
            foreach (var l in m.Locals)
                cm.Locals.Add(new LocalRecord(l.NameIndex, src.StringPool.Add(Substitute(src.Resolve(l.TypeIndex)))));
            cm.Attributes.AddRange(m.Attributes);
            cm.LineMappings.AddRange(m.LineMappings);

            // Instructions: substitute type-bearing operand strings (field refs,
            // call refs, newobj/castclass/isinst type refs). Literal strings
            // (ldstr) and indices are left untouched.
            foreach (var inst in EnsureDecoded(src, m))
            {
                Operand newOperand = inst.Operand switch
                {
                    OperandFieldRef of => new OperandFieldRef(src.StringPool.Add(Substitute(src.Resolve(of.StringIndex)))),
                    OperandNativeCall nc => new OperandNativeCall(src.StringPool.Add(Substitute(src.Resolve(nc.StringIndex))), nc.ParamCount),
                    OperandTypeRef ot => new OperandTypeRef(src.StringPool.Add(Substitute(src.Resolve(ot.StringIndex)))),
                    OperandString os when inst.Opcode is Opcode.Newobj or Opcode.Newarr
                        => new OperandString(src.StringPool.Add(Substitute(src.Resolve(os.StringIndex)))),
                    // ldstr: the compiler emits the target type name as a literal
                    // string for primitive `as T` / `is T` (TypeHelper.CastOrNull).
                    // Substitute only when the whole literal is exactly a type
                    // parameter name, so genuine string data is left untouched.
                    OperandString os when inst.Opcode == Opcode.Ldstr =>
                        new OperandString(src.StringPool.Add(SubstituteTypeNameLiteral(src.Resolve(os.StringIndex), paramMap))),
                    _ => inst.Operand,
                };
                cm.Instructions.Add(inst with { Operand = newOperand });
            }

            clone.Methods.Add(cm);
            clone.MethodCount++;
        }

        src.Types.Add(clone);
        return clone;
    }

    /// <summary>
    /// String-level type substitution for a materialized clone: type parameters
    /// (word-boundary aware) and the definition's own name (when it appears as a
    /// qualified declaring type: "Box::field", "Box.method", or exactly "Box").
    /// </summary>
    private static string SubstituteTypeText(string s, IReadOnlyDictionary<string, string> paramMap, string defName, string materializedName)
    {
        foreach (var (param, arg) in paramMap)
            s = ReplaceBoundary(s, param, arg);
        s = ReplaceQualified(s, defName, materializedName);
        return s;
    }

    /// <summary>
    /// Substitutes a type-parameter name in a literal string (a primitive
    /// `as T` / `is T` casts the target name as an ldstr for CastOrNull), but
    /// only when the entire literal is exactly one type parameter name —
    /// real user string data never matches a whole type-parameter word on its
    /// own, so it is left untouched.
    /// </summary>
    private static string SubstituteTypeNameLiteral(string s, IReadOnlyDictionary<string, string> paramMap)
    {
        if (paramMap.TryGetValue(s, out var arg)) return arg;
        return s;
    }

    /// <summary>Replaces <paramref name="from"/> with <paramref name="to"/> when not adjacent to a name character.</summary>
    private static string ReplaceBoundary(string s, string from, string to)
    {
        if (from.Length == 0 || s.Length < from.Length) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            int idx = s.IndexOf(from, i, StringComparison.Ordinal);
            if (idx < 0) { sb.Append(s, i, s.Length - i); break; }
            bool leftOk = idx == 0 || !IsNameChar(s[idx - 1]);
            int end = idx + from.Length;
            bool rightOk = end == s.Length || !IsNameChar(s[end]);
            if (leftOk && rightOk)
            {
                sb.Append(s, i, idx - i);
                sb.Append(to);
                i = end;
            }
            else
            {
                sb.Append(s, i, end - i);
                i = end;
            }
        }
        return sb.ToString();
    }

    /// <summary>Replaces the definition name when it appears as a qualified declaring type.</summary>
    private static string ReplaceQualified(string s, string defName, string materializedName)
    {
        if (defName.Length == 0) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            int idx = s.IndexOf(defName, i, StringComparison.Ordinal);
            if (idx < 0) { sb.Append(s, i, s.Length - i); break; }
            int end = idx + defName.Length;
            // Only replace when followed by '.', ':', or end-of-string — so
            // "Box" inside "Box<int>" (already materialized) is left alone.
            bool followed = end < s.Length
                ? s[end] == '.' || s[end] == ':'
                : true;
            bool leftOk = idx == 0 || !IsNameChar(s[idx - 1]);
            if (followed && leftOk)
            {
                sb.Append(s, i, idx - i);
                sb.Append(materializedName);
                i = end;
            }
            else
            {
                sb.Append(s, i, end - i);
                i = end;
            }
        }
        return sb.ToString();
    }

    private static bool IsNameChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '`';

    // ── Name helpers ───────────────────────────────────────────────

    private static string MethodFullName(ORBTModule src, TypeRecord type, MethodRecord method)
        => $"{src.Resolve(type.NameIndex)}.{src.Resolve(method.NameIndex)}";

    private static string FieldFullName(ORBTModule src, TypeRecord type, FieldRecord field)
        => $"{src.Resolve(type.NameIndex)}.{src.Resolve(field.NameIndex)}";

    /// <summary>
    /// Resolves a field reference string (as stored in bytecode) to its index
    /// in the compiled module's field table. The wire stores field refs using
    /// the declaring type as the source spelled it, which is sometimes short or
    /// namespace-relative ("option.Value.v", "std.Generics.Result.__tag") while
    /// the field table is keyed by the fully-qualified name. Fall back to a
    /// suffix match so those still resolve.
    /// </summary>
    private bool TryResolveFieldIndex(string fieldRef, out uint index)
    {
        if (_fieldMap.TryGetValue(fieldRef, out index)) return true;
        // Fall back to a fully-qualified key that ends with this (possibly
        // short/relative) ref — e.g. "ObjektRT.std.Generics.option.Value.v"
        // for "option.Value.v".
        var withDotField = "." + fieldRef;
        var withColonField = "::" + fieldRef;
        foreach (var kv in _fieldMap)
        {
            if (kv.Key.EndsWith(withDotField, StringComparison.Ordinal)
                || kv.Key.EndsWith(withColonField, StringComparison.Ordinal))
            {
                index = kv.Value;
                return true;
            }
        }
        return false;
    }

    // ── Compile a single method ────────────────────────────────────

    private Result<CompiledFunction> CompileMethod(ORBTModule src, TypeRecord type, MethodRecord method, string fullName)
    {
        // Build name→position mapping for args and locals
        var argNameToPos = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (ushort pi = 0; pi < method.Params.Count; pi++)
        {
            string pname = src.Resolve(method.Params[pi].NameIndex);
            argNameToPos[pname] = pi;
        }
        var localNameToPos = new Dictionary<string, ushort>(StringComparer.Ordinal);
        for (ushort li = 0; li < method.Locals.Count; li++)
        {
            string lname = src.Resolve(method.Locals[li].NameIndex);
            localNameToPos[lname] = li;
        }
        var func = new CompiledFunction
        {
            DebugName = fullName,
            NumParams = method.ParamCount,
            NumLocals = method.LocalCount,
            MaxStack = 0,
        };
        func.SourceMap = method.LineMappings;

        // Interop metadata: wire type names for the params and the return
        // (SignatureIndex holds the return type name), so the interpreter can
        // marshal structs and primitive widths across the native boundary.
        if (method.Params.Count > 0)
            func.ParamTypeNames = method.Params.Select(p => src.Resolve(p.TypeIndex)).ToArray();
        if (method.SignatureIndex < src.StringPool.Count)
            func.ReturnTypeName = src.Resolve(method.SignatureIndex);
        if (method.Params.Count > 0)
            func.ParamNames = method.Params.Select(p => src.Resolve(p.NameIndex)).ToArray();
        if (method.Locals.Count > 0)
            func.LocalNames = method.Locals.Select(l => src.Resolve(l.NameIndex)).ToArray();

        var state = new CompileState();

        // If we have raw bytecode but no decoded instructions, decode them now
        // so we can resolve references (call, ldfld, etc.) properly.
        if (method.Instructions.Count == 0 && method.RawInstructionData.Length > 0)
        {
            // Decode raw bytecode into Instruction structs using BinaryStream
            try
            {
                var decoded = DecodeRawBytecode(method.RawInstructionData, src);
                if (decoded.Count > 0)
                {
                    method.Instructions = decoded;
                }
                else
                {
                    // Passthrough as-is (no references to resolve)
                    func.Code = method.RawInstructionData;
                    func.MaxStack = 8;
                    return func;
                }
            }
            catch
            {
                // Passthrough on decode failure
                func.Code = method.RawInstructionData;
                func.MaxStack = 8;
                return func;
            }
        }

        if (method.Instructions.Count == 0)
        {
            func.Code = new byte[] { (byte)Opcode.Ret };
            return func;
        }

        // ── Pass 1: compute new PC for each instruction ──────────────
        var newPcByIdx = new List<uint>(method.Instructions.Count);
        uint newPc = 0;
        foreach (var instr in method.Instructions)
        {
            newPcByIdx.Add(newPc);
            newPc += GetOpcodeByteCount(instr.Opcode) + ComputeOperandSize(instr);
        }
        uint totalNewSize = newPc;

        // ── Pass 2: emit bytecode ────────────────────────────────────
        state.Code.Capacity = (int)totalNewSize;

        for (int i = 0; i < method.Instructions.Count; i++)
        {
            var instr = method.Instructions[i];

            // Emit opcode (variable-length for extension tables)
            EmitOpcode(state, (ushort)instr.Opcode);

            // Stack depth tracking
            switch (instr.Opcode)
            {
                case Opcode.LdcI4: case Opcode.LdcI8:
                case Opcode.LdcR4: case Opcode.LdcR8:
                case Opcode.Ldc: case Opcode.Ldstr:
                case Opcode.Ldarg: case Opcode.Ldloc:
                case Opcode.Ldfld: case Opcode.Ldsfld:
                case Opcode.Ldnull: case Opcode.Dup:
                case Opcode.Newobj: case Opcode.Newarr:
                    state.CurrentDepth++;
                    break;

                case Opcode.Starg: case Opcode.Stloc:
                case Opcode.Stfld: case Opcode.Stsfld:
                case Opcode.Pop: case Opcode.Ret:
                case Opcode.Throw:
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
                case Opcode.Div: case Opcode.Rem:
                case Opcode.Ceq: case Opcode.Cne:
                case Opcode.Cgt: case Opcode.Cge:
                case Opcode.Clt: case Opcode.Cle:
                case Opcode.And: case Opcode.Xor: case Opcode.Or: case Opcode.Shl:
                    // pop 2, push 1 → net -1
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.Ldelem:
                    // pop 2, push 1 → net -1
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.Stelem:
                    // pop 3, push 0 → net -3
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    break;

                case Opcode.StptrI4: case Opcode.StptrI8:
                case Opcode.StptrR4: case Opcode.StptrR8:
                case Opcode.PtrFree:
                    // pop 2 (value + pointer) or 1 (pointer) → net -2 / -1
                    if (state.CurrentDepth > 0) state.CurrentDepth--;
                    if ((Opcode)instr.Opcode != Opcode.PtrFree && state.CurrentDepth > 0)
                        state.CurrentDepth--;
                    break;

                case Opcode.PtrAlloc:
                    // pop 2 (count,size), push 1 → net -1
                    if (state.CurrentDepth > 1) state.CurrentDepth--;
                    break;

                case Opcode.Neg: case Opcode.Not:
                case Opcode.Call: case Opcode.Callvirt:
                case Opcode.Ldlen: // pop 1, push 1 → net 0
                case Opcode.Ldptr: case Opcode.LdptrI8:
                case Opcode.LdptrR4: case Opcode.LdptrR8:
                case Opcode.PtrAddr: case Opcode.PtrLen: // pop 1, push 1 → net 0
                    // neutral for now
                    break;
            }

            if (state.CurrentDepth > state.MaxStackDepth)
                state.MaxStackDepth = state.CurrentDepth;

            // ── Emit operand ─────────────────────────────────────────
            switch (instr.Opcode)
            {
                case Opcode.Nop: case Opcode.Add: case Opcode.Sub:
                case Opcode.Mul: case Opcode.Div: case Opcode.Rem:
                case Opcode.Neg: case Opcode.Ceq: case Opcode.Cne:
                case Opcode.Cgt: case Opcode.Cge: case Opcode.Clt:
                case Opcode.Cle: case Opcode.And: case Opcode.Xor:
                case Opcode.Or: case Opcode.Shl: case Opcode.Not: case Opcode.Dup:
                case Opcode.Pop: case Opcode.Ldnull: case Opcode.Ret:
                case Opcode.Break: case Opcode.Continue:
                case Opcode.Throw: case Opcode.Ldelem: case Opcode.Stelem:
                case Opcode.Ldptr: case Opcode.LdptrI8:
                case Opcode.LdptrR4: case Opcode.LdptrR8:
                case Opcode.StptrI4: case Opcode.StptrI8:
                case Opcode.StptrR4: case Opcode.StptrR8:
                case Opcode.PtrAddr: case Opcode.PtrLen:
                case Opcode.PtrAlloc: case Opcode.PtrFree:
                    break;

                case Opcode.LdcI4:
                case Opcode.Ldc:
                    EmitI32(state, ((OperandI4)instr.Operand).Value);
                    break;

                case Opcode.LdcI8:
                    EmitI64(state, ((OperandI8)instr.Operand).Value);
                    break;

                case Opcode.LdcR4:
                    EmitF32(state, ((OperandR4)instr.Operand).Value);
                    break;

                case Opcode.LdcR8:
                    EmitF64(state, ((OperandR8)instr.Operand).Value);
                    break;

                case Opcode.Ldstr:
                    EmitU16(state, ((OperandString)instr.Operand).StringIndex);
                    break;

                case Opcode.Ldarg: case Opcode.Starg:
                {
                    ushort rawIdx = ((OperandIndex)instr.Operand).Index;
                    // If the index is within the string pool, it might be a name — resolve to positional index
                    if (rawIdx < src.StringPool.Count)
                    {
                        string name = src.Resolve(rawIdx);
                        if (argNameToPos.TryGetValue(name, out var pos))
                            rawIdx = pos;
                    }
                    EmitU16(state, rawIdx);
                    break;
                }
                case Opcode.Ldloc: case Opcode.Stloc:
                {
                    ushort rawIdx = ((OperandIndex)instr.Operand).Index;
                    if (rawIdx < src.StringPool.Count)
                    {
                        string name = src.Resolve(rawIdx);
                        if (localNameToPos.TryGetValue(name, out var pos))
                            rawIdx = pos;
                    }
                    EmitU16(state, rawIdx);
                    break;
                }

                case Opcode.Ldfld: case Opcode.Stfld:
                case Opcode.Ldsfld: case Opcode.Stsfld:
                {
                    string fieldRef = src.Resolve(((OperandFieldRef)instr.Operand).StringIndex);
                    if (TryResolveFieldIndex(fieldRef, out var fi))
                        EmitU16(state, (ushort)fi);
                    else
                    {
                        state.Error ??= $"Unresolved field '{fieldRef}' in {fullName}";
                        EmitU16(state, 0);
                    }
                    break;
                }

                case Opcode.Call: case Opcode.Callvirt:
                case Opcode.NativeCall:
                {
                    // Method name string index + parameter count. Resolution is
                    // deferred to runtime: module function first, then the host
                    // native dispatch (ClrNativeResolver / InterfaceHostResolver).
                    var nc = (OperandNativeCall)instr.Operand;
                    EmitU16(state, nc.StringIndex);
                    EmitU16(state, nc.ParamCount);
                    break;
                }

                case Opcode.Newobj: case Opcode.Newarr:
                {
                    string typeRef = src.Resolve(((OperandString)instr.Operand).StringIndex);
                    // newarr's operand is the ELEMENT type name (e.g. "int32"),
                    // which is never a declared module type. The runtime arrays
                    // are untyped (CLR object arrays), so the operand is
                    // informational — don't require it to resolve.
                    if (instr.Opcode == Opcode.Newarr)
                    {
                        EmitU16(state, 0);
                    }
                    else if (_typeMap.TryGetValue(typeRef, out var ti))
                    {
                        EmitU16(state, (ushort)ti);
                    }
                    else
                    {
                        state.Error ??= $"Unresolved type '{typeRef}' in {fullName}";
                        EmitU16(state, 0);
                    }
                    break;
                }

                case Opcode.Conv: case Opcode.Castclass: case Opcode.Isinst:
                {
                    string typeRef = src.Resolve(((OperandTypeRef)instr.Operand).StringIndex);
                    if (_typeMap.TryGetValue(typeRef, out var ti))
                        EmitU16(state, (ushort)ti);
                    else
                    {
                        state.Error ??= $"Unresolved type '{typeRef}' in {fullName}";
                        EmitU16(state, 0);
                    }
                    break;
                }

                case Opcode.Br: case Opcode.Brtrue: case Opcode.Brfalse:
                {
                    var val = (OperandBranch)instr.Operand;
                    uint instrByteCount = GetOpcodeByteCount(instr.Opcode);
                    uint oldInstrSize = instrByteCount + 4;
                    uint oldTarget = instr.PcOffset + oldInstrSize + (uint)val.PcOffset;
                    uint newTarget = OldPcToNew(method.Instructions, newPcByIdx, oldTarget, totalNewSize);
                    uint newBranchPc = newPcByIdx[i];
                    uint newInstrSize = instrByteCount + 4;
                    int newOffset = (int)(newTarget - (newBranchPc + newInstrSize));
                    EmitI32(state, newOffset);
                    break;
                }

                case Opcode.If: case Opcode.While:
                {
                    var cond = (ConditionOperand)instr.Operand;
                    state.Code.Add((byte)cond.Kind);
                    if (cond.Kind == ConditionKind.Binary)
                        state.Code.Add(cond.Comparison);
                    else if (cond.Kind is ConditionKind.Expression or ConditionKind.Block)
                    {
                        EmitU32(state, (uint)(cond.EmbeddedData?.Length ?? 0));
                        if (cond.EmbeddedData != null)
                            state.Code.AddRange(cond.EmbeddedData);
                    }
                    break;
                }

                case Opcode.Try:
                {
                    var eh = (ExceptionHandlerOperand)instr.Operand;
                    EmitU32(state, (uint)(eh.TryBlock?.Length ?? 0));
                    if (eh.TryBlock != null) state.Code.AddRange(eh.TryBlock);
                    EmitU16(state, (ushort)(eh.CatchRecords?.Length ?? 0));
                    if (eh.CatchRecords != null)
                    {
                        foreach (var cr in eh.CatchRecords)
                        {
                            EmitU16(state, cr.TypeIndex);
                            EmitU32(state, (uint)(cr.Body?.Length ?? 0));
                            if (cr.Body != null) state.Code.AddRange(cr.Body);
                        }
                    }
                    state.Code.Add(eh.HasFinally ? (byte)1 : (byte)0);
                    if (eh.HasFinally && eh.FinallyBlock != null)
                    {
                        EmitU32(state, (uint)eh.FinallyBlock.Length);
                        state.Code.AddRange(eh.FinallyBlock);
                    }
                    break;
                }
            }
        }

        if (state.Error != null)
            return new VmError(VmErrorKind.UnresolvedField, state.Error);

        func.Code = state.Code.ToArray();
        func.MaxStack = state.MaxStackDepth + 8;
        return func;
    }

    // ── Opcode byte count (variable-length for extension tables) ──

    /// <summary>Number of bytes the opcode encoding occupies in the bytecode stream.
    /// Table 0 = 1 byte, table 1 = 2 bytes (0xFF + opcode), etc.</summary>
    private static uint GetOpcodeByteCount(Opcode op)
    {
        uint val = (ushort)op;
        return 1 + (val / 256);
    }

    // ── Opcode emit ───────────────────────────────────────────────

    /// <summary>Emit a variable-length opcode: 0xFF prefix bytes + final byte.</summary>
    private static void EmitOpcode(CompileState s, ushort op)
    {
        var val = op;
        while (val >= 256)
        {
            s.Code.Add(0xFF);
            val -= 256;
        }
        s.Code.Add((byte)val);
    }

    // ── Old PC → new PC remapping ─────────────────────────────────

    private static uint OldPcToNew(List<Instruction> instructions, List<uint> newPcByIdx, uint oldPc, uint totalNewSize)
    {
        int lo = 0, hi = instructions.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (instructions[mid].PcOffset < oldPc)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo < instructions.Count && instructions[lo].PcOffset == oldPc)
            return newPcByIdx[lo];
        return lo >= instructions.Count ? totalNewSize : newPcByIdx[lo];
    }

    // ── Operand size ───────────────────────────────────────────────

    private static uint ComputeOperandSize(Instruction instr)
    {
        return instr.Opcode switch
        {
            Opcode.Nop or Opcode.Add or Opcode.Sub or Opcode.Mul
                or Opcode.Div or Opcode.Rem or Opcode.Neg
                or Opcode.Ceq or Opcode.Cne or Opcode.Cgt or Opcode.Cge
                or Opcode.Clt or Opcode.Cle
                or Opcode.And or Opcode.Xor or Opcode.Or or Opcode.Shl
                or Opcode.Not or Opcode.Dup or Opcode.Pop
                or Opcode.Ldnull or Opcode.Ret or Opcode.Break
                or Opcode.Continue or Opcode.Throw
                or Opcode.Ldelem or Opcode.Stelem or Opcode.Ldlen
                => 0,

            Opcode.LdcI4 or Opcode.Ldc or Opcode.LdcR4 => 4,
            Opcode.LdcI8 or Opcode.LdcR8 => 8,

            Opcode.Ldarg or Opcode.Starg or Opcode.Ldloc or Opcode.Stloc
                or Opcode.Ldstr or Opcode.Newobj or Opcode.Newarr
                or Opcode.Conv or Opcode.Castclass or Opcode.Isinst
                or Opcode.Ldfld or Opcode.Stfld
                or Opcode.Ldsfld or Opcode.Stsfld
                => 2,

            Opcode.Call or Opcode.Callvirt => 4,
            Opcode.NativeCall => 4,
            Opcode.Br or Opcode.Brtrue or Opcode.Brfalse => 4,

            Opcode.If or Opcode.While => 5,
            Opcode.Try => 9,

            _ => 0,
        };
    }

    // ── Encoding helpers ───────────────────────────────────────────

    private static void EmitU16(CompileState s, ushort v)
    {
        s.Code.Add((byte)(v & 0xFF));
        s.Code.Add((byte)((v >> 8) & 0xFF));
    }

    private static void EmitU32(CompileState s, uint v)
    {
        s.Code.Add((byte)(v & 0xFF));
        s.Code.Add((byte)((v >> 8) & 0xFF));
        s.Code.Add((byte)((v >> 16) & 0xFF));
        s.Code.Add((byte)((v >> 24) & 0xFF));
    }

    private static void EmitI32(CompileState s, int v) => EmitU32(s, (uint)v);

    private static void EmitI64(CompileState s, long v)
    {
        EmitU32(s, (uint)(v & 0xFFFFFFFF));
        EmitU32(s, (uint)(((ulong)v >> 32) & 0xFFFFFFFF));
    }

    private static void EmitF32(CompileState s, float v)
    {
        int bits = BitConverter.SingleToInt32Bits(v);
        EmitI32(s, bits);
    }

    private static void EmitF64(CompileState s, double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        EmitI64(s, bits);
    }

    // ── Decode raw bytecode into Instructions for resolution ───────

    private static List<Instruction> DecodeRawBytecode(byte[] raw, ORBTModule src)
    {
        var stream = new ObjektRT.Core.Serialization.BinaryStream(raw);
        var instructions = new List<Instruction>();

        var pool = src.StringPool;
        uint pc = 0;

        while (stream.Position < stream.Length)
        {
            uint startPc = pc;
            var opcode = ReadRawOpcode(stream, ref pc);
            var operand = ReadRawOperand(stream, opcode, pool, ref pc);
            instructions.Add(new Instruction(opcode, operand, startPc));
        }

        return instructions;
    }

    private static Opcode ReadRawOpcode(ObjektRT.Core.Serialization.BinaryStream s, ref uint pc)
    {
        int table = 0;
        while (true)
        {
            byte b = s.ReadU8(); pc++;
            if (b == 0xFF)
            {
                table++;
                if (table > 255)
                    throw new InvalidDataException("Opcode table overflow");
                continue;
            }
            return (Opcode)(table * 256 + b);
        }
    }

    private static Operand ReadRawOperand(ObjektRT.Core.Serialization.BinaryStream s, Opcode opcode, StringPool pool, ref uint pc)
    {
        switch (opcode)
        {
            case Opcode.Nop: case Opcode.Add: case Opcode.Sub: case Opcode.Mul:
            case Opcode.Div: case Opcode.Rem: case Opcode.Neg:
            case Opcode.Ceq: case Opcode.Cne: case Opcode.Cgt: case Opcode.Cge:
            case Opcode.Clt: case Opcode.Cle: case Opcode.And: case Opcode.Xor: case Opcode.Or: case Opcode.Shl:
            case Opcode.Not: case Opcode.Dup: case Opcode.Pop: case Opcode.Ldnull:
            case Opcode.Ret: case Opcode.Break: case Opcode.Continue: case Opcode.Throw:
            case Opcode.Ldelem: case Opcode.Stelem:
                return new OperandNone();

            case Opcode.LdcI4:
            case Opcode.Ldc:
                { var v = s.ReadI32(); pc += 4; return new OperandI4(v); }

            case Opcode.LdcI8:
                { var v = s.ReadI64(); pc += 8; return new OperandI8(v); }

            case Opcode.LdcR4:
                { var v = s.ReadR4(); pc += 4; return new OperandR4(v); }

            case Opcode.LdcR8:
                { var v = s.ReadR8(); pc += 8; return new OperandR8(v); }

            case Opcode.Ldstr:
            case Opcode.Newobj:
            case Opcode.Newarr:
                { var v = s.ReadU16(); pc += 2; return new OperandString(v); }

            case Opcode.Ldarg: case Opcode.Starg:
            case Opcode.Ldloc: case Opcode.Stloc:
                { var v = s.ReadU16(); pc += 2; return new OperandIndex(v); }

            case Opcode.Ldfld: case Opcode.Stfld:
            case Opcode.Ldsfld: case Opcode.Stsfld:
                { var v = s.ReadU16(); pc += 2; return new OperandFieldRef(v); }

            case Opcode.Call:
            case Opcode.Callvirt:
            case Opcode.NativeCall:
                {
                    var sIdx = s.ReadU16(); pc += 2;
                    var pCnt = s.ReadU16(); pc += 2;
                    return new OperandNativeCall(sIdx, pCnt);
                }

            case Opcode.Conv: case Opcode.Castclass: case Opcode.Isinst:
                { var v = s.ReadU16(); pc += 2; return new OperandTypeRef(v); }

            case Opcode.Br: case Opcode.Brtrue: case Opcode.Brfalse:
                { var v = s.ReadI32(); pc += 4; return new OperandBranch(v); }

            case Opcode.If: case Opcode.While:
                return ReadRawCondition(s, ref pc);

            case Opcode.Try:
                return ReadRawExceptionHandler(s, ref pc);

            default:
                return new OperandNone();
        }
    }

    private static ConditionOperand ReadRawCondition(ObjektRT.Core.Serialization.BinaryStream s, ref uint pc)
    {
        byte kind = s.ReadU8(); pc++;
        switch ((ConditionKind)kind)
        {
            case ConditionKind.Stack:
                return new ConditionOperand(ConditionKind.Stack);
            case ConditionKind.Binary:
                { byte cmp = s.ReadU8(); pc++; return new ConditionOperand(ConditionKind.Binary, cmp); }
            case ConditionKind.Expression:
            case ConditionKind.Block:
                {
                    uint len = s.ReadU32(); pc += 4;
                    var data = s.ReadBytes((int)len); pc += len;
                    return new ConditionOperand((ConditionKind)kind, 0, data);
                }
            default:
                return new ConditionOperand(ConditionKind.Stack);
        }
    }

    private static ExceptionHandlerOperand ReadRawExceptionHandler(ObjektRT.Core.Serialization.BinaryStream s, ref uint pc)
    {
        uint tryLen = s.ReadU32(); pc += 4;
        var tryBlock = s.ReadBytes((int)tryLen); pc += tryLen;

        ushort catchCount = s.ReadU16(); pc += 2;
        var catches = new CatchRecord[catchCount];
        for (int i = 0; i < catchCount; i++)
        {
            ushort typeIdx = s.ReadU16(); pc += 2;
            uint bodyLen = s.ReadU32(); pc += 4;
            var body = s.ReadBytes((int)bodyLen); pc += bodyLen;
            catches[i] = new CatchRecord(typeIdx, body);
        }

        bool hasFinally = s.ReadU8() != 0; pc++;
        byte[]? finallyBlock = null;
        if (hasFinally)
        {
            uint finallyLen = s.ReadU32(); pc += 4;
            finallyBlock = s.ReadBytes((int)finallyLen); pc += finallyLen;
        }

        return new ExceptionHandlerOperand(tryBlock, catches, hasFinally, finallyBlock);
    }
}

// ── Convenience wrapper ─────────────────────────────────────────────────

public static class VmCompiler
{
    public static Result<CompiledModule> Compile(ORBTModule src)
    {
        var compiler = new ModuleCompiler();
        return compiler.Compile(src);
    }
}
