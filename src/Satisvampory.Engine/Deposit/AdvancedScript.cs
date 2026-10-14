// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Text;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Deposit
{
    public enum RuleOpKind
    {
        /// <summary>Put targets at the top, in order (first … then …).</summary>
        First,
        /// <summary>Only these targets, in order, are homes.</summary>
        Only,
        /// <summary>Remove a target, optionally only within one built-in row.</summary>
        Never,
        /// <summary>Reorder built-in rows.</summary>
        Move,
        /// <summary>Tie-break toward a plot ("here" when <see cref="RuleOp.Plot"/> is null).</summary>
        Prefer,
    }

    public sealed class RuleOp
    {
        public RuleOpKind Kind { get; init; }
        public List<Target> Targets { get; init; } = new();
        public BuiltInRow? WhenRow { get; init; }
        public BuiltInRow MoveRow { get; init; }
        public bool MoveAbove { get; init; }
        public BuiltInRow MoveAnchor { get; init; }
        public string? Plot { get; init; }
        public int Line { get; init; }
    }

    public sealed class Rule
    {
        public ItemSelector Selector { get; init; } = ItemSelector.Any;
        public List<RuleOp> Ops { get; } = new();
        public int Line { get; init; }
    }

    public sealed record ScriptMessage(int Line, string Text, bool IsError);

    /// <summary>
    /// Names the script may refer to. Unknown names are errors, so a saved script never points
    /// at nothing. Null sets skip that check (used by tests that only exercise grammar).
    /// </summary>
    public sealed class ScriptNames
    {
        public ISet<string>? ChestNames { get; init; }
        public ISet<string>? Belts { get; init; }
        public ISet<string>? PlotLabels { get; init; }
    }

    public sealed class CompiledScript
    {
        public List<Rule> Rules { get; } = new();
        public List<ScriptMessage> Messages { get; } = new();
        public bool Ok => !Messages.Exists(m => m.IsError);

        public static CompiledScript Empty { get; } = new();
    }

    /// <summary>
    /// The Mass Deposit Advanced language (spec 03). Line based; '#' starts a comment. A line
    /// beginning with "for" starts a rule; other non-blank lines add clauses to the current rule.
    /// There are no loops, variables or arithmetic, so a script cannot hang the server.
    /// </summary>
    public static class AdvancedScript
    {
        public static CompiledScript Compile(string source, ItemCatalog catalog, ScriptNames? names = null)
        {
            var result = new CompiledScript();
            names ??= new ScriptNames();
            Rule? current = null;
            var lines = (source ?? string.Empty).Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNo = i + 1;
                var text = StripComment(lines[i]).Trim();
                if (text.Length == 0) continue;

                List<Token> tokens;
                try { tokens = Tokenize(text); }
                catch (FormatException e) { result.Messages.Add(new(lineNo, e.Message, true)); continue; }

                var r = new Reader(tokens, lineNo);
                try
                {
                    if (r.PeekWord("for"))
                    {
                        r.Next();
                        var selector = ParseSelector(r, catalog);
                        r.Expect(":");
                        current = new Rule { Selector = selector, Line = lineNo };
                        result.Rules.Add(current);
                        if (r.AtEnd) continue;
                    }
                    else if (current == null)
                    {
                        throw new ScriptError(lineNo, "A rule must start with 'for <items>:'");
                    }

                    ParseClauses(r, current, names);
                }
                catch (ScriptError e)
                {
                    result.Messages.Add(new(e.Line, e.Message, true));
                }
            }

            foreach (var rule in result.Rules)
                if (rule.Ops.Count == 0)
                    result.Messages.Add(new(rule.Line, "This rule has no clauses and does nothing", false));
                else if (rule.Selector.Kind == ItemSelectorKind.Category && catalog.Members(rule.Selector.Category).Count == 0)
                    result.Messages.Add(new(rule.Line, $"Category '{rule.Selector.Category}' has no items", false));

            return result;
        }

        static void ParseClauses(Reader r, Rule rule, ScriptNames names)
        {
            while (!r.AtEnd)
            {
                var word = r.Word("a clause (first, then, only, never, move, prefer)");
                switch (word)
                {
                    case "first":
                    case "then":
                    {
                        var t = ParseTarget(r, names);
                        // "then" after "only" extends the only-list; otherwise it extends the first-list.
                        var last = rule.Ops.Count > 0 ? rule.Ops[rule.Ops.Count - 1] : null;
                        if (word == "then" && last != null && (last.Kind == RuleOpKind.Only || last.Kind == RuleOpKind.First))
                            last.Targets.Add(t);
                        else if (word == "then")
                            throw new ScriptError(r.Line, "'then' must follow 'first' or 'only'");
                        else
                            rule.Ops.Add(new RuleOp { Kind = RuleOpKind.First, Targets = { t }, Line = r.Line });
                        break;
                    }
                    case "only":
                        rule.Ops.Add(new RuleOp { Kind = RuleOpKind.Only, Targets = { ParseTarget(r, names) }, Line = r.Line });
                        break;
                    case "never":
                    {
                        var t = ParseTarget(r, names);
                        BuiltInRow? when = null;
                        if (r.PeekWord("when"))
                        {
                            r.Next();
                            r.ExpectWord("rank");
                            r.Expect("=");
                            when = ParseRow(r);
                        }
                        rule.Ops.Add(new RuleOp { Kind = RuleOpKind.Never, Targets = { t }, WhenRow = when, Line = r.Line });
                        break;
                    }
                    case "move":
                    {
                        var row = ParseRow(r);
                        var dir = r.Word("'above' or 'below'");
                        if (dir != "above" && dir != "below") throw new ScriptError(r.Line, "Expected 'above' or 'below'");
                        var anchor = ParseRow(r);
                        rule.Ops.Add(new RuleOp { Kind = RuleOpKind.Move, MoveRow = row, MoveAbove = dir == "above", MoveAnchor = anchor, Line = r.Line });
                        break;
                    }
                    case "prefer":
                        if (r.PeekWord("here"))
                        {
                            r.Next();
                            rule.Ops.Add(new RuleOp { Kind = RuleOpKind.Prefer, Plot = null, Line = r.Line });
                        }
                        else
                        {
                            r.ExpectWord("plot");
                            var label = r.Quoted("a plot label");
                            CheckKnown(names.PlotLabels, label, "plot", r.Line);
                            rule.Ops.Add(new RuleOp { Kind = RuleOpKind.Prefer, Plot = label, Line = r.Line });
                        }
                        break;
                    default:
                        throw new ScriptError(r.Line, $"Unknown clause '{word}'");
                }

                if (!r.AtEnd) r.Expect(",");
            }
        }

        static ItemSelector ParseSelector(Reader r, ItemCatalog catalog)
        {
            if (r.PeekWord("any")) { r.Next(); return ItemSelector.Any; }
            if (r.PeekWord("category"))
            {
                r.Next();
                var g = r.WordOrQuoted("a category name");
                if (!catalog.TryResolveCategory(g, out var key)) throw new ScriptError(r.Line, $"Unknown category '{g}'");
                return new ItemSelector(ItemSelectorKind.Category, Category: key);
            }
            var name = r.Quoted("\"item name\", category <name>, or any");
            if (!catalog.TryResolveItem(name, out var id)) throw new ScriptError(r.Line, $"Unknown item '{name}'");
            return new ItemSelector(ItemSelectorKind.Item, Item: id);
        }

        static Target ParseTarget(Reader r, ScriptNames names)
        {
            var kind = r.Word("a target (chest, belt, general, overflow, plot, special)");
            switch (kind)
            {
                case "chest":
                {
                    var name = r.Quoted("a chest name");
                    CheckKnown(names.ChestNames, name, "chest", r.Line);
                    string? plot = null;
                    if (r.Peek("@"))
                    {
                        r.Next();
                        plot = r.Quoted("a plot label");
                        CheckKnown(names.PlotLabels, plot, "plot", r.Line);
                    }
                    return new Target(TargetKind.Chest, name, plot);
                }
                case "belt":
                {
                    var name = r.Quoted("a belt name");
                    CheckKnown(names.Belts, name, "belt", r.Line);
                    return new Target(TargetKind.Belt, name);
                }
                case "general": return new Target(TargetKind.General);
                case "overflow": return new Target(TargetKind.Overflow);
                case "plot":
                {
                    var label = r.Quoted("a plot label");
                    CheckKnown(names.PlotLabels, label, "plot", r.Line);
                    return new Target(TargetKind.Plot, label);
                }
                case "special":
                {
                    var k = r.WordOrQuoted("a special kind");
                    if (!Enum.TryParse<SpecialKind>(k, true, out var sk) || sk == SpecialKind.None)
                        throw new ScriptError(r.Line, $"Unknown special kind '{k}'");
                    return new Target(TargetKind.Special, Special: sk);
                }
                default:
                    throw new ScriptError(r.Line, $"Unknown target '{kind}'");
            }
        }

        /// <summary>Row names: the display names, or short keys.</summary>
        static BuiltInRow ParseRow(Reader r)
        {
            var text = NameFold.Fold(r.WordOrQuoted("a row name"));
            return text switch
            {
                "priority" or "priority chests" => BuiltInRow.PriorityChests,
                "holding" or "already holding it" => BuiltInRow.AlreadyHolding,
                "exact" or "exact names" => BuiltInRow.ExactNames,
                "category" or "category names" => BuiltInRow.CategoryNames,
                "general" or "general chests" => BuiltInRow.General,
                "holding-any" or "holding it, any name" => BuiltInRow.HoldingAnyName,
                "overflow" => BuiltInRow.Overflow,
                _ => throw new ScriptError(r.Line, $"Unknown row '{text}'"),
            };
        }

        public static string RowDisplayName(BuiltInRow row) => row switch
        {
            BuiltInRow.PriorityChests => "Priority chests",
            BuiltInRow.AlreadyHolding => "Already holding it",
            BuiltInRow.ExactNames => "Exact names",
            BuiltInRow.CategoryNames => "Category names",
            BuiltInRow.General => "General chests",
            BuiltInRow.HoldingAnyName => "Holding it, any name",
            _ => "Overflow",
        };

        static void CheckKnown(ISet<string>? known, string name, string what, int line)
        {
            if (known != null && !known.Contains(NameFold.Fold(name)))
                throw new ScriptError(line, $"No {what} named '{name}'");
        }

        static string StripComment(string line)
        {
            var inQuote = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inQuote = !inQuote;
                else if (line[i] == '#' && !inQuote) return line.Substring(0, i);
            }
            return line;
        }

        // ---- tokens ----

        enum TokKind { Word, Quoted, Punct }

        readonly record struct Token(TokKind Kind, string Text);

        static List<Token> Tokenize(string text)
        {
            var list = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"')
                {
                    var end = text.IndexOf('"', i + 1);
                    if (end < 0) throw new FormatException("Unclosed quote");
                    list.Add(new Token(TokKind.Quoted, text.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                    continue;
                }
                if (c is ':' or ',' or '@' or '=')
                {
                    list.Add(new Token(TokKind.Punct, c.ToString()));
                    i++;
                    continue;
                }
                var sb = new StringBuilder();
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (':' or ',' or '@' or '=' or '"'))
                    sb.Append(text[i++]);
                list.Add(new Token(TokKind.Word, sb.ToString().ToLowerInvariant()));
            }
            return list;
        }

        sealed class ScriptError : Exception
        {
            public ScriptError(int line, string message) : base(message) => Line = line;
            public int Line { get; }
        }

        sealed class Reader
        {
            readonly List<Token> _t;
            int _i;

            public Reader(List<Token> tokens, int line)
            {
                _t = tokens;
                Line = line;
            }

            public int Line { get; }
            public bool AtEnd => _i >= _t.Count;

            public void Next() => _i++;

            public bool PeekWord(string w) => !AtEnd && _t[_i].Kind == TokKind.Word && _t[_i].Text == w;

            public bool Peek(string punct) => !AtEnd && _t[_i].Kind == TokKind.Punct && _t[_i].Text == punct;

            public void Expect(string punct)
            {
                if (!Peek(punct)) throw new ScriptError(Line, $"Expected '{punct}'{Got()}");
                _i++;
            }

            public void ExpectWord(string w)
            {
                if (!PeekWord(w)) throw new ScriptError(Line, $"Expected '{w}'{Got()}");
                _i++;
            }

            public string Word(string what)
            {
                if (AtEnd || _t[_i].Kind != TokKind.Word) throw new ScriptError(Line, $"Expected {what}{Got()}");
                return _t[_i++].Text;
            }

            public string Quoted(string what)
            {
                if (AtEnd || _t[_i].Kind != TokKind.Quoted) throw new ScriptError(Line, $"Expected {what} in quotes{Got()}");
                return _t[_i++].Text;
            }

            public string WordOrQuoted(string what)
            {
                if (AtEnd || _t[_i].Kind == TokKind.Punct) throw new ScriptError(Line, $"Expected {what}{Got()}");
                return _t[_i++].Text;
            }

            string Got() => AtEnd ? " at end of line" : $", found '{_t[_i].Text}'";
        }
    }
}
