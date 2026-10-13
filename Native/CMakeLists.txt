cmake_minimum_required(VERSION 3.20)
project(eastward_luajit LANGUAGES CXX)

set(LUAJIT_DECOMPILER_SOURCE "" CACHE PATH "Path to the node-luajit-decompiler source checkout")
if(NOT EXISTS "${LUAJIT_DECOMPILER_SOURCE}/src/main.cpp")
    message(FATAL_ERROR "Set LUAJIT_DECOMPILER_SOURCE to the node-luajit-decompiler checkout")
endif()

add_library(eastward-luajit SHARED
    luajit-decompiler.cpp
    "${LUAJIT_DECOMPILER_SOURCE}/src/bytecode/bytecode.cpp"
    "${LUAJIT_DECOMPILER_SOURCE}/src/bytecode/prototype.cpp"
    "${LUAJIT_DECOMPILER_SOURCE}/src/ast/ast.cpp"
    "${LUAJIT_DECOMPILER_SOURCE}/src/lua/lua.cpp"
)
target_include_directories(eastward-luajit PRIVATE "${LUAJIT_DECOMPILER_SOURCE}/src")
target_compile_features(eastward-luajit PRIVATE cxx_std_20)
set_target_properties(eastward-luajit PROPERTIES CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN YES)
if(MSVC)
    target_compile_options(eastward-luajit PRIVATE /J /EHsc)
else()
    target_compile_options(eastward-luajit PRIVATE -funsigned-char)
endif()
install(TARGETS eastward-luajit RUNTIME DESTINATION . LIBRARY DESTINATION .)
install(FILES "${LUAJIT_DECOMPILER_SOURCE}/LICENSE" DESTINATION licenses RENAME luajit-decompiler-MIT.txt)
