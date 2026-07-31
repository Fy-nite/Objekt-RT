using ObjectRT.Runtime;

/// <summary>
/// Interface for the ObjectRT Calculator class.
/// The source generator reads the [IRClassBinding] attribute and generates
/// a proxy that Runtime.Bind&lt;ICalculator&gt;() returns.
/// </summary>
[IRClassBinding("Calculator")]
public interface ICalculator
{
    int Add(int a, int b);
    int Subtract(int a, int b);
}

// ── CLR type exposed via ClrNativeResolver ────────────────────────────
// No [IRClassBinding] needed. Just a plain C# class whose static methods
// the ClrNativeResolver discovers by reflection at runtime.
// Register it with: rt.RegisterClrType("CalcLib", typeof(MyLibrary));

public class MyLibrary
{
    public static int Triple(int x) => x * 3;

    public static string Greet(string name) => $"Hello, {name}!";

    public static double Average(double a, double b) => (a + b) / 2.0;
}

