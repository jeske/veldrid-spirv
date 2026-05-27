#pragma once

#include "stdint.h"

// VELDRID_SPIRV_ABI_VERSION is injected by CMake from the single-source file
// NATIVE_ABI_VERSION at the repo root. Increment that file whenever the interop
// struct layout changes (CrossCompileInfo, CompilationResult, GlslCompileInfo,
// ReflectionInfo, etc.)
#ifndef VELDRID_SPIRV_ABI_VERSION
#error "VELDRID_SPIRV_ABI_VERSION must be defined by the build system (CMake)."
#endif

#ifdef _WIN32
#define VD_EXPORT extern "C" __declspec(dllexport)
#else
#define VD_EXPORT extern "C" __attribute__((visibility("default")))
#endif
