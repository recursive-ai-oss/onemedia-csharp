using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace RecursiveAI.OneMedia.Tests.BrowserHost;

/// <summary>
/// A small test runner for the browser runtime. It finds the <c>[Fact]</c> and <c>[Theory]</c> methods of the suite by reflection and runs
/// them one after another on the one thread the browser gives us. Tests carrying <c>[Trait("Browser", category)]</c> need something a
/// browser cannot provide (see docs/platforms.md) and are counted as excluded, per category, instead of run.
/// </summary>
internal static class Program
{
    /// <summary>The trait name that marks a test as not runnable in a browser. Its value is the exclusion category.</summary>
    private const string ExcludeTrait = "Browser";

    private sealed class Counts
    {
        public int Passed;
        public int Failed;
        public int Skipped;
        public int Excluded;
    }

    private static async Task<int> Main(string[] args)
    {
        string? filter = null;
        bool list = false;
        bool includeExcluded = false;
        bool quiet = false;
        bool trace = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i];
                    break;
                case "--list":
                    list = true;
                    break;
                case "--include-excluded":
                    includeExcluded = true;
                    break;
                case "--trace":
                    trace = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                default:
                    break;
            }
        }

        Counts counts = new();
        SortedDictionary<string, int> excludedByCategory = [];
        List<string> failures = [];
        Stopwatch total = Stopwatch.StartNew();
        Console.WriteLine($"BASE {AppContext.BaseDirectory}");

        foreach (Type type in typeof(Program).Assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if ((!type.IsPublic && !type.IsNestedPublic) || type.IsAbstract || type.Namespace == typeof(Program).Namespace)
            {
                continue;
            }

            Dictionary<string, string> classTraits = TraitsOf(type.GetCustomAttributes(true));
            if (!list)
            {
                Console.WriteLine($"CLASS {type.FullName}");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .OrderBy(m => m.MetadataToken))
            {
                FactAttribute? fact = method.GetCustomAttributes(typeof(FactAttribute), true).Cast<FactAttribute>().FirstOrDefault();
                if (fact is null)
                {
                    continue;
                }

                string name = $"{type.FullName}.{method.Name}";
                if (filter is not null && !name.Contains(filter, StringComparison.Ordinal))
                {
                    continue;
                }

                Dictionary<string, string> traits = new(classTraits);
                foreach ((string k, string v) in TraitsOf(method.GetCustomAttributes(true)))
                {
                    traits[k] = v;
                }

                if (traits.TryGetValue(ExcludeTrait, out string? category) && !includeExcluded)
                {
                    excludedByCategory[category] = excludedByCategory.GetValueOrDefault(category) + 1;
                    counts.Excluded++;
                    if (list)
                    {
                        Console.WriteLine($"EXCLUDED[{category}] {name}");
                    }

                    continue;
                }

                if (list)
                {
                    Console.WriteLine($"TEST {name}");
                    continue;
                }

                if (!string.IsNullOrEmpty(fact.Skip))
                {
                    counts.Skipped++;
                    Console.WriteLine($"SKIP {name}: {fact.Skip}");
                    continue;
                }

                bool theory = method.GetCustomAttributes(typeof(TheoryAttribute), true).Length > 0;
                List<object?[]> rows;
                try
                {
                    rows = theory ? await TheoryRows(method).ConfigureAwait(false) : [[]];
                }
                catch (Exception ex)
                {
                    counts.Failed++;
                    failures.Add(name);
                    Console.WriteLine($"FAIL {name}: theory data: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (theory && rows.Count == 0)
                {
                    counts.Failed++;
                    failures.Add(name);
                    Console.WriteLine($"FAIL {name}: the theory has no data rows");
                    continue;
                }

                int row = 0;
                foreach (object?[] data in rows)
                {
                    string display = theory ? $"{name}[{row++}]" : name;
                    if (trace)
                    {
                        Console.WriteLine($"START {display}");
                    }

                    Stopwatch watch = Stopwatch.StartNew();
                    string? error = await RunOne(type, method, data).ConfigureAwait(false);
                    watch.Stop();
                    if (error is null)
                    {
                        counts.Passed++;
                        if (!quiet || watch.ElapsedMilliseconds > 2000)
                        {
                            Console.WriteLine($"PASS {display} {watch.ElapsedMilliseconds} ms");
                        }
                    }
                    else if (error.StartsWith("skip:", StringComparison.Ordinal))
                    {
                        counts.Skipped++;
                        Console.WriteLine($"SKIP {display}: {error[5..]}");
                    }
                    else
                    {
                        counts.Failed++;
                        failures.Add(display);
                        Console.WriteLine($"FAIL {display} {watch.ElapsedMilliseconds} ms\n{error}");
                    }
                }
            }
        }

        foreach ((string category, int n) in excludedByCategory)
        {
            Console.WriteLine($"EXCLUDED-CATEGORY {category} {n}");
        }

        foreach (string f in failures)
        {
            Console.WriteLine($"FAILED {f}");
        }

        Console.WriteLine($"RESULT passed={counts.Passed} failed={counts.Failed} skipped={counts.Skipped} excluded={counts.Excluded} seconds={total.Elapsed.TotalSeconds:F0}");
        return counts.Failed == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> TraitsOf(object[] attributes)
    {
        Dictionary<string, string> result = [];
        foreach (object a in attributes)
        {
            if (a is TraitAttribute t)
            {
                result[t.Name] = t.Value;
            }
        }

        return result;
    }

    private static async Task<List<object?[]>> TheoryRows(MethodInfo method)
    {
        List<object?[]> rows = [];
        foreach (DataAttribute data in method.GetCustomAttributes(typeof(DataAttribute), true).Cast<DataAttribute>())
        {
            IReadOnlyCollection<ITheoryDataRow>? set = await data.GetData(method, new DisposalTracker()).ConfigureAwait(false);
            if (set is null || set.Count == 0)
            {
                // xUnit's attribute declines to enumerate some sources outside its own discovery (strongly typed TheoryData<T>); read the member directly.
                if (data is MemberDataAttributeBase member)
                {
                    rows.AddRange(MemberRows(method, member));
                }

                continue;
            }

            foreach (ITheoryDataRow r in set)
            {
                if (string.IsNullOrEmpty(r.Skip))
                {
                    rows.Add(r.GetData());
                }
            }
        }

        return rows;
    }

    private static IEnumerable<object?[]> MemberRows(MethodInfo method, MemberDataAttributeBase member)
    {
        Type type = member.MemberType ?? method.DeclaringType!;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        object? value =
            type.GetProperty(member.MemberName, All)?.GetValue(null)
            ?? type.GetField(member.MemberName, All)?.GetValue(null)
            ?? type.GetMethod(member.MemberName, All)?.Invoke(null, member.Arguments);
        if (value is not System.Collections.IEnumerable items)
        {
            throw new InvalidOperationException($"member data '{member.MemberName}' not found or not enumerable");
        }

        foreach (object? item in items)
        {
            yield return item switch
            {
                ITheoryDataRow r => r.GetData(),
                object?[] a => a,
                _ => throw new InvalidOperationException($"member data row of type {item?.GetType()} is not understood"),
            };
        }
    }

    /// <summary>Runs one test method on a fresh instance. Returns null on success, "skip:..." for a dynamic skip, else the failure text.</summary>
    private static async Task<string?> RunOne(Type type, MethodInfo method, object?[] data)
    {
        object? instance = null;
        try
        {
            if (!method.IsStatic)
            {
                instance = Activator.CreateInstance(type);
            }

            ParameterInfo[] ps = method.GetParameters();
            object?[] args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                args[i] = i < data.Length ? Convert(data[i], ps[i].ParameterType) : ps[i].DefaultValue;
            }

            object? result = method.Invoke(instance, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
            }
            else if (result is ValueTask vt)
            {
                await vt.ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex)
        {
            Exception e = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;
            if (e.Message.StartsWith("$XunitDynamicSkip$", StringComparison.Ordinal))
            {
                return "skip:" + e.Message["$XunitDynamicSkip$".Length..];
            }

            string text = e.ToString();
            return text.Length > 2500 ? text[..2500] + " …" : text;
        }
        finally
        {
            if (instance is IAsyncDisposable ad)
            {
                await ad.DisposeAsync().ConfigureAwait(false);
            }
            else if (instance is IDisposable d)
            {
                d.Dispose();
            }
        }
    }

    private static object? Convert(object? value, Type target)
    {
        if (value is null)
        {
            return null;
        }

        Type t = Nullable.GetUnderlyingType(target) ?? target;
        if (t.IsInstanceOfType(value))
        {
            return value;
        }

        if (t.IsEnum)
        {
            return Enum.ToObject(t, value);
        }

        return System.Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
    }
}
