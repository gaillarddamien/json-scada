/* 
 * IEC 60870-5-104 Server Protocol driver for {json:scada}
 * {json:scada} - Copyright (c) 2024 - Ricardo L. Olsen
 * This file is part of the JSON-SCADA distribution (https://github.com/riclolsen/json-scada).
 * 
 * This program is free software: you can redistribute it and/or modify  
 * it under the terms of the GNU General Public License as published by  
 * the Free Software Foundation, version 3.
 *
 * This program is distributed in the hope that it will be useful, but 
 * WITHOUT ANY WARRANTY; without even the implied warranty of 
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU 
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License 
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using Amazon.Runtime.Internal;
using Amazon.SecurityToken.Model;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Technosoftware.DaAeHdaClient;
using Technosoftware.DaAeHdaClient.Da;

namespace OPCDAClientDriver
{
    partial class MainClass
    {
        public static String CopyrightMessage = "{json:scada} OPC-DA Client Driver - Copyright 2021-2024 RLO";
        public static String ProtocolDriverName = "OPC-DA";
        public static String DriverVersion = "0.1.0";
        public static bool Active = false; // indicates this driver instance is the active node in the moment
        public static int DataBufferLimit = 20000; // limit to start dequeuing and discarding data from the acquisition buffer
        public static int BulkWriteLimit = 1250; // limit of each bulk write to mongodb

        public static int HandleCnt = 0;
        // public static Dictionary<string, string> MapNameToHandler = new Dictionary<string, string>();
        public static Dictionary<string, string> MapHandlerToItemName = new Dictionary<string, string>();
        public static Dictionary<string, string> MapHandlerToConnName = new Dictionary<string, string>();

        public static void Main(string[] args)
        {
            Log(CopyrightMessage);
            Log("Driver version " + DriverVersion);
            Log("Using Technosoftware " + LicenseHandler.Product + " " + LicenseHandler.Version);
            Technosoftware.DaAeHdaClient.Com.ApplicationInstance.InitializeSecurity(Technosoftware.DaAeHdaClient.Com.ApplicationInstance.AuthenticationLevel.Integrity);
            ApplicationInstance.EnableTrace(ApplicationInstance.GetLogFileDirectory(), "SampleClients.HDa.log");

            if (args.Length > 0) // first argument in number of the driver instance
            {
                int num;
                bool res = int.TryParse(args[0], out num);
                if (res) ProtocolDriverInstanceNumber = num;
            }
            if (args.Length > 1) // second argument is logLevel
            {
                int num;
                bool res = int.TryParse(args[1], out num);
                if (res) LogLevel = num;
            }

            string fname = JsonConfigFilePath;
            if (args.Length > 2) // third argument is config file name
            {
                if (File.Exists(args[2]))
                {
                    fname = args[2];
                }
            }
            if (!File.Exists(fname))
                fname = JsonConfigFilePathAlt;
            if (!File.Exists(fname))
            {
                Log("Missing config file " + JsonConfigFilePath);
                Environment.Exit(-1);
            }

            Log("Reading config file " + fname);
            string json = File.ReadAllText(fname);
            JSConfig = JsonSerializer.Deserialize<JSONSCADAConfig>(json);
            if (
                JSConfig.mongoConnectionString == "" ||
                JSConfig.mongoConnectionString == null
            )
            {
                Log("Missing MongoDB connection string in JSON config file " +
                fname);
                Environment.Exit(-1);
            }
            // Log("MongoDB connection string: " + JSConfig.mongoConnectionString);
            if (
                JSConfig.mongoDatabaseName == "" ||
                JSConfig.mongoDatabaseName == null
            )
            {
                Log("Missing MongoDB database name in JSON config file " +
                fname);
                Environment.Exit(-1);
            }
            Log("MongoDB database name: " + JSConfig.mongoDatabaseName);
            if (JSConfig.nodeName == "" || JSConfig.nodeName == null)
            {
                Log("Missing nodeName parameter in JSON config file " +
                fname);
                Environment.Exit(-1);
            }
            Log("Node name: " + JSConfig.nodeName);

            var Client = ConnectMongoClient(JSConfig);
            var DB = Client.GetDatabase(JSConfig.mongoDatabaseName);

            // read and process instances configuration
            var collinsts =
                DB
                    .GetCollection
                    <protocolDriverInstancesClass
                    >(ProtocolDriverInstancesCollectionName);
            var instances =
                collinsts
                    .Find(inst =>
                        inst.protocolDriver == ProtocolDriverName &&
                        inst.protocolDriverInstanceNumber ==
                        ProtocolDriverInstanceNumber &&
                        inst.enabled == true)
                    .ToList();
            var foundInstance = false;
            foreach (protocolDriverInstancesClass inst in instances)
            {
                if (
                    ProtocolDriverName == inst.protocolDriver &&
                    ProtocolDriverInstanceNumber ==
                    inst.protocolDriverInstanceNumber
                )
                {
                    foundInstance = true;
                    if (!inst.enabled)
                    {
                        Log("Driver instance [" +
                        ProtocolDriverInstanceNumber.ToString() +
                        "] disabled!");
                        Environment.Exit(-1);
                    }
                    Log("Instance: " +
                    inst.protocolDriverInstanceNumber.ToString());
                    var nodefound = false;
                    foreach (var name in inst.nodeNames)
                    {
                        if (JSConfig.nodeName == name)
                        {
                            nodefound = true;
                        }
                    }
                    if (!nodefound)
                    {
                        Log("Node '" +
                        JSConfig.nodeName +
                        "' not found in instances configuration!");
                        Environment.Exit(-1);
                    }
                    DriverInstance = inst;
                    break;
                }
                break; // process just first result
            }
            if (!foundInstance)
            {
                Log("Driver instance [" +
                ProtocolDriverInstanceNumber +
                "] not found in configuration!");
                Environment.Exit(-1);
            }

            // read and process connections configuration for this driver instance
            var collconns =
                DB
                    .GetCollection
                    <OPCUA_connection>(ProtocolConnectionsCollectionName);
            var conns =
                collconns
                    .Find(conn =>
                        conn.protocolDriver == ProtocolDriverName &&
                        conn.protocolDriverInstanceNumber ==
                        ProtocolDriverInstanceNumber &&
                        conn.enabled == true)
                    .ToList();
            var collRtData =
                DB.GetCollection<rtData>(RealtimeDataCollectionName);

            LogLevel = 3; // <<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<

            foreach (OPCUA_connection isrv in conns)
            {
                if (isrv.autoCreateTags)
                {
                    // look for existing tags in this connections, missing tags will be inserted later when discovered
                    var results = collRtData.Find<rtData>(new BsonDocument {
                                        { "protocolSourceConnectionNumber", isrv.protocolConnectionNumber },
                                        { "origin", "supervised" }
                                    }).ToList();
                    for (int i = 0; i < results.Count; i++)
                    {
                        isrv.InsertedTags.Add(results[i].tag.ToString());
                    }
                }
                isrv.LastNewKeyCreated = 0;
                if (isrv.endpointURLs.Length < 1)
                {
                    Log("Missing remote endpoint URLs list!");
                    Environment.Exit(-1);
                }
                OPCUAconns.Add(isrv);
                Log(isrv.name.ToString() + " - New Connection");
            }
            if (OPCUAconns.Count == 0)
            {
                Log("No connections found!");
                Environment.Exit(-1);
            }
            // start thread to process redundancy control
            var thrMongoRedundacy =
                new Thread(() =>
                        ProcessRedundancyMongo(JSConfig));
            thrMongoRedundacy.Start();


            // start thread to update acquired data to database
            var thrMongo =
                new Thread(() =>
                        ProcessMongo(JSConfig));
            thrMongo.Start();

            // start thread to watch for commands in the database using a change stream
            Thread thrMongoCmd =
                new Thread(() =>
                        ProcessMongoCmd(JSConfig));
            thrMongoCmd.Start();

            Thread.Sleep(1000);

            Log("Creating connections...");
            do
            {
                foreach (OPCUA_connection srv in OPCUAconns)
                {
                    if (srv.connection != null)
                    {
                        try
                        {
                            switch (srv.connection.GetServerStatus().ServerState)
                            {
                                //case OpcServerState.NeedsConfiguration:
                                //case OpcServerState.Initializing:
                                //    Log(srv.name.ToString() + "Status of Server is " + srv.connection.GetServerStatus().ServerState);
                                //    Thread.Sleep(1000);
                                //    continue;
                                case OpcServerState.Operational:
                                    Thread.Sleep(1000);
                                    continue;
                                default:
                                    Log(srv.name.ToString() + "Status of Server is " + srv.connection.GetServerStatus().ServerState);
                                    //groupState.Active = false;
                                    //group.ModifyState((int)TsCDaStateMask.Active, groupState);
                                    //group.Dispose();
                                    srv.connection.Subscriptions.Clear();
                                    srv.connection.Disconnect();
                                    srv.connection.Dispose();
                                    srv.connection = null;
                                    Thread.Sleep(1000);
                                    break;
                            }
                        }
                        catch (OpcResultException e)
                        {
                            Log(srv.name + " - " + e.Message);
                        }
                        catch (Exception e)
                        {
                            Log(srv.name + " - " + e.Message);
                        }
                    }

                    try
                    {
                        // "opcda://localhost/Matrikon.OPC.Simulation.1"; "opcda://localhost/SampleCompany.DaSample.30"; 
                        var serverUrl = srv.endpointURLs[srv.cntConnectRetries % srv.endpointURLs.Length];
                        srv.cntConnectRetries++;
                        List<string> topics = new List<string>(srv.topics);

                        var opcUrl = new OpcUrl(serverUrl);
                        if (opcUrl.Scheme != OpcUrlScheme.DA)
                        {
                            Log(srv.name + " - " + "Error! URL Scheme must be opcda://...");
                            Thread.Sleep(1000);
                            continue;
                        }
                        TsCDaServer daServer = new TsCDaServer(new Technosoftware.DaAeHdaClient.Com.Factory(), opcUrl);
                        daServer.SetClientName(srv.name);
                        srv.connection = daServer;

                        Log("Connecting to " + serverUrl);
                        // Connect to the server
                        OpcUserIdentity userIdentity = null;
                        if (srv.username != "")
                        {
                            var domainUser = srv.username.Split("/");
                            if (srv.useSecurity)
                            {
                                if (domainUser.Length > 1)
                                    userIdentity = new OpcUserIdentity(domainUser[0], domainUser[1], srv.password, srv.localCertFilePath, srv.peerCertFilePath);
                                else
                                    userIdentity = new OpcUserIdentity("", srv.username, srv.password, srv.localCertFilePath, srv.peerCertFilePath);
                            }
                            else
                            {
                                if (domainUser.Length > 1)
                                    userIdentity = new OpcUserIdentity(domainUser[0], domainUser[1], srv.password);
                                else
                                    userIdentity = new OpcUserIdentity(srv.username, srv.password);
                            }
                        }
                        daServer.Connect(new OpcConnectData(userIdentity));

                        Thread.Sleep(250);
                        switch (srv.connection.GetServerStatus().ServerState)
                        {
                            case OpcServerState.Operational:
                                break;
                            case OpcServerState.Initializing:
                                Log(srv.name.ToString() + srv.connection.GetServerStatus().ServerState);
                                Thread.Sleep(15000);
                                break;
                            default:
                                Log(srv.name.ToString() + srv.connection.GetServerStatus().ServerState);
                                Thread.Sleep(15000);
                                break;
                        }
                        switch (srv.connection.GetServerStatus().ServerState)
                        {
                            case OpcServerState.Operational:
                                break;
                            default:
                                Log(srv.name.ToString() + srv.connection.GetServerStatus().ServerState);
                                Thread.Sleep(1000);
                                continue;
                        }

                        // Get the status from the server
                        var status = daServer.GetServerStatus();
                        Log($"{srv.name} - Status of Server is {status.ServerState}");

                        var itemsForGroup = new List<TsCDaItem>();
                        BrowseServer(ref daServer, null, ref itemsForGroup, ref topics);

                        // Synchronous Read with server read function (DA 3.0) without a group
                        var itemValues = daServer.Read(itemsForGroup.ToArray());

                        for (var i = 0; i < itemValues.Length; i++)
                        {
                            if (itemValues[i].Result.IsError())
                            {
                                Log($"{srv.name} - Item {itemValues[i].ItemPath} {itemValues[i].ItemName} could not be read");
                            }
                            else
                            {
                                //Log($"{srv.name} - {itemValues[i].ItemName} {itemValues[i].Value} {itemValues[i].Quality} {itemValues[i].Value.GetType().Name}");
                                double value = 0;
                                string valueJson = string.Empty;
                                string valueString = string.Empty;
                                bool quality = true;
                                bool isDigital = false;
                                convertItemValue(itemValues[i].Value, out value, out valueString, out valueJson, out quality, out isDigital);
                                if (LogLevel > LogLevelDetailed)
                                    Log($"{srv.name} - {itemValues[i].ItemName} {valueString} {itemValues[i].Quality} {itemValues[i].Value.GetType().Name}", LogLevelDetailed);

                                var ov = new OPC_Value()
                                {
                                    valueJson = valueJson,
                                    selfPublish = false,
                                    address = "",
                                    asdu = itemValues[i].Value.GetType().Name,
                                    isDigital = isDigital,
                                    value = value,
                                    valueString = valueString,
                                    cot = 20,
                                    serverTimestamp = itemValues[i].Timestamp,
                                    sourceTimestamp = DateTime.MinValue,
                                    hasSourceTimestamp = false,
                                    quality = quality,
                                    conn_number = srv.protocolConnectionNumber,
                                    conn_name = srv.name,
                                    common_address = "",
                                    display_name = "",
                                };
                            }
                        }
                        // Console.ReadLine();

                        // Add a group with default values Active = true and UpdateRate = 500ms
                        var groupState = new TsCDaSubscriptionState { Name = "MyGroup", UpdateRate = 1000 };
                        var group = (TsCDaSubscription)daServer.CreateSubscription(groupState);

                        var itemResults = group.AddItems(itemsForGroup.ToArray());

                        for (var i = 0; i < itemResults.Length; i++)
                        {
                            if (itemResults[i].Result.IsError())
                            {
                                Log($"{srv.name} - Item {itemResults[i].ItemName} could not be added to the group");
                            }
                        }

                        group.DataChangedEvent += OnDataChangeEvent;

                        /*
                        Console.ReadLine();

                        // Set group inactive
                        groupState.Active = false;
                        group.ModifyState((int)TsCDaStateMask.Active, groupState);

                        Console.WriteLine("   Data change subscription deactivated, press <Enter> to remove all");
                        Console.WriteLine("   and disconnect from the server.");
                        group.Dispose();
                        myDaServer.Disconnect();
                        myDaServer.Dispose();
                        // Console.ReadLine();
                        Console.WriteLine("   Disconnected from the server.");
                        Console.WriteLine();
                        */
                    }
                    catch (OpcResultException e)
                    {
                        Log(srv.name + " - " + e.Message);
                        Thread.Sleep(1000);
                    }
                    catch (Exception e)
                    {
                        Log(srv.name + " - " + e.Message);
                        Thread.Sleep(1000);
                    }
                }

                if (!Console.IsInputRedirected)
                    if (Console.KeyAvailable)
                    {
                        if (Console.ReadKey().Key == ConsoleKey.Escape)
                        {
                            Log("Exiting application!");
                            Environment.Exit(0);
                        }
                        else
                            Log("Press 'Esc' key to terminate...");
                    }

            } while (true);
        }

        public static void BrowseServer(ref TsCDaServer server, OpcItem item, ref List<TsCDaItem> itemsForGroup, ref List<string> topics)
        {
            TsCDaBrowsePosition position = null;
            TsCDaBrowseFilters filters = new TsCDaBrowseFilters();
            filters.BrowseFilter = TsCDaBrowseFilter.All;

            TsCDaBrowseElement[] elements = server.Browse(item, filters, out position);
            Console.WriteLine(position);

            if (elements != null)
            {
                do
                {
                    foreach (TsCDaBrowseElement elem in elements)
                    {
                        Console.WriteLine(elem.ItemName, elem.GetType().ToString());
                        item = new OpcItem(elem.ItemPath, elem.ItemName);
                        if (elem.GetType() == typeof(TsCDaBrowseElement) && elem.HasChildren && (topics.Count == 0 || topics.Contains(elem.Name)))
                        {
                            BrowseServer(ref server, item, ref itemsForGroup, ref topics);
                        }

                        if (!elem.HasChildren)
                        {
                            Console.WriteLine("Add item to group - " + elem.ItemName);

                            var it = new TsCDaItem(item);
                            HandleCnt++;
                            it.ClientHandle = HandleCnt;
                            MapHandlerToItemName[it.ClientHandle.ToString()] = it.ItemName;
                            MapHandlerToConnName[it.ClientHandle.ToString()] = server.ClientName;

                            // MapNameToHandler[it.ItemName] = it.ClientHandle.ToString();
                            itemsForGroup.Add(it);
                        }
                    }
                    if (position != null)
                    {
                        elements = server.BrowseNext(ref position);
                        continue;
                    }
                } while (position != null);
            }
        }
        public static void convertItemValue(object iv, out double value, out string valueString, out string valueJson, out bool quality, out bool isDigital)
        {
            value = 0;
            valueJson = string.Empty;
            valueString = string.Empty;
            quality = true;
            isDigital = false;
            try
            {
                if (iv.GetType().IsArray)
                {
                    valueJson = JsonSerializer.Serialize(iv);
                    valueString = valueJson;
                }
                else
                    switch (iv.GetType().Name)
                    {
                        case "String":
                            valueString = Convert.ToString(iv);
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "Boolean":
                            value = Convert.ToBoolean(iv) ? 1 : 0;
                            isDigital = true;
                            valueJson = JsonSerializer.Serialize(iv);
                            valueString = valueJson;
                            break;
                        case "SByte":
                            value = Convert.ToSByte(iv);
                            valueJson = JsonSerializer.Serialize(iv);
                            valueString = valueJson;
                            break;
                        case "Decimal":
                            value = Convert.ToDouble(Convert.ToDecimal(iv));
                            valueJson = JsonSerializer.Serialize(iv);
                            valueString = valueJson;
                            break;
                        case "Time":
                        case "DateTime":
                            value = Convert.ToDateTime(iv).Subtract(DateTime.UnixEpoch).TotalMilliseconds;
                            valueString = Convert.ToDateTime(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "Single":
                        case "Double":
                            value = Convert.ToDouble(iv);
                            valueString = Convert.ToDouble(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "Int64":
                            value = Convert.ToDouble(iv);
                            valueString = Convert.ToInt64(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "UInt64":
                            value = Convert.ToDouble(iv);
                            valueString = Convert.ToUInt64(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "Int32":
                            value = Convert.ToDouble(iv);
                            valueString = Convert.ToInt32(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "UInt32":
                            value = Convert.ToDouble(iv);
                            valueString = Convert.ToUInt32(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "Int16":
                            value = Convert.ToDouble(iv);
                            valueString = Convert.ToInt16(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        case "UInt16":
                            value = Convert.ToDouble(iv);
                            valueString = Convert.ToUInt16(iv).ToString();
                            valueJson = JsonSerializer.Serialize(iv);
                            break;
                        default:
                            value = Convert.ToDouble(iv);
                            valueJson = JsonSerializer.Serialize(iv);
                            valueString = valueJson;
                            break;
                    }
            }
            catch
            {
                value = 0;
                quality = false;
                Log(iv.GetType().Name);
            }
        }
    }
}