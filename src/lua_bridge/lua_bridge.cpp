#include "lua_bridge.h"
#include <lua.hpp>

extern "C" void* create_lua_runtime() {
    lua_State* L = luaL_newstate();
    if (L) {
        luaL_openlibs(L);
    }
    return static_cast<void*>(L);
}

extern "C" int run_lua_script(void* state_ptr, const char* script) {
    if (!state_ptr) return -1;
    
    auto* L = static_cast<lua_State*>(state_ptr);
    return luaL_dostring(L, script);
}

extern "C" void destroy_lua_runtime(void* state_ptr) {
    if (state_ptr) {
        auto* L = static_cast<lua_State*>(state_ptr);
        lua_close(L);
    }
}
