using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace OpcUaAml.Tests;

/// <summary>
/// An OPC UA server in the test process, on a free port, with a small address
/// space in <see cref="Namespace"/>:
/// <code>
/// Objects/Plant (folder)
///   Pump1 (object)   Speed: Double 12.5, Running: Boolean true, Label: String "Pump 1"
///     Motor (object) Temperature: Int32 42, Samples: Int32[] {1, 2, 3}
///   Counter: UInt32, incremented every 100 ms
/// </code>
/// </summary>
public class TestServer : IAsyncLifetime
{
    public const string Namespace = "http://example.org/Plant/";

    private ApplicationInstance? _app;
    private PlantServer? _server;

    public string EndpointUrl { get; private set; } = "";
    public string PkiRoot { get; } = Path.Combine(Path.GetTempPath(), "amlopcua-test-pki-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task InitializeAsync()
    {
        var port = FreePort();
        EndpointUrl = $"opc.tcp://localhost:{port}/AMLOpcUaTest";
        _app = new ApplicationInstance { ApplicationName = "AMLOpcUaTestServer", ApplicationType = ApplicationType.Server };
        var config = await _app.Build("urn:localhost:AMLOpcUaTestServer", "uri:test:AMLOpcUaTestServer")
            .AsServer(new[] { EndpointUrl })
            .AddUnsecurePolicyNone()
            .AddSignAndEncryptPolicies()
            .AddSecurityConfiguration("CN=AMLOpcUaTestServer", Path.Combine(PkiRoot, "server"))
            .SetAutoAcceptUntrustedCertificates(true)
            .CreateAsync();
        await _app.CheckApplicationInstanceCertificatesAsync(false, null);
        _server = new PlantServer(CreateNodeManager);
        await _app.StartAsync(_server);
    }

    public async Task DisposeAsync()
    {
        if (_server != null) await _server.StopAsync();
        try { Directory.Delete(PkiRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The address space of the server; the plant by default.</summary>
    protected virtual INodeManager CreateNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
        new PlantNodeManager(server, configuration);

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class PlantServer(Func<IServerInternal, ApplicationConfiguration, INodeManager> nodeManager) : StandardServer
    {
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration) =>
            new(server, configuration, null, nodeManager(server, configuration));
    }

    private sealed class PlantNodeManager : CustomNodeManager2
    {
        private BaseDataVariableState _counter = null!;
        private Timer? _timer;

        public PlantNodeManager(IServerInternal server, ApplicationConfiguration configuration)
            : base(server, configuration, Namespace) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                var ns = NamespaceIndexes[0];
                var plant = new FolderState(null)
                {
                    NodeId = new NodeId("Plant", ns),
                    BrowseName = new QualifiedName("Plant", ns),
                    DisplayName = "Plant",
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                };
                plant.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var refs))
                    externalReferences[ObjectIds.ObjectsFolder] = refs = new List<IReference>();
                refs.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, plant.NodeId));

                var pump = Object(plant, "Pump1", ns);
                Variable(pump, "Speed", ns, DataTypeIds.Double, 12.5);
                Variable(pump, "Running", ns, DataTypeIds.Boolean, true);
                Variable(pump, "Label", ns, DataTypeIds.String, "Pump 1");
                var motor = Object(pump, "Motor", ns);
                Variable(motor, "Temperature", ns, DataTypeIds.Int32, 42);
                var samples = Variable(motor, "Samples", ns, DataTypeIds.Int32, new[] { 1, 2, 3 });
                samples.ValueRank = ValueRanks.OneDimension;

                _counter = Variable(plant, "Counter", ns, DataTypeIds.UInt32, 0u);
                _timer = new Timer(_ =>
                {
                    lock (Lock)
                    {
                        _counter.Value = (uint)_counter.Value + 1;
                        _counter.Timestamp = DateTime.UtcNow;
                        _counter.ClearChangeMasks(SystemContext, false);
                    }
                }, null, 100, 100);

                AddPredefinedNode(SystemContext, plant);
            }
        }

        private static BaseObjectState Object(NodeState parent, string name, ushort ns)
        {
            var o = new BaseObjectState(parent)
            {
                NodeId = new NodeId($"{parent.NodeId.Identifier}.{name}", ns),
                BrowseName = new QualifiedName(name, ns),
                DisplayName = name,
                TypeDefinitionId = ObjectTypeIds.BaseObjectType,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
            };
            parent.AddChild(o);
            return o;
        }

        private static BaseDataVariableState Variable(NodeState parent, string name, ushort ns, NodeId dataType, object value)
        {
            var v = new BaseDataVariableState(parent)
            {
                NodeId = new NodeId($"{parent.NodeId.Identifier}.{name}", ns),
                BrowseName = new QualifiedName(name, ns),
                DisplayName = name,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                DataType = dataType,
                ValueRank = ValueRanks.Scalar,
                AccessLevel = AccessLevels.CurrentRead,
                UserAccessLevel = AccessLevels.CurrentRead,
                Value = value,
                StatusCode = StatusCodes.Good,
                Timestamp = DateTime.UtcNow,
            };
            parent.AddChild(v);
            return v;
        }
    }
}
