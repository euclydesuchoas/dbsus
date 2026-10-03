// Turns every csharp block of README.md and docs/*.md into code that tests/doc-examples compiles
// (net10.0 and net472, C# 8, no implicit usings) and runs, and checks the links of those files.
//
// Usage, from anywhere inside the repository:
//   dotnet run tools/doc-examples/generate.cs
//   dotnet build tests/doc-examples --configuration Release
//   dotnet run --project tests/doc-examples --configuration Release --no-build --framework net10.0
//   (and the same with --framework net472)
//
// Blocks:
// - A block opens with ```csharp. Its leading "using X;" lines become the file's usings; the rest
//   must be statements, which run inside a method (async when they contain "await").
// - ```csharp no-check marks a block left out on purpose (it needs a package the check does not
//   restore, such as Dapper or Microsoft.Data.SqlClient); such blocks are listed, not compiled.
// - ```csharp net-only and ```csharp netfx-only limit a block to .NET 5 or later, or to .NET
//   Framework (for example, code-page encodings come from different places on each).
// - Any other marker is an error, and README.md, the package page on nuget.org, takes no marker
//   at all: every block there is compiled.
// - A block that uses DatasusClient is compiled but not run: it would reach the DATASUS server.
// - Each block's lines keep their position through #line directives, so compiler errors and run
//   failures name the Markdown file and line.
// - Some examples use a split SIA-PA table that the repository does not hold. To run them, the
//   generated code reads the committed CNES fixture instead: PAMG2407a.dbc and PAMG2407b.dbc become
//   LTRR0610.dbc, and the fields PA_UFMUN and PA_VALAPR become CNES and QT_EXIST. The Markdown is
//   not changed; only the generated copy is.
//
// Links: every Markdown link in those files must be absolute; links into this repository must
// point at an existing file on the main branch, with no #anchor (nuget.org does not resolve them).
//
// Output goes to tests/doc-examples/obj/generated/ and is never committed. The process fails, with
// the file and line, on a block it does not recognize, on a broken link, or when there is no block.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

const string RepositoryUrl = "https://github.com/euclydesuchoas/dbsus/blob/main/";
string[] KnownMarkers = ["no-check", "net-only", "netfx-only"];
var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
var output = Path.Combine(root, "tests", "doc-examples", "obj", "generated");
var substitutions = new (string From, string To)[]
{
    ("PAMG2407a.dbc", "LTRR0610.dbc"),
    ("PAMG2407b.dbc", "LTRR0610.dbc"),
    ("\"PA_UFMUN\"", "\"CNES\""),
    ("\"PA_VALAPR\"", "\"QT_EXIST\""),
};

var sources = new List<string> { "README.md" };
sources.AddRange(Directory.GetFiles(Path.Combine(root, "docs"), "*.md").Select(path => "docs/" + Path.GetFileName(path)).Order(StringComparer.Ordinal));

var errors = new List<string>();
var blocks = new List<Block>();
var skipped = new List<string>();
foreach (var source in sources)
{
    var lines = File.ReadAllLines(Path.Combine(root, source));
    CheckLinks(source, lines, errors);
    for (var i = 0; i < lines.Length; i++)
    {
        if (!lines[i].StartsWith("```", StringComparison.Ordinal) || lines[i].Trim() == "```")
        {
            continue;
        }

        var info = lines[i].Substring(3).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = i + 1;
        var end = start;
        while (end < lines.Length && lines[end].Trim() != "```")
        {
            end++;
        }

        var location = $"{source}:{i + 1}";
        if (end == lines.Length)
        {
            errors.Add($"{location}: the code block is not closed.");
            break;
        }

        var language = info[0];
        var markers = info.Skip(1).ToArray();
        var unknown = markers.Except(KnownMarkers, StringComparer.Ordinal).ToArray();
        if (language is "cs" or "c#" or "C#" or "CSharp")
        {
            errors.Add($"{location}: write ```csharp (or ```csharp no-check) so the block is recognized.");
        }
        else if (language == "csharp" && source == "README.md" && markers.Length > 0)
        {
            errors.Add($"{location}: README.md is the package page on nuget.org; its csharp blocks must be plain (no markers), so that each one is compiled and, when offline, run.");
        }
        else if (language == "csharp" && unknown.Length > 0)
        {
            errors.Add($"{location}: unknown marker '{string.Join("', '", unknown)}'; the accepted markers are {string.Join(", ", KnownMarkers)}.");
        }
        else if (language == "csharp" && markers.Contains("no-check"))
        {
            skipped.Add(location);
        }
        else if (language == "csharp")
        {
            var target = markers.Contains("net-only") ? "NET" : markers.Contains("netfx-only") ? "NETFRAMEWORK" : null;
            var block = Block.Parse(source, Path.Combine(root, source).Replace('\\', '/'), start, lines[start..end], substitutions, target);
            if (block.DeclaresTypes)
            {
                errors.Add($"{location}: the block declares a type or namespace; only statements are recognized (mark it ```csharp no-check to leave it out).");
            }
            else
            {
                blocks.Add(block);
            }
        }

        i = end;
    }
}

if (blocks.Count == 0)
{
    errors.Add("No csharp block found in README.md or docs/*.md.");
}

if (errors.Count > 0)
{
    foreach (var error in errors)
    {
        Console.Error.WriteLine("error: " + error);
    }

    return 1;
}

if (Directory.Exists(output))
{
    Directory.Delete(output, recursive: true);
}

Directory.CreateDirectory(output);
for (var n = 0; n < blocks.Count; n++)
{
    File.WriteAllText(Path.Combine(output, $"Block{n + 1:D2}.cs"), blocks[n].ToCode(n + 1), new UTF8Encoding(false));
}

File.WriteAllText(Path.Combine(output, "Program.cs"), ProgramCode(blocks), new UTF8Encoding(false));
Console.WriteLine($"{blocks.Count} block(s) generated ({blocks.Count(b => b.Runs)} to run, {blocks.Count(b => !b.Runs)} compiled only); {skipped.Count} marked no-check:");
foreach (var location in skipped)
{
    Console.WriteLine("  no-check " + location);
}

return 0;

static void CheckLinks(string source, string[] lines, List<string> errors)
{
    var inCode = false;
    for (var i = 0; i < lines.Length; i++)
    {
        if (lines[i].StartsWith("```", StringComparison.Ordinal))
        {
            inCode = !inCode;
            continue;
        }

        if (inCode)
        {
            continue;
        }

        foreach (Match link in Regex.Matches(lines[i], @"\]\(([^)\s]+)\)"))
        {
            var url = link.Groups[1].Value;
            var location = $"{source}:{i + 1}";
            if (!url.StartsWith("https://", StringComparison.Ordinal))
            {
                errors.Add($"{location}: link '{url}' is not absolute.");
            }
            else if (url.Contains("github.com/euclydesuchoas/dbsus", StringComparison.Ordinal))
            {
                var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
                if (!url.StartsWith(RepositoryUrl, StringComparison.Ordinal) || url.Contains('#', StringComparison.Ordinal)
                    || !File.Exists(Path.Combine(root, url.Substring(RepositoryUrl.Length))))
                {
                    errors.Add($"{location}: link '{url}' must point at an existing file under {RepositoryUrl}, without an anchor.");
                }
            }
        }
    }
}

static string ProgramCode(List<Block> blocks)
{
    var code = new StringBuilder();
    code.AppendLine("// Generated by tools/doc-examples/generate.cs; do not edit.");
    code.AppendLine("namespace DocExamples");
    code.AppendLine("{");
    code.AppendLine("    public static class Program");
    code.AppendLine("    {");
    code.AppendLine("        public static async System.Threading.Tasks.Task<int> Main()");
    code.AppendLine("        {");
    code.AppendLine("            System.IO.Directory.SetCurrentDirectory(System.AppContext.BaseDirectory);");
    code.AppendLine("            var failures = 0;");
    for (var n = 0; n < blocks.Count; n++)
    {
        if (!blocks[n].Runs)
        {
            continue;
        }

        var call = (blocks[n].IsAsync ? "await " : string.Empty) + $"Block{n + 1:D2}.Example.Run();";
        code.AppendLine(CultureInfo.InvariantCulture, $"            try {{ {call} System.Console.WriteLine(\"ok   {blocks[n].Location}\"); }}");
        code.AppendLine(CultureInfo.InvariantCulture, $"            catch (System.Exception e) {{ failures++; System.Console.WriteLine(\"FAIL {blocks[n].Location}: \" + e.GetType().Name + \": \" + e.Message); }}");
    }

    code.AppendLine("            System.Console.WriteLine(failures == 0 ? \"every runnable block passed\" : failures + \" block(s) failed\");");
    code.AppendLine("            return failures;");
    code.AppendLine("        }");
    code.AppendLine("    }");
    code.AppendLine("}");
    return code.ToString();
}

static string FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DbSus.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run this from inside the DbSus repository.");
}

internal sealed class Block
{
    private static readonly Regex UsingLine = new(@"^using [A-Za-z][A-Za-z0-9.]*;$");
    private static readonly Regex TypeDeclaration = new(@"^\s*(public |internal |private )?(sealed |static |abstract |partial )*(class|struct|record|interface|enum|namespace)\s");

    private Block(string source, string fullPath, int firstLine, string[] usings, int bodyLine, string[] body, string? target)
    {
        FullPath = fullPath;
        Target = target;
        Source = source;
        FirstLine = firstLine;
        Usings = usings;
        BodyLine = bodyLine;
        Body = body;
    }

    public string Source { get; }

    // The Markdown file's full path, which #line needs: the compiler opens the file it names.
    public string FullPath { get; }

    // The preprocessor symbol the block is limited to (NET or NETFRAMEWORK), or null for every target.
    public string? Target { get; }

    // 1-based line of the block's first code line in the Markdown file.
    public int FirstLine { get; }

    public string[] Usings { get; }

    public int BodyLine { get; }

    public string[] Body { get; }

    public string Location => $"{Source}:{FirstLine}";

    public bool IsAsync => Body.Any(line => line.Contains("await ", StringComparison.Ordinal));

    // A block that talks to the DATASUS server is compiled, never run.
    public bool Runs => !Body.Any(line => line.Contains("DatasusClient", StringComparison.Ordinal));

    public bool DeclaresTypes => Body.Any(line => TypeDeclaration.IsMatch(line));

    public static Block Parse(string source, string fullPath, int startIndex, string[] lines, (string From, string To)[] substitutions, string? target)
    {
        var usingCount = 0;
        while (usingCount < lines.Length && UsingLine.IsMatch(lines[usingCount]))
        {
            usingCount++;
        }

        var bodyStart = usingCount;
        while (bodyStart < lines.Length && lines[bodyStart].Trim().Length == 0)
        {
            bodyStart++;
        }

        var body = lines[bodyStart..].Select(line => substitutions.Aggregate(line, (text, pair) => text.Replace(pair.From, pair.To, StringComparison.Ordinal))).ToArray();
        return new Block(source, fullPath, startIndex + 1, lines[..usingCount], startIndex + bodyStart + 1, body, target);
    }

    public string ToCode(int number)
    {
        var code = new StringBuilder();
        code.AppendLine(CultureInfo.InvariantCulture, $"// Generated from {Location} by tools/doc-examples/generate.cs; do not edit.");
        if (Usings.Length > 0)
        {
            code.AppendLine(CultureInfo.InvariantCulture, $"#line {FirstLine} \"{FullPath}\"");
            foreach (var line in Usings)
            {
                code.AppendLine(line);
            }

            code.AppendLine("#line default");
        }

        code.AppendLine(CultureInfo.InvariantCulture, $"namespace DocExamples.Block{number:D2}");
        code.AppendLine("{");
        code.AppendLine("    public static class Example");
        code.AppendLine("    {");
        code.AppendLine(CultureInfo.InvariantCulture, $"        public static {(IsAsync ? "async System.Threading.Tasks.Task" : "void")} Run()");
        code.AppendLine("        {");
        if (Target is not null)
        {
            code.AppendLine("#if " + Target);
        }

        code.AppendLine(CultureInfo.InvariantCulture, $"#line {BodyLine} \"{FullPath}\"");
        foreach (var line in Body)
        {
            code.AppendLine(line);
        }

        code.AppendLine("#line default");
        if (Target is not null)
        {
            code.AppendLine("#endif");
        }
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }
}
