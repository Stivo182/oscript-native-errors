using System;
using System.IO;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ContextClassAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ContextMethodAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[ContextClass("Fixture")]
public sealed class FixtureContext
{
    [ContextMethod("Read")]
    public string Read() => File.ReadAllText("fixture.txt");
}
