using Opc.Ua;
using Opc.Ua.Server;
using System.Collections.Generic;
using System.Threading;

namespace IndustrialBridge
{
    public class DaNodeManager : CustomNodeManager2
    {
        private LatestValueStore _store;
        private Timer _timer;
        private Dictionary<string, BaseDataVariableState> _uaNodes = new Dictionary<string, BaseDataVariableState>();

        public DaNodeManager(IServerInternal server, ApplicationConfiguration configuration, LatestValueStore store)
            : base(server, configuration)
        {
            _store = store;
            // Update UA nodes every 500ms from the Store
            _timer = new Timer(UpdateValues, null, 1000, 500);
        }

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                // 1. Create a Folder "DA_Data"
                FolderState folder = CreateFolder(null, "DA_Data", "DA_Data");
                folder.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                externalReferences[ObjectIds.ObjectsFolder].Add(new NodeStateReference(ReferenceTypes.Organizes, false, folder.NodeId));
                folder.EventNotifier = EventNotifiers.SubscribeToEvents;
                AddPredefinedNode(SystemContext, folder);

                // 2. Create Variables manually (Mirroring the DA tags)
                CreateVariable(folder, "numeric.random.double", BuiltInType.Double);
                CreateVariable(folder, "numeric.random.int32", BuiltInType.Int32);
            }
        }

        private void CreateVariable(NodeState parent, string name, BuiltInType type)
        {
            var variable = new BaseDataVariableState(parent);
            variable.NodeId = new NodeId(name, NamespaceIndex);
            variable.BrowseName = new QualifiedName(name, NamespaceIndex);
            variable.DisplayName = new LocalizedText(name);
            variable.WriteMask = AttributeWriteMask.None;
            variable.UserWriteMask = AttributeWriteMask.None;
            variable.DataType = (uint)type;
            variable.ValueRank = ValueRanks.Scalar;
            variable.AccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
            variable.Historizing = false;
            variable.Value = 0;
            variable.StatusCode = StatusCodes.Good;
            variable.Timestamp = System.DateTime.UtcNow;

            if (parent != null) parent.AddChild(variable);

            AddPredefinedNode(SystemContext, variable);
            _uaNodes[name] = variable;
        }

        private void UpdateValues(object state)
        {
            // Pull data from Store -> Push to UA
            foreach (var kvp in _uaNodes)
            {
                string tagName = kvp.Key;
                var node = kvp.Value;

                object val = _store.Get(tagName);
                if (val != null)
                {
                    node.Value = val;
                    node.Timestamp = System.DateTime.UtcNow;
                    // Important: Notify clients of change
                    node.ClearChangeMasks(SystemContext, false);
                }
            }
        }
    }
}