using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
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
/// and a comparison with null is false, except <c>== null</c> and <c>!= null</c>.
/// </remarks>
public sealed class RuleDemo : IDemo
{
    public string Name => "rules";
    public string Title => "Object rules: 'Person.Age > 18 && Person.Job == \"SE\"' compiled to a Func<Person, bool>";

    private abstract record Node;
    private sealed record Lit(object? Value) : Node;                 // 18, "SE", true, null
    private sealed record Prop(string[] Parts) : Node;               // Person.Address.City
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
            ["path"] = c => stack.Push(new Prop(((string)c.Vars["p"]!).Split('.'))),
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
            path    = copy p [name any ["." name]] (@path)
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
        var tree = Read(text);
        var root = Expression.Parameter(typeof(T), name);
        var check = Expression.Lambda<Func<T, bool>>(new Builder(root, name).Condition(tree), root).Compile();
        return item => item is null ? throw new ArgumentNullException(name) : check(item);
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
            var what = node is Prop p ? string.Join('.', p.Parts) : Convert.ToString(((Lit)node).Value, CultureInfo.InvariantCulture) ?? "null";
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
            if (path.Parts[0] != rootName)
                throw new FormatException($"'{string.Join('.', path.Parts)}' should start with '{rootName}'");
            // the object itself is never null here: the compiled check refuses a null before it runs
            Expression e = Nullable.GetUnderlyingType(root.Type) != null ? Expression.Property(root, "Value") : root;
            for (var i = 1; i < path.Parts.Length; i++) e = Access(e, path.Parts[i], nullSafe: i > 1);
            return e;
        }

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

        private static bool IsNullLiteral(Expression e) => e is ConstantExpression { Value: null };
        private static Type Bare(Type type) => Nullable.GetUnderlyingType(type) ?? type;
        private static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
        private static bool IsNumber(Type type) => !Bare(type).IsEnum && Type.GetTypeCode(Bare(type)) is >= TypeCode.SByte and <= TypeCode.Decimal;
        private static string Show(Type type) => Nullable.GetUnderlyingType(type) is { } bare ? bare.Name + "?" : type.Name;
    }

    private enum Level { Junior, Senior, Lead }
    private sealed record Address(string City, int Zip);
    private sealed record Person(string Name, int Age, string? Job, Level Level, double Height, decimal Salary,
        bool Remote, Address? Address = null, int? Badge = null);

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
