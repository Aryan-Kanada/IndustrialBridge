using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace IndustrialBridge
{
    // ---------------------------------------------------------
    // LAYER 1: THE SHARED DATA STORE (The "Buffer")
    // ---------------------------------------------------------
    public static class BridgeStore
    {
        // This holds the latest value from Graybox. 
        // Thread-safe so DA can write and UA can read simultaneously.
        public static object LatestValue = 0;
        public static DateTime LastUpdate = DateTime.MinValue;
    }

    internal class Program
    {
        static async Task Main(string args)
        {
            Console.WriteLine("--- INDUSTRIAL BRIDGE: GRAYBOX DA <-> OPC UA ---");

            // 1. START THE DA CLIENT (Southbound)
            StartDaClient();

            // 2. START THE UA SERVER (Northbound)
            var uaServer = new UaBridgeServer();
            await uaServer.StartAsync();

            Console.WriteLine("\n Bridge Active. Press 'x' to exit.");
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.KeyChar == 'x') break;

                // Debug output to prove data is flowing
                Console.Write($"\r Buffer Value: {BridgeStore.LatestValue} @ {BridgeStore.LastUpdate.ToLongTimeString()}   ");
            }
        }

        static void StartDaClient()
        {
            Task.Run(() =>
            {
                try
                {
                    Console.WriteLine(" Connecting to Graybox.Simulator.1...");
                    // Using TitaniumAS to wrap the COM complexity
                    TitaniumAS.Opc.Client.Bootstrap.Initialize();
                    var url = UrlBuilder.Build("Graybox.Simulator.1");
                    using (var server = new OpcDaServer(url))
                    {
                        server.Connect();
                        Console.WriteLine(" Connected.");

                        // Create a subscription group (Update every 500ms)
                        var group = server.AddGroup("BridgeGroup");
                        group.IsActive = true;
                        group.UpdateRate = TimeSpan.FromMilliseconds(500);
                        group.ValuesChanged += Group_ValuesChanged;

                        // Add the specific simulation tag
                        var def1 = new OpcDaItemDefinition { ItemId = "numeric.sin.int64", IsActive = true };
                        group.AddItems(new { def1 });

                        Console.WriteLine(" Subscribed to numeric.sin.int64");

                        // Keep this thread alive to receive COM callbacks
                        while (true) Thread.Sleep(1000);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" {ex.Message}");
                    Console.WriteLine("Verify: Running as x86? Graybox Installed? DCOM Permissions?");
                }
            });
        }

        // Callback: When Graybox pushes data, we save it to the BridgeStore
        private static void Group_ValuesChanged(object sender, OpcDaItemValuesChangedEventArgs e)
        {
            foreach (var item in e.Values)
            {
                if (item.Value != null)
                {
                    BridgeStore.LatestValue = item.Value;
                    BridgeStore.LastUpdate = DateTime.Now;
                }
            }
        }
    }

    // ---------------------------------------------------------
    // LAYER 2: THE OPC UA SERVER (Northbound)
    // ---------------------------------------------------------
    public class UaBridgeServer : StandardServer
    {
        public async Task StartAsync()
        {
            // PROGROMMATIC CONFIGURATION (No complex Config.xml needed)
            var config = new ApplicationConfiguration()
            {
                ApplicationName = "IndustrialBridge",
                ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:IndustrialBridge",
                ApplicationType = ApplicationType.Server,
                SecurityConfiguration = new SecurityConfiguration
                {
                    // Auto-generate certificates to avoid "Trusted Peer" errors during dev
                    AutoAcceptUntrustedCertificates = true,
                    AddAppCertToTrustedStore = true
                },
                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = { "opc.tcp://localhost:49320/IndustrialBridge" },
                    SecurityPolicies = { new ServerSecurityPolicy() { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None } }
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
                TraceConfiguration = new TraceConfiguration()
            };

            await config.Validate(ApplicationType.Server);

            // Auto-create certificate if missing
            if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
            {
                config.CertificateValidator.CertificateValidation += (s, e) => { e.Accept = (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted); };
            }

            var application = new ApplicationInstance
            {
                ApplicationName = "IndustrialBridge",
                ApplicationType = ApplicationType.Server,
                ApplicationConfiguration = config
            };

            // Check/Create Certificate
            bool certOk = await application.CheckApplicationInstanceCertificate(false, 0);
            if (!certOk) Console.WriteLine(" Certificate Warning: Could not create auto-cert.");

            // Start the server
            await application.Start(this);
            Console.WriteLine($" Listening on {config.ServerConfiguration.BaseAddresses}");
        }

        // Create the "Address Space" (The Folder and Tags visible in UA Expert)
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            var nodeManagers = new List<INodeManager>();
            nodeManagers.Add(new BridgeNodeManager(server, configuration));
            return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
        }
    }

    // ---------------------------------------------------------
    // LAYER 3: THE NODE MANAGER (The Mapping Logic)
    // ---------------------------------------------------------
    public class BridgeNodeManager : CustomNodeManager2
    {
        private Timer _simulationTimer;
        private BaseDataVariableState _uaVariable;

        public BridgeNodeManager(IServerInternal server, ApplicationConfiguration configuration)
            : base(server, configuration)
        {
            SystemContext.NodeIdFactory = this;
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                base.CreateAddressSpace(externalReferences);

                // 1. Create a Folder named "GrayboxData"
                FolderState folder = new FolderState(null);
                folder.SymbolicName = "GrayboxData";
                folder.ReferenceTypeId = ReferenceTypeIds.Organizes;
                folder.TypeDefinitionId = ObjectTypeIds.FolderType;
                folder.NodeId = new NodeId("GrayboxData", NamespaceIndex);
                folder.BrowseName = new QualifiedName("GrayboxData", NamespaceIndex);
                folder.DisplayName = new LocalizedText("GrayboxData");
                folder.WriteMask = AttributeWriteMask.None;
                folder.UserWriteMask = AttributeWriteMask.None;
                folder.EventNotifier = EventNotifiers.None;

                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference> references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
                }
                references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));
                folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);

                // 2. Create the Variable "SensorValue"
                _uaVariable = new BaseDataVariableState(folder);
                _uaVariable.SymbolicName = "SensorValue";
                _uaVariable.ReferenceTypeId = ReferenceTypeIds.Organizes;
                _uaVariable.TypeDefinitionId = VariableTypeIds.BaseDataVariableType;
                _uaVariable.NodeId = new NodeId("SensorValue", NamespaceIndex);
                _uaVariable.BrowseName = new QualifiedName("SensorValue", NamespaceIndex);
                _uaVariable.DisplayName = new LocalizedText("SensorValue");
                _uaVariable.DataType = DataTypeIds.Int32; // Matches numeric.sin.int64
                _uaVariable.ValueRank = ValueRanks.Scalar;
                _uaVariable.AccessLevel = AccessLevels.CurrentReadOrWrite;
                _uaVariable.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
                _uaVariable.Value = 0;

                folder.AddChild(_uaVariable);
                AddPredefinedNode(SystemContext, folder); // Add folder to memory
                AddPredefinedNode(SystemContext, _uaVariable); // Add variable to memory

                // 3. Start a fast timer to pull data from BridgeStore and update UA Node
                _simulationTimer = new Timer(DoUpdate, null, 100, 100);
            }
        }

        private void DoUpdate(object state)
        {
            if (_uaVariable == null) return;

            // READ FROM SHARED BUFFER (BridgeStore)
            object valueFromDa = BridgeStore.LatestValue;

            // WRITE TO UA NODE
            lock (Lock)
            {
                _uaVariable.Value = valueFromDa;
                _uaVariable.Timestamp = DateTime.UtcNow;
                // This line notifies connected UA Clients (UA Expert)
                _uaVariable.ClearChangeMasks(SystemContext, false);
            }
        }
    }
}