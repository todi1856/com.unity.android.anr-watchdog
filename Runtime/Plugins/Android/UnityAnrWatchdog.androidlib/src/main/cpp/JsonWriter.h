#pragma once

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace anrwatchdog
{
    // Minimal JSON object writer. The engine's JSONUtility is not available to a package, and the
    // report shape is fixed and small, so this is all that is needed.
    //
    // Output is indented with four spaces per level, matching the Java half of the report, which
    // org.json produces with toString(4) - the two are spliced into one file and should not change
    // formatting halfway through.
    class JsonWriter
    {
    public:
        explicit JsonWriter(bool pretty = true) : m_Pretty(pretty) {}

        void BeginObject() { Separator(); m_Out += '{'; m_Depth.push_back(true); }
        void EndObject() { CloseScope('}'); }
        void BeginArray() { Separator(); m_Out += '['; m_Depth.push_back(true); }
        void EndArray() { CloseScope(']'); }

        void Key(const char* key)
        {
            Separator();
            AppendEscaped(key);
            m_Out += m_Pretty ? ": " : ":";
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
        static constexpr size_t kIndent = 4;

        // A value that follows a key stays on the key's line; anything else opens a new one.
        void Separator()
        {
            if (m_PendingValue)
            {
                m_PendingValue = false;
                return;
            }

            if (m_Depth.empty())
                return;

            if (m_Depth.back())
                m_Depth.back() = false;
            else
                m_Out += ',';

            NewLine(m_Depth.size());
        }

        void CloseScope(char bracket)
        {
            const bool empty = m_Depth.back();
            m_Depth.pop_back();

            // An empty object or array stays on one line: {} rather than {\n}.
            if (!empty)
                NewLine(m_Depth.size());

            m_Out += bracket;
        }

        void NewLine(size_t depth)
        {
            if (!m_Pretty)
                return;

            m_Out += '\n';
            m_Out.append(depth * kIndent, ' ');
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

        // One entry per open scope, true while that scope is still empty.
        std::vector<bool> m_Depth;
        bool m_PendingValue = false;
        bool m_Pretty = true;
    };
}
