using System.Reflection;

namespace EfLens.Tests;

/// <summary>Where the build put samples/Shop, the EF Core app the EF tests load.</summary>
public static class ShopSample
{
    public static string AssemblyPath { get; } = Path.GetFullPath(
        typeof(ShopSample).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ShopAssembly").Value!);
}
