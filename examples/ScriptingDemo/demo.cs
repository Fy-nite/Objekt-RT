using ObjectRT.Runtime;
const string calculatorOil = """
module MathApp version 1.0.0

.metadata {
    spec objectrt = "1.0"
}

class Calculator {
    static method Add(a: int32, b: int32) -> int32 {
        ldarg a
        ldarg b
        add
        ret
    }

    static method Subtract(a: int32, b: int32) -> int32 {
        ldarg a
        ldarg b
        sub
        ret
    }

    static method Main() -> void {
        ldc.i4 42
        pop
        ret
    }
}
""";

Runtime.Shared.LoadModule(calculatorOil);

ICalculator calc = Runtime.Shared.Bind<ICalculator>("Calculator");
Console.WriteLine(calc.Add(5,5));
