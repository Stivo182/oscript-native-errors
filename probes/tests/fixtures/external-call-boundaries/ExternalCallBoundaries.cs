using System.Text;

namespace ExternalCallBoundaries;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ContextClassAttribute : Attribute
{
    // Executing attribute constructors would make the analyzer unsafe.
    public ContextClassAttribute(string name) => throw new InvalidOperationException();
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class GlobalContextAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ContextMethodAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ContextPropertyAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ScriptConstructorAttribute : Attribute;

[ContextClass("Fixture")]
public class BoundaryContext
{
    [ContextMethod("Read")]
    public string Read(string path) => File.ReadAllText(path) + File.ReadAllText(path);

    [ContextMethod("Encode")]
    public byte[] Encode(string value) => EncodeHelper(value);

    [ContextMethod("EncodeAgain")]
    public byte[] EncodeAgain(string value) => EncodeHelper(value);

    private static byte[] EncodeHelper(string value) => Encoding.UTF8.GetBytes(value);

    [ContextMethod("Delegate")]
    public string Delegate(string value)
    {
        Func<string, string> callback = TrimHelper;
        return callback(value);
    }

    private static string TrimHelper(string value) => value.Trim();

    [ContextMethod("Interface")]
    public string Interface(IEncoder encoder, byte[] value) => encoder.Encode(value);

    [ContextMethod("Async")]
    public async Task<bool> Async(string path)
    {
        await Task.Yield();
        return File.Exists(path);
    }

    [ScriptConstructor]
    public static MemoryStream Construct() => new();

    [ContextMethod("Missing")]
    public void Missing()
    {
        MissingBoundaryDependency.ExternalDependency.Call();
        MissingBoundaryDependency.ExternalDependency.Call(42);
    }

    [ContextMethod("GenericAsync")]
    public Task<object[]> GenericAsync() => Task.WhenAll(
        AsyncHelper<int>.Run<Guid>(), AsyncHelper<string>.Run<Guid>());

    [ContextMethod("GenericIterator")]
    public IEnumerable<object> GenericIterator() =>
        IteratorHelper<int>.Run<Guid>().Concat(IteratorHelper<string>.Run<Guid>());

    [ContextProperty("Property")]
    public string Property { get => Path.GetTempPath(); set => Console.WriteLine(value); }

    [ContextMethod("Iterator")]
    public IEnumerable<string> Iterator() { yield return Path.GetRandomFileName(); }

    [ContextMethod("Static")]
    public int Static() => InitializedHelper.Read();

    [ContextMethod("Virtual")]
    public string Virtual(VirtualEncoder encoder) => encoder.Encode();

    [ContextMethod("Generic")]
    public string Generic() => GenericHelper<int>.Format(42) + GenericHelper<string>.Format("value");

    [ContextMethod("ExternalDelegate")]
    public Func<string, string> ExternalDelegate() => Path.GetFullPath;

    [ContextMethod("ExternalGeneric")]
    public object ExternalGeneric() => new object[] { Array.Empty<int>(), Array.Empty<string>() };

    [ContextMethod("GenericInterface")]
    public string GenericInterface(IEncoder<int> numbers, IEncoder<string> text) =>
        numbers.Encode(42) + text.Encode("value");

    [ContextMethod("GenericVirtual")]
    public string GenericVirtual(VirtualEncoder<int> numbers, VirtualEncoder<string> text) =>
        numbers.Encode(42) + text.Encode("value");

    [ContextMethod("GenericInterfaceMethod")]
    public object GenericInterfaceMethod(IGenericMethod encoder) => encoder.Encode<int>();

    [ContextMethod("GenericVirtualMethod")]
    public object GenericVirtualMethod(VirtualMethod encoder) => encoder.Encode<string>();

    [ContextMethod("Conversions")]
    public long Conversions(System.Numerics.BigInteger value) => (int)value + (long)value;

    private void Unreachable() => Environment.Exit(1);
}

public interface IEncoder { string Encode(byte[] value); }
public sealed class Encoder : IEncoder
{
    string IEncoder.Encode(byte[] value) => Convert.ToBase64String(value);
}

public interface IEncoder<T> { string Encode(T value); }
public class GenericEncoder<T> : IEncoder<T>
{
    public string Encode(T value) => string.Join("|", Array.Empty<T>());
}

public class StructEncoder<T> : IEncoder<T> where T : struct
{
    public string Encode(T value) => Convert.ToBase64String(BitConverter.GetBytes(42));
}

public class ExtraEncoder<T, TState> : IEncoder<T>
{
    public string Encode(T value) => File.Exists("unused") ? "exists" : "missing";
}

public interface IGenericMethod { object Encode<T>(); }
public class GenericMethodEncoder : IGenericMethod
{
    public object Encode<T>() => Array.Empty<T>();
}

public class VirtualMethod { public virtual object Encode<T>() => "base"; }
public class DerivedMethod : VirtualMethod
{
    public override object Encode<T>() => Array.Empty<T>();
}

public class VirtualEncoder<T> { public virtual string Encode(T value) => "base"; }
public class DerivedEncoder<T> : VirtualEncoder<T>
{
    public override string Encode(T value) => string.Join("|", Array.Empty<T>());
}

public class VirtualEncoder { public virtual string Encode() => "base"; }
public sealed class DerivedEncoder : VirtualEncoder
{
    public override string Encode() => Guid.NewGuid().ToString();
}

internal static class InitializedHelper
{
    private static readonly int Value;
    static InitializedHelper() { Value = Environment.ProcessorCount; }
    public static int Read() => Value;
}

internal static class GenericHelper<T>
{
    public static string Format(T value) => Convert.ToString(value) ?? "";
}

internal static class AsyncHelper<T>
{
    public static async Task<object> Run<TMethod>()
    {
        await Task.Yield();
        return new object[] { Array.Empty<T>(), Array.Empty<TMethod>() };
    }
}

internal static class IteratorHelper<T>
{
    public static IEnumerable<object> Run<TMethod>()
    {
        yield return Array.Empty<T>();
        yield return Array.Empty<TMethod>();
    }
}

[GlobalContext("Global")]
public class GlobalBoundaryContext
{
    [ContextMethod("Now")]
    public DateTime Now() => DateTime.UtcNow;
}
