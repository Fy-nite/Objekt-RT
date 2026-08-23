using ObjektRT.Core.Model;
using ObjektRT.Core.Parsing;
using ObjektRT.Core.Serialization;
using ObjectRT.Runtime;
using ObjectRT.Runtime.Reflection;

// ── Tiny assertion harness (no external test framework) ────────────────────
var failures = new List<string>();
int passed = 0;

void Check(bool condition, string name, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"  FAIL  {name}{(detail != null ? $"\n        {detail}" : "")}");
    }
}

// ── Sample module: inheritance (classes), overrides, attributes ────────────

const string ShapeOil = """
module ReflectTest version 1.0.0

@Author("alice")
class Shape {
    field id: int32
    virtual method Describe(this: object) -> string {
        ldstr "shape"
        ret
    }
}

class Circle : Shape {
    override method Describe(this: object) -> string {
        ldstr "circle"
        ret
    }
}

class Square : Circle {
}

class MathBase {
    static field factor: int32
    static method Triple(x: int32) -> int32 {
        ldarg x
        ldc.i4 3
        mul
        ret
    }
}

class Calc : MathBase {
    static method Quadruple(x: int32) -> int32 {
        ldarg x
        ldc.i4 4
        mul
        ret
    }
}

class ShapeFactory {
    static method Make() -> object {
        newobj Circle
        ret
    }
}
""";

// ── 1. Module-level reflection ─────────────────────────────────────────────

Console.WriteLine("== 1. Module reflection ==");

var mod = OilFileReader.ParseString(ShapeOil);
var reflector = ModuleReflector.From(mod);

Check(reflector.ModuleName == "ReflectTest", "module name");
Check(reflector.GetTypes().Count == 6, "six types found", $"got {reflector.GetTypes().Count}");
Check(reflector.GetType("Shape") is { Name: "Shape" }, "GetType by name");
Check(reflector.GetType("Missing") == null, "GetType unknown -> null");

var shape = reflector.GetType("Shape")!;
var circle = reflector.GetType("Circle")!;
var square = reflector.GetType("Square")!;
var mathBase = reflector.GetType("MathBase")!;
var calc = reflector.GetType("Calc")!;

Check(shape.Kind == TypeKind.Class && !shape.IsInterface && !shape.IsStruct, "Shape is a class");
Check(shape.GetAttributes() is [{ Name: "Author" } attrs] && attrs.Arguments.Count == 1,
    "type attribute found", $"attrs={string.Join(";", shape.GetAttributes())}");
Check(shape.GetAttributes()[0].Arguments[0] == "alice", "attribute arg resolved (unquoted)");

// ── 1b. Fields ────────────────────────────────────────────────────────

Console.WriteLine("== 1b. Field reflection ==");

Check(shape.GetField("id") is { IsStatic: false }, "instance field IsStatic false");
Check(mathBase.GetField("factor") is { IsStatic: true }, "static field IsStatic true");
Check(calc.GetField("factor") is { IsStatic: true }, "inherited static field found via derived");
Check(shape.GetFields().Select(f => f.Name).Contains("id"), "GetFields includes declared field");
Check(calc.GetFields().Select(f => f.Name).Contains("factor"), "GetFields includes inherited field");

// ── 2. Inheritance hierarchy ───────────────────────────────────────────────

Console.WriteLine("== 2. Inheritance hierarchy ==");

Check(circle.BaseType == shape, "Circle : Shape");
Check(square.BaseType == circle, "Square : Circle");
Check(shape.BaseType == null, "Shape has no base");
Check(square.IsSubclassOf(shape), "Square is subclass of Shape");
Check(square.IsSubclassOf(circle), "Square is subclass of Circle");
Check(!circle.IsSubclassOf(square), "Circle is not subclass of Square");
Check(shape.IsAssignableFrom(square), "Shape assignable from Square");
Check(square.IsAssignableFrom(square), "type assignable from itself");
Check(circle.IsAssignableFrom(square), "Circle assignable from Square");
Check(!square.IsAssignableFrom(shape), "Square not assignable from Shape");
Check(square.GetHierarchy().SequenceEqual(new[] { square, circle, shape }),
    "hierarchy order most-derived first",
    string.Join(" <- ", square.GetHierarchy().Select(t => t.Name)));

// ── 3. Inheritance-aware method lookup ─────────────────────────────────────

Console.WriteLine("== 3. Method lookup through inheritance ==");

var describe = square.FindMethod("Describe");
Check(describe != null, "FindMethod finds method through base chain");
Check(describe!.DeclaringType == circle, "most-derived override wins", $"declaring={describe.DeclaringType.Name}");
Check(describe.IsOverride && !describe.IsStatic && describe.IsVirtual == false, "Describe is override, not static");
Check(describe.GetBaseDefinition()?.DeclaringType == shape, "GetBaseDefinition resolves to Shape.Describe");
Check(describe.GetBaseDefinition() is { IsVirtual: true, IsOverride: false }, "base definition is virtual, not override");
Check(square.GetMethod("Describe")?.DeclaringType == circle, "GetMethod walks inheritance");

var ownDescribe = circle.GetDeclaredMethod("Describe");
Check(ownDescribe?.DeclaringType == circle, "GetDeclaredMethod returns own only");

Check(describe.QualifiedName == "Circle.Describe", "qualified name", describe.QualifiedName);
Check(describe.ReturnTypeName == "string", "return type resolved");
Check(describe.ParameterCount == 1, "instance method declares 'this' as param 0", $"count={describe.ParameterCount}");
Check(describe.GetParameters() is [{ Name: "this" }], "parameter metadata", string.Join(",", describe.GetParameters()));

var squareMethods = square.GetMethods();
Check(squareMethods.Count(m => m.Name == "Describe") == 2,
    "GetMethods includes declared + inherited", $"count={squareMethods.Count}");

var triple = calc.FindMethod("Triple");
Check(triple?.DeclaringType == mathBase, "inherited static found via derived type", $"declaring={triple?.DeclaringType.Name}");
Check(triple is { IsStatic: true }, "Triple is static");
Check(triple?.QualifiedName == "MathBase.Triple", "inherited method's qualified name uses declaring type");

var byQualified = reflector.FindMethod("Square.Describe");
Check(byQualified?.DeclaringType == circle, "qualified name resolves through inheritance");

var quad = calc.GetDeclaredMethod("Quadruple");
Check(quad?.DeclaringType == calc && quad!.IsStatic, "own static method found");
Check(quad!.GetBaseDefinition() == quad, "non-override base definition is itself");

// ── 4. Runtime invocation (method references in action) ────────────────────

Console.WriteLine("== 4. Invocation ==");

var rt = new Runtime();
rt.LoadModule(mod);

Check(rt.GetReflector() != null, "GetReflector after load");
Check(rt.GetReflector()!.ModuleName == "ReflectTest", "reflector wraps loaded module");

Check(rt.CallMethod<int>("MathBase.Triple", 5) == 15, "direct static call");
Check(rt.CallMethod<int>("Calc.Triple", 7) == 21, "inherited static call via derived type name");
Check(rt.CallMethod<int>("Calc.Quadruple", 5) == 20, "own static call via derived type");

Check(triple!.Invoke(rt, null, 6) is int t6 && t6 == 18, "MethodInfo.Invoke static");
Check(quad!.Invoke(rt, null, 6) is int q6 && q6 == 24, "MethodInfo.Invoke own static");

// Instance round-trip: factory returns a VM object handle (boxed uint),
// which Invoke marshals back as the 'this' receiver.
var receiver = rt.CallMethod<object>("ShapeFactory.Make");
Check(receiver is uint, "factory returned an object handle", receiver?.GetType().FullName ?? "null");
var describeInfo = circle.FindMethod("Describe")!;
var result = describeInfo.Invoke(rt, receiver);
Check(result is "circle", "instance method invoked with receiver", result?.ToString() ?? "null");

// Static invocation must reject a receiver.
bool threw = false;
try { triple.Invoke(rt, new object()); }
catch (ArgumentException) { threw = true; }
Check(threw, "Invoke rejects receiver on static method");

// ── 4b. In-language Reflect binding ────────────────────────────────────────

Console.WriteLine("== 4b. Reflect binding ==");
var reflectNs = typeof(ObjectRT.Runtime.Reflection.ReflectBinding);

rt.RegisterClrType("Reflect", reflectNs);
Check(reflectNs.GetProperty("Host")!.GetValue(null) != null, "reflect host attached on load");
Check(rt.CallMethod<string>("Reflect.ModuleName") == "ReflectTest", "Reflect.ModuleName via binding");

var reflectedTypes = (string[]?)rt.CallMethod<object?>("Reflect.Types");
Check(reflectedTypes != null && reflectedTypes.Contains("Circle"), "Reflect.Types lists Circle",
    string.Join(",", reflectedTypes ?? Array.Empty<string>()));
Check(rt.CallMethod<bool>("Reflect.HasType", "Shape"), "Reflect.HasType resolves short name");
Check(rt.CallMethod<string>("Reflect.Resolve", "Square.Describe") == "Circle.Describe",
    "Reflect.Resolve walks inheritance");
Check(rt.CallMethod<bool>("Reflect.IsSubclassOf", "Square", "Shape"), "Reflect.IsSubclassOf");

rt.CallMethod<object?>("Reflect.SetStatic", "MathBase", "factor", 7);
Check((int)rt.CallMethod<object>("Reflect.GetStatic", "MathBase", "factor")! == 7,
    "Reflect.GetStatic/SetStatic round-trip");

// ── 5. Static field metadata survives the ORBT binary round-trip ──────────

Console.WriteLine("== 5. ORBT binary round-trip (static fields) ==");

var binary = new ORBTWriter().WriteModule(mod);
var fromBinary = OrbtFileReader.ReadBytes(binary);
var reflBin = ModuleReflector.From(fromBinary);
Check(reflBin.GetType("MathBase")?.GetField("factor") is { IsStatic: true }, "static field survives binary round-trip");
Check(reflBin.GetType("Shape")?.GetField("id") is { IsStatic: false }, "instance field flag survives binary round-trip");
Check(reflBin.GetType("Circle")?.FindMethod("Describe")?.DeclaringType.Name == "Circle", "inheritance lookup works on binary-loaded module");

// ── Summary ────────────────────────────────────────────────────────────────

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"ALL {passed} CHECKS PASSED");
    return 0;
}

Console.WriteLine($"{failures.Count} FAILED / {passed + failures.Count} total:");
foreach (var f in failures) Console.WriteLine($"  - {f}");
return 1;
