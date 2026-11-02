using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// Usage: OneMedia.ApiList [--lib=<dir of dependencies>] <assembly.dll> [more.dll] > api.txt
// Loads the assemblies for inspection only (MetadataLoadContext is not in the BCL, so this uses plain reflection on the built files)
// and prints every public type and member, then a "FLAG" section for names and shapes the review looks at.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: OneMedia.ApiList [--lib=<dir>] <assembly.dll>...");
            return 2;
        }

        // Dependencies of the inspected assemblies are looked up beside them and in any --lib=<dir>.
        string[] libs = args.Where(a => a.StartsWith("--lib=", StringComparison.Ordinal)).Select(a => a[6..]).ToArray();
        args = args.Where(a => !a.StartsWith("--lib=", StringComparison.Ordinal)).ToArray();
        string[] dirs = args.Select(a => Path.GetDirectoryName(Path.GetFullPath(a))!).Concat(libs).Distinct().ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string file = new AssemblyName(e.Name).Name + ".dll";
            foreach (string d in dirs)
            {
                string candidate = Path.Combine(d, file);
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }

            return null;
        };

        List<string> lines = [];
        List<string> flags = [];
        foreach (string path in args)
        {
            Assembly asm = Assembly.LoadFrom(Path.GetFullPath(path));
            Type[] types;
            try
            {
                types = asm.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t is { IsVisible: true }).ToArray()!;
            }

            foreach (Type t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                string kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : t.IsSealed ? "sealed class" : t.IsAbstract ? "abstract class" : "class";
                string tn = t.FullName ?? t.Name;
                lines.Add($"{asm.GetName().Name} | {kind} {tn}");
                if (t.Namespace == "RecursiveAI.OneMedia")
                {
                    flags.Add($"ROOT-NS {tn}");
                }

                if (t.IsNested)
                {
                    continue;
                }

                foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!IsExposed(m))
                    {
                        continue;
                    }

                    string sig = Describe(m);
                    lines.Add($"{asm.GetName().Name} |   {tn} :: {sig}");
                    Check(tn, m, sig, flags);
                }
            }
        }

        foreach (string l in lines)
        {
            Console.WriteLine(l);
        }

        Console.WriteLine("=== FLAGS");
        foreach (string f in flags.Distinct())
        {
            Console.WriteLine(f);
        }

        return 0;
    }

    private static bool IsExposed(MemberInfo m) => m switch
    {
        MethodBase mb => (mb.IsPublic || mb.IsFamily || mb.IsFamilyOrAssembly) && !(mb is MethodInfo mi && mi.IsSpecialName),
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p => (p.GetMethod is { } g && (g.IsPublic || g.IsFamily)) || (p.SetMethod is { } s && (s.IsPublic || s.IsFamily)),
        EventInfo e => e.AddMethod is { IsPublic: true },
        _ => false,
    };

    private static string Describe(MemberInfo m)
    {
        switch (m)
        {
            case ConstructorInfo c:
                return $"ctor({string.Join(", ", c.GetParameters().Select(P))})";
            case MethodInfo mi:
                return $"{Name(mi.ReturnType)} {mi.Name}({string.Join(", ", mi.GetParameters().Select(P))})";
            case FieldInfo f:
                return $"field {Name(f.FieldType)} {f.Name}{(f.IsLiteral ? " (const)" : f.IsInitOnly ? " (readonly)" : " (MUTABLE)")}";
            case PropertyInfo p:
                return $"prop {Name(p.PropertyType)} {p.Name}{(p.SetMethod is { } s ? (s.IsPublic ? (IsInit(s) ? " {get;init}" : " {get;SET}") : " {get;protected set}") : " {get}")}";
            case EventInfo e:
                return $"event {Name(e.EventHandlerType!)} {e.Name}";
            default:
                return m.Name;
        }
    }

    private static bool IsInit(MethodInfo setter) => setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string P(ParameterInfo p) => $"{Name(p.ParameterType)} {p.Name}";

    private static string Name(Type t)
    {
        if (t.IsGenericType)
        {
            string n = t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)];
            return $"{n}<{string.Join(",", t.GetGenericArguments().Select(Name))}>";
        }

        return t.IsArray ? Name(t.GetElementType()!) + "[]" : t.Name;
    }

    private static void Check(string type, MemberInfo m, string sig, List<string> flags)
    {
        string n = m.Name;
        if (sig.Contains("{get;SET}", StringComparison.Ordinal))
        {
            flags.Add($"PUBLIC-SETTER {type}.{n}");
        }

        if (m is FieldInfo { IsLiteral: false, IsInitOnly: false } && !((FieldInfo)m).DeclaringType!.IsEnum)
        {
            flags.Add($"MUTABLE-FIELD {type}.{n}");
        }

        if (n.Contains("Debug", StringComparison.Ordinal) || n.Contains("ForTest", StringComparison.Ordinal) || n.Contains("Testing", StringComparison.Ordinal) || n.StartsWith("Internal", StringComparison.Ordinal) || n.Contains("Hook", StringComparison.Ordinal))
        {
            flags.Add($"SUSPECT-NAME {type}.{n}");
        }

        if (m is MethodInfo && n.StartsWith("Get", StringComparison.Ordinal) && !n.StartsWith("GetEnumerator", StringComparison.Ordinal) && !n.StartsWith("GetHashCode", StringComparison.Ordinal) && !n.StartsWith("GetType", StringComparison.Ordinal))
        {
            flags.Add($"GET-METHOD {type}.{sig}");
        }

        if (sig.Contains("TimeSpan", StringComparison.Ordinal))
        {
            flags.Add($"TIMESPAN {type}.{sig}");
        }

        if (sig.Contains("Double", StringComparison.Ordinal) || sig.Contains("Single", StringComparison.Ordinal))
        {
            flags.Add($"FLOATING {type}.{sig}");
        }
    }
}
