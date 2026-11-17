using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Минимальный JSON-парсер в дерево объектов: <see cref="Dictionary{TKey,TValue}"/> (string -> object),
    /// <see cref="List{T}"/> (object), double, string, bool, null.
    /// <c>JsonUtility</c> не подходит: в файле эффекта одно и то же поле бывает числом, массивом цвета
    /// или объектом трека с ключами.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            object v = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw p.Error("Unexpected trailing characters");
            return v;
        }

        /// <summary>Записать дерево (то, что вернул <see cref="Parse"/>) обратно в JSON.</summary>
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case string s: WriteString(sb, s); break;
                case Dictionary<string, object> o:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                }
                case List<object> l:
                {
                    sb.Append('[');
                    for (int i = 0; i < l.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(sb, l[i]);
                    }
                    sb.Append(']');
                    break;
                }
                default:
                    WriteString(sb, v.ToString());
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private readonly StringBuilder _sb = new StringBuilder();

            public Parser(string s)
            {
                _s = s;
                // BOM
                if (_s.Length > 0 && _s[0] == '﻿') _i = 1;
            }

            public bool End => _i >= _s.Length;

            public FormatException Error(string msg) => new FormatException("JSON: " + msg + " at " + _i);

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            public object ReadValue()
            {
                if (End) throw Error("Unexpected end");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error("Expected " + word);
                _i += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>();
                _i++;
                SkipWs();
                if (!End && _s[_i] == '}') { _i++; return d; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[_i] != '"') throw Error("Expected key");
                    string key = ReadString();
                    SkipWs();
                    if (End || _s[_i] != ':') throw Error("Expected ':'");
                    _i++;
                    SkipWs();
                    d[key] = ReadValue();
                    SkipWs();
                    if (End) throw Error("Unterminated object");
                    char c = _s[_i++];
                    if (c == ',') continue;
                    if (c == '}') return d;
                    throw Error("Expected ',' or '}'");
                }
            }

            private List<object> ReadArray()
            {
                var l = new List<object>();
                _i++;
                SkipWs();
                if (!End && _s[_i] == ']') { _i++; return l; }
                while (true)
                {
                    SkipWs();
                    l.Add(ReadValue());
                    SkipWs();
                    if (End) throw Error("Unterminated array");
                    char c = _s[_i++];
                    if (c == ',') continue;
                    if (c == ']') return l;
                    throw Error("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _i++; // "
                int start = _i;
                // быстрый путь: строка без экранирования (base64 текстур — мегабайты)
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == '"') { string r = _s.Substring(start, _i - start); _i++; return r; }
                    if (c == '\\') break;
                    _i++;
                }
                _sb.Clear();
                _sb.Append(_s, start, _i - start);
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return _sb.ToString();
                    if (c != '\\') { _sb.Append(c); continue; }
                    if (End) break;
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': _sb.Append('"'); break;
                        case '\\': _sb.Append('\\'); break;
                        case '/': _sb.Append('/'); break;
                        case 'b': _sb.Append('\b'); break;
                        case 'f': _sb.Append('\f'); break;
                        case 'n': _sb.Append('\n'); break;
                        case 'r': _sb.Append('\r'); break;
                        case 't': _sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("Bad unicode escape");
                            _sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error("Bad escape");
                    }
                }
                throw Error("Unterminated string");
            }

            private double ReadNumber()
            {
                int start = _i;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _i++;
                    else break;
                }
                if (start == _i) throw Error("Unexpected character '" + _s[_i] + "'");
                return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
