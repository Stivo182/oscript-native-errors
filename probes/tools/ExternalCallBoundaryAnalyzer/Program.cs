using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

try
{
    if (args.Length > 0 && args[0] == "--worklist")
    {
        BoundaryWorklist.Run(args[1..]);

        return 0;
    }

    var options = Options.Parse(args);
    using var analyzer = new BoundaryAnalyzer(options);
    var inventory = analyzer.Analyze();
    var unresolvedCount = inventory.Boundaries.Count(boundary => !boundary.Resolved);

    using (var stream = File.Create(options.Output))
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("SchemaVersion", 2);
        writer.WriteString("SourceRevision", options.SourceRevision);
        writer.WriteBoolean("SourceDirty", options.SourceDirty);
        writer.WriteStartObject("Environment");
        writer.WriteString("OperatingSystem", options.OperatingSystem);
        writer.WriteString("DotNetVersion", Environment.Version.ToString());
        writer.WriteEndObject();
        writer.WritePropertyName("EntryAssemblies");
        JsonSerializer.Serialize(writer, options.EntryAssemblies);
        writer.WritePropertyName("InternalAssemblies");
        JsonSerializer.Serialize(writer, options.InternalAssemblies);
        writer.WriteNumber("RootCount", inventory.RootCount);
        writer.WriteNumber("BoundaryCount", inventory.Boundaries.Count);
        writer.WriteNumber("UnresolvedCallCount", unresolvedCount);
        writer.WriteStartArray("Boundaries");
        foreach (var boundary in inventory.Boundaries) JsonSerializer.Serialize(writer, boundary);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        BoundaryCount = inventory.Boundaries.Count,
        UnresolvedCallCount = unresolvedCount
    }));

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

internal sealed record Options(
    string AssemblyDirectory,
    string Output,
    IReadOnlyList<string> EntryAssemblies,
    IReadOnlyList<string> InternalAssemblies,
    string SourceRevision,
    bool SourceDirty,
    string OperatingSystem)
{
    public static Options Parse(string[] arguments)
    {
        string? directory = null;
        string? output = null;
        var entries = new List<string>();
        var internals = new List<string>();
        var revision = "";
        var dirty = false;
        var operatingSystem = "";

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument is not ("--assembly-directory" or "--output" or "--entry-assembly" or "--internal-assembly" or
                "--source-revision" or "--source-dirty" or "--operating-system"))
                throw new ArgumentException($"Unknown argument: {argument}");
            if (++index == arguments.Length || string.IsNullOrWhiteSpace(arguments[index]) ||
                arguments[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {argument}");
            var value = arguments[index];
            switch (argument)
            {
                case "--assembly-directory": directory = Path.GetFullPath(value); break;
                case "--output": output = Path.GetFullPath(value); break;
                case "--entry-assembly": entries.Add(AssemblyName(value)); break;
                case "--internal-assembly": internals.Add(AssemblyName(value)); break;
                case "--source-revision": revision = value; break;
                case "--source-dirty": dirty = bool.Parse(value); break;
                case "--operating-system": operatingSystem = value; break;
            }
        }

        if (directory is null || output is null || entries.Count == 0 || internals.Count == 0)
            throw new ArgumentException("Assembly directory, output, entry and internal assemblies are required.");

        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);

        return new Options(directory, output,
            Array.AsReadOnly(entries.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(internals.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            revision, dirty, operatingSystem);
    }

    private static string AssemblyName(string value) => value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        ? value[..^4] : value;
}

internal sealed record Boundary(
    string OneScriptObject,
    string OneScriptMember,
    string CallerAssembly,
    string CallerType,
    string CallerMember,
    string Instruction,
    string ExternalAssembly,
    string ExternalType,
    string ExternalMember,
    bool Resolved,
    int IlOffset,
    int MetadataToken);

internal sealed record BoundaryInventory(int RootCount, IReadOnlyList<Boundary> Boundaries);

internal sealed class BoundaryLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly AssemblyDependencyResolver[] _resolvers;

    public BoundaryLoadContext(Options options) : base(isCollectible: true)
    {
        _directory = options.AssemblyDirectory;
        _resolvers = options.EntryAssemblies.Select(name =>
            new AssemblyDependencyResolver(Path.Combine(_directory, name + ".dll"))).ToArray();
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        foreach (var resolver in _resolvers)
        {
            var resolved = resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null) return LoadFromAssemblyPath(resolved);
        }
        var local = Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
    }
}

internal sealed class BoundaryAnalyzer : IDisposable
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly IReadOnlyDictionary<short, OpCode> Codes = typeof(OpCodes).GetFields()
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => code.Value);
    private readonly Options _options;
    private readonly BoundaryLoadContext _context;
    private readonly HashSet<string> _internalNames;
    private readonly Dictionary<(Module, int), List<(MethodInfo Contract, MethodInfo Implementation)>>
        _implementations = new();

    public BoundaryAnalyzer(Options options)
    {
        _options = options;
        _context = new BoundaryLoadContext(options);
        _internalNames = options.InternalAssemblies.ToHashSet(StringComparer.Ordinal);

        BuildImplementationCache(options.InternalAssemblies.Select(LoadAssembly).SelectMany(assembly => assembly.GetTypes()));
    }

    private Assembly LoadAssembly(string name) => _context.LoadFromAssemblyPath(
        Path.Combine(_options.AssemblyDirectory, name + ".dll"));

    public BoundaryInventory Analyze()
    {
        var roots = new Dictionary<(Module, int), (MethodBase Method, string Object, string Member)>();

        foreach (var name in _options.EntryAssemblies)
            foreach (var type in LoadAssembly(name).GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                var context = Attribute(type, "ContextClassAttribute") ?? Attribute(type, "GlobalContextAttribute");
                if (context is null) continue;
                var objectName = AttributeName(context, type.Name);
                foreach (var method in type.GetMethods(Members).OrderBy(method => method.MetadataToken))
                {
                    var attribute = Attribute(method, "ContextMethodAttribute") ?? Attribute(method, "ScriptConstructorAttribute");
                    if (attribute is not null) roots.TryAdd((method.Module, method.MetadataToken),
                        (method, objectName, AttributeName(attribute, method.Name)));
                }
                foreach (var property in type.GetProperties(Members).OrderBy(property => property.MetadataToken))
                {
                    var attribute = Attribute(property, "ContextPropertyAttribute");
                    if (attribute is null) continue;
                    foreach (var accessor in property.GetAccessors(nonPublic: true))
                        roots.TryAdd((accessor.Module, accessor.MetadataToken),
                            (accessor, objectName, AttributeName(attribute, property.Name)));
                }
            }

        var results = Traverse(roots);
        var sorted = results
            .OrderBy(boundary => boundary.OneScriptObject, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.OneScriptMember, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.ExternalAssembly, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.ExternalType, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.ExternalMember, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.CallerAssembly, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.CallerType, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.CallerMember, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.Instruction, StringComparer.Ordinal)
            .ThenBy(boundary => boundary.Resolved)
            .ThenBy(boundary => boundary.IlOffset)
            .ThenBy(boundary => boundary.MetadataToken).ToArray();
        return new BoundaryInventory(roots.Count, sorted);
    }

    private IReadOnlyCollection<Boundary> Traverse(
        IReadOnlyDictionary<(Module, int), (MethodBase Method, string Object, string Member)> roots)
    {
        var queue = new Queue<MethodBase>();
        var visited = new HashSet<MethodBase>();
        var results = new Dictionary<Boundary, Boundary>();

        void Enqueue(MethodBase method)
        {
            if (!_internalNames.Contains(method.Module.Assembly.GetName().Name!) || method.GetMethodBody() is null ||
                !visited.Add(method)) return;

            queue.Enqueue(method);

            if (method.DeclaringType?.TypeInitializer is { } initializer) Enqueue(initializer);

            foreach (var attribute in method.GetCustomAttributesData())
            {
                if (attribute.AttributeType.Name is not ("AsyncStateMachineAttribute" or "IteratorStateMachineAttribute"))
                    continue;

                if (attribute.ConstructorArguments.FirstOrDefault().Value is not Type stateMachine) continue;

                if (stateMachine.ContainsGenericParameters)
                {
                    var arguments = (method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes)
                        .Concat(method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes).ToArray();
                    stateMachine = stateMachine.GetGenericTypeDefinition().MakeGenericType(arguments);
                }

                if (stateMachine.GetMethod("MoveNext", Members) is { } moveNext) Enqueue(moveNext);
            }
        }

        foreach (var root in roots.Values) Enqueue(root.Method);

        while (queue.TryDequeue(out var caller))
            foreach (var instruction in Instructions(caller))
            {
                MethodBase? target;
                try
                {
                    target = caller.Module.ResolveMethod(instruction.Token,
                        caller.DeclaringType?.GetGenericArguments(), caller.IsGenericMethod ? caller.GetGenericArguments() : null);

                    if (target is null) throw new MissingMethodException();
                }
                catch (Exception exception) when (exception is ArgumentException or TypeLoadException or
                    FileNotFoundException or FileLoadException or BadImageFormatException or MissingMethodException)
                {
                    Add(CreateBoundary(null, false));

                    continue;
                }

                if (_internalNames.Contains(target.Module.Assembly.GetName().Name!))
                {
                    Enqueue(target);
                    if (target is MethodInfo dispatch && (dispatch.IsVirtual || dispatch.DeclaringType?.IsInterface == true))
                        if (_implementations.TryGetValue((target.Module, target.MetadataToken), out var implementations))
                            foreach (var (contract, implementation) in implementations)
                                if (CloseImplementation(contract, implementation, dispatch) is { } closed) Enqueue(closed);
                }
                else if (instruction.Code != OpCodes.Ldftn && instruction.Code != OpCodes.Ldvirtftn)
                    Add(CreateBoundary(target, true));

                Boundary CreateBoundary(MethodBase? external, bool resolved)
                {
                    roots.TryGetValue((caller.Module, caller.MetadataToken), out var root);
                    return new Boundary(root.Object ?? "", root.Member ?? "", caller.Module.Assembly.GetName().Name!,
                        caller.DeclaringType?.FullName ?? caller.DeclaringType?.ToString() ?? "",
                        MemberName(caller), instruction.Code.Name!, external?.Module.Assembly.GetName().Name ?? "",
                        external?.DeclaringType?.FullName ?? external?.DeclaringType?.ToString() ?? "",
                        external is null ? "" : MemberName(external), resolved, instruction.Offset, instruction.Token);
                }
            }

        return results.Values;

        void Add(Boundary boundary)
        {
            var key = boundary with
            {
                OneScriptObject = "",
                OneScriptMember = "",
                IlOffset = boundary.Resolved ? 0 : boundary.IlOffset,
                MetadataToken = boundary.Resolved ? 0 : boundary.MetadataToken
            };

            results.TryAdd(key, boundary);
        }
    }

    private void BuildImplementationCache(IEnumerable<Type> types)
    {
        foreach (var type in types)
        {
            if (type.IsInterface) continue;

            foreach (var method in type.GetMethods(Members).Where(method => method.IsVirtual))
            {
                var definition = method.GetBaseDefinition();
                Add(definition, method);
                // A call can name an intermediate override, not just the base slot.
                for (var ancestor = type.BaseType; ancestor is not null; ancestor = ancestor.BaseType)
                    foreach (var inherited in ancestor.GetMethods(Members).Where(candidate => candidate.IsVirtual))
                        if (inherited.GetBaseDefinition() == definition) Add(inherited, method);
            }

            foreach (var contract in type.GetInterfaces())
            {
                var mapping = type.GetInterfaceMap(contract);
                for (var index = 0; index < mapping.InterfaceMethods.Length; index++)
                    Add(mapping.InterfaceMethods[index], mapping.TargetMethods[index]);
            }
        }

        void Add(MethodInfo contract, MethodInfo implementation)
        {
            var key = (contract.Module, contract.MetadataToken);

            if (!_implementations.TryGetValue(key, out var methods)) _implementations[key] = methods = new();

            var pair = (contract, implementation);

            if (!methods.Contains(pair)) methods.Add(pair);
        }
    }

    private static MethodInfo? CloseImplementation(MethodInfo contract, MethodInfo implementation, MethodInfo target)
    {
        var bindings = new Dictionary<Type, Type>();

        if (!Bind(contract.DeclaringType!, target.DeclaringType!)) return null;

        var implementationType = implementation.DeclaringType!;

        if (implementationType.ContainsGenericParameters)
        {
            var arguments = implementationType.GetGenericArguments()
                .Select(argument => bindings.GetValueOrDefault(argument, argument)).ToArray();
            try { implementationType = implementationType.GetGenericTypeDefinition().MakeGenericType(arguments); }
            catch (ArgumentException) { return null; } // Generic constraints can exclude a cached implementation.

            implementation = implementationType.GetMethods(Members)
                .Single(method => method.MetadataToken == implementation.MetadataToken);
        }

        if (implementation.IsGenericMethodDefinition && target.IsGenericMethod)
        {
            try { implementation = implementation.MakeGenericMethod(target.GetGenericArguments()); }
            catch (ArgumentException) { return null; }
        }

        return implementation;

        bool Bind(Type pattern, Type actual)
        {
            if (pattern.IsGenericParameter)
            {
                if (bindings.TryGetValue(pattern, out var existing)) return existing == actual;

                bindings[pattern] = actual;

                return true;
            }

            if (pattern == actual) return true;

            if (pattern.IsArray && actual.IsArray && pattern.GetArrayRank() == actual.GetArrayRank())
                return Bind(pattern.GetElementType()!, actual.GetElementType()!);

            if (!pattern.IsGenericType || !actual.IsGenericType ||
                pattern.GetGenericTypeDefinition() != actual.GetGenericTypeDefinition()) return false;
            return pattern.GetGenericArguments().Zip(actual.GetGenericArguments())
                .All(pair => Bind(pair.First, pair.Second));
        }
    }

    private static CustomAttributeData? Attribute(MemberInfo member, string name) =>
        member.GetCustomAttributesData().FirstOrDefault(attribute => attribute.AttributeType.Name == name);

    private static string AttributeName(CustomAttributeData attribute, string fallback)
    {
        var name = attribute.ConstructorArguments.FirstOrDefault(argument => argument.ArgumentType == typeof(string)).Value;
        if (name is string { Length: > 0 } text) return text;
        return attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == "Name").TypedValue.Value
            as string ?? fallback;
    }

    private static string MemberName(MethodBase method) => method.Name +
        (method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments()
            .Select(argument => argument.ToString())) + ">" : "") + "(" +
        string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.ToString())) + "):" +
        (method is MethodInfo info ? info.ReturnType.ToString() : "System.Void");

    private static IEnumerable<(OpCode Code, int Offset, int Token)> Instructions(MethodBase method)
    {
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        var position = 0;
        while (position < bytes.Length)
        {
            var offset = position;
            short value = bytes[position++];
            if (value == 0xfe) value = (short)(0xfe00 | bytes[position++]);
            if (!Codes.TryGetValue(value, out var code)) throw new BadImageFormatException($"Unknown IL opcode at {offset}");
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(bytes, position)),
                _ => 4
            };
            if (size < 0 || position + size > bytes.Length) throw new BadImageFormatException("Truncated IL operand.");
            if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj ||
                code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn)
                yield return (code, offset, BitConverter.ToInt32(bytes, position));
            position += size;
        }
    }

    public void Dispose() => _context.Unload();
}
