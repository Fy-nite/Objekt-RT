// // ── SCRIPTING DEMO ─────────────────────────────────────────────────────
// // Demonstrates 5 ways to call ObjectRT + CLR methods:
// //
// // 1. Direct via Runtime.CallMethod<T>() — dynamic by name
// // 2. Via IRRuntimeBinding.Invoke<T>() — late-bound handle
// // 3. Via source-generated strongly-typed interface proxy (Bind<T>)
// // 4. Via ClrNativeResolver — reflection-based CLR interop
// // 5. NativeAOT toggle — disable CLR reflection cleanly
// //
// // The source generator reads [IRClassBinding] interfaces and produces
// // concrete proxy classes at compile time — no DispatchProxy overhead.
// // ───────────────────────────────────────────────────────────────────────

// using ObjectRT.Runtime;

// // ── Step 1: Load some inline ObjectIL source ──────────────────────────

// const string calculatorOil = """
// module MathApp version 1.0.0

// .metadata {
//     spec objectrt = "1.0"
// }

// class Calculator {
//     static method Add(a: int32, b: int32) -> int32 {
//         ldarg a
//         ldarg b
//         add
//         ret
//     }

//     static method Subtract(a: int32, b: int32) -> int32 {
//         ldarg a
//         ldarg b
//         sub
//         ret
//     }

//     static method Main() -> void {
//         ldc.i4 42
//         pop
//         ret
//     }
// }
// """;

// Runtime.Shared.LoadModule(calculatorOil);

// // ── Way 1: Direct dynamic call ───────────────────────────────────────
// int sum = Runtime.Shared.CallMethod<int>("Calculator.Add", 3, 4);
// Console.WriteLine($"Direct call: Calculator.Add(3, 4) = {sum}");

// // ── Way 2: Via IRRuntimeBinding handle ───────────────────────────────
// var binding = new IRRuntimeBinding(Runtime.Shared, "Calculator");
// int diff = binding.Invoke<int>("Subtract", 10, 3);
// Console.WriteLine($"Binding call: Calculator.Subtract(10, 3) = {diff}");

// // ── Way 3: Source-generated strongly-typed proxy ────────────────────
// ICalculator calc = Runtime.Shared.Bind<ICalculator>("Calculator");
// int result = calc.Add(40, 2);
// int result2 = calc.Subtract(100, 50);
// Console.WriteLine($"Bind<T> proxy: calc.Add(40, 2) = {result}");
// Console.WriteLine($"Bind<T> proxy: calc.Subtract(100, 50) = {result2}");

// // ── Way 4: CLR reflection resolver ───────────────────────────────────
// // Register a plain C# class so ObjectRT can call its static methods
// // via reflection — no interface, no generated proxy.
// Runtime.Shared.RegisterClrType("CalcLib", typeof(MyLibrary));

// int tripled = Runtime.Shared.CallMethod<int>("CalcLib.Triple", 7);
// string greeted = Runtime.Shared.CallMethod<string>("CalcLib.Greet", "ObjectRT");
// double averaged = Runtime.Shared.CallMethod<double>("CalcLib.Average", 10.0, 20.0);

// Console.WriteLine($"CLR resolver: CalcLib.Triple(7) = {tripled}");
// Console.WriteLine($"CLR resolver: CalcLib.Greet(\"ObjectRT\") = {greeted}");
// Console.WriteLine($"CLR resolver: CalcLib.Average(10, 20) = {averaged}");

// // ── Way 5: Show the NativeAOT toggle ────────────────────────────────
// Console.WriteLine();
// Console.WriteLine($"CLR reflection allowed: {Runtime.Shared.ClrResolver.AllowReflection}");
// Console.WriteLine($"IsDynamicCodeSupported: {System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
// Console.WriteLine("(Set rt.ClrResolver.AllowReflection = false to disable for NativeAOT)");

// // ── Verify everything matches ────────────────────────────────────────
// Console.WriteLine();
// Console.WriteLine("All calls completed successfully!");
