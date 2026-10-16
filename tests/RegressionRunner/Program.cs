using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(Path.Combine(args[0], "Stardew Valley.dll")))
        {
            Console.Error.WriteLine("Pass the installed Stardew Valley directory as the sole argument.");
            return 2;
        }
        string gamePath = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string directory in new[] { gamePath, Path.Combine(gamePath, "smapi-internal") })
            {
                string path = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(path))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            return null;
        };
        try
        {
            RunTests();
            Console.WriteLine("All headless regression groups passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunTests()
    {
        NetworkRegressionTests.Run();
        CompanionRegressionTests.Run();
        SaveRegressionTests.Run();
        InventoryRegressionTests.Run();
        WorkRegressionTests.Run();
    }
}

internal static class Assert
{
    public static void True(bool condition, string message = "Assertion failed.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public static void Equal<T>(T expected, T actual, string message = "Values differ.") =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} Expected={expected}, actual={actual}.");
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
