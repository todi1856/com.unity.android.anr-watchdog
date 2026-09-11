#pragma once

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace anrwatchdog
{
    // Minimal JSON object writer. The engine's JSONUtility is not available to a package, and the
    // report shape is fixed and small, so this is all that is needed.
    class JsonWriter
    {
    public:
        void BeginObject() { Separator(); m_Out += '{'; m_First.push_back(true); }
        void EndObject() { m_Out += '}'; m_First.pop_back(); }
        void BeginArray() { Separator(); m_Out += '['; m_First.push_back(true); }
        void EndArray() { m_Out += ']'; m_First.pop_back(); }

        void Key(const char* key)
        {
            Separator();
            AppendEscaped(key);
            m_Out += ':';
            m_PendingValue = true;
        }

        void Value(const char* value) { Separator(); AppendEscaped(value); }
        void Value(const std::string& value) { Separator(); AppendEscaped(value.c_str()); }

        void Value(uint64_t value)
        {
            Separator();
            char buffer[32];
            snprintf(buffer, sizeof(buffer), "%llu", static_cast<unsigned long long>(value));
            m_Out += buffer;
        }

        void Value(int value)
        {
            Separator();
            char buffer[32];
            snprintf(buffer, sizeof(buffer), "%d", value);
            m_Out += buffer;
        }

        const std::string& Result() const { return m_Out; }

    private:
        void Separator()
        {
            if (m_PendingValue)
            {
                m_PendingValue = false;
                return;
            }

            if (m_First.empty())
                return;

            if (m_First.back())
                m_First.back() = false;
            else
                m_Out += ',';
        }

        void AppendEscaped(const char* text)
        {
            m_Out += '"';
            for (const char* c = text; *c != '\0'; c++)
            {
                switch (*c)
                {
                    case '"': m_Out += "\\\""; break;
                    case '\\': m_Out += "\\\\"; break;
                    case '\b': m_Out += "\\b"; break;
                    case '\f': m_Out += "\\f"; break;
                    case '\n': m_Out += "\\n"; break;
                    case '\r': m_Out += "\\r"; break;
                    case '\t': m_Out += "\\t"; break;
                    default:
                        if (static_cast<unsigned char>(*c) < 0x20)
                        {
                            char buffer[8];
                            snprintf(buffer, sizeof(buffer), "\\u%04x", *c);
                            m_Out += buffer;
                        }
                        else
                        {
                            m_Out += *c;
                        }
                        break;
                }
            }
            m_Out += '"';
        }

        std::string m_Out;
        std::vector<bool> m_First;
        bool m_PendingValue = false;
    };
}
