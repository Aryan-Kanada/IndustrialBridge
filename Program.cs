using System;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace IndustrialBridge
{
    internal class Program
    {
        // FIX: Main must be static void Main(string[] args)
        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting Industrial Bridge...");

            // 1. Initialize Store
            var store = new LatestValueStore();

            // 2. Start DA Client (TitaniumAS)
            try
            {
                var daClient = new DaClient(store);
                daClient.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("CRITICAL DA ERROR: " + ex.Message);
                Console.WriteLine("Ensure Graybox Simulator is running!");
                return;
            }

            // 3. Start UA Server (OPC Foundation)
            ApplicationInstance app = new ApplicationInstance
            {
                ApplicationName = "IndustrialBridge",
                ApplicationType = ApplicationType.Server,
                ConfigSectionName = "IndustrialBridge" // Matches the .xml file name logic
            };

            // FIX: Load config explicitly to avoid "file not found"
            // Note: The file name here must match what you created in Step 3
            var config = await app.LoadApplicationConfiguration("IndustrialBridge.Config.xml", false);

            // FIX: Check Certificates using the async method
            bool haveAppCertificate = await app.CheckApplicationInstanceCertificate(false, 2048);
            if (!haveAppCertificate)
            {
                throw new Exception("Application instance certificate invalid!");
            }

            // 4. Create Server
            var server = new StandardServer();
            server.CreateNodeManagers = (serverBase, configuration) =>
            {
                return new System.Collections.Generic.List<INodeManager>
                {
                    new DaNodeManager(serverBase, configuration, store)
                };
            };

            // 5. Run
            await app.Start(server);

            Console.WriteLine("UA Server Running at: " + config.ServerConfiguration.BaseAddresses[0]);
            Console.WriteLine("Press Enter to quit.");
            Console.ReadLine();
        }
    }
}