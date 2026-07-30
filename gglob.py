

import pathlib

# Configuration
search_directory = pathlib.Path('.')
cpp_extensions = ('.d') # Exclude .h files as Meson tracks headers automatically

# Find all source files recursively
sources = [
    f"    '{file.as_posix()}',"  # Formats with 4 spaces and trailing comma
    for file in search_directory.rglob('*') 
    if file.suffix in cpp_extensions and 'build' not in file.parts and '.git' not in file.parts
]

# Generate the Meson code block
meson_output = "my_sources = files([\n" + "\n".join(sorted(sources)) + "\n])"

# Print the formatted output
print(meson_output)
