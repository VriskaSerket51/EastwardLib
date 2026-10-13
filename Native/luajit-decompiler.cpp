#include <cstdlib>
#include <cstring>
#include <exception>

// The existing engine defines its diagnostic Error alongside the entry points.
#include "main.cpp"

#ifdef _WIN32
#define EW_EXPORT extern "C" __declspec(dllexport)
#else
#define EW_EXPORT extern "C" __attribute__((visibility("default")))
#endif

static int copy_result(const char* text, size_t size, char** output, size_t* length, int status) noexcept {
    auto memory = static_cast<char*>(std::malloc(size + 1));
    if (!memory) return 2;
    std::memcpy(memory, text, size);
    memory[size] = '\0';
    *output = memory;
    *length = size;
    return status;
}

EW_EXPORT int ew_decompile(const uint8_t* bytes, size_t size, char** output, size_t* length) noexcept {
    if (!output || !length) return 1;
    *output = nullptr;
    *length = 0;
    if (!bytes || size == 0) {
        constexpr char message[] = "Empty LuaJIT bytecode";
        return copy_result(message, sizeof(message) - 1, output, length, 1);
    }
    try {
        const auto source = decompile(bytes, size);
        return copy_result(source.data(), source.size(), output, length, 0);
    }
    catch (const Error& error) {
        return copy_result(error.message.data(), error.message.size(), output, length, 1);
    }
    catch (const std::exception& error) {
        return copy_result(error.what(), std::strlen(error.what()), output, length, 1);
    }
    catch (...) {
        constexpr char message[] = "Unknown LuaJIT decompiler error";
        return copy_result(message, sizeof(message) - 1, output, length, 1);
    }
}

EW_EXPORT void ew_free(void* memory) noexcept {
    std::free(memory);
}
