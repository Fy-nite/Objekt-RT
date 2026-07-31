project('ObjectRT', 'c', 'cpp')

# ── Find required tools ────────────────────────────────────────────────────
dotnet = find_program('dotnet', required: true)

# ── Map Meson host machine → .NET RID ──────────────────────────────────────
system   = host_machine.system()
cpu      = host_machine.cpu_family()

if system == 'windows' and cpu == 'x86_64'
    rid = 'win-x64'
elif system == 'linux' and cpu == 'x86_64'
    rid = 'linux-x64'
elif system == 'linux' and cpu == 'aarch64'
    rid = 'linux-arm64'
elif system == 'darwin' and cpu == 'x86_64'
    rid = 'osx-x64'
elif system == 'darwin' and cpu == 'aarch64'
    rid = 'osx-arm64'
else
    error('Unsupported platform: ' + system + ' ' + cpu)
endif

message('Using .NET RID: ' + rid)

# ── Existing dependencies & include dirs ────────────────────────────────────
dependencies = [
    dependency('luajit'),
    dependency('gtest'),
    dependency('ftxui'),
]

inc_dir = include_directories([
    'src/include',
    'src',
])

# ── Build C# Native AOT static library ────────────────────────────────────
python3 = find_program('python3', required: true)
csharp_project = 'src/csharp/ObjectRT.csproj'

# The static library extension depends on the platform
if host_machine.system() == 'windows'
    csharp_lib_name = 'ObjectRT.lib'
else
    csharp_lib_name = 'libObjectRT.a'
endif

csharp_aot_lib = custom_target('csharp-native-aot',
    input  : csharp_project,
    output : csharp_lib_name,
    command: [
        python3,
        files('build-support/dotnet-publish-aot.py'),
        '--project',   '@INPUT@',
        '--rid',       rid,
        '--output-lib','@OUTPUT@',
    ],
    build_by_default : true,
    console          : true,
)

# ── Main ObjectRT runtime (linked with C# AOT static library) ────────────
rt_sources = files([
    'src/Program.cpp',
    'src/stdlib/IO/IO.cpp',
])

executable('ObjectRT', rt_sources,
    dependencies : dependencies,
    link_with    : csharp_aot_lib,
    include_directories : inc_dir)

# ── Reader & VM ────────────────────────────────────────────────────────────
reader_sources = files([
    'src/reader/main.cpp',
    'src/reader/ORBTReader.cpp',
    'src/reader/ObjectILParser.cpp',
    'src/vm/ModuleCompiler.cpp',
    'src/vm/Interpreter.cpp',
])
compiler_sources = files([
    'src/reader/ORBTReader.cpp',
    'src/reader/ObjectILParser.cpp',
    'src/vm/ModuleCompiler.cpp',
    'src/vm/Interpreter.cpp',
    'src/compiler/Program.cpp'
])

executable('objectrt-reader', reader_sources,
    dependencies: dependencies,
    include_directories: inc_dir)

executable('ORBC', compiler_sources,
    dependencies: dependencies,
    include_directories: inc_dir)

