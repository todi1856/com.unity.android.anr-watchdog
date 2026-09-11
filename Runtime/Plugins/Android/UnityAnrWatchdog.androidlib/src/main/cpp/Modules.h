#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace anrwatchdog
{
    struct LoadedModule
    {
        uintptr_t base = 0;
        std::string name;

        // GNU build id as hex, empty when the module carries no build id note. This is what lets
        // an offline symbolicator pick the exact binary an address came from.
        std::string buildId;
    };

    // Snapshot of the shared objects currently loaded into the process.
    std::vector<LoadedModule> CollectLoadedModules();

    const LoadedModule* FindModuleByBase(const std::vector<LoadedModule>& modules, uintptr_t base);
}
