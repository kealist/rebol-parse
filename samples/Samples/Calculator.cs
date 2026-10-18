using System.Globalization;
using RebolParse;
using static RebolParse.P;

namespace Samples;

/// <summary>
/// A recursive-descent calculator written as a grammar. The rules decide what is valid; small actions run
/// as each piece matches and drive a value stack, so the parse evaluates as it goes.
/// </summary>
public sealed class Calculator : IDemo
{
    public string Name => "calc";
    public string Title => "Calculator: a grammar whose actions evaluate as it parses";

    private readonly Stack<double> stack = new();
    private readonly Dictionary<string, Rule> rules;

    public Calculator()
    {
        void Binary(Stack<double> s, Func<double, double, double> f) { var b = s.Pop(); s.Push(f(s.Pop(), b)); }
        var actions = new Dictionary<string, Action<Ctx>>
        {
            ["num"] = c => stack.Push(double.Parse((string)c.Vars["n"]!, CultureInfo.InvariantCulture)),
            ["add"] = _ => Binary(stack, (a, b) => a + b),
            ["sub"] = _ => Binary(stack, (a, b) => a - b),
            ["mul"] = _ => Binary(stack, (a, b) => a * b),
            ["div"] = _ => Binary(stack, (a, b) => b == 0 ? throw new DivideByZeroException("division by zero") : a / b),
            ["neg"] = _ => stack.Push(-stack.Pop()),
        };
        rules = Dialect.ReadGrammar("""
            _      = any space
            expr   = term   any [_ "+" term   (@add) | _ "-" term   (@sub)]
            term   = factor any [_ "*" factor (@mul) | _ "/" factor (@div)]
            factor = _ [copy n [some digit opt ["." some digit]] (@num)
                      | "(" expr _ ")"
                      | "-" factor (@neg)]
            calc   = expr _ end
            """, actions).Compiled();   // compiled to IL: this runs on every evaluation
    }

    /// <summary>Evaluate <paramref name="text"/>. Throws <see cref="FormatException"/> when it is not an expression.</summary>
    public double Eval(string text)
    {
        stack.Clear();
        if (!Parser.Parse(text, Ref("calc"), rules).Matched)
        {
            // the longest prefix that is a valid expression tells the user where it went wrong
            stack.Clear();
            var at = Parser.Parse(text, Ref("expr"), rules, partial: true).Position;
            throw new FormatException(at >= text.Length ? "the expression ends too early" : $"cannot read '{text[at]}' at position {at}");
        }
        return stack.Pop();
    }

    public void Run(Report r)
    {
        (string Text, double Value)[] good =
        [
            ("1+2*3", 7), ("(1+2)*3", 9), ("  2 * ( 3 + ( 4 - 1 ) )  ", 12), ("10/4", 2.5),
            ("-3+5", 2), ("2--3", 5), ("1.5*4", 6), ("100-1-1-1", 97), ("2*3*4/6", 4), ("((((7))))", 7),
        ];
        foreach (var (text, value) in good)
            r.Expect($"{text.Trim(),-24} = {value}", Eval(text), value);

        foreach (var text in new[] { "1+*2", "(1+2", "", "1 2", "2*(3+", "abc" })
        {
            try { Eval(text); r.Expect($"'{text}' is rejected", false); }
            catch (FormatException ex) { r.Expect($"'{text}' is rejected", true); r.Say($"'{text}': {ex.Message}"); }
        }

        try { Eval("1/(2-2)"); r.Expect("1/(2-2) throws", false); }
        catch (DivideByZeroException) { r.Expect("1/(2-2) throws DivideByZeroException", true); }
        r.Expect("the parser is reusable after an error", Eval("6/3"), 2);
    }
}
