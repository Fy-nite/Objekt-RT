using System.Reflection;
using ObjektRT.Core.Attributes;

namespace ObjectRT.Runtime;

/// <summary>
/// Auto-discovers stdlib types annotated with <see cref="ClassBindingAttribute"/>
/// and registers them on a <see cref="Runtime"/> instance. Any host can call
/// <see cref="RegisterStdLib"/> to get the standard library without hardcoding
/// individual types.
/// </summary>
public static class StdLibRegistrar
{
    /// <summary>
    /// Scans <paramref name="assembly"/> for types with <c>[ClassBinding]</c>
    /// and registers each as a CLR type on <paramref name="runtime"/>.
    /// </summary>
    public static void RegisterAssembly(Runtime runtime, Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            var attr = type.GetCustomAttribute<ClassBindingAttribute>();
            if (attr == null) continue;
            runtime.RegisterClrType(attr.Name, type);
        }
    }

    /// <summary>
    /// Scans the ObjektRT.Stdlib assembly for <c>[ClassBinding]</c>-annotated
    /// types and registers them on <paramref name="runtime"/>.
    /// </summary>
    public static void RegisterStdLib(Runtime runtime)
    {
        var stdlibAssembly = typeof(ObjektRT.Stdlib.System.IO).Assembly;
        RegisterAssembly(runtime, stdlibAssembly);
    }
}
