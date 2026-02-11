using System;
using System.Collections.Generic;
using TitaniumAS.Opc.Client.Common;
using TitaniumAS.Opc.Client.Da;

namespace IndustrialBridge
{
    public class DaClient
    {
        private TitaniumAS.Opc.Client.Da.OpcDaServer _server;
        private LatestValueStore _store;

        public DaClient(LatestValueStore store)
        {
            _store = store;
        }

        public void Start()
        {
            // 1. Connect to Graybox
            // Note: Graybox.Simulator.1 is the ProgID
            _server = new TitaniumAS.Opc.Client.Da.OpcDaServer("Graybox.Simulator.1");
            _server.Connect();
            Console.WriteLine("[DA] Connected to Graybox Simulator");

            // 2. Create a Group
            var group = _server.AddGroup("BridgeGroup");
            group.IsActive = true;
            group.UpdateRate = TimeSpan.FromMilliseconds(1000);

            // 3. Define Items (Tags)
            var def1 = new OpcDaItemDefinition { ItemID = "numeric.random.double", IsActive = true };
            var def2 = new OpcDaItemDefinition { ItemID = "numeric.random.int32", IsActive = true };

            // FIX: The error "cannot convert..." happened because you passed a single item
            // to a method expecting a List. We put them in a List<T>.
            var definitions = new List<OpcDaItemDefinition> { def1, def2 };

            // 4. Add Items and Subscribe
            OpcDaItemResult[] results = group.AddItems(definitions);

            // 5. Hook up the "DataChanged" event (The Event-Based Pattern)
            group.ValuesChanged += Group_ValuesChanged;

            Console.WriteLine("[DA] Subscribed to tags.");
        }

        private void Group_ValuesChanged(object sender, OpcDaItemValuesChangedEventArgs e)
        {
            foreach (var value in e.Values)
            {
                // Push to our safe buffer
                if (value.Item.ItemID != null && value.Value != null)
                {
                    _store.Update(value.Item.ItemID, value.Value);
                }
            }
        }
    }
}