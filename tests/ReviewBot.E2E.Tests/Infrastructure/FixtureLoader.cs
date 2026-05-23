using System.Reflection;

namespace ReviewBot.E2E.Tests.Infrastructure;

public static class FixtureLoader
{
    private static readonly Assembly Assembly = typeof(FixtureLoader).Assembly;

    public static string ReadText(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var resourceName = $"{typeof(FixtureLoader).Namespace!.Replace(".Infrastructure", string.Empty)}.Fixtures.{fileName}";
        using var stream = Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded fixture '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
