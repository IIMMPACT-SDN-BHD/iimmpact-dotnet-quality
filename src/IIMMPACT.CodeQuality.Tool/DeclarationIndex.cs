using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

internal sealed record EntityKey(CompilationKey Compilation, string Source, string Declaration);

internal sealed class DeclarationIndex
{
    private const string FileDeclaration = "file";
    // Partial declarations of one type or method share an identity and own several spans.
    private readonly Dictionary<string, List<TextSpan>> _byIdentity;
    private readonly List<(TextSpan Span, string Identity)> _bySpan;
    private readonly SyntaxNode _root;
    private readonly SourceText _text;
    private readonly string _prefix;

    private DeclarationIndex(SyntaxTree tree, CompilationKey compilation, string source)
    {
        _root = tree.GetRoot();
        _text = tree.GetText();
        _prefix = $"{compilation.Project}@{compilation.TargetFramework}|{source}|";
        _byIdentity = new(StringComparer.Ordinal);
        _bySpan = [];
        foreach (var node in _root.DescendantNodes())
        {
            if (DeclarationNamer.TryName(node) is not { } name) continue;
            var identity = name;
            if (_byIdentity.TryGetValue(name, out var spans) && !IsPartial(node))
            {
                // Same-named declarations in separate scopes, such as local functions in sibling
                // blocks, are distinguished by their order in the file.
                var ordinal = 2;
                while (_byIdentity.ContainsKey($"{name}#{ordinal}")) ordinal++;
                identity = $"{name}#{ordinal}";
            }
            if (!_byIdentity.TryGetValue(identity, out spans)) _byIdentity[identity] = spans = [];
            spans.Add(node.Span);
            _bySpan.Add((node.Span, identity));
        }
    }

    public string? OwnerOf(int line, int column) => OwnerOf(line, column, out _)?.Identity;

    public (string Identity, TextSpan Span)? OwnerOf(int line, int column, out int firstLine)
    {
        firstLine = 0;
        if (line < 1 || line > _text.Lines.Count) throw new ScanException($"diagnostic line {line} is outside the source");
        var sourceLine = _text.Lines[line - 1];
        if (column < 1 || column > sourceLine.SpanIncludingLineBreak.Length + 1)
            throw new ScanException($"diagnostic column {column} is outside source line {line}");
        var offset = Math.Min(sourceLine.End, sourceLine.Start + column - 1);
        var owners = _bySpan.Where(d => d.Span.Contains(offset)).OrderBy(d => d.Span.Length).ToList();
        if (owners.Count == 0) return null;
        if (owners.Count > 1 && owners[0].Span == owners[1].Span)
            throw new ScanException($"ambiguous declaration at {line},{column}");
        var owner = owners[0];
        firstLine = _text.Lines.GetLineFromPosition(owner.Span.Start).LineNumber + 1;
        return (_prefix + owner.Identity, owner.Span);
    }

    public string TokenDigestOf(string declaration)
    {
        var tokens = TokensOf(declaration);

        using var buffer = new MemoryStream();
        foreach (var token in tokens)
        {
            WriteField(buffer, token.RawKind.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteField(buffer, token.ValueText);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    public TokenSite TokenSiteOf(string declaration, int line, int column)
    {
        if (line < 1 || line > _text.Lines.Count) throw new ScanException($"diagnostic line {line} is outside the source");
        var sourceLine = _text.Lines[line - 1];
        var position = Math.Min(sourceLine.End, sourceLine.Start + column - 1);
        var tokens = TokensOf(declaration).ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Span.Contains(position) || token.Span.End == position)
                return new TokenSite(index, Math.Clamp(position - token.SpanStart, 0, token.Span.Length));
        }
        throw new ScanException($"diagnostic at {line},{column} does not map to a token in '{declaration}'");
    }

    private IEnumerable<SyntaxToken> TokensOf(string declaration)
    {
        if (declaration == FileDeclaration) return _root.DescendantTokens(descendIntoTrivia: false);
        if (_byIdentity.TryGetValue(declaration, out var spans))
            return spans.SelectMany(span => _root.FindNode(span, getInnermostNodeForTie: true).DescendantTokens(descendIntoTrivia: false));
        throw new ScanException($"declaration '{declaration}' is not present in the syntax index");
    }

    private static bool IsPartial(SyntaxNode node) =>
        node is MemberDeclarationSyntax member && member.Modifiers.Any(SyntaxKind.PartialKeyword);

    private static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(BitConverter.GetBytes(bytes.Length));
        stream.Write(bytes);
    }

    public static DeclarationIndex Build(
        string filePath, CompilationKey compilation, string relativeSource,
        CSharpParseOptions? parseOptions = null, Encoding? encoding = null)
    {
        using var stream = File.OpenRead(filePath);
        var text = SourceText.From(stream, encoding ?? Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(text, parseOptions ?? CSharpParseOptions.Default, filePath);
        if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new ScanException($"cannot parse compile input {relativeSource} with its compiler parse options");
        return new DeclarationIndex(tree, compilation, relativeSource);
    }

    private static class DeclarationNamer
    {
        public static string? TryName(SyntaxNode node) => node switch
        {
            BaseTypeDeclarationSyntax type => TypeName(type),
            DelegateDeclarationSyntax d => $"{Parent(d)}/delegate {d.Identifier.Text}{Arity(d.TypeParameterList)}{Parameters(d.ParameterList)}",
            MethodDeclarationSyntax m => $"{Parent(m)}/method {Explicit(m.ExplicitInterfaceSpecifier)}{m.Identifier.Text}{Arity(m.TypeParameterList)}{Parameters(m.ParameterList)}",
            ConstructorDeclarationSyntax c => $"{Parent(c)}/ctor {(c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static " : string.Empty)}{c.Identifier.Text}{Parameters(c.ParameterList)}",
            DestructorDeclarationSyntax d => $"{Parent(d)}/dtor {d.Identifier.Text}()",
            OperatorDeclarationSyntax o => $"{Parent(o)}/operator {Checked(o.CheckedKeyword)}{o.OperatorToken.Text}{Parameters(o.ParameterList)}",
            ConversionOperatorDeclarationSyntax c => $"{Parent(c)}/conversion {c.ImplicitOrExplicitKeyword.Text} {Checked(c.CheckedKeyword)}{TypeKey(c.Type)}{Parameters(c.ParameterList)}",
            FieldDeclarationSyntax f => $"{Parent(f)}/field {string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
            EventFieldDeclarationSyntax e => $"{Parent(e)}/event {string.Join(",", e.Declaration.Variables.Select(v => v.Identifier.Text))}",
            PropertyDeclarationSyntax p => $"{Parent(p)}/property {Explicit(p.ExplicitInterfaceSpecifier)}{p.Identifier.Text}",
            IndexerDeclarationSyntax i => $"{Parent(i)}/indexer {Explicit(i.ExplicitInterfaceSpecifier)}this{BracketParameters(i.ParameterList)}",
            EventDeclarationSyntax e => $"{Parent(e)}/event {Explicit(e.ExplicitInterfaceSpecifier)}{e.Identifier.Text}",
            LocalFunctionStatementSyntax f => $"{Parent(f)}/local {f.Identifier.Text}{Arity(f.TypeParameterList)}{Parameters(f.ParameterList)}",
            AccessorDeclarationSyntax a => $"{Parent(a)}/accessor {a.Keyword.Text}",
            _ => null,
        };

        private static string Parent(SyntaxNode node)
        {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (TryName(parent) is { } name) return name;
                if (parent is BaseNamespaceDeclarationSyntax ns) return NamespaceName(ns);
                if (parent is CompilationUnitSyntax) return "global";
            }
            return "global";
        }

        private static string TypeName(BaseTypeDeclarationSyntax type)
        {
            var kind = type switch
            {
                ClassDeclarationSyntax => "class",
                StructDeclarationSyntax => "struct",
                InterfaceDeclarationSyntax => "interface",
                RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
                EnumDeclarationSyntax => "enum",
                _ => "type",
            };
            var arity = type is TypeDeclarationSyntax declaration && declaration.Arity > 0 ? $"`{declaration.Arity}" : string.Empty;
            return $"{Parent(type)}/{kind} {type.Identifier.Text}{arity}";
        }

        private static string NamespaceName(BaseNamespaceDeclarationSyntax ns)
        {
            var parent = ns.Parent is BaseNamespaceDeclarationSyntax enclosing ? NamespaceName(enclosing) : "global";
            return $"{parent}/namespace {ns.Name}";
        }

        private static string Checked(SyntaxToken keyword) =>
            keyword.IsKind(SyntaxKind.CheckedKeyword) ? "checked " : string.Empty;

        private static string Explicit(ExplicitInterfaceSpecifierSyntax? specifier) =>
            specifier is null ? string.Empty : specifier.Name + ".";

        private static string Arity(TypeParameterListSyntax? list) =>
            list is { Parameters.Count: > 0 } ? $"`{list.Parameters.Count}" : string.Empty;

        private static string Parameters(ParameterListSyntax list) =>
            $"({string.Join(",", list.Parameters.Select(Parameter))})";

        private static string BracketParameters(BracketedParameterListSyntax list) =>
            $"[{string.Join(",", list.Parameters.Select(Parameter))}]";

        private static string Parameter(ParameterSyntax parameter)
        {
            var modifier = parameter.Modifiers.FirstOrDefault(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword));
            return (modifier == default ? string.Empty : modifier.Text + " ") + TypeKey(parameter.Type);
        }

        private static string TypeKey(TypeSyntax? type) => type is null
            ? "?"
            : string.Concat(type.DescendantTokens(descendIntoTrivia: false).Select(token => token.Text));
    }
}
