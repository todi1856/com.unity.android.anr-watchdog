#include "Modules.h"

#include <cstring>
#include <elf.h>
#include <link.h>

#ifndef NT_GNU_BUILD_ID
    #define NT_GNU_BUILD_ID 3
#endif

namespace anrwatchdog
{
    namespace
    {
        std::string ToHex(const uint8_t* data, size_t size)
        {
            static const char kDigits[] = "0123456789abcdef";

            std::string hex;
            hex.reserve(size * 2);
            for (size_t i = 0; i < size; i++)
            {
                hex += kDigits[data[i] >> 4];
                hex += kDigits[data[i] & 0x0F];
            }
            return hex;
        }

        size_t Align4(size_t value)
        {
            return (value + 3) & ~static_cast<size_t>(3);
        }

        // The build id lives in a PT_NOTE segment, which is part of the first read-only mapping,
        // so this reads mapped memory only - no file access, and it works on stripped libraries.
        std::string ReadBuildId(const dl_phdr_info* info)
        {
            for (int i = 0; i < info->dlpi_phnum; i++)
            {
                const ElfW(Phdr)& header = info->dlpi_phdr[i];
                if (header.p_type != PT_NOTE)
                    continue;

                uintptr_t address = static_cast<uintptr_t>(info->dlpi_addr) + header.p_vaddr;
                const uintptr_t end = address + header.p_memsz;

                while (address + sizeof(ElfW(Nhdr)) <= end)
                {
                    const auto* note = reinterpret_cast<const ElfW(Nhdr)*>(address);
                    const auto* name = reinterpret_cast<const char*>(address + sizeof(ElfW(Nhdr)));
                    const auto* descriptor = reinterpret_cast<const uint8_t*>(name + Align4(note->n_namesz));

                    if (reinterpret_cast<uintptr_t>(descriptor) + note->n_descsz > end)
                        break;

                    if (note->n_type == NT_GNU_BUILD_ID && note->n_namesz == 4 && memcmp(name, "GNU", 4) == 0)
                        return ToHex(descriptor, note->n_descsz);

                    address = reinterpret_cast<uintptr_t>(descriptor) + Align4(note->n_descsz);
                }
            }

            return std::string();
        }

        int CollectModule(dl_phdr_info* info, size_t /*size*/, void* data)
        {
            auto* modules = static_cast<std::vector<LoadedModule>*>(data);

            LoadedModule module;
            module.base = static_cast<uintptr_t>(info->dlpi_addr);
            module.name = info->dlpi_name != nullptr ? info->dlpi_name : "";
            module.buildId = ReadBuildId(info);
            modules->push_back(module);

            return 0;
        }
    }

    std::vector<LoadedModule> CollectLoadedModules()
    {
        std::vector<LoadedModule> modules;
        dl_iterate_phdr(&CollectModule, &modules);
        return modules;
    }

    const LoadedModule* FindModuleByBase(const std::vector<LoadedModule>& modules, uintptr_t base)
    {
        for (const LoadedModule& module : modules)
        {
            if (module.base == base)
                return &module;
        }

        return nullptr;
    }
}
