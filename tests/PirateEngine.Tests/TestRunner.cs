using System.Diagnostics;
using System.Reflection;

namespace PirateEngine.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool condition, string message = "expected true")
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message = "expected false") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message ?? "values differ"}: expected <{expected}>, got <{actual}>");
    }

    public static void InRange(double value, double min, double max, string what = "value")
    {
        if (value < min || value > max) throw new AssertionException($"{what} = {value:0.###} not in [{min}, {max}]");
    }

    public static T NotNull<T>(T? value, string message = "expected non-null") where T : class =>
        value ?? throw new AssertionException(message);

    public static void Null(object? value, string message = "expected null")
    {
        if (value != null) throw new AssertionException($"{message}: got <{value}>");
    }

    public static void Throws<TEx>(Action a, string message = "expected exception") where TEx : Exception
    {
        try { a(); }
        catch (TEx) { return; }
        throw new AssertionException($"{message} ({typeof(TEx).Name})");
    }
}

/// <summary>Minimal reflection-based test runner. Pass a substring to filter test names.</summary>
public static class TestRunner
{
    public static int Main(string[] args)
    {
        string? filter = args.FirstOrDefault();
        var tests = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
            .Where(m => filter == null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.MetadataToken)
            .ToList();

        int passed = 0, failed = 0;
        var total = Stopwatch.StartNew();
        foreach (var m in tests)
        {
            string name = $"{m.DeclaringType!.Name}.{m.Name}";
            var sw = Stopwatch.StartNew();
            try
            {
                Log.Clear();
                m.Invoke(null, null);
                passed++;
                Console.WriteLine($"  PASS  {name}  ({sw.ElapsedMilliseconds} ms)");
                foreach (var line in Log) Console.WriteLine($"        {line}");
            }
            catch (TargetInvocationException tie)
            {
                failed++;
                var ex = tie.InnerException!;
                Console.WriteLine($"  FAIL  {name}  ({sw.ElapsedMilliseconds} ms)");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
                if (ex is not AssertionException) Console.WriteLine(ex.StackTrace);
                foreach (var line in Log) Console.WriteLine($"        {line}");
            }
        }
        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed, {tests.Count} total in {total.Elapsed.TotalSeconds:0.0}s");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Diagnostic lines printed under the test's result.</summary>
    public static readonly List<string> Log = new();
    public static void Info(string s) => Log.Add(s);
}
