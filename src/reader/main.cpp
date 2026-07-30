// ObjectRT Module Reader & VM
// Reads ObjectIL (.oil) text and ORBT (.orbt) binary module files.
// Can compile to VM-friendly bytecode and execute via the built-in interpreter.
//
// Usage: objectrt-reader [options] <file.oil|file.orbt>
//
// Options:
//   -v, --verbose     Show detailed output (string pool, all instructions)
//   -r, --run         Compile to VM bytecode and execute
//   -t, --trace       Run with instruction trace
//   -h, --help        Show this help message

#include "Module.hpp"
#include "ORBTReader.hpp"
#include "ObjectILParser.hpp"
#include "../vm/ModuleCompiler.hpp"
#include "../vm/Interpreter.hpp"

#include <iostream>
#include <fstream>
#include <string>
#include <memory>
#include <cstring>

// ============================================================================
// Help
// ============================================================================

void print_help(const char* program_name) {
    std::cout
        << "ObjectRT Module Reader & VM\n"
        << "Reads ObjectIL (.oil) and ORBT (.orbt) module files.\n"
        << "Can compile to VM-friendly bytecode and execute.\n\n"
        << "Usage: " << program_name << " [options] <file>\n\n"
        << "Options:\n"
        << "  -v, --verbose     Show detailed output (string pool, all instructions)\n"
        << "  -r, --run         Compile and execute via the flat-bytecode VM\n"
        << "  -t, --trace       Run with per-instruction trace\n"
        << "  -h, --help        Show this help message\n\n"
        << "Supported formats:\n"
        << "  .oil   ObjectIL text format\n"
        << "  .orbt  ORBT binary format\n"
        << std::endl;
}

// ============================================================================
// File format detection
// ============================================================================

enum class FileFormat { ObjectIL, ORBT, Unknown };

FileFormat detect_format(const std::string& path) {
    // Try opening and reading the first 4 bytes for ORBT magic
    std::ifstream file(path, std::ios::binary);
    if (!file) return FileFormat::Unknown;

    char magic[4]{};
    file.read(magic, 4);

    if (magic[0] == 'O' && magic[1] == 'R' && magic[2] == 'B' && magic[3] == 'T') {
        return FileFormat::ORBT;
    }

    // Check extension as fallback
    if (path.size() >= 4) {
        std::string ext = path.substr(path.size() - 4);
        if (ext == ".oil" || ext == ".oir") {
            return FileFormat::ObjectIL;
        }
    }
    if (path.size() >= 5) {
        std::string ext = path.substr(path.size() - 5);
        if (ext == ".orbt") {
            return FileFormat::ORBT;
        }
    }

    // If magic is not ORBT, it might be ObjectIL
    // This could be a text file starting with "module"
    if (magic[0] == 'm' && path.size() >= 6) {
        // Read a bit more to check for "module"
        char buf[7]{};
        file.seekg(0);
        file.read(buf, 6);
        if (std::strncmp(buf, "module", 6) == 0) {
            return FileFormat::ObjectIL;
        }
    }

    return FileFormat::Unknown;
}

// ============================================================================
// Main
// ============================================================================

int main(int argc, char* argv[]) {
    bool verbose = false;
    bool run_vm  = false;
    bool trace   = false;
    std::string file_path;

    // Parse arguments
    for (int i = 1; i < argc; i++) {
        std::string arg = argv[i];
        if (arg == "-h" || arg == "--help") {
            print_help(argv[0]);
            return 0;
        } else if (arg == "-v" || arg == "--verbose") {
            verbose = true;
        } else if (arg == "-r" || arg == "--run") {
            run_vm = true;
        } else if (arg == "-t" || arg == "--trace") {
            trace = true;
            run_vm = true;
        } else if (arg[0] == '-') {
            std::cerr << "Unknown option: " << arg << "\n";
            print_help(argv[0]);
            return 1;
        } else {
            file_path = arg;
        }
    }

    if (file_path.empty()) {
        std::cerr << "Error: No input file specified.\n";
        print_help(argv[0]);
        return 1;
    }

    // Detect file format
    FileFormat format = detect_format(file_path);
    if (format == FileFormat::Unknown) {
        std::cerr << "Error: Unrecognized file format for '" << file_path
                  << "'.\n  Expected .oil (ObjectIL text) or .orbt (ORBT binary).\n";
        return 1;
    }

    // Read the module
    std::unique_ptr<objectrt::ORBTModule> module;
    try {
        if (format == FileFormat::ORBT) {
            std::cout << "; Reading ORBT binary: " << file_path << "\n\n";
            module = objectrt::read_orbt_file(file_path);
        } else {
            std::cout << "; Reading ObjectIL text: " << file_path << "\n\n";
            module = objectrt::parse_oil_file(file_path);
        }
    } catch (const std::exception& e) {
        std::cerr << "Error reading '" << file_path << "': " << e.what() << "\n";
        return 1;
    }

    // ── Dump ──────────────────────────────────────────────────────────
    if (!run_vm) {
        module->dump(std::cout, verbose);
        return 0;
    }

    // ── Compile to VM bytecode and execute ────────────────────────────
    std::cout << "; Compiling to VM bytecode...\n\n";
    objectrt::vm::CompiledModule compiled;
    try {
        compiled = objectrt::vm::compile_module(*module);
    } catch (const std::exception& e) {
        std::cerr << "Compilation error: " << e.what() << "\n";
        return 1;
    }

    // Print compiled layout
    std::cout << "; Compiled module: " << compiled.functions.size()
              << " functions, " << compiled.types.size()
              << " types, " << compiled.strings.size() << " strings\n";
    if (compiled.has_entry()) {
        std::cout << "; Entry point: " << compiled.functions[compiled.entry_function].debug_name
                  << " [" << compiled.functions[compiled.entry_function].code.size() << " bytes]\n";
    }

    if (verbose) {
        for (const auto& func : compiled.functions) {
            std::cout << ";   " << func.debug_name
                      << ": " << func.code.size() << " bytes"
                      << ", " << func.num_params << " params"
                      << ", " << func.num_locals << " locals"
                      << ", max_stack=" << func.max_stack << "\n";
        }
    }

    // Execute
    std::cout << "\n; Executing...\n";
    objectrt::vm::Interpreter vm(compiled);
    vm.set_trace(trace);

    try {
        objectrt::vm::Value result = vm.run();
        std::cout << "; Execution complete";
        if (result.tag != objectrt::vm::ValueTag::Nil) {
            std::cout << " (result: ";
            switch (result.tag) {
                case objectrt::vm::ValueTag::I4: std::cout << result.i4; break;
                case objectrt::vm::ValueTag::I8: std::cout << result.i8; break;
                case objectrt::vm::ValueTag::R4: std::cout << result.r4; break;
                case objectrt::vm::ValueTag::R8: std::cout << result.r8; break;
                default: std::cout << "?"; break;
            }
            std::cout << ")";
        }
        std::cout << "\n";
    } catch (const std::exception& e) {
        std::cerr << "Runtime error: " << e.what() << "\n";
        return 1;
    }

    return 0;
}
