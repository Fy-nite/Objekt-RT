#pragma once

#ifdef __cplusplus
extern "C" {
#endif

// Allocates and initializes a new persistent Lua environment
void* create_lua_runtime();

// Executes a script string within an existing environment context
int run_lua_script(void* state_ptr, const char* script);

// Safely destroys the environment and frees allocated memory
void destroy_lua_runtime(void* state_ptr);

#ifdef __cplusplus
}
#endif
