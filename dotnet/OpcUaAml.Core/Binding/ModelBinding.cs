// Binding a plant model of one's own to the variables of a server at once.
// Names in a model and on a PLC are rarely the same: SpindleSpeed below Drill
// is DV_Drill_spindle/Speed on the practice plant, SensorOccupied is
// GVL/DI_FB_SensorOccupied on a CODESYS controller. A signal is matched with a
// variable by the words of both paths: its own name counts most, its parents
// less; a word few variables carry counts more than one most carry (the
// station's name). An element of the model that clearly stands for an object
// of the server (ProcessingStation for ST30_Processing) keeps the signals below
// it to the variables below that object. Numbers in a name must agree
// (Motor1 is not Motor2), and a value of another kind (a boolean for a double)
// is never proposed. Each variable goes to one signal. What is sure is kept;
// the rest is proposed for the user to check.

using System.Globalization;
using System.Text;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using OpcUaAml.Addressing;
using OpcUaAml.Server;
using OpcUaAml.Types;

namespace OpcUaAml.Binding;

/// <summary>A value of the model to bind: an element, by the names of its path from the root picked.</summary>
public sealed record ModelSignal(IReadOnlyList<string> Path, string? DataType, UaNodeAddress? BoundTo)
{
    /// <summary>The element; null for a signal made up in a test.</summary>
    public InternalElementType? Element { get; init; }

    public string PathText => string.Join("/", Path);
}

/// <summary>A variable of the server, by the browse names of its path from the start browsed.</summary>
public sealed record ServerVariable(UaNodeAddress Address, IReadOnlyList<string> Path, string? DataType)
{
    public string PathText => string.Join("/", Path);
}

public enum BindingConfidence
{
    /// <summary>No variable fits.</summary>
    None,
    /// <summary>A variable fits, but another nearly as well: the user decides.</summary>
    Check,
    /// <summary>Kept: clearly the best, though not every word agrees.</summary>
    Likely,
    /// <summary>Kept: the words agree.</summary>
    Sure,
    /// <summary>The element has a NodeId already; it stays as it is.</summary>
    Bound,
}

/// <summary>The variable proposed for one signal, the ones next in line, and whether it is kept.</summary>
public sealed record BindingProposal(
    ModelSignal Signal,
    ServerVariable? Variable,
    double Score,
    BindingConfidence Confidence,
    IReadOnlyList<string> Shared,
    IReadOnlyList<ServerVariable> Candidates)
{
    /// <summary>Bound when the user does not change it.</summary>
    public bool Keep => Confidence is BindingConfidence.Sure or BindingConfidence.Likely;

    public bool BoundAlready => Confidence == BindingConfidence.Bound;

    public override string ToString() =>
        $"{Signal.PathText} -> {Variable?.PathText ?? "(none)"} {Confidence} {Score.ToString("0.00", CultureInfo.InvariantCulture)}"
        + $" [{string.Join(", ", Shared)}]; next: {string.Join(", ", Candidates.Where(c => c != Variable).Select(c => c.PathText))}";
}

/// <summary>An element of the model that stands for an object of the server: the signals below it look there first.</summary>
public sealed record StandsFor(string Element, string Object);

/// <summary>What the model and the server have to say to each other.</summary>
public sealed record BindingPlan(IReadOnlyList<BindingProposal> Proposals, IReadOnlyList<ServerVariable> Unmatched)
{
    public IReadOnlyList<StandsFor> Stands { get; init; } = [];
}

/// <summary>Words of a name, as model and PLC names are written: DI_FB_SensorOccupied is di fb sensor occupied.</summary>
public static class NameWords
{
    public static IReadOnlyList<string> Of(string name)
    {
        var text = Fold(name);
        var words = new List<string>();
        var word = new StringBuilder();
        void Cut() { if (word.Length > 0) { words.Add(word.ToString().ToLowerInvariant()); word.Clear(); } }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) { Cut(); continue; }
            if (word.Length > 0)
            {
                var p = text[i - 1];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (char.IsDigit(c) != char.IsDigit(p)
                    || (char.IsUpper(c) && char.IsLower(p))
                    || (char.IsUpper(c) && char.IsUpper(p) && char.IsLower(next)))
                    Cut();
            }
            word.Append(c);
        }
        Cut();
        return words;
    }

    /// <summary>German letters written out, as PLC names have them: Füllstand is Fuellstand.</summary>
    private static string Fold(string name) => name
        .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss")
        .Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue");
}

public static class ModelBinder
{
    /// <summary>
    /// An element of the user's own: not below an element taken from a server
    /// and not an instance of a UA type, whose values the server describes itself.
    /// </summary>
    public static bool IsOwn(InternalElementType element)
    {
        for (var e = element; e != null; e = e.CAEXParent as InternalElementType)
            if (UaTypes.IsUaLibraryPath(e.RefBaseSystemUnitPath) || e.Attribute[AddressSpaceMirror.EndpointAttribute] != null) return false;
        return true;
    }

    /// <summary>The instance hierarchies that hold elements of the user's own.</summary>
    public static IReadOnlyList<CAEXObject> Models(CAEXDocument document) =>
        document.CAEXFile.InstanceHierarchy.Where(ih => ih.Descendants<InternalElementType>().Any(IsOwn)).Cast<CAEXObject>().ToList();

    /// <summary>The signals of a model that are the user's own.</summary>
    public static IReadOnlyList<ModelSignal> OwnSignals(CAEXObject model) =>
        Signals(model).Where(s => s.Element == null || IsOwn(s.Element)).ToList();

    /// <summary>
    /// The plan as CSV with semicolons: every value with its variable, the
    /// match and whether it is bound (kept), then the variables no value got.
    /// </summary>
    public static string Report(BindingPlan plan)
    {
        var text = new StringBuilder();
        void Line(params string?[] cells) => text.Append(string.Join(";", cells.Select(Cell))).Append(Environment.NewLine);
        Line("Value of the model", "Variable of the server", "NodeId", "Match", "Bind", "Words in common");
        foreach (var p in plan.Proposals)
            Line(p.Signal.PathText, p.Variable?.PathText, p.Variable?.Address.ToString(), MatchWord(p.Confidence), p.Keep ? "yes" : "no", string.Join(", ", p.Shared));
        text.Append(Environment.NewLine);
        Line("Variables without a value of the model", "NodeId");
        var used = plan.Proposals.Where(p => p.Keep || p.BoundAlready).Select(p => p.Variable).OfType<ServerVariable>().ToHashSet();
        foreach (var v in plan.Unmatched.Concat(plan.Proposals.Select(p => p.Variable).OfType<ServerVariable>()).Distinct().Where(v => !used.Contains(v)))
            Line(v.PathText, v.Address.ToString());
        return text.ToString();
    }

    /// <summary>The confidence as the report and the command line name it.</summary>
    public static string MatchWord(BindingConfidence confidence) => confidence switch
    {
        BindingConfidence.Sure => "sure",
        BindingConfidence.Likely => "likely",
        BindingConfidence.Check => "check",
        BindingConfidence.Bound => "bound already",
        _ => "none found",
    };

    private static string Cell(string? s) =>
        s == null ? "" : s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>
    /// The signals of a model: below <paramref name="root"/>, every element
    /// without elements of its own, and every element with a Value attribute.
    /// </summary>
    public static IReadOnlyList<ModelSignal> Signals(CAEXObject root)
    {
        var signals = new List<ModelSignal>();
        void Walk(IEnumerable<InternalElementType> elements, List<string> path)
        {
            foreach (var e in elements)
            {
                path.Add(e.Name ?? "");
                var children = e.InternalElement.ToList();
                var value = e.Attribute["Value"];
                if (children.Count == 0 || value != null)
                {
                    UaNodeAddress? bound = null;
                    try { bound = AnnexANodeId.Of(e); }
                    catch (AddressingException) { /* a browse path or alias: bound, though not to an address */ }
                    signals.Add(new ModelSignal(path.ToArray(), NonEmpty(value?.AttributeDataType), bound) { Element = e });
                }
                Walk(children, path);
                path.RemoveAt(path.Count - 1);
            }
        }
        var start = new List<string> { root.Name ?? "" };
        switch (root)
        {
            case InstanceHierarchyType ih: Walk(ih.InternalElement, start); break;
            case InternalElementType ie: Walk(ie.InternalElement, start); break;
            default: throw new ArgumentException("A model is an InstanceHierarchy or an InternalElement.", nameof(root));
        }
        return signals;
    }

    /// <summary>
    /// Gives the element of each proposal its variable's address as its NodeId
    /// (Annex A) with <paramref name="serverUri"/>, and a Value attribute, as
    /// binding one by one does. Returns how many were bound.
    /// </summary>
    public static int Apply(IEnumerable<BindingProposal> proposals, string? serverUri)
    {
        var n = 0;
        foreach (var p in proposals)
        {
            if (p.BoundAlready || p.Variable == null || p.Signal.Element is not { } element) continue;
            AnnexANodeId.Write(element, p.Variable.Address with { ServerUri = serverUri });
            if (element.Attribute["Value"] == null) element.Attribute.Append("Value");
            n++;
        }
        return n;
    }

    // Weights of the words by their level: own name first, then the parents.
    private static readonly double[] SignalLevels = [1.0, 0.6, 0.4, 0.25];
    private static readonly double[] VariableLevels = [1.0, 0.8, 0.6, 0.4];
    private const double Floor = 0.69;          // the rarity of a word the server does not use: ln 2
    private const double Least = 0.25;          // below this a variable is not proposed
    private const double Elsewhere = 0.5;       // a variable outside the object its signal's parent stands for
    private const int Shown = 5;

    /// <summary>Proposes a variable for each signal; see the head of this file.</summary>
    public static BindingPlan Propose(IReadOnlyList<ModelSignal> signals, IReadOnlyList<ServerVariable> variables)
    {
        var corpus = new Corpus(variables);
        var objects = ObjectsOf(variables, corpus);
        var stands = StandsFor(signals, objects, corpus);

        var reserved = new HashSet<UaNodeAddress>();
        var byAddress = variables.GroupBy(v => Bare(v.Address)).ToDictionary(g => g.Key, g => g.First());
        var scored = new List<(int Signal, ServerVariable Variable, double Score, IReadOnlyList<string> Shared)>();
        var raw = new List<(ServerVariable Variable, double Score, IReadOnlyList<string> Shared)>[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            raw[i] = [];
            if (signals[i].BoundTo is { } b) { reserved.Add(Bare(b)); continue; }
            var words = Weighted(signals[i].Path, SignalLevels);
            foreach (var v in corpus.Near(words.Where(w => w.Level == 0).Select(w => w.Word)))
            {
                var (score, shared) = Score(words, signals[i].DataType, corpus.WordsOf(v), v.DataType, corpus);
                if (score > 0) raw[i].Add((v, score, shared));
            }
        }
        // An object stands for an element of the model only where a signal below
        // that element finds something below the object: ProcessingStation is not
        // StationState, though "station" is in that name, when no value of the
        // station is below it.
        stands = stands.Where(s => Enumerable.Range(0, signals.Count).Any(i => IsBelow(signals[i].Path, s.Prefix)
            && raw[i].Any(c => c.Score >= Least && c.Variable.PathText.StartsWith(s.Object + "/", StringComparison.Ordinal)))).ToList();

        var candidates = new List<(ServerVariable Variable, double Score, IReadOnlyList<string> Shared)>[signals.Count];
        for (var i = 0; i < signals.Count; i++)
        {
            candidates[i] = [];
            foreach (var (v, first, shared) in raw[i])
            {
                var score = first;
                foreach (var (prefix, obj) in stands.Where(s => IsBelow(signals[i].Path, s.Prefix)).Select(s => (s.Prefix, s.Object)))
                    if (!v.PathText.StartsWith(obj + "/", StringComparison.Ordinal)) score *= Elsewhere;
                if (score < Least) continue;
                candidates[i].Add((v, score, shared));
            }
            candidates[i].Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var c in candidates[i]) scored.Add((i, c.Variable, c.Score, c.Shared));
        }

        // Each variable to one signal: the best pairs first.
        var chosen = new (ServerVariable Variable, double Score, IReadOnlyList<string> Shared)?[signals.Count];
        var taken = new HashSet<UaNodeAddress>(reserved);
        foreach (var s in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Signal))
        {
            if (chosen[s.Signal] != null || taken.Contains(Bare(s.Variable.Address))) continue;
            chosen[s.Signal] = (s.Variable, s.Score, s.Shared);
            taken.Add(Bare(s.Variable.Address));
        }

        var proposals = new List<BindingProposal>();
        for (var i = 0; i < signals.Count; i++)
        {
            var shown = candidates[i].Take(Shown).Select(c => c.Variable).ToList();
            if (signals[i].BoundTo is { } b)
            {
                proposals.Add(new BindingProposal(signals[i], byAddress.GetValueOrDefault(Bare(b)), 1, BindingConfidence.Bound, [], []));
                continue;
            }
            if (chosen[i] is not { } c)
            {
                proposals.Add(new BindingProposal(signals[i], null, 0, BindingConfidence.None, [], shown));
                continue;
            }
            if (!shown.Contains(c.Variable)) shown.Insert(0, c.Variable);
            var next = candidates[i].Where(o => o.Variable != c.Variable).Select(o => o.Score).DefaultIfEmpty(0).Max();
            var confidence = c.Score >= 0.75 && c.Score - next >= 0.15 ? BindingConfidence.Sure
                : c.Score >= 0.6 && c.Score - next >= 0.1 ? BindingConfidence.Likely
                : BindingConfidence.Check;
            proposals.Add(new BindingProposal(signals[i], c.Variable, c.Score, confidence, c.Shared, shown));
        }
        var unmatched = variables.Where(v => !taken.Contains(Bare(v.Address))).ToList();
        return new BindingPlan(proposals, unmatched) { Stands = stands.Select(s => new StandsFor(string.Join("/", s.Prefix), s.Object)).ToList() };
    }

    private static UaNodeAddress Bare(UaNodeAddress a) => a with { ServerUri = null };

    private static bool IsBelow(IReadOnlyList<string> path, IReadOnlyList<string> prefix) =>
        path.Count > prefix.Count && prefix.Select((p, i) => p == path[i]).All(x => x);

    // ── Containers: an element of the model that stands for an object of the server ──

    private sealed record ServerObject(string Key, IReadOnlyList<(string Word, int Level, double Weight)> Words);

    private static List<ServerObject> ObjectsOf(IReadOnlyList<ServerVariable> variables, Corpus corpus)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var v in variables)
            for (var n = 1; n < v.Path.Count; n++) keys.Add(string.Join("/", v.Path.Take(n)));
        return keys.Select(k => new ServerObject(k, Weighted(k.Split('/'), VariableLevels))).ToList();
    }

    private static List<(IReadOnlyList<string> Prefix, string Object)> StandsFor(
        IReadOnlyList<ModelSignal> signals, List<ServerObject> objects, Corpus corpus)
    {
        var containers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in signals)
            for (var n = 1; n < s.Path.Count; n++) containers.Add(string.Join("/", s.Path.Take(n)));
        var stands = new List<(IReadOnlyList<string>, string)>();
        foreach (var c in containers)
        {
            var path = c.Split('/');
            var words = Weighted(path, SignalLevels);
            var ranked = objects
                .Select(o => (o.Key, Score: Score(words, null, o.Words, null, corpus).Score))
                .Where(o => o.Score > 0).OrderByDescending(o => o.Score).ToList();
            if (ranked.Count == 0 || ranked[0].Score < 0.3) continue;
            var best = ranked[0].Key;
            // Objects above or below the best one do not compete with it.
            var rival = ranked.Skip(1).Where(o => !o.Key.StartsWith(best + "/", StringComparison.Ordinal) && !best.StartsWith(o.Key + "/", StringComparison.Ordinal))
                .Select(o => o.Score).DefaultIfEmpty(0).Max();
            if (ranked[0].Score - rival < 0.15) continue;
            // An object above the best one that fits nearly as well is taken
            // instead: ProcessingStation is ST30_Processing, not its StationState,
            // though "station" is in the name of that one only. The wider
            // object keeps fewer variables from the signals below.
            var wider = ranked
                .Where(o => best.StartsWith(o.Key + "/", StringComparison.Ordinal) && o.Score >= 0.3 && o.Score >= ranked[0].Score / 2)
                .OrderBy(o => o.Key.Length).Select(o => o.Key).FirstOrDefault();
            stands.Add((path, wider ?? best));
        }
        return stands;
    }

    // ── Scoring ──

    private static List<(string Word, int Level, double Weight)> Weighted(IReadOnlyList<string> path, double[] levels)
    {
        var best = new Dictionary<string, (int Level, double Weight)>(StringComparer.Ordinal);
        for (var k = 0; k < path.Count; k++)
        {
            var level = path.Count - 1 - k;
            var weight = levels[Math.Min(level, levels.Length - 1)];
            foreach (var w in NameWords.Of(path[k]))
                if (!best.TryGetValue(w, out var b) || b.Weight < weight) best[w] = (level, weight);
        }
        return best.Select(b => (b.Key, b.Value.Level, b.Value.Weight)).ToList();
    }

    /// <summary>
    /// How well the variable covers the signal's words, weighted by level and
    /// rarity, a little less where the variable's own words are not the
    /// signal's; 0 where a number disagrees, the kinds of value differ, or no
    /// word of the signal's own name is in the variable's name or its parent's.
    /// </summary>
    private static (double Score, IReadOnlyList<string> Shared) Score(
        List<(string Word, int Level, double Weight)> signal, string? signalType,
        IReadOnlyList<(string Word, int Level, double Weight)> variable, string? variableType, Corpus corpus)
    {
        if (!Kinds.Agree(signalType, variableType)) return (0, []);
        var near = signal.Where(w => w.Level <= 1 && IsNumber(w.Word)).Select(w => w.Word).ToList();
        if (near.Count > 0 && !near.All(n => variable.Any(v => v.Word == n))) return (0, []);

        double covered = 0, total = 0;
        var own = false;
        var shared = new List<string>();
        foreach (var (word, level, weight) in signal)
        {
            double match = 0; string? via = null; int viaLevel = 0;
            foreach (var v in variable)
            {
                var m = Words.Match(word, v.Word) * v.Weight;
                if (m > match) { match = m; via = v.Word; viaLevel = v.Level; }
            }
            var rarity = corpus.Rarity(word) ?? (via != null ? corpus.Rarity(via) : null) ?? Floor;
            total += weight * rarity;
            if (match > 0)
            {
                covered += weight * rarity * match;
                shared.Add(word);
                if (level == 0 && viaLevel <= 1) own = true;
            }
        }
        if (!own || total <= 0) return (0, []);
        var score = covered / total;

        // The variable's own words and its parent's the signal does not have.
        double mine = 0, all = 0;
        foreach (var v in variable.Where(v => v.Level <= 1))
        {
            var rarity = corpus.Rarity(v.Word) ?? Floor;
            all += v.Weight * rarity;
            if (signal.Any(s => Words.Match(s.Word, v.Word) > 0)) mine += v.Weight * rarity;
        }
        if (all > 0) score *= 0.75 + 0.25 * mine / all;
        return (score, shared);
    }

    private static bool IsNumber(string word) => word.Length > 0 && char.IsDigit(word[0]);

    /// <summary>The variables' words, how many variables carry each, and which variables a word leads to.</summary>
    private sealed class Corpus
    {
        private readonly Dictionary<string, int> _count = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ServerVariable>> _near = new(StringComparer.Ordinal);
        private readonly Dictionary<ServerVariable, List<(string Word, int Level, double Weight)>> _words = new(ReferenceEqualityComparer.Instance);
        private readonly int _n;

        public Corpus(IReadOnlyList<ServerVariable> variables)
        {
            _n = variables.Count;
            foreach (var v in variables)
            {
                var words = Weighted(v.Path, VariableLevels);
                _words[v] = words;
                foreach (var w in words)
                {
                    _count[w.Word] = _count.GetValueOrDefault(w.Word) + 1;
                    if (w.Level <= 1)
                    {
                        if (!_near.TryGetValue(w.Word, out var list)) _near[w.Word] = list = [];
                        list.Add(v);
                    }
                }
            }
        }

        public IReadOnlyList<(string Word, int Level, double Weight)> WordsOf(ServerVariable v) => _words[v];

        /// <summary>ln(1 + n/df): a word few variables carry says more. Null for a word none carries.</summary>
        public double? Rarity(string word) =>
            _count.TryGetValue(word, out var df) ? Math.Log(1 + (double)_n / df) : null;

        /// <summary>The variables whose own name or parent's name has a word that matches one of these.</summary>
        public IEnumerable<ServerVariable> Near(IEnumerable<string> words)
        {
            var found = new HashSet<ServerVariable>(ReferenceEqualityComparer.Instance);
            foreach (var w in words)
                foreach (var (key, list) in _near)
                    if (Words.Match(w, key) > 0)
                        foreach (var v in list) found.Add(v);
            return found;
        }
    }

    /// <summary>When two words name the same: equal, written for one another, or one short for the other.</summary>
    private static class Words
    {
        private static readonly string[][] Groups =
        [
            ["speed", "velocity", "rpm", "spd", "drehzahl", "geschwindigkeit"],
            ["count", "counter", "cnt", "anzahl", "zaehler"],
            ["temperature", "temp", "temperatur"],
            ["pressure", "druck"],
            ["position", "pos"],
            ["level", "fuellstand", "niveau"],
            ["setpoint", "sollwert", "soll"],
            ["actual", "act", "istwert", "ist"],
            ["running", "run", "laeuft"],
            ["error", "fault", "fehler", "stoerung"],
            ["conveyor", "band", "foerderband"],
            ["weight", "gewicht"],
            ["voltage", "spannung"],
            ["power", "leistung"],
            ["flow", "durchfluss"],
            ["valve", "ventil"],
            ["pump", "pumpe"],
            ["cylinder", "zylinder"],
            ["spindle", "spindel"],
            ["door", "tuer"],
            ["time", "zeit"],
            // Abbreviations of the plant's parts, as PLC and device names use them (ST10, MD_, DV_).
            ["station", "st", "stn"],
            ["module", "md", "mod"],
            ["device", "dv", "dev"],
            ["control", "ctrl"],
        ];

        private static readonly Dictionary<string, int> Group =
            Groups.SelectMany((g, i) => g.Select(w => (w, i))).ToDictionary(x => x.w, x => x.i, StringComparer.Ordinal);

        public static double Match(string a, string b)
        {
            if (a == b) return 1;
            if (IsNumber(a) || IsNumber(b)) return 0;
            if (Group.TryGetValue(a, out var ga) && Group.TryGetValue(b, out var gb) && ga == gb) return 0.9;
            var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
            return shorter.Length >= 3 && longer.StartsWith(shorter, StringComparison.Ordinal) ? 0.8 : 0;
        }
    }

    /// <summary>The kind of a value, from an AML data type (xs:) or an OPC UA built-in type.</summary>
    private static class Kinds
    {
        public static bool Agree(string? aml, string? ua)
        {
            var a = Of(aml);
            var b = Of(ua);
            return a == null || b == null || a == b;
        }

        private static string? Of(string? type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            var t = type.StartsWith("xs:", StringComparison.Ordinal) ? type[3..] : type;
            return t.ToLowerInvariant() switch
            {
                "boolean" => "bool",
                "double" or "float" or "decimal" or "int" or "integer" or "long" or "short" or "byte" or "unsignedint"
                    or "unsignedlong" or "unsignedshort" or "unsignedbyte" or "nonnegativeinteger" or "positiveinteger"
                    or "negativeinteger" or "nonpositiveinteger" or "sbyte" or "int16" or "int32" or "int64"
                    or "uint16" or "uint32" or "uint64" => "number",
                "string" or "normalizedstring" or "token" or "anyuri" or "localizedtext" or "qualifiedname" => "text",
                "datetime" or "date" or "time" or "duration" => "time",
                _ => null,
            };
        }
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
