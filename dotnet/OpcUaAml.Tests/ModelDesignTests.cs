using System.Xml.Linq;
using OpcUaAml.ModelDesign;
using OpcUaAml.NodeSets;

namespace OpcUaAml.Tests;

public class ModelDesignTests
{
    private static readonly XNamespace Opc = ModelDesignWriter.DesignNamespace;

    private static ModelDesignResult Bundled(string uri)
    {
        var catalog = NodeSetCatalog.Create(Array.Empty<string>());
        var file = catalog.Find(uri) ?? throw new InvalidOperationException($"{uri} is not bundled.");
        return ModelDesignWriter.FromFile(file.FilePath);
    }

    [Fact]
    public void Writes_the_types_of_a_model_as_a_design()
    {
        var result = Bundled(Fixtures.DiUri);
        var root = result.Design.Root!;

        Assert.Equal(Fixtures.DiUri, (string?)root.Attribute("TargetNamespace"));
        Assert.Equal("1.05.0", (string?)root.Attribute("TargetVersion"));
        var names = root.Element(Opc + "Namespaces")!.Elements(Opc + "Namespace").Select(n => n.Value).ToList();
        Assert.Equal([Fixtures.DiUri, ModelDesignWriter.UaNamespace], names);

        var device = root.Elements(Opc + "ObjectType").Single(t => (string?)t.Attribute("SymbolicName") == "DeviceType");
        Assert.Equal("ComponentType", (string?)device.Attribute("BaseType"));
        Assert.Equal("true", (string?)device.Attribute("IsAbstract"));
        var serial = device.Element(Opc + "Children")!.Elements(Opc + "Property")
            .Single(p => (string?)p.Attribute("SymbolicName") == "SerialNumber");
        Assert.Equal("Mandatory", (string?)serial.Attribute("ModellingRule"));
        Assert.Equal("ua:String", (string?)serial.Attribute("DataType"));
    }

    [Fact]
    public void A_type_stands_after_the_type_it_derives_from()
    {
        // The compiler resolves a BaseType while it reads the file.
        var root = Bundled(Fixtures.DiUri).Design.Root!;
        var own = root.Elements().Where(e => e.Name != Opc + "Namespaces")
            .Select(e => (string?)e.Attribute("SymbolicName")).ToList();

        foreach (var type in root.Elements().Where(e => e.Name.LocalName.EndsWith("Type", StringComparison.Ordinal)))
        {
            var super = (string?)type.Attribute("BaseType");
            if (super is null || super.Contains(':')) continue;            // a type of another model
            Assert.True(own.IndexOf(super) < own.IndexOf((string?)type.Attribute("SymbolicName")),
                $"'{(string?)type.Attribute("SymbolicName")}' stands before its base type '{super}'.");
        }
    }

    [Fact]
    public void Keeps_a_placeholder_name_and_the_namespace_of_a_browse_name()
    {
        var root = Bundled(Fixtures.DiUri).Design.Root!;
        var device = root.Elements(Opc + "ObjectType").Single(t => (string?)t.Attribute("SymbolicName") == "DeviceType");
        var placeholder = device.Element(Opc + "Children")!.Elements(Opc + "Object")
            .Single(o => (string?)o.Attribute("ModellingRule") == "MandatoryPlaceholder");

        // A name XML cannot carry as a QName is kept as a BrowseName element.
        Assert.Equal("_CPIdentifier_", (string?)placeholder.Attribute("SymbolicName"));
        Assert.Equal("<CPIdentifier>", placeholder.Element(Opc + "BrowseName")?.Value);
    }

    [Fact]
    public void Writes_fields_arguments_and_reference_types()
    {
        var root = Bundled(Fixtures.DiUri).Design.Root!;

        var health = root.Elements(Opc + "DataType").Single(d => (string?)d.Attribute("SymbolicName") == "DeviceHealthEnumeration");
        Assert.Equal("ua:Enumeration", (string?)health.Attribute("BaseType"));
        var normal = health.Element(Opc + "Fields")!.Elements(Opc + "Field").First();
        Assert.Equal("NORMAL", (string?)normal.Attribute("Name"));
        Assert.Equal("0", (string?)normal.Attribute("Identifier"));

        // Two types declare GetUpdateBehavior; this is the one of CachedLoadingType.
        var update = root.Elements(Opc + "ObjectType").Single(t => (string?)t.Attribute("SymbolicName") == "CachedLoadingType")
            .Descendants(Opc + "Method").Single(m => (string?)m.Attribute("SymbolicName") == "GetUpdateBehavior");
        var arguments = update.Element(Opc + "InputArguments")!.Elements(Opc + "Argument").ToList();
        Assert.Equal("ManufacturerUri", (string?)arguments[0].Attribute("Name"));
        Assert.Equal("ua:String", (string?)arguments[0].Attribute("DataType"));
        Assert.Equal("Array", (string?)arguments[2].Attribute("ValueRank"));

        var online = root.Elements(Opc + "ReferenceType").Single(r => (string?)r.Attribute("SymbolicName") == "IsOnline");
        Assert.Equal("OnlineOf", online.Element(Opc + "InverseName")?.Value);
    }

    [Fact]
    public void Leaves_out_what_the_compiler_writes_itself()
    {
        var root = Bundled(Fixtures.DiUri).Design.Root!;
        var symbols = root.Descendants().Select(e => (string?)e.Attribute("SymbolicName")).ToList();

        // Encodings, the type dictionaries, the namespace metadata and the
        // arguments as a property: the compiler makes all of them from the design.
        Assert.DoesNotContain("Default_Binary", symbols);
        Assert.DoesNotContain("Opc_Ua_Di", symbols);
        Assert.DoesNotContain("InputArguments", symbols);
        Assert.DoesNotContain(root.Descendants(Opc + "Reference"), r =>
            r.Element(Opc + "ReferenceType")?.Value is "ua:HasEncoding" or "ua:HasDescription");
    }

    [Fact]
    public void Keeps_the_NodeIds_in_the_identifier_file()
    {
        var identifiers = Bundled(Fixtures.DiUri).Identifiers
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

        Assert.Contains("DeviceType,1002,ObjectType", identifiers);
        Assert.Contains("DeviceType_SerialNumber,6001,Variable", identifiers);
        // The nodes the compiler generates keep their ids too, under the names it gives them.
        Assert.Contains("DeviceHealthEnumeration_EnumStrings,6450,Variable", identifiers);
        Assert.Contains("FetchResultDataType_Encoding_DefaultBinary,6551,Object", identifiers);
    }

    [Fact]
    public void Refuses_a_file_that_is_no_NodeSet()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ModelDesignWriter.From(new XDocument(new XElement("Nothing"))));
        Assert.Contains("NodeSet", ex.Message);
    }

    [Fact]
    public void Says_how_to_get_the_compiler_when_it_is_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-compiler.exe");
        Assert.NotEqual(missing, ModelCompilerTool.Locate(missing));
        Assert.Contains("dotnet tool install", ModelCompilerTool.InstallHint);
        Assert.Contains(ModelCompilerTool.PackageId, ModelCompilerTool.InstallHint);
    }

    /// <summary>
    /// The whole way with the real compiler: a small NodeSet becomes a design,
    /// the compiler makes a NodeSet of it again, and the types come back with
    /// the ids they had. Where the compiler is not installed there is nothing
    /// to run, and the test passes without having checked anything.
    /// </summary>
    [Fact]
    public async Task A_small_model_survives_the_way_through_the_compiler()
    {
        if (ModelCompilerTool.Locate() is null) return;

        var folder = Directory.CreateTempSubdirectory("uaaml-design-test-");
        try
        {
            var design = Path.Combine(folder.FullName, "Mini.xml");
            ModelDesignWriter.FromFile(Fixtures.Path("modeldesign", "Mini.NodeSet2.xml")).Save(design);
            var result = await ModelCompilerTool.CompileAsync(design, Path.Combine(folder.FullName, "out"));

            var back = SafeXml.Load(result.NodeSetPath);
            XNamespace ua = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
            var nodes = back.Root!.Elements()
                .Where(e => (string?)e.Attribute("NodeId") is { } id && id.StartsWith("ns=", StringComparison.Ordinal))
                .ToDictionary(e => (string)e.Attribute("NodeId")!, e => (e.Name.LocalName, (string?)e.Attribute("BrowseName")));

            Assert.Equal(("UAObjectType", "1:PumpType"), nodes["ns=1;i=1000"]);
            Assert.Equal(("UAVariable", "1:SerialNumber"), nodes["ns=1;i=1001"]);
            Assert.Equal(("UAMethod", "1:Start"), nodes["ns=1;i=1002"]);
            Assert.Equal(("UADataType", "1:PumpState"), nodes["ns=1;i=1010"]);
            // The method keeps its InputArguments, though the compiler leaves
            // the argument list itself to the code it generates: the compiled
            // NodeSet carries the property without its value.
            Assert.Equal(("UAVariable", "InputArguments"), nodes["ns=1;i=1003"]);
            Assert.Null(back.Root.Elements(ua + "UAVariable")
                .First(v => (string?)v.Attribute("BrowseName") == "InputArguments").Element(ua + "Value"));
        }
        finally
        {
            try { folder.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// A finite state machine, the way the modeler writes one (OPC 10000-5
    /// Annex B): its states and transitions are children with a type
    /// definition, their numbers are properties of the UA namespace, and the
    /// ends of a transition are references between them. All of it has to
    /// arrive in the design, or the machine is gone after a compile.
    /// </summary>
    [Fact]
    public void A_state_machine_keeps_its_states_transitions_and_ends()
    {
        var result = ModelDesignWriter.FromFile(Fixtures.Path("modeldesign", "Machine.NodeSet2.xml"));
        var machine = result.Design.Root!.Elements(Opc + "ObjectType")
            .Single(t => (string?)t.Attribute("SymbolicName") == "PumpStateMachineType");
        var children = machine.Element(Opc + "Children")!;

        Assert.Equal("ua:FiniteStateMachineType", (string?)machine.Attribute("BaseType"));
        var idle = children.Elements(Opc + "Object").Single(o => (string?)o.Attribute("SymbolicName") == "Idle");
        Assert.Equal("ua:StateType", (string?)idle.Attribute("TypeDefinition"));
        Assert.Equal("ua:StateNumber", (string?)idle.Element(Opc + "Children")!.Elements().Single().Attribute("SymbolicName"));

        var transition = children.Elements(Opc + "Object").Single(o => (string?)o.Attribute("SymbolicName") == "IdleToRunning");
        Assert.Equal("ua:TransitionType", (string?)transition.Attribute("TypeDefinition"));
        var ends = transition.Element(Opc + "References")!.Elements(Opc + "Reference")
            .ToDictionary(r => r.Element(Opc + "ReferenceType")!.Value, r => r.Element(Opc + "TargetId")!.Value);
        // A reference names its target by the symbolic id, the path through its parents.
        Assert.Equal("PumpStateMachineType_Idle", ends["ua:FromState"]);
        Assert.Equal("PumpStateMachineType_Running", ends["ua:ToState"]);
        Assert.Equal("PumpStateMachineType_Start", ends["ua:HasCause"]);

        var identifiers = result.Identifiers.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        Assert.Contains("PumpStateMachineType_Idle_StateNumber,1005,Variable", identifiers);
        Assert.Contains("PumpStateMachineType_IdleToRunning_TransitionNumber,1011,Variable", identifiers);
    }

    [Fact]
    public async Task Names_a_design_that_does_not_exist()
    {
        var ex = await Assert.ThrowsAsync<ModelCompilerException>(
            () => ModelCompilerTool.CompileAsync("no-such-design.xml", Path.GetTempPath()));
        Assert.Contains("no-such-design.xml", ex.Message);
    }
}
