using System.Text.Json.Nodes;

namespace AiKeepAwake.Tests;

/// <summary>The Python behaviours the port reproduces by hand. Every expected value here was printed by
/// Python 3.14 (json.dumps, format(), repr(), difflib.unified_diff), not worked out.</summary>
public class ParityTests
{
    private const string Bs = "\\";
    private static readonly string E = ((char)0xE9).ToString();
    private static readonly string LineSeparator = ((char)0x2028).ToString();
    private static readonly string Del = ((char)0x7F).ToString();

    [Fact]
    public void Json_dumps_with_default_settings()
    {
        var x = new JsonArray(PyJson.Float(1e16), PyJson.Float(1.5e-07), PyJson.Float(0.001), PyJson.Float(100.0), PyJson.Float(-0.0));
        var data = new JsonObject
        {
            ["until"] = PyJson.Float(1800000900.0),
            ["bg"] = false,
            ["pid"] = PyJson.Int(42),
            ["cwd"] = "C:" + Bs + "Jos" + E,
            ["event"] = null,
            ["at"] = PyJson.Float(1731412345.123456),
            ["x"] = x,
        };
        var expected = "{\"until\": 1800000900.0, \"bg\": false, \"pid\": 42, \"cwd\": \"C:" + Bs + Bs + "Jos" + Bs + "u00e9\", "
                       + "\"event\": null, \"at\": 1731412345.123456, \"x\": [1e+16, 1.5e-07, 0.001, 100.0, -0.0]}";
        Assert.Equal(expected, PyJson.Dump(data));
    }

    [Fact]
    public void Json_dumps_with_an_indent_and_non_ascii_kept()
    {
        var data = new JsonObject
        {
            ["a"] = new JsonObject { ["b"] = new JsonArray(), ["c"] = new JsonObject() },
            ["s"] = "caf" + E + " " + LineSeparator + " " + Del + " \t",
            ["n"] = new JsonArray(PyJson.Int(1), PyJson.Float(2.5)),
        };
        var expected = "{\n  \"a\": {\n    \"b\": [],\n    \"c\": {}\n  },\n  \"s\": \"caf" + E + " " + LineSeparator + " " + Del
                       + " \\t\",\n  \"n\": [\n    1,\n    2.5\n  ]\n}\n";
        Assert.Equal(expected, PyJson.Dump(data, 2, ensureAscii: false) + "\n");
    }

    [Fact]
    public void Parsed_numbers_are_written_back_as_python_wrote_them()
    {
        var data = PyJson.Parse("{\"a\": 1.50, \"b\": 1e5, \"c\": -0, \"d\": 12345678901234567890, \"e\": 1E400, \"a\": 2}")!;
        Assert.Equal("{\"a\": 2, \"b\": 100000.0, \"c\": 0, \"d\": 12345678901234567890, \"e\": Infinity}", PyJson.Dump(data));
    }

    [Fact]
    public void Python_equality()
    {
        Assert.True(PyJson.Equal(PyJson.Parse("{\"a\": 1, \"b\": [true]}"), PyJson.Parse("{\"b\": [1.0], \"a\": 1.0}")));
        Assert.False(PyJson.Equal(PyJson.Parse("[1, 2]"), PyJson.Parse("[2, 1]")));
        Assert.False(PyJson.Equal(PyJson.Parse("\"1\""), PyJson.Parse("1")));
    }

    [Theory]
    [InlineData(0.25, "0", "0.2")]
    [InlineData(0.35, "0", "0.3")]
    [InlineData(2.5, "2", "2.5")]
    [InlineData(3.5, "4", "3.5")]
    [InlineData(1.25, "1", "1.2")]
    [InlineData(12.345, "12", "12.3")]
    [InlineData(0.05, "0", "0.1")]
    public void Fixed_decimals_round_as_python_does(double x, string none, string one)
    {
        Assert.Equal(none, Py.Fixed(x, 0));
        Assert.Equal(one, Py.Fixed(x, 1));
    }

    [Theory]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e-07, "1.5e-07")]
    [InlineData(0.001, "0.001")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(100.0, "100.0")]
    [InlineData(1731412345.123456, "1731412345.123456")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(123456789012345680.0, "1.2345678901234568e+17")]
    [InlineData(-2.5, "-2.5")]
    public void Float_repr(double x, string expected) => Assert.Equal(expected, Py.Repr(x));

    [Fact]
    public void G_format()
    {
        Assert.Equal("3", Py.G(3));
        Assert.Equal("1.5", Py.G(1.5));
        Assert.Equal("1e-05", Py.G(0.00001));
    }

    [Fact]
    public void A_small_unified_diff()
    {
        var a = string.Concat(Enumerable.Range(1, 9).Select(i => $"line{i}\n"));
        var b = a.Replace("line3", "CHANGED") + "line10\n";
        Assert.Equal("--- f (now)\n+++ f (after)\n@@ -1,9 +1,10 @@\n line1\n line2\n-line3\n+CHANGED\n line4\n line5\n line6\n"
                     + " line7\n line8\n line9\n+line10\n",
            UnifiedDiff.Of(Py.SplitLinesKeepEnds(a), Py.SplitLinesKeepEnds(b), "f (now)", "f (after)"));
        Assert.Equal("--- f (now)\n+++ f (after)\n@@ -0,0 +1,2 @@\n+{\n+}\n", UnifiedDiff.Of([], ["{\n", "}\n"], "f (now)", "f (after)"));
        Assert.Equal("", UnifiedDiff.Of(["x\n"], ["x\n"], "f", "g"));
    }

    /// <summary>281 and 332 lines of settings-like JSON: over 200 lines, so difflib's "popular lines" rule applies.</summary>
    [Fact]
    public void A_large_unified_diff()
    {
        string Data(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));
        var diff = UnifiedDiff.Of(Py.SplitLinesKeepEnds(Data("diff-a.txt")), Py.SplitLinesKeepEnds(Data("diff-b.txt")), "s (now)", "s (after)");
        Assert.Equal(Data("diff-expected.txt"), diff);
    }

    [Fact]
    public void Splitlines_knows_every_line_boundary()
    {
        Assert.Equal(["a\r\n", "b\r", "c" + LineSeparator, "d"], Py.SplitLinesKeepEnds("a\r\nb\rc" + LineSeparator + "d"));
    }

    [Fact]
    public void Expandvars_as_ntpath()
    {
        static string? Env(string name) => name.ToUpperInvariant() switch { "A" => "1", "B_C" => "2", _ => null };
        Assert.Equal("1/2/%NO%/$NO/${NO}/%/$/'%A%'", PyPath.ExpandVars("%A%/${B_C}/%NO%/$NO/${NO}/%%/$$/'%A%'", Env));
    }
}
