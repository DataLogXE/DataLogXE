/*
DataLogXE - A simple and high performance data logger.
Copyright (C) 2026 datalogxe@gmail.com

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Net;
using System.Net.Sockets;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace DataLogLib.Tests
{
    /// <summary>
    /// An OPC UA server of the OPC Foundation stack in the test process (no security, anonymous) with an address space
    /// for the browse: a folder with more children than one page, arrays with and without ArrayDimensions, a matrix,
    /// element nodes like a Siemens S7-1500 server adds them ("Arr"[1] for Array[1..10]) and an array of structures.
    /// </summary>
    internal sealed class OpcUaTestServer : IAsyncDisposable
    {
        public const string NamespaceUri = "urn:datalogxe:test";
        public const string SiemensNamespaceUri = "http://www.siemens.com/simatic-s7-opcua";
        public const int BigFolderChildren = 2500;
        public const int HugeArrayLength = 70000;

        private readonly ApplicationInstance _application;
        private readonly string _pkiRoot;

        public string Url { get; }

        private OpcUaTestServer(ApplicationInstance application, string url, string pkiRoot)
        {
            _application = application;
            Url = url;
            _pkiRoot = pkiRoot;
        }

        /// <param name="maxBrowseContinuationPoints">Continuation points per session; the stack refuses more, 0 allows none</param>
        public static async Task<OpcUaTestServer> StartAsync(int maxBrowseContinuationPoints = 100)
        {
            var port = FreePort();
            var url = $"opc.tcp://localhost:{port}/test";
            var pkiRoot = Path.Combine(Path.GetTempPath(), "datalogxe-opcua-test-" + Guid.NewGuid().ToString("N"));
            var config = new ApplicationConfiguration
            {
                ApplicationName = "DataLogXE Test Server",
                ApplicationUri = "urn:localhost:DataLogXE:TestServer",
                ApplicationType = ApplicationType.Server,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = Path.Combine(pkiRoot, "own"),
                        SubjectName = "CN=DataLogXE Test Server"
                    },
                    TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "issuers") },
                    TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "trusted") },
                    RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "rejected") },
                    AutoAcceptUntrustedCertificates = true
                },
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = 15000,
                    MaxMessageSize = 4 * 1024 * 1024,
                    MaxArrayLength = 100000,
                    MaxStringLength = 65536,
                    MaxByteStringLength = 65536
                },
                ServerConfiguration = new ServerConfiguration
                {
                    BaseAddresses = { url },
                    SecurityPolicies =
                    {
                        new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None }
                    },
                    UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                    MaxBrowseContinuationPoints = maxBrowseContinuationPoints,
                    MinRequestThreadCount = 5,
                    MaxRequestThreadCount = 50,
                    MaxQueuedRequestCount = 200,
                    MinSubscriptionLifetime = 1000,
                    MaxSessionCount = 20,
                    MinPublishingInterval = 50,
                    MinSessionTimeout = 1000,
                    MaxSessionTimeout = 600000
                },
                TraceConfiguration = new TraceConfiguration()
            };
            await config.Validate(ApplicationType.Server);

            var application = new ApplicationInstance { ApplicationName = config.ApplicationName, ApplicationType = ApplicationType.Server, ApplicationConfiguration = config };
            await application.CheckApplicationInstanceCertificates(false);
            await application.Start(new TestServer());
            return new OpcUaTestServer(application, url, pkiRoot);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Task.Run(() => _application.Stop());
            }
            finally
            {
                try
                {
                    Directory.Delete(_pkiRoot, true);
                }
                catch
                {
                    // Only a temp folder.
                }
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class TestServer : StandardServer
        {
            protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
            {
                return new MasterNodeManager(server, configuration, null, new TestNodeManager(server, configuration));
            }
        }

        private sealed class TestNodeManager : CustomNodeManager2
        {
            public TestNodeManager(IServerInternal server, ApplicationConfiguration configuration)
                : base(server, configuration, NamespaceUri, SiemensNamespaceUri)
            {
            }

            private ushort Ns => NamespaceIndexes[0];
            private ushort SiemensNs => NamespaceIndexes[1];

            public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
            {
                lock (Lock)
                {
                    base.CreateAddressSpace(externalReferences);

                    var plant = Folder(null, "Plant");
                    plant.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
                    AddExternalReference(ObjectIds.ObjectsFolder, ReferenceTypeIds.Organizes, false, plant.NodeId, externalReferences);

                    // More children than one page of a shared Browse call, next to small siblings in the same call.
                    var big = Folder(plant, "Big");
                    for (var i = 0; i < BigFolderChildren; i++)
                    {
                        Variable(big, $"Big.v{i:D4}", DataTypeIds.Int32, ValueRanks.Scalar, null, i);
                    }

                    for (var f = 1; f <= 5; f++)
                    {
                        var small = Folder(plant, $"Small{f}");
                        for (var i = 0; i < 3; i++)
                        {
                            Variable(small, $"Small{f}.v{i}", DataTypeIds.Double, ValueRanks.Scalar, null, (double)i);
                        }
                    }

                    Variable(plant, "Scalar1", DataTypeIds.Int32, ValueRanks.Scalar, null, 7);

                    // Array[1..10] of Int with the element nodes of a Siemens server.
                    var arr = Variable(plant, "Arr", DataTypeIds.Int32, ValueRanks.OneDimension, [10u],
                        Enumerable.Range(100, 10).ToArray());
                    for (var i = 1; i <= 10; i++)
                    {
                        // Named like the S7-1500 names them: only the index.
                        Variable(arr, $"Arr[{i}]", DataTypeIds.Int32, ValueRanks.Scalar, null, 99 + i, i.ToString());
                    }

                    // No ArrayDimensions: the size comes from the value.
                    Variable(plant, "NoDims", DataTypeIds.Double, ValueRanks.OneDimension, null,
                        new[] { 0.5, 1.5, 2.5, 3.5, 4.5 });

                    Variable(plant, "Matrix", DataTypeIds.Int16, 2, [2u, 3u],
                        new Matrix(new short[] { 0, 1, 2, 10, 11, 12 }, BuiltInType.Int16, 2, 3));

                    Variable(plant, "Texts", DataTypeIds.String, ValueRanks.OneDimension, [3u], new[] { "a", "b", "c" });

                    Variable(plant, "Huge", DataTypeIds.Byte, ValueRanks.OneDimension, [(uint)HugeArrayLength], null);

                    // Element nodes that do not start at the first element: the name of the first one is not taken.
                    var wrong = Variable(plant, "Wrong", DataTypeIds.Int32, ValueRanks.OneDimension, [4u], new[] { 1, 2, 3, 4 });
                    for (var i = 5; i <= 8; i++)
                    {
                        Variable(wrong, $"Wrong[{i}]", DataTypeIds.Int32, ValueRanks.Scalar, null, i);
                    }

                    Variable(plant, "Wrong[4]", DataTypeIds.Int32, ValueRanks.Scalar, null, 4);

                    // An array of a structure type: its members are reachable only through the element nodes.
                    var myStruct = new DataTypeState
                    {
                        NodeId = new NodeId("MyStruct", Ns),
                        BrowseName = new QualifiedName("MyStruct", Ns),
                        DisplayName = "MyStruct",
                        SuperTypeId = DataTypeIds.Structure
                    };
                    AddPredefinedNode(SystemContext, myStruct);
                    AddExternalReference(DataTypeIds.Structure, ReferenceTypeIds.HasSubtype, false, myStruct.NodeId, externalReferences);
                    var structArr = Variable(plant, "StructArr", myStruct.NodeId, ValueRanks.OneDimension, [2u], null);
                    for (var i = 0; i < 2; i++)
                    {
                        var element = Variable(structArr, $"StructArr[{i}]", myStruct.NodeId, ValueRanks.Scalar, null, null);
                        Variable(element, $"StructArr[{i}].x", DataTypeIds.Int32, ValueRanks.Scalar, null, i);
                    }

                    // Instances of system FBs as an S7-1500 shows them: a Program_Alarm (left out like in the TIA export)
                    // and an R_TRIG (kept).
                    var alarm = Instance(plant, "Alm", "OT_Program_Alarm");
                    Variable(alarm, "Alm.SIG", DataTypeIds.Boolean, ValueRanks.Scalar, null, false);
                    var trig = Instance(plant, "Trig", "OT_R_TRIG");
                    Variable(trig, "Trig.Q", DataTypeIds.Boolean, ValueRanks.Scalar, null, false);

                    AddPredefinedNode(SystemContext, plant);
                }
            }

            private FolderState Folder(NodeState parent, string id)
            {
                var folder = new FolderState(parent)
                {
                    SymbolicName = id,
                    NodeId = new NodeId(id, Ns),
                    BrowseName = new QualifiedName(id.Split('.').Last(), Ns),
                    DisplayName = id.Split('.').Last(),
                    TypeDefinitionId = ObjectTypeIds.FolderType,
                    ReferenceTypeId = ReferenceTypeIds.Organizes
                };
                parent?.AddChild(folder);
                return folder;
            }

            private BaseObjectState Instance(NodeState parent, string id, string siemensType)
            {
                var instance = new BaseObjectState(parent)
                {
                    SymbolicName = id,
                    NodeId = new NodeId(id, Ns),
                    BrowseName = new QualifiedName(id, Ns),
                    DisplayName = id,
                    TypeDefinitionId = new NodeId(siemensType, SiemensNs),
                    ReferenceTypeId = ReferenceTypeIds.HasComponent
                };
                parent?.AddChild(instance);
                return instance;
            }

            private BaseDataVariableState Variable(
                NodeState parent, string id, NodeId dataType, int valueRank, uint[] dimensions, object value, string name = null)
            {
                name ??= id.Split('.').Last();
                var variable = new BaseDataVariableState(parent)
                {
                    SymbolicName = id,
                    NodeId = new NodeId(id, Ns),
                    BrowseName = new QualifiedName(name, Ns),
                    DisplayName = name,
                    TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                    ReferenceTypeId = ReferenceTypeIds.HasComponent,
                    DataType = dataType,
                    ValueRank = valueRank,
                    ArrayDimensions = dimensions == null ? null : new ReadOnlyList<uint>(dimensions),
                    AccessLevel = AccessLevels.CurrentRead,
                    UserAccessLevel = AccessLevels.CurrentRead,
                    Value = value,
                    StatusCode = StatusCodes.Good,
                    Timestamp = DateTime.UtcNow
                };
                parent?.AddChild(variable);
                return variable;
            }
        }
    }
}
