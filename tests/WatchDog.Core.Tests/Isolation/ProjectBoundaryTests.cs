using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureMetadata;
using WatchDog.CaptureProtocol;

namespace WatchDog.Core.Tests.Isolation;

public sealed class ProjectBoundaryTests
{
    public static TheoryData<Type, string[]> LeafPolicies => new()
    {
        {
            typeof(CaptureMetadataCorrelationSnapshot),
            ["System.Collections", "System.Linq", "System.Runtime"]
        },
        {
            typeof(CaptureCorrelation),
            [
                "System.Collections",
                "System.Linq",
                "System.Memory",
                "System.Runtime",
                "System.Security.Cryptography",
                "System.Text.Json",
                "System.Threading",
            ]
        },
        {
            typeof(ManagedCaptureSupervisor),
            [
                "System.Collections",
                "System.Linq",
                "System.Runtime",
                "System.Security.Cryptography",
                "System.Text.Json",
                "System.Threading",
                "WatchDog.CaptureProtocol",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(LeafPolicies))]
    public void LeafAssembliesMatchExactManagedBoundary(Type marker, string[] allowedReferences)
    {
        var violations = CompiledAssemblyBoundaryInspector.Inspect(
            marker.Assembly.Location,
            allowedReferences);

        Assert.True(
            violations.Count == 0,
            $"{marker.Assembly.GetName().Name} boundary violations:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations.Select(static item => item.ToString())));
    }

    [Fact]
    public void BootstrapObservationPreservesOneWayProjectReferencesAndDoesNotBroadenFriendAccess()
    {
        var protocolReferences = typeof(CaptureCorrelation).Assembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .ToArray();
        var isolationAssembly = typeof(HelperBootstrapObservation).Assembly;
        var isolationReferences = isolationAssembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .ToArray();
        var isolationFriends = isolationAssembly
            .GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(static attribute => attribute.AssemblyName)
            .ToArray();

        Assert.DoesNotContain("WatchDog.CaptureIsolation", protocolReferences);
        Assert.DoesNotContain("WatchDog.CaptureIsolation.Windows", protocolReferences);
        Assert.Contains("WatchDog.CaptureProtocol", isolationReferences);
        Assert.DoesNotContain("WatchDog.CaptureIsolation.Windows", isolationReferences);
        Assert.DoesNotContain(
            isolationFriends,
            static friend => friend.StartsWith(
                "WatchDog.CaptureIsolation.Windows",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DetectorRejectsUnauthorizedAssemblyReference()
    {
        var violations = CompiledAssemblyBoundaryInspector.Inspect(
            typeof(WatchDog.Core.Capture.CaptureState).Assembly.Location,
            ["System.Runtime"]);

        Assert.Contains(
            violations,
            violation => violation.Kind == BoundaryViolationKind.UnauthorizedAssemblyReference);
    }

    [Fact]
    public void DetectorRejectsPInvokeAndNativeModuleReferences()
    {
        var violations = InspectTestFixtureAssembly();

        Assert.Contains(violations, violation => violation.Kind == BoundaryViolationKind.PInvoke);
        Assert.Contains(violations, violation => violation.Kind == BoundaryViolationKind.NativeModuleReference);
    }

    [Fact]
    public void DetectorRejectsDynamicAssemblyLoadingSurfaces()
    {
        var violations = InspectTestFixtureAssembly();

        Assert.Contains(
            violations,
            violation => violation.Kind == BoundaryViolationKind.DynamicLoading &&
                violation.Detail.Contains("System.Reflection.Assembly.Load", StringComparison.Ordinal));
        Assert.Contains(
            violations,
            violation => violation.Kind == BoundaryViolationKind.DynamicLoading &&
                violation.Detail.Contains("AssemblyLoadContext.LoadFromAssemblyPath", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectorRejectsNativeLibraryAndMarshalFunctionPointerSurfaces()
    {
        var violations = InspectTestFixtureAssembly();

        Assert.Contains(
            violations,
            violation => violation.Kind == BoundaryViolationKind.DynamicLoading &&
                violation.Detail.Contains("NativeLibrary.Load", StringComparison.Ordinal));
        Assert.Contains(
            violations,
            violation => violation.Kind == BoundaryViolationKind.DynamicLoading &&
                violation.Detail.Contains("Marshal.GetFunctionPointerForDelegate", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectorRejectsIndirectNativeCalli()
    {
        var violations = InspectTestFixtureAssembly();

        Assert.Contains(violations, violation => violation.Kind == BoundaryViolationKind.Calli);
    }

    [Fact]
    public void DetectorRejectsUninvokedUnmanagedFunctionPointerSignatures()
    {
        var violations = InspectTestFixtureAssembly();

        Assert.Contains(
            violations,
            violation => violation.Kind == BoundaryViolationKind.UnmanagedFunctionPointer);
    }

    [Theory]
    [InlineData(new byte[] { 0xFE })]
    [InlineData(new byte[] { 0x24 })]
    [InlineData(new byte[] { 0x20, 0x01 })]
    public void IlInspectorRejectsMalformedBodies(byte[] malformedIl)
    {
        Assert.Equal(IlInspectionResult.Malformed, CompiledAssemblyBoundaryInspector.InspectIl(malformedIl));
    }

    private static IReadOnlyList<BoundaryViolation> InspectTestFixtureAssembly() =>
        CompiledAssemblyBoundaryInspector.Inspect(
            typeof(ProjectBoundaryTests).Assembly.Location,
            typeof(ProjectBoundaryTests).Assembly.GetReferencedAssemblies()
                .Select(static reference => reference.Name!)
                .ToArray());

    [DllImport("watchdog-boundary-fixture", EntryPoint = "never_called")]
    private static extern int PInvokeFixture();

    private static Assembly DynamicAssemblyFixture(byte[] bytes) => Assembly.Load(bytes);

    private static Assembly DynamicLoadContextFixture(string path) =>
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

    private static nint NativeLibraryFixture(string path) => NativeLibrary.Load(path);

    private static nint MarshalFunctionPointerFixture(Delegate callback) =>
        Marshal.GetFunctionPointerForDelegate(callback);

    private static unsafe int CalliFixture(delegate* unmanaged<int, int> callback) => callback(1);

    private static unsafe delegate* unmanaged<int, int> UninvokedFunctionPointerFixture(
        delegate* unmanaged<int, int> callback) => callback;
}

internal enum BoundaryViolationKind
{
    UnauthorizedAssemblyReference,
    PInvoke,
    NativeModuleReference,
    DynamicLoading,
    Calli,
    UnmanagedFunctionPointer,
    MalformedMethodBody,
}

internal enum IlInspectionResult
{
    Clean,
    ContainsCalli,
    Malformed,
}

internal sealed record BoundaryViolation(BoundaryViolationKind Kind, string Detail)
{
    public override string ToString() => $"{Kind}: {Detail}";
}

internal static class CompiledAssemblyBoundaryInspector
{
    private static readonly IReadOnlyDictionary<short, OperandType> OperandTypes =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static opcode => opcode.Value, static opcode => opcode.OperandType);

    public static IReadOnlyList<BoundaryViolation> Inspect(
        string assemblyPath,
        IEnumerable<string> allowedAssemblyReferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(allowedAssemblyReferences);
        var allowed = new HashSet<string>(allowedAssemblyReferences, StringComparer.Ordinal);
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            return [new(BoundaryViolationKind.MalformedMethodBody, "Assembly has no managed metadata.")];
        var metadata = pe.GetMetadataReader();
        var violations = new List<BoundaryViolation>();

        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            if (!allowed.Contains(name))
                violations.Add(new(BoundaryViolationKind.UnauthorizedAssemblyReference, name));
        }

        var moduleReferenceCount = metadata.GetTableRowCount(TableIndex.ModuleRef);
        for (var row = 1; row <= moduleReferenceCount; row++)
        {
            var handle = MetadataTokens.ModuleReferenceHandle(row);
            var module = metadata.GetModuleReference(handle);
            violations.Add(new(
                BoundaryViolationKind.NativeModuleReference,
                metadata.GetString(module.Name)));
        }

        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            var methodName = metadata.GetString(method.Name);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                violations.Add(new(BoundaryViolationKind.PInvoke, methodName));
            try
            {
                var signature = method.DecodeSignature(FunctionPointerSignatureProvider.Instance, genericContext: null);
                if (signature.ReturnType || signature.ParameterTypes.Any(static contains => contains))
                {
                    violations.Add(new(
                        BoundaryViolationKind.UnmanagedFunctionPointer,
                        methodName));
                }
            }
            catch (BadImageFormatException)
            {
                violations.Add(new(BoundaryViolationKind.MalformedMethodBody, methodName));
            }
            if (method.RelativeVirtualAddress == 0)
                continue;
            try
            {
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                var il = body.GetILBytes();
                if (il is null)
                {
                    violations.Add(new(BoundaryViolationKind.MalformedMethodBody, methodName));
                    continue;
                }
                var ilResult = InspectIl(il);
                if (ilResult == IlInspectionResult.ContainsCalli)
                    violations.Add(new(BoundaryViolationKind.Calli, methodName));
                else if (ilResult == IlInspectionResult.Malformed)
                    violations.Add(new(BoundaryViolationKind.MalformedMethodBody, methodName));
            }
            catch (BadImageFormatException)
            {
                violations.Add(new(BoundaryViolationKind.MalformedMethodBody, methodName));
            }
        }

        foreach (var handle in metadata.FieldDefinitions)
        {
            var field = metadata.GetFieldDefinition(handle);
            var fieldName = metadata.GetString(field.Name);
            try
            {
                if (field.DecodeSignature(FunctionPointerSignatureProvider.Instance, genericContext: null))
                {
                    violations.Add(new(
                        BoundaryViolationKind.UnmanagedFunctionPointer,
                        fieldName));
                }
            }
            catch (BadImageFormatException)
            {
                violations.Add(new(BoundaryViolationKind.MalformedMethodBody, fieldName));
            }
        }

        foreach (var handle in metadata.PropertyDefinitions)
        {
            var property = metadata.GetPropertyDefinition(handle);
            var propertyName = metadata.GetString(property.Name);
            try
            {
                var signature = property.DecodeSignature(FunctionPointerSignatureProvider.Instance, genericContext: null);
                if (signature.ReturnType || signature.ParameterTypes.Any(static contains => contains))
                {
                    violations.Add(new(
                        BoundaryViolationKind.UnmanagedFunctionPointer,
                        propertyName));
                }
            }
            catch (BadImageFormatException)
            {
                violations.Add(new(BoundaryViolationKind.MalformedMethodBody, propertyName));
            }
        }

        var standaloneSignatureCount = metadata.GetTableRowCount(TableIndex.StandAloneSig);
        for (var row = 1; row <= standaloneSignatureCount; row++)
        {
            var handle = MetadataTokens.StandaloneSignatureHandle(row);
            var signature = metadata.GetStandaloneSignature(handle);
            try
            {
                var containsFunctionPointer = signature.GetKind() switch
                {
                    StandaloneSignatureKind.LocalVariables =>
                        signature.DecodeLocalSignature(FunctionPointerSignatureProvider.Instance, genericContext: null)
                            .Any(static contains => contains),
                    StandaloneSignatureKind.Method =>
                        ContainsFunctionPointer(signature.DecodeMethodSignature(
                            FunctionPointerSignatureProvider.Instance,
                            genericContext: null)),
                    _ => true,
                };
                if (containsFunctionPointer)
                {
                    violations.Add(new(
                        BoundaryViolationKind.UnmanagedFunctionPointer,
                        $"standalone-signature:{row}"));
                }
            }
            catch (BadImageFormatException)
            {
                violations.Add(new(
                    BoundaryViolationKind.MalformedMethodBody,
                    $"standalone-signature:{row}"));
            }
        }

        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            try
            {
                var containsFunctionPointer = member.GetKind() switch
                {
                    MemberReferenceKind.Field =>
                        member.DecodeFieldSignature(FunctionPointerSignatureProvider.Instance, genericContext: null),
                    MemberReferenceKind.Method =>
                        ContainsFunctionPointer(member.DecodeMethodSignature(
                            FunctionPointerSignatureProvider.Instance,
                            genericContext: null)),
                    _ => true,
                };
                if (containsFunctionPointer)
                {
                    violations.Add(new(
                        BoundaryViolationKind.UnmanagedFunctionPointer,
                        metadata.GetString(member.Name)));
                }
            }
            catch (BadImageFormatException)
            {
                violations.Add(new(
                    BoundaryViolationKind.MalformedMethodBody,
                    metadata.GetString(member.Name)));
            }
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            var typeNamespace = metadata.GetString(type.Namespace);
            var typeName = metadata.GetString(type.Name);
            var memberName = metadata.GetString(member.Name);
            if (IsDynamicOrNativeLoadingSurface(typeNamespace, typeName, memberName))
            {
                violations.Add(new(
                    BoundaryViolationKind.DynamicLoading,
                    $"{typeNamespace}.{typeName}.{memberName}"));
            }
        }

        return violations;
    }

    private static bool ContainsFunctionPointer(MethodSignature<bool> signature) =>
        signature.ReturnType || signature.ParameterTypes.Any(static contains => contains);

    private static bool IsDynamicOrNativeLoadingSurface(
        string typeNamespace,
        string typeName,
        string memberName)
    {
        if (typeNamespace == "System.Runtime.InteropServices" && typeName == "NativeLibrary")
            return true;
        if (typeNamespace == "System.Reflection" && typeName == "Assembly" &&
            memberName.StartsWith("Load", StringComparison.Ordinal))
        {
            return true;
        }
        if (typeNamespace == "System.Runtime.Loader" && typeName == "AssemblyLoadContext" &&
            memberName.StartsWith("Load", StringComparison.Ordinal))
        {
            return true;
        }
        return typeNamespace == "System.Runtime.InteropServices" && typeName == "Marshal" &&
            memberName is "GetDelegateForFunctionPointer" or "GetFunctionPointerForDelegate";
    }

    internal static IlInspectionResult InspectIl(byte[] il)
    {
        ArgumentNullException.ThrowIfNull(il);
        var offset = 0;
        while (offset < il.Length)
        {
            var first = il[offset++];
            short opcodeValue;
            if (first == 0xFE)
            {
                if (offset >= il.Length)
                    return IlInspectionResult.Malformed;
                opcodeValue = unchecked((short)(0xFE00 | il[offset++]));
            }
            else
            {
                opcodeValue = first;
            }

            if (opcodeValue == OpCodes.Calli.Value)
                return IlInspectionResult.ContainsCalli;
            if (!OperandTypes.TryGetValue(opcodeValue, out var operandType))
                return IlInspectionResult.Malformed;
            if (!TrySkipOperand(il, ref offset, operandType))
                return IlInspectionResult.Malformed;
        }
        return IlInspectionResult.Clean;
    }

    private static bool TrySkipOperand(byte[] il, ref int offset, OperandType operandType)
    {
        var operandBytes = operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
                OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
                OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => SwitchOperandBytes(il, offset),
            _ => -1,
        };
        if (operandBytes < 0 || offset > il.Length - operandBytes)
            return false;
        offset += operandBytes;
        return true;
    }

    private static int SwitchOperandBytes(byte[] il, int offset)
    {
        if (offset > il.Length - sizeof(int))
            return -1;
        var count = BitConverter.ToInt32(il, offset);
        if (count < 0 || count > (il.Length - offset - sizeof(int)) / sizeof(int))
            return -1;
        return checked(sizeof(int) + (count * sizeof(int)));
    }
}

internal sealed class FunctionPointerSignatureProvider : ISignatureTypeProvider<bool, object?>
{
    public static FunctionPointerSignatureProvider Instance { get; } = new();

    private FunctionPointerSignatureProvider()
    {
    }

    public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;

    public bool GetByReferenceType(bool elementType) => elementType;

    public bool GetFunctionPointerType(MethodSignature<bool> signature) => true;

    public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) =>
        genericType || typeArguments.Any(static contains => contains);

    public bool GetGenericMethodParameter(object? genericContext, int index) => false;

    public bool GetGenericTypeParameter(object? genericContext, int index) => false;

    public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) =>
        modifier || unmodifiedType;

    public bool GetPinnedType(bool elementType) => elementType;

    public bool GetPointerType(bool elementType) => elementType;

    public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;

    public bool GetSZArrayType(bool elementType) => elementType;

    public bool GetTypeFromDefinition(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        byte rawTypeKind) => false;

    public bool GetTypeFromReference(
        MetadataReader reader,
        TypeReferenceHandle handle,
        byte rawTypeKind) => false;

    public bool GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
