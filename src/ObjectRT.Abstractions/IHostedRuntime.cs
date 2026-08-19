using System.Reflection;
using ObjektRT.Core.Model;

namespace ObjectRT.Abstractions;

/// <summary>
/// The minimal surface a hosted runtime must expose to be bundleable by
/// <see cref="ObjectRT.Runtime.BundleDriver"/>. A bundle host is a single
/// class that can register bindings (CLR types, binding assemblies) and run a
/// module's entry point.
///
/// <see cref="ObjectRT.Runtime.Runtime"/> implements this directly, as does
/// the Contract runtime (<c>Contract.Runtime.ContractRuntime</c>), so both the
/// generic ObjectRT CLI (<c>objectrt -b</c>) and the Contract CLI
/// (<c>ccl bundle</c>) can produce standalone executables from the same
/// driver.
/// </summary>
public interface IHostedRuntime
{
    /// <summary>Registers a CLR type so its static methods are callable from
    /// module code under the given module name (e.g. "Ui" for a native
    /// binding facade).</summary>
    void RegisterBinding(string name, Type type);

    /// <summary>Registers every binding found in an assembly, keyed by the
    /// host's binding attribute (e.g. <c>[ClassBinding("Ui")]</c>).</summary>
    void RegisterBindingAssembly(Assembly assembly);

    /// <summary>Loads the module and runs its entry point (the static
    /// <c>Main</c> of the first type that has one), returning its result.
    /// The host decides how imports resolve — CLR reflection, JIT, native
    /// resolvers, etc.</summary>
    object? RunModule(ORBTModule module);

    /// <summary>Loads the module and runs its entry point, passing the
    /// command-line arguments through to a C#-style <c>Main(string[] args)</c>.
    /// When the entry declares no parameter, the arguments are ignored.</summary>
    object? RunModule(ORBTModule module, string[]? args);
}
