using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RebolParse;
using static RebolParse.P;

namespace Samples;

/// <summary>
/// Validation rules for objects, written as text: <c>Person.Age &gt; 18 &amp;&amp; Person.Job == "SE"</c>.
/// The grammar reads the rule into a small tree, the tree is checked against the object's type, and the
/// result is a compiled <see cref="Func{T, TResult}"/> to call on as many objects as you like.
/// </summary>
/// <remarks>
/// A rule can read public properties and fields, and nothing else: it cannot call methods. A property
/// reached through a null (<c>Person.Address.City</c> with no address) is null instead of an exception,
/// and a comparison with null is false, except that null equals null (so <c>== null</c> works, and two
/// missing values are equal).
/// <para>
/// A path can also step into a list by position (<c>Order.Lines[0].Qty</c>), into a dictionary by key
/// (<c>Order.Tags["channel"]</c>), and into JSON: once a path reaches a <see cref="JsonDocument"/> or
/// <see cref="JsonElement"/>, the names after it are JSON properties (<c>Order.Metadata.customer.tier</c>,
/// <c>Order.Metadata["ship-to"].city</c>, <c>Order.Metadata.tags[0]</c>). Anything missing, out of range or
/// of another type is null, never an exception.
/// </para>
/// </remarks>
public sealed class RuleDemo : IDemo
{
    public string Name => "rules";
    public string Title => "Object rules: 'Person.Age > 18 && Person.Job == \"SE\"' compiled to a Func<Person, bool>";

    private abstract record Node;
    private sealed record Lit(object? Value) : Node;                 // 18, "SE", true, null
    private sealed record Key(string Name);                          // the "channel" in Order.Tags["channel"]
    /// <summary>A path. The first part is the object's name; the rest are member names, list positions (int) and keys.</summary>
    private sealed record Prop(List<object> Parts) : Node            // Person.Address.City, Order.Lines[0].Qty
    {
        public override string ToString() =>
            string.Concat(Parts.Select((part, i) => part switch { int at => $"[{at}]", Key key => $"[\"{key.Name}\"]", _ => i == 0 ? $"{part}" : $".{part}" }));
    }
    private sealed record Cmp(string Op, Node Left, Node Right) : Node;
    private sealed record Logic(bool IsAnd, Node Left, Node Right) : Node;
    private sealed record Neg(Node Inner) : Node;

    private readonly Stack<Node> stack = new();
    private readonly Dictionary<string, Rule> grammar;

    public RuleDemo()
    {
        void Binary(Func<Node, Node, Node> make) { var right = stack.Pop(); stack.Push(make(stack.Pop(), right)); }
        var actions = new Dictionary<string, Action<Ctx>>
        {
            ["number"] = c => stack.Push(new Lit(Number((string)c.Vars["n"]!))),
            ["text"] = c => stack.Push(new Lit(Regex.Replace((string)c.Vars["s"]!, @"\\(.)", "$1", RegexOptions.Singleline))),
            ["true"] = _ => stack.Push(new Lit(true)),
            ["false"] = _ => stack.Push(new Lit(false)),
            ["null"] = _ => stack.Push(new Lit(null)),
            ["root"] = c => stack.Push(new Prop([(string)c.Vars["p"]!])),
            ["prop"] = c => ((Prop)stack.Peek()).Parts.Add((string)c.Vars["p"]!),
            ["index"] = c => ((Prop)stack.Peek()).Parts.Add(
                int.TryParse((string)c.Vars["i"]!, NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : throw new FormatException($"the position {c.Vars["i"]} is too large")),
            ["key"] = _ => { var key = (Lit)stack.Pop(); ((Prop)stack.Peek()).Parts.Add(new Key((string)key.Value!)); },   // the text rule pushed it
            ["compare"] = c => Binary((l, r) => new Cmp((string)c.Vars["op"]!, l, r)),
            ["and"] = _ => Binary((l, r) => new Logic(true, l, r)),
            ["or"] = _ => Binary((l, r) => new Logic(false, l, r)),
            ["not"] = _ => stack.Push(new Neg(stack.Pop())),
        };
        // lowest precedence first: || then && then ! then a comparison
        grammar = Dialect.ReadGrammar("""
            _       = any space
            rule    = orExpr _ end
            orExpr  = andExpr any [_ "||" andExpr (@or)]
            andExpr = unary   any [_ "&&" unary   (@and)]
            unary   = _ ["!" unary (@not) | "(" orExpr _ ")" | compare]
            compare = value opt [_ copy op ["==" | "!=" | ">=" | "<=" | ">" | "<"] value (@compare)]
            value   = _ [number | text
                       | "true" not idchar (@true) | "false" not idchar (@false) | "null" not idchar (@null)
                       | path]
            number  = copy n [opt "-" some digit opt ["." some digit]] not idchar (@number)
            text    = "^"" copy s any [not ["^"" | "\"] skip | "\" skip] "^"" (@text)
                    | "'"  copy s any [not ["'"  | "\"] skip | "\" skip] "'"  (@text)
            path    = copy p name (@root) any ["." copy p name (@prop) | "[" _ [copy i some digit (@index) | text (@key)] _ "]"]
            name    = charset "a-zA-Z_" any idchar
            idchar  = charset "a-zA-Z0-9_"
            """, actions).Compiled();
    }

    /// <summary>
    /// Compile <paramref name="text"/> into a check for a <typeparamref name="T"/>. Do this once per rule and keep
    /// the result. Throws <see cref="FormatException"/> when the text is not a rule, names a member the type
    /// does not have, or compares things that cannot be compared.
    /// </summary>
    /// <param name="text">The rule.</param>
    /// <param name="name">What the rule calls the object. Defaults to the type's name, so a Person is <c>Person</c>.</param>
    public Func<T, bool> Compile<T>(string text, string? name = null)
    {
        name ??= typeof(T).Name;
        var check = ToExpression<T>(text, name).Compile();
        return item => item is null ? throw new ArgumentNullException(name) : check(item);
    }

    /// <summary>
    /// The rule as an expression tree, for a caller that wants to compile it another way or hand it to a query
    /// provider. Unlike <see cref="Compile"/>, the tree does not check that the object itself is not null.
    /// </summary>
    public Expression<Func<T, bool>> ToExpression<T>(string text, string? name = null)
    {
        name ??= typeof(T).Name;
        var tree = Read(text);
        var root = Expression.Parameter(typeof(T), name);
        return Expression.Lambda<Func<T, bool>>(new Builder(root, name).Condition(tree), root);
    }

    private Node Read(string text)
    {
        lock (stack)   // one value stack per grammar, so one parse at a time
        {
            stack.Clear();
            if (Parser.Parse(text, Ref("rule"), grammar).Matched) return stack.Pop();
            // the longest prefix that is a valid rule tells the user where it went wrong
            stack.Clear();
            var at = Parser.Parse(text, Ref("orExpr"), grammar, partial: true).Position;
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            throw new FormatException(at >= text.Length ? "the rule ends too early" : $"cannot read '{text[at]}' at position {at}");
        }
    }

    private static object Number(string digits)
    {
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (long.TryParse(digits, style & ~NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var whole)) return whole;
        return decimal.TryParse(digits, style, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"the number {digits} is too large");
    }

    /// <summary>Turns the tree into a typed expression, checking every name and comparison against the real types.</summary>
    private sealed class Builder(ParameterExpression root, string rootName)
    {
        private static readonly Dictionary<string, ExpressionType> Operators = new()
        {
            ["=="] = ExpressionType.Equal, ["!="] = ExpressionType.NotEqual,
            [">"] = ExpressionType.GreaterThan, [">="] = ExpressionType.GreaterThanOrEqual,
            ["<"] = ExpressionType.LessThan, ["<="] = ExpressionType.LessThanOrEqual,
        };

        /// <summary>A node used as true or false: a comparison, or a bool property on its own.</summary>
        public Expression Condition(Node node)
        {
            var e = Value(node);
            if (e.Type == typeof(bool)) return e;
            if (e.Type == typeof(bool?)) return Expression.Equal(e, Expression.Constant(true, typeof(bool?)));
            if (IsJson(e.Type)) return Expression.Equal(Expression.Call(Json.Bool, e), Expression.Constant(true, typeof(bool?)));   // only JSON true
            var what = node is Prop p ? p.ToString() : Convert.ToString(((Lit)node).Value, CultureInfo.InvariantCulture) ?? "null";
            throw new FormatException($"'{what}' is not true or false on its own; compare it with something");
        }

        private Expression Value(Node node) => node switch
        {
            Lit l => Expression.Constant(l.Value, l.Value?.GetType() ?? typeof(object)),
            Prop p => Path(p),
            Cmp c => Compare(c.Op, Value(c.Left), Value(c.Right)),
            Logic { IsAnd: true } a => Expression.AndAlso(Condition(a.Left), Condition(a.Right)),
            Logic o => Expression.OrElse(Condition(o.Left), Condition(o.Right)),
            Neg n => Expression.Not(Condition(n.Inner)),
            _ => throw new InvalidOperationException(),
        };

        private Expression Path(Prop path)
        {
            if ((string)path.Parts[0] != rootName)
                throw new FormatException($"'{path}' should start with '{rootName}'");
            // the object itself is never null here: the compiled check refuses a null before it runs
            Expression e = Nullable.GetUnderlyingType(root.Type) != null ? Expression.Property(root, "Value") : root;
            for (var i = 1; i < path.Parts.Count; i++)
            {
                if (IsJson(e.Type))   // inside JSON every step is a lookup at run time
                {
                    e = path.Parts[i] is int at
                        ? Expression.Call(Json.At, e, Expression.Constant(at))
                        : Expression.Call(Json.Get, e, Expression.Constant(path.Parts[i] is Key key ? key.Name : (string)path.Parts[i]));
                    continue;
                }
                e = path.Parts[i] switch
                {
                    int at => Item(e, at, path),
                    Key key => Lookup(e, key.Name, path),
                    var name => Access(e, (string)name, nullSafe: i > 1),
                };
                // reaching JSON: from here the value is a JsonElement? that is null when there is nothing there
                if (e.Type == typeof(JsonDocument)) e = Expression.Call(Json.Root, e);
                else if (Bare(e.Type) == typeof(JsonElement)) e = Expression.Call(Json.Clean, Expression.Convert(e, typeof(JsonElement?)));
            }
            return e;
        }

        /// <summary><c>list[2]</c> for an array or list. Past the end is null.</summary>
        private static Expression Item(Expression list, int at, Prop path)
        {
            var type = Implements(list.Type, typeof(IReadOnlyList<>))
                       ?? throw new FormatException($"'{path}': {Show(list.Type)} is not a list, so it has no [{at}]");
            return Expression.Call(Items.Method("At", type.GetGenericArguments()[0]), list, Expression.Constant(at));
        }

        /// <summary><c>tags["channel"]</c> for a dictionary with string keys. A missing key is null.</summary>
        private static Expression Lookup(Expression dictionary, string key, Prop path)
        {
            var type = Implements(dictionary.Type, typeof(IReadOnlyDictionary<,>));
            if (type == null || type.GetGenericArguments()[0] != typeof(string))
                throw new FormatException($"'{path}': {Show(dictionary.Type)} is not a dictionary with string keys, so it has no [\"{key}\"]");
            return Expression.Call(Items.Method("Get", type.GetGenericArguments()[1]), dictionary, Expression.Constant(key));
        }

        private static Type? Implements(Type type, Type generic) =>
            type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == generic);

        /// <summary>Read a public property or field. When the target can be null, so can the result.</summary>
        private static Expression Access(Expression target, string name, bool nullSafe)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var owner = Bare(target.Type);
            MemberInfo? member = owner.GetProperty(name, flags) is { CanRead: true } property && property.GetIndexParameters().Length == 0
                ? property
                : owner.GetField(name, flags);
            if (member == null) throw new FormatException($"{owner.Name} has no public property or field '{name}'");
            if (!nullSafe || !CanBeNull(target.Type)) return Expression.MakeMemberAccess(target, member);

            // { var t = target; t == null ? null : t.Member }
            var local = Expression.Variable(target.Type);
            var isStruct = target.Type.IsValueType;   // a Nullable<T>
            var access = Expression.MakeMemberAccess(isStruct ? Expression.Property(local, "Value") : local, member);
            var type = CanBeNull(access.Type) ? access.Type : typeof(Nullable<>).MakeGenericType(access.Type);
            Expression isNull = isStruct
                ? Expression.Not(Expression.Property(local, "HasValue"))
                : Expression.ReferenceEqual(local, Expression.Constant(null, target.Type));
            return Expression.Block(type, [local],
                Expression.Assign(local, target),
                Expression.Condition(isNull, Expression.Default(type), Expression.Convert(access, type)));
        }

        private static Expression Compare(string op, Expression left, Expression right)
        {
            if (IsJson(left.Type) || IsJson(right.Type))
            {
                // JSON has no type until it is read, so read it as whatever it is being compared with
                if (left.Type == right.Type)
                    throw new FormatException("two JSON values cannot be compared with each other; compare each with a number, a string, true, false or null");
                var jsonFirst = IsJson(left.Type);
                var (json, other) = jsonFirst ? (left, right) : (right, left);
                var equality = op is "==" or "!=";
                if (IsNullLiteral(other) && equality)
                    return op == "==" ? Expression.Not(Expression.Property(json, "HasValue")) : Expression.Property(json, "HasValue");
                if (other is ConstantExpression { Value: string } && equality)   // compares the JSON text in place: no string is made
                    return op == "==" ? Expression.Call(Json.TextIs, json, other) : Expression.Not(Expression.Call(Json.TextIs, json, other));
                if (!IsNullLiteral(other))
                {
                    var read = other.Type == typeof(string) ? Json.Text
                        : Bare(other.Type) == typeof(bool) ? Json.Bool
                        : !IsNumber(other.Type) ? throw new FormatException($"a JSON value cannot be compared with {Show(other.Type)}")
                        : Type.GetTypeCode(Bare(other.Type)) is TypeCode.Single or TypeCode.Double ? Json.Double
                        : Json.Decimal;
                    json = Expression.Call(read, json);
                    (left, right) = jsonFirst ? (json, other) : (other, json);
                }
            }

            if (IsNullLiteral(left) != IsNullLiteral(right))
            {
                var other = IsNullLiteral(left) ? right : left;
                if (op is not ("==" or "!=")) throw new FormatException($"'{op}' cannot be used with null; use == or !=");
                if (!CanBeNull(other.Type)) throw new FormatException($"{Show(other.Type)} is never null");
                if (IsNullLiteral(left)) left = Expression.Constant(null, other.Type);
                else right = Expression.Constant(null, other.Type);
            }
            else if (EnumName(left, ref right) || EnumName(right, ref left))
            {
                // Person.Level == "Lead": the name was turned into the enum value
            }
            else if (IsNumber(left.Type) && IsNumber(right.Type))
            {
                var type = Widest(left.Type, right.Type);
                left = To(left, type);
                right = To(right, type);
            }
            else if (left.Type != right.Type && Bare(left.Type) == Bare(right.Type))   // a bool against a bool?
            {
                var type = typeof(Nullable<>).MakeGenericType(Bare(left.Type));
                left = To(left, type);
                right = To(right, type);
            }

            if (left.Type != right.Type) throw new FormatException($"cannot compare {Show(left.Type)} with {Show(right.Type)}");
            try { return Expression.MakeBinary(Operators[op], left, right); }
            catch (InvalidOperationException) { throw new FormatException($"'{op}' does not work on {Show(left.Type)}"); }
        }

        /// <summary>When <paramref name="side"/> is an enum and <paramref name="other"/> is a string literal, replace the string with the enum value it names.</summary>
        private static bool EnumName(Expression side, ref Expression other)
        {
            var type = Bare(side.Type);
            if (!type.IsEnum || other is not ConstantExpression { Value: string name }) return false;
            if (!Enum.IsDefined(type, name))
                throw new FormatException($"'{name}' is not a {type.Name}; use one of {string.Join(", ", Enum.GetNames(type))}");
            other = Expression.Constant(Enum.Parse(type, name), side.Type);
            return true;
        }

        /// <summary>The type two numbers are compared as: double if either is floating point, else decimal if either needs it, else long.</summary>
        private static Type Widest(Type a, Type b)
        {
            bool Any(params TypeCode[] codes) => codes.Contains(Type.GetTypeCode(Bare(a))) || codes.Contains(Type.GetTypeCode(Bare(b)));
            var type = Any(TypeCode.Single, TypeCode.Double) ? typeof(double)
                : Any(TypeCode.Decimal, TypeCode.UInt64) ? typeof(decimal)
                : typeof(long);
            return CanBeNull(a) || CanBeNull(b) ? typeof(Nullable<>).MakeGenericType(type) : type;
        }

        private static Expression To(Expression e, Type type) =>
            e.Type == type ? e
            : e is ConstantExpression { Value: IConvertible literal }   // convert a literal now, not on every call
                ? Expression.Constant(Convert.ChangeType(literal, Bare(type), CultureInfo.InvariantCulture), type)
                : Expression.Convert(e, type);

        private static bool IsJson(Type type) => type == typeof(JsonElement?);
        private static bool IsNullLiteral(Expression e) => e is ConstantExpression { Value: null };
        private static Type Bare(Type type) => Nullable.GetUnderlyingType(type) ?? type;
        private static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        private static bool IsNumber(Type type) => !Bare(type).IsEnum && Type.GetTypeCode(Bare(type)) is >= TypeCode.SByte and <= TypeCode.Decimal;
        private static string Show(Type type) => Nullable.GetUnderlyingType(type) is { } bare ? bare.Name + "?" : type.Name.Split('`')[0];
    }

    /// <summary>Reading JSON from a compiled rule. Every method takes and gives null for "nothing there".</summary>
    private static class Json
    {
        public static readonly MethodInfo Root = Of(nameof(RootOf)), Clean = Of(nameof(CleanOf)), Get = Of(nameof(GetOf)), At = Of(nameof(AtOf)),
            TextIs = Of(nameof(TextIsOf)), Text = Of(nameof(TextOf)), Bool = Of(nameof(BoolOf)), Decimal = Of(nameof(DecimalOf)), Double = Of(nameof(DoubleOf));

        private static MethodInfo Of(string name) => typeof(Json).GetMethod(name)!;

        public static JsonElement? RootOf(JsonDocument? document) => document == null ? null : CleanOf(document.RootElement);
        /// <summary>JSON null, and a JsonElement that was never set, count as nothing.</summary>
        public static JsonElement? CleanOf(JsonElement? e) => e is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? e : null;
        public static JsonElement? GetOf(JsonElement? e, string name) =>
            e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var value) ? CleanOf(value) : null;
        public static JsonElement? AtOf(JsonElement? e, int at) =>
            e is { ValueKind: JsonValueKind.Array } a && at < a.GetArrayLength() ? CleanOf(a[at]) : null;
        public static bool TextIsOf(JsonElement? e, string text) => e is { ValueKind: JsonValueKind.String } s && s.ValueEquals(text);
        public static string? TextOf(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
        public static bool? BoolOf(JsonElement? e) => e?.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        public static decimal? DecimalOf(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var value) ? value : null;
        public static double? DoubleOf(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;
    }

    /// <summary>Reading lists and dictionaries from a compiled rule without throwing for a missing item.</summary>
    private static class Items
    {
        /// <summary>The <paramref name="name"/> method for items of <paramref name="type"/>: one version gives a T?, the other a Nullable&lt;T&gt;.</summary>
        public static MethodInfo Method(string name, Type type) =>
            Nullable.GetUnderlyingType(type) != null ? throw new FormatException($"a list or dictionary of {type.GetGenericArguments()[0].Name}? is not supported")
                : typeof(Items).GetMethod(name + (type.IsValueType ? "Value" : ""))!.MakeGenericMethod(type);

        public static T? At<T>(IReadOnlyList<T>? list, int at) where T : class => list != null && at < list.Count ? list[at] : null;
        public static T? AtValue<T>(IReadOnlyList<T>? list, int at) where T : struct => list != null && at < list.Count ? list[at] : null;
        public static T? Get<T>(IReadOnlyDictionary<string, T>? items, string key) where T : class => items != null && items.TryGetValue(key, out var value) ? value : null;
        public static T? GetValue<T>(IReadOnlyDictionary<string, T>? items, string key) where T : struct => items != null && items.TryGetValue(key, out var value) ? value : null;
    }

    private enum Level { Junior, Senior, Lead }
    private sealed record Address(string City, int Zip);
    private sealed record Person(string Name, int Age, string? Job, Level Level, double Height, decimal Salary,
        bool Remote, Address? Address = null, int? Badge = null);

    private sealed record Line(string Sku, int Qty, decimal Price);
    private sealed record Order(int Id, Person Buyer, List<Line> Lines, Dictionary<string, string> Tags, int[] Codes,
        JsonDocument? Metadata, Order? Previous = null);

    /// <summary>Deeper objects: lists, dictionaries, an order that points at another order, and a JSON document of loose metadata.</summary>
    private void RunOrders(Report r, Person ada, Person tim)
    {
        var first = new Order(1, tim, [], [], [], Metadata: null);
        var full = new Order(2, ada, [new("pen", 12, 1.50m), new("desk", 1, 240m)], new() { ["channel"] = "web" }, [7, 9],
            JsonDocument.Parse("""
                {"priority": 5, "source": "web", "gift": true, "ratio": 0.25, "note": null,
                 "customer": {"segment": "enterprise", "score": 87.5},
                 "tags": ["rush", "fragile"], "ship-to": {"city": "London"}}
                """), Previous: first);
        // the same order with metadata whose values are all the wrong type
        var odd = full with { Metadata = JsonDocument.Parse("""{"priority": "high", "gift": "yes", "customer": "n/a", "tags": {}, "source": 7}""") };

        (string Rule, bool Full, bool Odd, bool First)[] cases =
        [
            ("Order.Lines[0].Qty > 10 && Order.Lines[1].Sku == 'desk'", true, true, false),              // a list by position
            ("Order.Lines[5].Price > 0 || Order.Codes[9] == 1", false, false, false),                      // past the end is null
            ("Order.Lines.Count >= 2 && Order.Codes[1] == 9", true, true, false),
            ("Order.Tags['channel'] == 'web'", true, true, false),                                         // a dictionary by key
            ("Order.Tags['missing'] == null", true, true, true),
            ("Order.Previous.Buyer.Age < 18 && Order.Previous.Previous == null", true, true, false),       // an order behind an order
            ("Order.Lines[0].Price < Order.Lines[1].Price", true, true, false),
            ("Order.Metadata.priority >= 3", true, false, false),                                          // JSON: a number
            ("Order.Metadata.source == 'web' && Order.Metadata.source != 'phone'", true, false, false),    // a string
            ("Order.Metadata.gift && Order.Metadata.ratio < 0.5", true, false, false),                     // true on its own, a fraction
            ("Order.Metadata.customer.segment == 'enterprise' && Order.Metadata.customer.score > 80", true, false, false),
            ("Order.Metadata.tags[0] == 'rush' && Order.Metadata.tags[2] == null", true, false, false),    // a JSON array
            ("Order.Metadata['ship-to'].city == Order.Buyer.Address.City", true, false, true),             // JSON against a property; null == null
            ("Order.Metadata.note == null && Order.Metadata.nothing.at.all == null", true, true, true),    // JSON null and missing
            ("Order.Metadata != null && Order.Metadata.priority != 5", false, true, false),                // the wrong type is not 5
            ("Order.Metadata.priority > 3 && Order.Buyer.Level == 'Lead' && Order.Lines.Count > 1", true, false, false),
        ];
        foreach (var (rule, forFull, forOdd, forFirst) in cases)
        {
            var check = Compile<Order>(rule);
            r.Expect($"{rule,-92} full, odd, empty", (check(full), check(odd), check(first)), (forFull, forOdd, forFirst));
        }

        foreach (var rule in new[]
                 {
                     "Order.Id[0] == 1", "Order.Lines['a'] == null", "Order.Tags[0] == 'web'", "Order.Lines[0]", "Order.Lines[99999999999].Qty > 1",
                     "Order.Metadata.a == Order.Metadata.b", "Order.Metadata.level == Order.Buyer.Level", "Order.Metadata.priority > 'x'",
                     "Order.Metadata.priority > null", "Order.Lines[0.5].Qty > 1", "Order.Lines[0.Qty > 1",
                 })
        {
            try { Compile<Order>(rule); r.Expect($"'{rule}' is rejected", false); }
            catch (FormatException ex) { r.Expect($"'{rule}' is rejected", true); r.Say($"'{rule}': {ex.Message}"); }
        }
    }

    public void Run(Report r)
    {
        var ada = new Person("Ada", 36, "SE", Level.Lead, 1.70, 185_000m, Remote: true, new Address("London", 1815), Badge: 7);
        var tim = new Person("Tim", 17, "SE", Level.Junior, 1.82, 0m, Remote: false);
        var bob = new Person("Bob", 52, "PM", Level.Senior, 1.75, 120_000.50m, Remote: false, new Address("Tampa", 33602));

        (string Rule, bool Ada, bool Tim, bool Bob)[] cases =
        [
            ("Person.Age > 18 && Person.Job == \"SE\"", true, false, false),
            ("Person.Age >= 18 && (Person.Job == 'PM' || Person.Level == 'Lead')", true, false, true),   // an enum by name
            ("Person.Job != 'SE' || Person.Age < 18", false, true, true),
            ("!Person.Remote && Person.Height > 1.8", false, true, false),                               // a bool on its own
            ("  Person.Remote  ", true, false, false),
            ("Person.Salary > 120000.25 && Person.Salary != 185000", false, false, true),                // decimals stay exact
            ("Person.Address.City == 'London'", true, false, false),                                     // no address: null, not a crash
            ("Person.Address == null || Person.Address.Zip < 2000", true, true, false),
            ("Person.Address.Zip > Person.Age", true, false, true),                                      // two properties
            ("Person.Badge != null && Person.Badge <= 7", true, false, false),                           // an int?
            ("!(Person.Age < 18) && !!Person.Remote", true, false, false),
            ("Person.Name != \"A\\\"da\" && true", true, true, true),                                    // an escaped quote
        ];
        foreach (var (rule, forAda, forTim, forBob) in cases)
        {
            var check = Compile<Person>(rule);
            r.Expect($"{rule.Trim(),-68} Ada, Tim, Bob", (check(ada), check(tim), check(bob)), (forAda, forTim, forBob));
        }

        // a rule set: compile each rule once, then report what an object fails
        (string Name, Func<Person, bool> Check)[] hiring =
        [
            ("is an adult", Compile<Person>("Person.Age >= 18")),
            ("is an engineer", Compile<Person>("Person.Job == 'SE'")),
            ("has an address", Compile<Person>("Person.Address != null")),
        ];
        string Failed(Person p) => string.Join(", ", hiring.Where(rule => !rule.Check(p)).Select(rule => rule.Name));
        r.Expect("Ada passes every hiring rule", Failed(ada), "");
        r.Expect("Tim fails two", Failed(tim), "is an adult, has an address");
        r.Expect("Bob fails one", Failed(bob), "is an engineer");

        r.Expect("the object can have another name", Compile<Person>("p.Age > 18", "p")(ada), true);
        try { Compile<Person>("Person.Age > 18")(null!); r.Expect("a null object is refused", false); }
        catch (ArgumentNullException) { r.Expect("a null object is refused", true); }

        RunOrders(r, ada, tim);

        // mistakes are found when the rule is compiled, not when it first meets an object
        foreach (var rule in new[]
                 {
                     "Person.Agee > 18", "Employee.Age > 18", "Person.ToString == 'x'", "Person.Age > 'old'", "Person.Age",
                     "Person.Level == 'Boss'", "Person.Name > 'A'", "Person.Age == null", "Person.Badge > null",
                     "Person.Age > 18 &&", "Person.Age = 18", "Person.Age > 18 Person.Job == 'SE'", "(Person.Age > 18", "",
                     "Person.Age > 99999999999999999999999999999999",
                 })
        {
            try { Compile<Person>(rule); r.Expect($"'{rule}' is rejected", false); }
            catch (FormatException ex) { r.Expect($"'{rule}' is rejected", true); r.Say($"'{rule}': {ex.Message}"); }
        }
    }
}
