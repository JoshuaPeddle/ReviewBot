namespace Demo;

public sealed class Greeter
{
    private readonly string prefix;
    private string suffix = "";

    public Greeter(string prefix)
    {
        this.prefix = prefix;
    }

    public string Greet(string name) => $"{this.prefix}{name}{this.suffix}";
}
