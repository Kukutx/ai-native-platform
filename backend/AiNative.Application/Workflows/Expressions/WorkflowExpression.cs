using System.Globalization;
using System.Text;
using System.Text.Json;
using AiNative.Application.Common;

namespace AiNative.Application.Workflows.Expressions;

public sealed class WorkflowExpressionException(string message) : Exception(message);

/// <summary>
/// 工作流表达式：从运行上下文里取值，做比较和逻辑运算。语法刻意保持很小，便于人写、也便于模型生成：
/// <list type="bullet">
/// <item>路径：trigger.task.title、steps.review.output.items[0]、input.客户、input['带空格的键']</item>
/// <item>字面量：'文本'、"文本"、12、1.5、true、false、null</item>
/// <item>运算：== != &gt; &gt;= &lt; &lt;= &amp;&amp; || ! contains，括号分组</item>
/// <item>函数：length(x) lower(x) upper(x) trim(x) json(x) default(x, 备选)</item>
/// </list>
/// 取不到的路径得到 null，不会报错；比较不做隐式类型转换（数字只和数字比，字符串只和字符串比）。
/// </summary>
public sealed class WorkflowExpression
{
    private const int MaxLength = 2000;
    private const int MaxDepth = 64;

    private readonly Node _root;

    private WorkflowExpression(string source, Node root, IReadOnlySet<string> roots)
    {
        Source = source;
        _root = root;
        Roots = roots;
    }

    public string Source { get; }

    /// <summary>
    /// 表达式引用的顶层变量名（trigger、steps、item 等）。
    /// </summary>
    public IReadOnlySet<string> Roots { get; }

    public static WorkflowExpression Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new WorkflowExpressionException("表达式不能为空。");
        }

        if (source.Length > MaxLength)
        {
            throw new WorkflowExpressionException($"表达式不能超过 {MaxLength} 个字符。");
        }

        var parser = new Parser(Tokenize(source));
        var root = parser.ParseAll();
        return new WorkflowExpression(source, root, parser.Roots);
    }

    /// <summary>
    /// 校验表达式语法，返回错误说明；合法时返回 null。
    /// </summary>
    public static string? Validate(string source)
    {
        try
        {
            Parse(source);
            return null;
        }
        catch (WorkflowExpressionException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 求值。来自上下文的值保持原样（JsonElement），字面量与函数结果是 string / double / bool / null。
    /// </summary>
    public object? Evaluate(JsonElement context) => _root.Evaluate(context);

    public bool IsTrue(JsonElement context) => Values.IsTruthy(Evaluate(context));

    private static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (c is '\'' or '"')
            {
                var builder = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= source.Length)
                    {
                        throw new WorkflowExpressionException($"第 {start + 1} 个字符开始的字符串没有结束引号。");
                    }

                    var ch = source[i];
                    if (ch == c)
                    {
                        i++;
                        break;
                    }

                    if (ch == '\\' && i + 1 < source.Length)
                    {
                        var next = source[i + 1];
                        builder.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                        i += 2;
                        continue;
                    }

                    builder.Append(ch);
                    i++;
                }

                tokens.Add(new Token(TokenKind.String, builder.ToString(), start));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                while (i < source.Length && (char.IsAsciiDigit(source[i]) || source[i] == '.'))
                {
                    i++;
                }

                var text = source[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new WorkflowExpressionException($"无法识别的数字 {text}。");
                }

                tokens.Add(new Token(TokenKind.Number, text, start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, source[start..i], start));
                continue;
            }

            var two = i + 1 < source.Length ? source.Substring(i, 2) : null;
            if (two is "==" or "!=" or ">=" or "<=" or "&&" or "||")
            {
                tokens.Add(new Token(TokenKind.Operator, two, start));
                i += 2;
                continue;
            }

            if (c is '>' or '<' or '!' or '(' or ')' or '[' or ']' or '.' or ',')
            {
                tokens.Add(new Token(TokenKind.Operator, c.ToString(), start));
                i++;
                continue;
            }

            throw new WorkflowExpressionException($"第 {i + 1} 个字符 '{c}' 无法识别。");
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, source.Length));
        return tokens;
    }

    private enum TokenKind
    {
        Identifier,
        Number,
        String,
        Operator,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position)
    {
        public bool Is(string op) => Kind is TokenKind.Operator or TokenKind.Identifier && Text == op;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;
        private int _depth;

        public HashSet<string> Roots { get; } = new(StringComparer.Ordinal);

        private Token Current => tokens[_position];

        public Node ParseAll()
        {
            var node = ParseOr();
            if (Current.Kind != TokenKind.End)
            {
                throw Error($"多余的内容 '{Current.Text}'");
            }

            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (Current.Is("||"))
            {
                _position++;
                left = new LogicalNode(left, ParseAnd(), isAnd: false);
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseEquality();
            while (Current.Is("&&"))
            {
                _position++;
                left = new LogicalNode(left, ParseEquality(), isAnd: true);
            }

            return left;
        }

        private Node ParseEquality()
        {
            var left = ParseComparison();
            while (Current.Is("==") || Current.Is("!="))
            {
                var op = Current.Text;
                _position++;
                left = new CompareNode(left, ParseComparison(), op);
            }

            return left;
        }

        private Node ParseComparison()
        {
            var left = ParseUnary();
            while (Current.Is(">") || Current.Is(">=") || Current.Is("<") || Current.Is("<=") || Current.Is("contains"))
            {
                var op = Current.Text;
                _position++;
                left = new CompareNode(left, ParseUnary(), op);
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Current.Is("!"))
            {
                _position++;
                return new NotNode(Enter(ParseUnary));
            }

            return ParsePostfix();
        }

        private Node ParsePostfix()
        {
            var node = ParsePrimary();
            while (true)
            {
                if (Current.Is("."))
                {
                    _position++;
                    if (Current.Kind != TokenKind.Identifier)
                    {
                        throw Error("'.' 后面应该是属性名");
                    }

                    node = new MemberNode(node, new LiteralNode(Current.Text));
                    _position++;
                }
                else if (Current.Is("["))
                {
                    _position++;
                    var index = Enter(ParseOr);
                    Expect("]");
                    node = new MemberNode(node, index);
                }
                else
                {
                    return node;
                }
            }
        }

        private Node ParsePrimary()
        {
            var token = Current;
            switch (token.Kind)
            {
                case TokenKind.String:
                    _position++;
                    return new LiteralNode(token.Text);
                case TokenKind.Number:
                    _position++;
                    return new LiteralNode(double.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture));
                case TokenKind.Identifier:
                    _position++;
                    switch (token.Text)
                    {
                        case "true":
                            return new LiteralNode(true);
                        case "false":
                            return new LiteralNode(false);
                        case "null":
                            return new LiteralNode(null);
                    }

                    if (Current.Is("("))
                    {
                        return ParseCall(token);
                    }

                    Roots.Add(token.Text);
                    return new RootNode(token.Text);
                case TokenKind.Operator when token.Text == "(":
                    _position++;
                    var inner = Enter(ParseOr);
                    Expect(")");
                    return inner;
                default:
                    throw Error(token.Kind == TokenKind.End ? "表达式不完整" : $"意外的 '{token.Text}'");
            }
        }

        private Node ParseCall(Token name)
        {
            _position++;
            var args = new List<Node>();
            if (!Current.Is(")"))
            {
                args.Add(Enter(ParseOr));
                while (Current.Is(","))
                {
                    _position++;
                    args.Add(Enter(ParseOr));
                }
            }

            Expect(")");
            return CallNode.Create(name.Text, args) ?? throw new WorkflowExpressionException($"未知函数 {name.Text}，可用：length、lower、upper、trim、json、default。");
        }

        private Node Enter(Func<Node> parse)
        {
            if (++_depth > MaxDepth)
            {
                throw new WorkflowExpressionException("表达式嵌套过深。");
            }

            try
            {
                return parse();
            }
            finally
            {
                _depth--;
            }
        }

        private void Expect(string op)
        {
            if (!Current.Is(op))
            {
                throw Error($"缺少 '{op}'");
            }

            _position++;
        }

        private WorkflowExpressionException Error(string message)
            => new($"{message}（第 {Current.Position + 1} 个字符）。");
    }

    private abstract class Node
    {
        public abstract object? Evaluate(JsonElement context);
    }

    private sealed class LiteralNode(object? value) : Node
    {
        public override object? Evaluate(JsonElement context) => value;
    }

    private sealed class RootNode(string name) : Node
    {
        public override object? Evaluate(JsonElement context) => Values.Member(context, name);
    }

    private sealed class MemberNode(Node target, Node member) : Node
    {
        public override object? Evaluate(JsonElement context)
        {
            if (target.Evaluate(context) is not JsonElement element)
            {
                return null;
            }

            return Values.Normalize(member.Evaluate(context)) switch
            {
                string name => Values.Member(element, name),
                double index when element.ValueKind == JsonValueKind.Array && index >= 0 && index < element.GetArrayLength() && index == Math.Floor(index)
                    => element[(int)index],
                _ => null,
            };
        }
    }

    private sealed class NotNode(Node operand) : Node
    {
        public override object? Evaluate(JsonElement context) => !Values.IsTruthy(operand.Evaluate(context));
    }

    private sealed class LogicalNode(Node left, Node right, bool isAnd) : Node
    {
        public override object? Evaluate(JsonElement context)
        {
            var l = Values.IsTruthy(left.Evaluate(context));
            if (isAnd ? !l : l)
            {
                return l;
            }

            return Values.IsTruthy(right.Evaluate(context));
        }
    }

    private sealed class CompareNode(Node left, Node right, string op) : Node
    {
        public override object? Evaluate(JsonElement context)
        {
            var l = Values.Normalize(left.Evaluate(context));
            var r = Values.Normalize(right.Evaluate(context));
            return op switch
            {
                "==" => Values.AreEqual(l, r),
                "!=" => !Values.AreEqual(l, r),
                "contains" => Values.Contains(l, r),
                _ => Values.Compare(l, r) is { } order && op switch
                {
                    ">" => order > 0,
                    ">=" => order >= 0,
                    "<" => order < 0,
                    _ => order <= 0,
                },
            };
        }
    }

    private sealed class CallNode(Func<object?[], object?> function, List<Node> args) : Node
    {
        public static CallNode? Create(string name, List<Node> args)
        {
            Func<object?[], object?>? function = (name, args.Count) switch
            {
                ("length", 1) => a => Values.Length(a[0]),
                ("lower", 1) => a => Values.ToText(a[0]).ToLowerInvariant(),
                ("upper", 1) => a => Values.ToText(a[0]).ToUpperInvariant(),
                ("trim", 1) => a => Values.ToText(a[0]).Trim(),
                ("json", 1) => a => PlatformJson.Serialize(Values.ToElement(a[0])),
                ("default", 2) => a => Values.Normalize(a[0]) is null or "" ? a[1] : a[0],
                _ => null,
            };

            if (function is null)
            {
                if (name is "length" or "lower" or "upper" or "trim" or "json" or "default")
                {
                    throw new WorkflowExpressionException($"函数 {name} 的参数个数不对。");
                }

                return null;
            }

            return new CallNode(function, args);
        }

        public override object? Evaluate(JsonElement context)
            => function([.. args.Select(a => a.Evaluate(context))]);
    }
}

/// <summary>
/// 表达式运行时的值处理：来自上下文的 JsonElement 与字面量统一按 JSON 语义比较、转文本。
/// </summary>
internal static class Values
{
    private static readonly JsonElement NullElement = JsonSerializer.SerializeToElement<object?>(null);

    public static object? Member(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    /// <summary>
    /// JSON 基本类型转成 string / double / bool / null，对象和数组保持 JsonElement。
    /// </summary>
    public static object? Normalize(object? value) => value switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object or JsonValueKind.Array => e,
            _ => null,
        },
        int i => (double)i,
        long l => (double)l,
        decimal d => (double)d,
        _ => value,
    };

    public static bool IsTruthy(object? value) => Normalize(value) switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
        JsonElement { ValueKind: JsonValueKind.Array } a => a.GetArrayLength() > 0,
        _ => true,
    };

    public static bool AreEqual(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (string l, string r) => string.Equals(l, r, StringComparison.Ordinal),
        (double l, double r) => l.Equals(r),
        (bool l, bool r) => l == r,
        (JsonElement l, JsonElement r) => JsonElement.DeepEquals(l, r),
        _ => false,
    };

    public static int? Compare(object? left, object? right) => (left, right) switch
    {
        (double l, double r) => l.CompareTo(r),
        (string l, string r) => string.CompareOrdinal(l, r),
        _ => null,
    };

    public static bool Contains(object? haystack, object? needle) => haystack switch
    {
        string s when Normalize(needle) is { } n => s.Contains(ToText(n), StringComparison.OrdinalIgnoreCase),
        JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Any(item => AreEqual(Normalize(item), needle)),
        JsonElement { ValueKind: JsonValueKind.Object } obj when needle is string key => obj.TryGetProperty(key, out _),
        _ => false,
    };

    public static double Length(object? value) => Normalize(value) switch
    {
        string s => s.Length,
        JsonElement { ValueKind: JsonValueKind.Array } a => a.GetArrayLength(),
        JsonElement { ValueKind: JsonValueKind.Object } o => o.EnumerateObject().Count(),
        _ => 0,
    };

    /// <summary>
    /// 插入到文本模板里的写法：字符串原样，null 为空，数字和布尔按 JSON 写法，对象和数组输出 JSON。
    /// </summary>
    public static string ToText(object? value) => Normalize(value) switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        JsonElement e => e.GetRawText(),
        var other => other.ToString() ?? string.Empty,
    };

    public static JsonElement ToElement(object? value) => value switch
    {
        JsonElement e => e.ValueKind == JsonValueKind.Undefined ? NullElement : e,
        null => NullElement,
        _ => PlatformJson.ToElement(value),
    };
}
