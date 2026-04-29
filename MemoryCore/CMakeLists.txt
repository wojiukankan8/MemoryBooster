cmake_minimum_required(VERSION 3.20)
project(MemoryCore LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(MSVC)
    # Target Windows 7+ so MIB_IF_ROW2 / ESTATS APIs are declared in the SDK headers.
    add_compile_definitions(_WIN32_WINNT=0x0601 WINVER=0x0601 NTDDI_VERSION=0x06010000)
    add_compile_options(/EHsc /W3)
endif()

add_library(MemoryCore SHARED MemoryCore.cpp MemoryCore.h)
target_link_libraries(MemoryCore PRIVATE psapi iphlpapi ws2_32)
target_compile_definitions(MemoryCore PRIVATE MEMORYCORE_EXPORTS)

# 输出到 WPF 项目的输出目录
set_target_properties(MemoryCore PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
