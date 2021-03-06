﻿using MobileAppServer.ServerObjects;
using MobileAppServerClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace MobileAppServer.LoadBalancingServices
{
    internal class LoadBalanceServer : IBasicServerController
    {
        internal static bool EnabledCachedResultsForUnreachableServers { get; private set; }
        internal static INotifiableSubServerRequirement NotifiableSubServerRequirement { get; private set; }
        internal static int ServersLifetimeInMinutes { get; private set; }

        internal static void EnableSubServerAutoRequirement(INotifiableSubServerRequirement notifiable,
            int serversLifetimeInMinutes = 10)
        {
            NotifiableSubServerRequirement = notifiable;
            ServersLifetimeInMinutes = serversLifetimeInMinutes;
        }

        private static void SubServer_OnLifeTimeEnded(SubServer subServer)
        {
            LogController.WriteLog($"Sub-server '{subServer.Address}:{subServer.Port}' is no longer needed and will be detached from the pool of sub-servers. A notification was issued for the target sub-server to be shut down", ServerLogType.ALERT);
            SubServers.Remove(subServer);
        }

        internal static List<SubServer> SubServers { get; private set; }

        public static void EnableCachedResultsForUnreachableServers()
        {
            EnabledCachedResultsForUnreachableServers = true;
        }

        private static void AddSubServerInternal(SubServer server, bool isDynamicInstance = false)
        {
            SubServers.Add(server);

            if (SubServers.Count > 1)
            {
                if (isDynamicInstance)
                {
                    if (NotifiableSubServerRequirement != null)
                    {
                        server.EnableLifetime(NotifiableSubServerRequirement, ServersLifetimeInMinutes);
                        server.OnLifeTimeEnded += SubServer_OnLifeTimeEnded;
                    }
                }
            }

            LogController.WriteLog($"Added sub-server node named as '{server.Address}:{server.Port}'", ServerLogType.INFO);

        }

        public static void AddSubServer(string address, int port,
            Encoding encoding, int bufferSize, int maxConnectionAttempts,
            int acceptableProcesses)
        {
            if (SubServers == null)
                SubServers = new List<SubServer>();

            var server = new SubServer(address, port, encoding,
                bufferSize, maxConnectionAttempts, acceptableProcesses);

            AddSubServerInternal(server);
      }
        
        private Client BuildClient(SubServer server)
        {
            try
            {
                Client client = new Client(server.Address,
                    server.Port, server.Encoding, server.BufferSize,
                    server.MaxConnectionAttempts);
                return client;
            }
            catch
            {
                return null;
            }
        }

        private int GetCurrentThreadCountOnServer(SubServer server, Client client)
        {
            LogController.WriteLog($"Querying availability on '{server.Address}:{server.Port}'", ServerLogType.INFO);

            MobileAppServerClient.RequestBody rb = MobileAppServerClient
                .RequestBody.Create("ServerInfoController", "GetCurrentThreadsCount");
            client.SendRequest(rb);

            int result = int.Parse(client.ReadResponse().Content.ToString());
            return result;
        }

        int attemptsToGetServerAvailable = 0;

        private bool IsServerUnavailable(SubServer server)
        {
            string key = $"unavailable-{server.Address}:{server.Port}";
            Cache<bool> cached = CacheRepository<bool>.Get(key);
            if (cached != null)
                LogController.WriteLog($"The sub-server node '{server.Address}:{server.Port}' is unreachable. It is temporarily ignored but will be reconsidered in less than 120 seconds", ServerLogType.ALERT);
            return cached != null;
        }

        private SubServer GetAvailableSubServer()
        {
            foreach (SubServer server in SubServers)
            {
                if (IsServerUnavailable(server))
                    continue;

                string key = $"unavailable-{server.Address}:{server.Port}";
                Client client = BuildClient(server);
                if (client == null)
                {
                    LogController.WriteLog($"Sub-server node '{server.Address}:{server.Port}' is unreachable", ServerLogType.ALERT);
                    try
                    {
                        client.Close();
                    }
                    catch { }
                    CacheRepository<bool>.Set(key, true, 120);
                    continue;
                }

                int serverCurrentThreadsCount = 0;

                try
                {
                    serverCurrentThreadsCount = GetCurrentThreadCountOnServer(server, client);
                }
                catch
                {
                    CacheRepository<bool>.Set(key, true, 120);
                    continue;
                }

                if (serverCurrentThreadsCount > server.AcceptableProcesses)
                {
                    LogController.WriteLog($"Sub-server node '{server.Address}:{server.Port}' is too busy", ServerLogType.ALERT);
                    //It is not necessary to close the client
                    //because it was already closed when making the request
                    continue;
                }

                LogController.WriteLog($"Elected sub-server: '{server.Address}:{server.Port}'");
                return server;
            }

            if (attemptsToGetServerAvailable == Server.GlobalInstance.AttemptsToGetSubServerAvailable)
            {
                LogController.WriteLog($"It was not possible to choose a sub-server to fulfill the request after {attemptsToGetServerAvailable} attempts", ServerLogType.ERROR);
                return null;
            }

            LogController.WriteLog($"It was not possible to elect a sub-server to process the request, but a new attempt will be made ...", ServerLogType.ERROR);
            attemptsToGetServerAvailable += 1;
            Thread.Sleep(500);
            return GetAvailableSubServer();
        }

        private string BuildCacheResultKey(MobileAppServerClient.RequestBody rb)
        {
            string cacheResultKey = $"{rb.Controller}-{rb.Action}";
            if (rb.Parameters != null)
                rb.Parameters.ForEach(p => cacheResultKey += $"-{p.Name}:{p.Value}");
            return cacheResultKey;
        }

        private ActionResult ResolveResultOnUreachableServer(string cacheResultKey)
        {
            try
            {
                if (EnabledCachedResultsForUnreachableServers)
                {
                    Cache<MobileAppServerClient.OperationResult> cached = CacheRepository<MobileAppServerClient.OperationResult>.Get(cacheResultKey);
                    if (cached != null)
                        return ActionResult.Json(cached.Value);
                    else
                        throw new Exception();
                }
                else throw new Exception();
            }
            catch
            {
                return ActionResult.Json(null, 500, "None of the sub-servers are available to fulfill the request");
            }
        }

        private void CheckAllocateNewInstance()
        {
            if (NotifiableSubServerRequirement != null)
            {
                AddSubServerInternal(NotifiableSubServerRequirement.StartNewInstance(), true);
                LogController.WriteLog("A new server instance was requested to meet the next requests", ServerLogType.ALERT);
            }
        }

        public ActionResult RunAction(string receivedData)
        {
            MobileAppServerClient.RequestBody rb = JsonConvert.DeserializeObject<MobileAppServerClient.RequestBody>(receivedData);
            SubServer targetServer = GetAvailableSubServer();

            string cacheResultKey = BuildCacheResultKey(rb);

            if (targetServer == null)
            {
                CheckAllocateNewInstance();
                return ResolveResultOnUreachableServer(cacheResultKey);
            }

            Client client = BuildClient(targetServer);
            client.SendRequest(rb);

            MobileAppServerClient.OperationResult result = client.GetResult();

            if (EnabledCachedResultsForUnreachableServers)
                CacheRepository<MobileAppServerClient.OperationResult>.Set(cacheResultKey, result, 380);

            if (targetServer.HasLifetime())
                targetServer.RefreshLifetime();
            return ActionResult.Json(result);
        }
    }
}
